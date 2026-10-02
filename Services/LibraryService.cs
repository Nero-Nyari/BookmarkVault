using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BookmarkVault.Models;

namespace BookmarkVault.Services;

/// <summary>
/// 主数据服务：负责本地库的加载保存、Edge 同步、检测结果写入、分类维护。
/// 所有数据都在本应用自己的库里，不回写 Edge。
/// </summary>
public sealed class LibraryService
{
    private static readonly string[] CategoryPalette =
    {
        "#66C0F4", "#4FC3F7", "#B388FF", "#FF8A65", "#FF5252", "#4DD0E1", "#FFB74D",
        "#81C784", "#7E57C2", "#64B5F6", "#FFD54F", "#4DB6AC", "#7986CB", "#AED581",
        "#F06292", "#A1887F"
    };

    /// <summary>Edge 收藏夹的三个根节点，取文件夹名时要跳过</summary>
    private static readonly string[] EdgeRootNames = { "收藏夹栏", "其他收藏", "移动设备收藏" };

    public LibraryDatabase Database { get; private set; } = new();
    public CacheDatabase Cache { get; private set; } = new();
    public string? EdgeProfilePath { get; private set; }
    public string? LastSyncMessage { get; private set; }

    private readonly EdgeBookmarkReader _reader = new();

    public void Load()
    {
        AppPaths.EnsureCreated();
        Database = JsonStore.Load(AppPaths.LibraryFile, () => new LibraryDatabase());
        Cache = JsonStore.Load(AppPaths.CacheFile, () => new CacheDatabase());
        Database.Settings ??= new AppSettings();
        Database.Settings.Filter ??= new FilterState();
        Database.Bookmarks ??= new List<BookmarkNode>();
        Database.Categories ??= new List<CategoryNode>();
        Database.Accounts ??= new List<AccountEntry>();
        Cache.Probes ??= new Dictionary<string, ProbeRecord>(StringComparer.OrdinalIgnoreCase);

        EdgeProfilePath = ResolveProfilePath();
        MigrateCategoryKinds();
        MigrateCategoryPrivacy();
        MigrateCategoryIds();
        MigrateBookmarkSource();
    }

    public void Save() => JsonStore.Save(AppPaths.LibraryFile, Database);

    public void SaveCache() => JsonStore.Save(AppPaths.CacheFile, Cache);

    private string? ResolveProfilePath()
    {
        var configured = Database.Settings.EdgeProfilePath;
        if (!string.IsNullOrWhiteSpace(configured) &&
            File.Exists(Path.Combine(configured, "Bookmarks")))
            return configured;

        return _reader.DetectProfilePath();
    }

    /// <summary>从 Edge 拉取书签并合并进本地库</summary>
    public (int added, int updated) SyncEdge()
    {
        EdgeProfilePath = ResolveProfilePath();
        if (string.IsNullOrEmpty(EdgeProfilePath))
        {
            LastSyncMessage = "没有找到 Edge 配置目录，请在设置里手动指定";
            return (0, 0);
        }

        var incoming = _reader.Read(EdgeProfilePath);
        if (incoming.Count == 0)
        {
            LastSyncMessage = "Edge 书签为空，或读取被占用";
            return (0, 0);
        }

        var result = _reader.Merge(incoming, Database);
        Database.LastEdgeSyncAt = DateTime.Now;
        RebuildBrowserCategories();
        Save();

        LastSyncMessage = $"同步完成：新增 {result.added}，更新 {result.updated}，共 {Database.Bookmarks.Count} 条";
        return result;
    }

    /// <summary>
    /// v1 → v2：把原来只有一套的分类拆成「项目分类」和「浏览器分类」。
    /// 名称能和 Edge 文件夹对上的旧分类挪进浏览器分类，其余留在项目分类。
    /// </summary>
    private void MigrateCategoryKinds()
    {
        if (Database.SchemaVersion >= 2) return;

        // 1. 先统计所有书签用到的 Edge 文件夹名
        var edgeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bookmark in Database.Bookmarks)
        {
            var name = FirstFolderName(bookmark.SourceFolder, EdgeRootNames);
            if (!string.IsNullOrEmpty(name)) edgeNames.Add(name);
        }

        // 2. 名字能对上的旧分类翻成浏览器分类（要先翻，重建时才能复用同一批记录）
        var moved = 0;
        foreach (var category in Database.Categories.Where(c => c.Kind == CategoryKind.Project).ToList())
        {
            if (!edgeNames.Contains(category.Name)) continue;
            category.Kind = CategoryKind.Browser;
            category.IsAuto = true;
            moved++;
        }

        // 3. 按 Edge 文件夹结构补全浏览器分类，并给每条书签写上浏览器归属
        RebuildBrowserCategories();

        // 4. 项目分类里已经不存在的归属要清掉（这些书签改由浏览器分类表示）
        var browserIds = new HashSet<string>(
            Database.Categories.Where(c => c.Kind == CategoryKind.Browser).Select(c => c.Id));
        foreach (var bookmark in Database.Bookmarks)
            if (bookmark.CategoryId != null && browserIds.Contains(bookmark.CategoryId))
                bookmark.CategoryId = null;

