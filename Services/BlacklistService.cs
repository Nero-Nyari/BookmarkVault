using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BookmarkVault.Models;

namespace BookmarkVault.Services;

/// <summary>订阅更新的结果</summary>
public sealed record FeedUpdateResult(int UrlhausCount, int OpenPhishCount, string? Error)
{
    public int TotalCount => UrlhausCount + OpenPhishCount;
}

/// <summary>
/// 外部恶意网址库（黑名单）。数据独立存在 security/blacklist.json，不塞进 library.json。
/// 订阅源：URLhaus 纯文本 + OpenPhish 纯文本，都是每行一个网址，无需 API Key，域名不出本机。
/// 支持三种匹配：纯域名（含子域）、完整网址（路径前缀）、通配 *.example.com。
/// </summary>
public sealed class BlacklistService
{
    private static readonly Dictionary<BlacklistSource, string> FeedUrls = new()
    {
        [BlacklistSource.Urlhaus] = "https://urlhaus.abuse.ch/downloads/text/",
        [BlacklistSource.OpenPhish] = "https://openphish.com/feed.txt"
    };

    private static readonly HttpClient Http = CreateClient();

    private BlacklistDatabase _database = new();
    private volatile Index _index = new();

    /// <summary>条目有增删或订阅更新后触发（界面刷新用）</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<BlacklistRule> Rules => _database.Rules;
    public IReadOnlyList<string> Ignored => _database.Ignored;
    public DateTime? LastUpdatedAt => _database.UpdatedAt;

    public int Count => _database.Rules.Count;
    public int SubscriptionCount => _database.Rules.Count(r => r.Source != BlacklistSource.Manual);
    public int ManualCount => _database.Rules.Count(r => r.Source == BlacklistSource.Manual);
    public int IgnoredCount => _database.Ignored.Count;

    public static string SourceName(BlacklistSource source) => source switch
    {
        BlacklistSource.Urlhaus => "URLhaus",
        BlacklistSource.OpenPhish => "OpenPhish",
        _ => "自定义黑名单"
    };

    // ---------- 读写 ----------

    public void Load()
    {
        _database = JsonStore.Load(AppPaths.BlacklistFile, () => new BlacklistDatabase());
        RebuildIndex();
    }

    public void Save() => JsonStore.Save(AppPaths.BlacklistFile, _database);

    // ---------- 匹配 ----------

    /// <summary>查一个地址命中了哪些黑名单规则（可能命中多条）</summary>
    public IReadOnlyList<BlacklistRule> Match(string? url)
    {
        var results = new List<BlacklistRule>();
        if (string.IsNullOrWhiteSpace(url)) return results;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return results;

        var host = uri.Host.ToLowerInvariant();
        if (string.IsNullOrEmpty(host)) return results;

        var pathAndQuery = uri.PathAndQuery.ToLowerInvariant();
        if (pathAndQuery.Length > 1 && pathAndQuery.EndsWith('/'))
            pathAndQuery = pathAndQuery[..^1];

        var index = _index;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in CandidateHosts(host))
        {
            if (index.DomainRules.TryGetValue(candidate, out var domainRules))
            {
                foreach (var rule in domainRules)
                    if (seen.Add(rule.Pattern)) results.Add(rule);
            }

            if (index.PathRules.TryGetValue(candidate, out var pathRules))
            {
                foreach (var (prefix, rule) in pathRules)
                    if (PathMatches(pathAndQuery, prefix) && seen.Add(rule.Pattern))
                        results.Add(rule);
            }
        }

        foreach (var (suffix, rule) in index.Wildcards)
        {
            if (host.EndsWith(suffix, StringComparison.Ordinal) && seen.Add(rule.Pattern))
                results.Add(rule);
        }

