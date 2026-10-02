using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BookmarkVault.Models;

namespace BookmarkVault.Services;

/// <summary>Edge（Chromium）书签读取与同步</summary>
public sealed class EdgeBookmarkReader
{
    /// <summary>Edge 会解析的根节点，workspaces_v2 属于标签页工作区，不读</summary>
    private static readonly string[] Roots = { "bookmark_bar", "other", "synced" };

    private static readonly Dictionary<string, string> RootNames = new()
    {
        ["bookmark_bar"] = "收藏夹栏",
        ["other"] = "其他收藏",
        ["synced"] = "移动设备收藏"
    };

    /// <summary>自动探测最近使用的 Edge 配置目录</summary>
    public string? DetectProfilePath()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Edge", "User Data");

        if (!Directory.Exists(userData)) return null;

        var candidates = new List<string>();
        var def = Path.Combine(userData, "Default", "Bookmarks");
        if (File.Exists(def)) candidates.Add(Path.Combine(userData, "Default"));

        foreach (var dir in Directory.GetDirectories(userData))
        {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.Combine(dir, "Bookmarks"))) candidates.Add(dir);
        }

        return candidates
            .OrderByDescending(p => File.GetLastWriteTimeUtc(Path.Combine(p, "Bookmarks")))
            .FirstOrDefault();
    }

    /// <summary>从配置目录读取全部书签；读取失败返回空列表</summary>
    public IReadOnlyList<BookmarkNode> Read(string profilePath)
    {
        var result = new List<BookmarkNode>();
        var file = Path.Combine(profilePath, "Bookmarks");
        if (!File.Exists(file)) return result;

        string json;
        try
        {
            // Edge 运行中会锁定文件，共享读取；实在读不到就复制副本再读
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        catch (IOException)
        {
            try
            {
                var temp = Path.Combine(Path.GetTempPath(), "bv-bookmarks-" + Guid.NewGuid().ToString("N") + ".json");
                File.Copy(file, temp, true);
                json = File.ReadAllText(temp);
                File.Delete(temp);
            }
            catch (Exception ex)
            {
                JsonStore.Log($"读取 Edge 书签失败：{ex.Message}");
                return result;
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("roots", out var roots)) return result;

            foreach (var rootName in Roots)
            {
                if (!roots.TryGetProperty(rootName, out var root)) continue;
                Walk(root, RootNames[rootName], result);
            }
        }
        catch (Exception ex)
        {
            JsonStore.Log($"解析 Edge 书签失败：{ex.Message}");
        }

        return result;
    }

    private static void Walk(JsonElement node, string folderPath, List<BookmarkNode> sink)
    {
        if (!node.TryGetProperty("children", out var children) ||
            children.ValueKind != JsonValueKind.Array) return;

        foreach (var child in children.EnumerateArray())
        {
            var type = child.TryGetProperty("type", out var t) ? t.GetString() : null;
            var name = child.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;

            if (type == "folder")
            {
                Walk(child, folderPath + " / " + name, sink);
                continue;
            }

            if (type != "url") continue;
            var url = child.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue; // 跳过 javascript: 等

            sink.Add(new BookmarkNode
            {
                Title = string.IsNullOrWhiteSpace(name) ? UrlHelper.GetDomain(url) : name,
                Url = url,
                SourceId = child.TryGetProperty("guid", out var g) ? g.GetString() ?? string.Empty : string.Empty,
                SourceFolder = folderPath,
                VisitCount = child.TryGetProperty("visit_count", out var v) && v.TryGetInt32(out var vc) ? vc : 0,
                EdgeDateAdded = ReadEdgeDate(child)
            });
        }
    }

    /// <summary>Edge 使用 1601-01-01 起的微秒数</summary>
    private static DateTime? ReadEdgeDate(JsonElement node)
    {
        if (!node.TryGetProperty("date_added", out var d)) return null;
        var raw = d.GetString();
        if (!long.TryParse(raw, out var micros) || micros <= 0) return null;
        try
        {
            return new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(micros * 10).ToLocalTime();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把 Edge 书签合并进本地库：已有的按 GUID 更新标题/URL，新的追加</summary>
    public (int added, int updated) Merge(IReadOnlyList<BookmarkNode> incoming, LibraryDatabase database)
    {
        var bySource = database.Bookmarks
            .Where(b => !string.IsNullOrEmpty(b.SourceId))
            .GroupBy(b => b.SourceId)
            .ToDictionary(g => g.Key, g => g.First());

        var byUrl = database.Bookmarks
            .GroupBy(b => b.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int added = 0, updated = 0;
        foreach (var item in incoming)
        {
            BookmarkNode? existing = null;
            if (!string.IsNullOrEmpty(item.SourceId)) bySource.TryGetValue(item.SourceId, out existing);
            existing ??= byUrl.GetValueOrDefault(item.Url);

            if (existing == null)
            {
                database.Bookmarks.Add(item);
                byUrl[item.Url] = item;
                if (!string.IsNullOrEmpty(item.SourceId)) bySource[item.SourceId] = item;
                added++;
            }
            else
            {
                var changed = false;
                if (existing.Title != item.Title && !string.IsNullOrWhiteSpace(item.Title))
                {
                    existing.Title = item.Title;
                    changed = true;
                }
                if (existing.SourceFolder != item.SourceFolder)
                {
                    existing.SourceFolder = item.SourceFolder;
                    changed = true;
                }
                if (string.IsNullOrEmpty(existing.SourceId) && !string.IsNullOrEmpty(item.SourceId))
                {
                    existing.SourceId = item.SourceId;
                    changed = true;
                }
                if (existing.VisitCount < item.VisitCount)
                {
                    existing.VisitCount = item.VisitCount;
                    changed = true;
                }
                if (existing.EdgeDateAdded == null) existing.EdgeDateAdded = item.EdgeDateAdded;
                if (changed) updated++;
            }
        }
        return (added, updated);
    }
}