        Database.SchemaVersion = 2;
        Database.Settings.CategorySource = CategoryKind.Project;
        Save();
        JsonStore.Log($"分类体系升级到 v2：浏览器分类 {browserIds.Count} 个（其中 {moved} 个由旧分类挪过来）");
    }

    /// <summary>
    /// v2 → v3：给项目分类补上「常规 / 隐私」的归属。
    /// 老数据没有这个字段，只能按分类下的书签推断：一个分类下的书签如果全是隐私的，就归隐私侧，
    /// 混着两种或者空分类一律算常规侧（保守，不会把常规分类误判成隐私）。
    /// </summary>
    private void MigrateCategoryPrivacy()
    {
        if (Database.SchemaVersion >= 3) return;

        var privateCounts = new Dictionary<string, int>();
        var normalCounts = new Dictionary<string, int>();
        foreach (var bookmark in Database.Bookmarks)
        {
            if (string.IsNullOrEmpty(bookmark.CategoryId)) continue;
            var bucket = bookmark.IsPrivate ? privateCounts : normalCounts;
            bucket[bookmark.CategoryId] = bucket.TryGetValue(bookmark.CategoryId, out var n) ? n + 1 : 1;
        }

        var moved = 0;
        foreach (var category in Database.Categories.Where(c => c.Kind == CategoryKind.Project))
        {
            if (normalCounts.ContainsKey(category.Id)) continue;      // 有常规书签 → 常规侧
            if (!privateCounts.ContainsKey(category.Id)) continue;    // 空分类 → 常规侧
            category.IsPrivate = true;
            moved++;
        }

        Database.SchemaVersion = 3;
        Save();
        JsonStore.Log($"分类体系升级到 v3：{moved} 个项目分类按书签推断为隐私侧");
    }

    /// <summary>
    /// v3 → v4：一个网址从一个分类放开到多个分类。
    /// 老数据只有单值的 CategoryId，直接包成单元素列表；老字段随后清空，不再写回文件。
    /// </summary>
    private void MigrateCategoryIds()
    {
        if (Database.SchemaVersion >= 4) return;

        var moved = 0;
        foreach (var bookmark in Database.Bookmarks)
        {
            if (!string.IsNullOrEmpty(bookmark.CategoryId) && bookmark.CategoryIds.Count == 0)
            {
                bookmark.CategoryIds.Add(bookmark.CategoryId!);
                moved++;
            }
            bookmark.CategoryId = null;
        }

        Database.SchemaVersion = 4;
        Save();
        JsonStore.Log($"分类体系升级到 v4：{moved} 条书签的归类改为多分类");
    }

    /// <summary>
    /// v4 → v5：书签多了「来源」（Edge 同步 / 手动添加），用来区分入库方式并在筛选面板里过滤。
    /// 升级前的条目只可能来自 Edge 同步，全部标成 Edge；老数据没写这个字段时反序列化本来就落到 Edge，这里显式写一遍。
    /// </summary>
    private void MigrateBookmarkSource()
    {
        if (Database.SchemaVersion >= 5) return;

        foreach (var bookmark in Database.Bookmarks) bookmark.Source = BookmarkSource.Edge;

        Database.SchemaVersion = 5;
        Save();
        JsonStore.Log($"书签来源升级到 v5：{Database.Bookmarks.Count} 条标记为 Edge 同步");
    }

    /// <summary>
    /// 按 Edge 当前的文件夹结构重建浏览器分类，并刷新每条书签的浏览器归属。
    /// 浏览器分类是只读的，每次同步都以 Edge 为准。
    /// </summary>
    private void RebuildBrowserCategories()
    {
        var cache = new Dictionary<string, CategoryNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var bookmark in Database.Bookmarks)
        {
            var name = FirstFolderName(bookmark.SourceFolder, EdgeRootNames);
            if (string.IsNullOrEmpty(name))
            {
                bookmark.BrowserCategoryId = null;
                continue;
            }

            if (!cache.TryGetValue(name, out var category))
            {
                category = Database.Categories.FirstOrDefault(c =>
                    c.Kind == CategoryKind.Browser &&
                    c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

                if (category == null)
                {
                    category = new CategoryNode
                    {
                        Name = name,
                        Kind = CategoryKind.Browser,
                        IsAuto = true,
                        Glyph = "📁",
                        Order = Database.Categories.Count,
                        Color = CategoryPalette[Database.Categories.Count % CategoryPalette.Length]
                    };
                    Database.Categories.Add(category);
                }
                cache[name] = category;
            }

            bookmark.BrowserCategoryId = category.Id;
        }

        // 清掉 Edge 里已经不存在（没有书签引用）的浏览器分类
        var used = new HashSet<string>(Database.Bookmarks
            .Where(b => !string.IsNullOrEmpty(b.BrowserCategoryId))
            .Select(b => b.BrowserCategoryId!));
        Database.Categories.RemoveAll(c => c.Kind == CategoryKind.Browser && !used.Contains(c.Id));
    }

    /// <summary>把 Edge 文件夹结构复制成项目分类，方便在此基础上手工调整</summary>
    private void CopyEdgeFoldersToProjectCategories()
    {
        // 键带上隐私归属：常规侧和隐私侧各建各的同名分类，别把隐私书签塞进常规分类
        var created = new Dictionary<(bool IsPrivate, string Name), CategoryNode>();

        foreach (var bookmark in Database.Bookmarks.Where(b => b.CategoryIds.Count == 0))
        {
            var name = FirstFolderName(bookmark.SourceFolder, EdgeRootNames);
            if (string.IsNullOrEmpty(name)) continue;

            var key = (bookmark.IsPrivate, name.ToLowerInvariant());
            if (!created.TryGetValue(key, out var category))
            {
                category = Database.Categories.FirstOrDefault(c =>
                    c.Kind == CategoryKind.Project &&
                    c.IsPrivate == bookmark.IsPrivate &&
                    c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

                if (category == null)
                {
                    category = new CategoryNode
                    {
                        Name = name,
                        Kind = CategoryKind.Project,
                        IsAuto = true,
                        IsPrivate = bookmark.IsPrivate,
                        Order = Database.Categories.Count,
                        Color = CategoryPalette[Database.Categories.Count % CategoryPalette.Length]
                    };
                    Database.Categories.Add(category);
                }
                created[key] = category;
            }

            bookmark.CategoryIds.Add(category.Id);
        }
    }

    private static string FirstFolderName(string? sourceFolder, string[] roots)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder)) return string.Empty;
        var segments = sourceFolder.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        if (segments.Count == 0) return string.Empty;

        segments.RemoveAll(s => roots.Contains(s));
        return segments.Count == 0 ? string.Empty : segments[0];
    }

    /// <summary>把 Edge 原有文件夹结构复制成项目分类（覆盖式）</summary>
    public int RegroupByEdgeFolder()
    {
        foreach (var bookmark in Database.Bookmarks) bookmark.CategoryIds.Clear();
        CopyEdgeFoldersToProjectCategories();
        Save();
        return Database.Bookmarks.Count(b => b.CategoryIds.Count > 0);
    }

    /// <summary>按站点类型重新归类（覆盖式，用于手动触发的「智能归类」）</summary>
    public int RegroupBySiteType()
    {
        var created = new Dictionary<(bool IsPrivate, string Name), CategoryNode>();
        var changed = 0;

        foreach (var bookmark in Database.Bookmarks)
        {
            var probe = GetProbe(bookmark.Url);
            var typeName = !string.IsNullOrEmpty(probe?.SiteType)
                ? probe!.SiteType
                : new SiteClassifier().Classify(bookmark.Url).Name;
            if (string.IsNullOrEmpty(typeName)) continue;

            // 同 CopyEdgeFoldersToProjectCategories：常规侧和隐私侧各建各的分类
            var key = (bookmark.IsPrivate, typeName.ToLowerInvariant());
            if (!created.TryGetValue(key, out var category))
            {
                category = Database.Categories.FirstOrDefault(c =>
                    c.Kind == CategoryKind.Project &&
                    c.IsPrivate == bookmark.IsPrivate &&
                    c.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
                if (category == null)
                {
                    var info = SiteClassifier.AllTypes.FirstOrDefault(t => t.Name == typeName);
                    category = new CategoryNode
                    {
                        Name = typeName,
                        Kind = CategoryKind.Project,
                        Glyph = info?.Glyph ?? string.Empty,
                        Color = info?.Color ?? CategoryPalette[Database.Categories.Count % CategoryPalette.Length],
                        IsAuto = true,
                        IsPrivate = bookmark.IsPrivate,
                        Order = Database.Categories.Count
                    };
                    Database.Categories.Add(category);
                }
                created[key] = category;
            }

            // 覆盖式归类：清掉原有归属，只留自动算出来的这一个
            if (bookmark.CategoryIds.Count != 1 || bookmark.CategoryIds[0] != category.Id)
            {
                bookmark.CategoryIds.Clear();
                bookmark.CategoryIds.Add(category.Id);
                changed++;
            }
        }

        Save();
        return changed;
    }

    public CategoryNode? FindCategory(string? id) =>
        string.IsNullOrEmpty(id) ? null : Database.Categories.FirstOrDefault(c => c.Id == id);

    public ProbeRecord? GetProbe(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        return Cache.Probes.TryGetValue(url, out var record) ? record : null;
    }

    public void ApplyProbe(ProbeRecord record)
    {
        Cache.Probes[record.Url] = record;
    }

    /// <summary>把某条归类从所有书签上摘掉（多分类下只摘这一个，别的分类不受影响）</summary>
    public void ResetCategory(string categoryId)
    {
        foreach (var b in Database.Bookmarks)
            b.CategoryIds.Remove(categoryId);
    }

    public void DeleteCategory(string categoryId)
    {
        ResetCategory(categoryId);
        Database.Categories.RemoveAll(c => c.Id == categoryId);
        Save();
    }

    public int CountInCategory(string? categoryId) =>
        string.IsNullOrEmpty(categoryId)
            ? Database.Bookmarks.Count
            : Database.Bookmarks.Count(b => b.CategoryIds.Contains(categoryId));
}