        return results;
    }

    /// <summary>从 host 逐级往上找各级父域名，用于让 example.com 也能命中 a.example.com</summary>
    private static IEnumerable<string> CandidateHosts(string host)
    {
        if (Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            yield return host;
            yield break;
        }

        var current = host;
        while (!string.IsNullOrEmpty(current) && current.Contains('.'))
        {
            yield return current;
            current = current[(current.IndexOf('.') + 1)..];
        }
    }

    /// <summary>路径前缀匹配，要求后面是结尾或分隔符，避免 /bad 命中 /bad2</summary>
    private static bool PathMatches(string pathAndQuery, string prefix)
    {
        if (!pathAndQuery.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (pathAndQuery.Length == prefix.Length) return true;
        var next = pathAndQuery[prefix.Length];
        return next == '/' || next == '?' || next == '&' || next == '#';
    }

    // ---------- 增删改查 ----------

    /// <summary>手工添加一条规则；表达式不合法时返回 null</summary>
    public BlacklistRule? AddManual(string input, string? note)
    {
        if (!TryNormalize(input, out var pattern)) return null;

        var existing = _database.Rules.FirstOrDefault(r => string.Equals(r.Pattern, pattern, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Note = string.IsNullOrWhiteSpace(note) ? existing.Note : note.Trim();
            Save();
            RebuildIndex();
            RaiseChanged();
            return existing;
        }

        var rule = new BlacklistRule
        {
            Pattern = pattern,
            Raw = pattern,
            Source = BlacklistSource.Manual,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            AddedAt = DateTime.Now
        };
        _database.Rules.Add(rule);
        // 之前删过的同名订阅条目要解除忽略，否则下次更新又会消失
        _database.Ignored.RemoveAll(p => string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase));

        Save();
        RebuildIndex();
        RaiseChanged();
        return rule;
    }

    /// <summary>修改一条规则；表达式不合法时返回 false</summary>
    public bool UpdateRule(BlacklistRule rule, string input, string? note)
    {
        if (rule == null) return false;
        if (!TryNormalize(input, out var pattern)) return false;

        rule.Pattern = pattern;
        if (rule.Source == BlacklistSource.Manual) rule.Raw = pattern;
        rule.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        Save();
        RebuildIndex();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 删除一条规则。订阅条目删掉后会进「忽略名单」，下次更新订阅不会再拉回来。
    /// </summary>
    public void RemoveRule(BlacklistRule rule)
    {
        if (rule == null) return;
        _database.Rules.Remove(rule);

        if (rule.Source != BlacklistSource.Manual &&
            !_database.Ignored.Any(p => string.Equals(p, rule.Pattern, StringComparison.OrdinalIgnoreCase)))
        {
            _database.Ignored.Add(rule.Pattern);
        }

        Save();
        RebuildIndex();
        RaiseChanged();
    }

    /// <summary>把一条被忽略的订阅条目放回来（仅解除忽略标记，等下次更新订阅重新拉取）</summary>
    public void Unignore(string pattern)
    {
        if (_database.Ignored.RemoveAll(p => string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Save();
            RaiseChanged();
        }
    }

    /// <summary>清空整个黑名单（含忽略名单），下次更新订阅可完整重建</summary>
    public void ClearAll()
    {
        _database.Rules.Clear();
        _database.Ignored.Clear();
        Save();
        RebuildIndex();
        RaiseChanged();
    }

    // ---------- 订阅更新 ----------

    /// <summary>
    /// 拉取两个订阅源并整体替换订阅条目。手工条目与忽略名单保留。
    /// 两个源都失败时不改动现有数据。
    /// </summary>
    public async Task<FeedUpdateResult> RefreshFeedsAsync(CancellationToken ct = default)
    {
        var tasks = FeedUrls.Select(kv => FetchAsync(kv.Value, kv.Key, ct)).ToArray();
        var fetched = await Task.WhenAll(tasks).ConfigureAwait(false);

        var errors = fetched.Where(f => f.Error != null).Select(f => f.Error!).ToList();
        if (fetched.All(f => f.Error != null))
            return new FeedUpdateResult(0, 0, string.Join("；", errors));

        var ignored = new HashSet<string>(_database.Ignored, StringComparer.OrdinalIgnoreCase);
        var manualPatterns = new HashSet<string>(
            _database.Rules.Where(r => r.Source == BlacklistSource.Manual).Select(r => r.Pattern),
            StringComparer.OrdinalIgnoreCase);

        var manualRules = _database.Rules.Where(r => r.Source == BlacklistSource.Manual).ToList();
        var fresh = new List<BlacklistRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int urlhaus = 0, openPhish = 0;

        foreach (var feed in fetched)
        {
            if (feed.Error != null) continue;
            foreach (var line in feed.Lines)
            {
                if (ignored.Contains(line) || manualPatterns.Contains(line) || !seen.Add(line)) continue;
                fresh.Add(new BlacklistRule
                {
                    Pattern = line,
                    Raw = line,
                    Source = feed.Source,
                    AddedAt = DateTime.Now
                });
                if (feed.Source == BlacklistSource.Urlhaus) urlhaus++;
                else openPhish++;
            }
        }

        _database.Rules = manualRules.Concat(fresh).ToList();
        _database.UpdatedAt = DateTime.Now;

        Save();
        RebuildIndex();
        RaiseChanged();

        JsonStore.Log($"黑名单订阅更新完成：URLhaus {urlhaus} 条，OpenPhish {openPhish} 条"
                      + (errors.Count > 0 ? $"，部分失败：{string.Join("；", errors)}" : ""));
        return new FeedUpdateResult(urlhaus, openPhish, errors.Count > 0 ? string.Join("；", errors) : null);
    }

    private static async Task<FeedFetch> FetchAsync(string url, BlacklistSource source, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var lines = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (TryNormalize(line, out var pattern)) lines.Add(pattern);
            }
            return new FeedFetch(source, lines, null);
        }
        catch (Exception ex)
        {
            JsonStore.Log($"拉取黑名单订阅失败（{SourceName(source)}）：{ex.Message}");
            return new FeedFetch(source, new List<string>(), $"{SourceName(source)}：{ex.Message}");
        }
    }

    private sealed record FeedFetch(BlacklistSource Source, List<string> Lines, string? Error);

    // ---------- 表达式规范化 / 索引 ----------

    /// <summary>
    /// 把手输的表达式规范成统一小写形式。
    /// 支持：example.com、https://example.com/bad、*.example.com、example.com/bad
    /// </summary>
    public static bool TryNormalize(string? input, out string pattern)
    {
        pattern = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var value = input.Trim().ToLowerInvariant();
        if (value.Length > 2048) return false;
        if (value.Contains(' ')) return false;

        if (value.StartsWith("*."))
        {
            var rest = value[2..];
            if (!IsHostLike(rest)) return false;
            pattern = "*." + rest;
            return true;
        }

        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0) value = value[(schemeIndex + 3)..];

        var slash = value.IndexOf('/');
        var host = slash < 0 ? value : value[..slash];
        var path = slash < 0 ? string.Empty : value[slash..];

        if (!IsHostLike(host)) return false;
        pattern = host + path;
        return true;
    }

    private static bool IsHostLike(string host)
    {
        if (host.Length == 0 || host.Length > 253) return false;
        if (host.Contains("..")) return false;
        if (host.StartsWith('.') || host.EndsWith('.')) return false;
        return Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }

    private void RebuildIndex()
    {
        var index = new Index();

        foreach (var rule in _database.Rules)
        {
            var pattern = rule.Pattern;
            if (pattern.StartsWith("*."))
            {
                index.Wildcards.Add(("." + pattern[2..], rule));
                continue;
            }

            var slash = pattern.IndexOf('/');
            var host = slash < 0 ? pattern : pattern[..slash];
            var path = slash < 0 ? string.Empty : pattern[slash..];

            if (path.Length == 0 || path == "/")
            {
                if (!index.DomainRules.TryGetValue(host, out var list))
                    index.DomainRules[host] = list = new List<BlacklistRule>();
                list.Add(rule);
            }
            else
            {
                if (path.Length > 1 && path.EndsWith('/')) path = path[..^1];
                if (!index.PathRules.TryGetValue(host, out var list))
                    index.PathRules[host] = list = new List<(string, BlacklistRule)>();
                list.Add((path, rule));
            }
        }

        _index = index;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BookmarkVault/1.0");
        return client;
    }

    private sealed class Index
    {
        public Dictionary<string, List<BlacklistRule>> DomainRules { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<(string Prefix, BlacklistRule Rule)>> PathRules { get; } = new(StringComparer.Ordinal);
        public List<(string Suffix, BlacklistRule Rule)> Wildcards { get; } = new();
    }
}