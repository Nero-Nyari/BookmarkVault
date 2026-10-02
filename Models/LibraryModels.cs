using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BookmarkVault.Models;

/// <summary>检测状态</summary>
public enum ProbeState
{
    Unknown = 0,
    Pending,
    Online,
    Redirected,
    Unauthorized,
    Offline,
    Timeout,
    Error
}

/// <summary>安全等级</summary>
public enum SecurityLevel
{
    Unknown = 0,
    Safe = 1,
    Caution = 2,
    Risk = 3
}

/// <summary>分类归属：项目分类（本应用自己维护）/ 浏览器分类（来自 Edge 文件夹，只读）</summary>
public enum CategoryKind
{
    Project = 0,
    Browser = 1
}

/// <summary>这条收藏是怎么进到库里来的（不随 Edge 同步变化）</summary>
public enum BookmarkSource
{
    /// <summary>从 Edge 书签同步进来的</summary>
    Edge = 0,
    /// <summary>在本应用里手动添加的</summary>
    Manual = 1
}

/// <summary>黑名单条目来自哪个数据源</summary>
public enum BlacklistSource
{
    /// <summary>URLhaus 恶意网址库订阅</summary>
    Urlhaus = 0,
    /// <summary>OpenPhish 钓鱼网址库订阅</summary>
    OpenPhish = 1,
    /// <summary>自己手工添加的</summary>
    Manual = 2
}

/// <summary>详情页 Hero 图的显示方式</summary>
public enum HeroDisplayMode
{
    /// <summary>原样式：固定 240px 高，图片裁剪铺满</summary>
    Cropped = 0,
    /// <summary>完整显示：Hero 区按图片原始比例加高，图片可以延伸到下方卡片背后</summary>
    FullHeight = 1
}

/// <summary>详情页卡片的材质</summary>
public enum CardMaterial
{
    /// <summary>原样式：不透明深色</summary>
    Solid = 0,
    /// <summary>半透明：透出背后的 Hero 图</summary>
    Translucent = 1,
    /// <summary>毛玻璃：Hero 图整体模糊 + 半透明面板</summary>
    Frosted = 2
}

/// <summary>未选中网址时，主内容区默认展示什么</summary>
public enum HomeViewMode
{
    /// <summary>简洁卡片墙：图标 + 标题 + 状态点 + 域名</summary>
    CardWall = 0,
    /// <summary>概览页：统计 Hero + 最近打开</summary>
    Overview = 1,
    /// <summary>Steam 封面墙：2:3 竖版封面大图</summary>
    CoverWall = 2,
    /// <summary>归类看板：把侧栏的收藏拖到分类文件夹上完成归类</summary>
    Organize = 3
}

/// <summary>一条收藏（来自 Edge，之后归本应用管理）</summary>
public sealed class BookmarkNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    /// <summary>Edge 中的节点 GUID，用于增量同步去重</summary>
    public string SourceId { get; set; } = string.Empty;
    /// <summary>Edge 中的原始文件夹路径，例如「收藏夹栏 / 开发」；手动添加的固定写「手动添加」</summary>
    public string SourceFolder { get; set; } = string.Empty;
    /// <summary>
    /// 这条收藏从哪来：Edge 同步 / 手动添加。
    /// 这是「入库方式」的标签，即使同 URL 后来被 Edge 同步认领（补上 SourceId），也不会改回 Edge。
    /// </summary>
    public BookmarkSource Source { get; set; } = BookmarkSource.Edge;
    /// <summary>项目分类（本应用自己整理的分类），一个网址可以同时归入多个</summary>
    public List<string> CategoryIds { get; set; } = new();
    /// <summary>
    /// v3 及更早版本的老字段：那时候一个网址只有一个分类。
    /// 只在读老数据时用，v4 迁移把它包进 CategoryIds 后清空，之后不会再写回文件。
    /// </summary>
    public string? CategoryId { get; set; }
    /// <summary>浏览器分类（Edge 文件夹对应的分类），同步时自动维护，只读</summary>
    public string? BrowserCategoryId { get; set; }
    /// <summary>已移入隐私：常规模式下完全隐身，只在隐私模式里显示</summary>
    public bool IsPrivate { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? Note { get; set; }
    /// <summary>手动导入的封面图（相对数据目录，例如 covers/xxx.png）</summary>
    public string? CoverPath { get; set; }
    /// <summary>卡片墙专用的 2:3 封面（相对数据目录）；没设置时卡片沿用 CoverPath</summary>
    public string? CardCoverPath { get; set; }
    /// <summary>详情页封面是自动抓来的（手动导入过就置回 false，重抓时不会覆盖手动的）</summary>
    public bool CoverAutoFetched { get; set; }
    /// <summary>卡片封面是自动抓来的</summary>
    public bool CardCoverAutoFetched { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsHidden { get; set; }
    public int VisitCount { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? LastOpenedAt { get; set; }
    public DateTime? EdgeDateAdded { get; set; }

    public string Domain => UrlHelper.GetDomain(Url);
}

/// <summary>分类节点</summary>
public sealed class CategoryNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#66C0F4";
    /// <summary>分类图标（emoji 或单个字符）</summary>
    public string Glyph { get; set; } = string.Empty;
    public int Order { get; set; }
    /// <summary>是否为自动分类生成的（可被清理）</summary>
    public bool IsAuto { get; set; }
    /// <summary>归属哪一套分类</summary>
    public CategoryKind Kind { get; set; }

    /// <summary>
    /// 项目分类归哪一侧：true = 隐私侧，false = 常规侧（创建时按当时的隐私模式定，之后不变）。
    /// 浏览器分类没有这个概念，两边都能看到，所以只有 Kind == Project 时才有意义。
    /// </summary>
    public bool IsPrivate { get; set; }

    /// <summary>浏览器分类只读，不能重命名 / 删除</summary>
    [JsonIgnore]
    public bool IsEditable => Kind == CategoryKind.Project;
}

/// <summary>账号条目（密码经 DPAPI 加密）</summary>
public sealed class AccountEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Domain { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    /// <summary>DPAPI 加密后的 Base64 密码，可能为空</summary>
    public string? PasswordCipher { get; set; }
    public string? Note { get; set; }
    /// <summary>打开该站点时是否自动提示账号</summary>
    public bool AutoPrompt { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>一条黑名单规则</summary>
public sealed class BlacklistRule
{
    /// <summary>匹配表达式：纯域名（含子域）、完整网址或通配 *.example.com，统一小写保存</summary>
    public string Pattern { get; set; } = string.Empty;
    /// <summary>订阅源里的原始行（手工条目等于 Pattern）</summary>
    public string? Raw { get; set; }
    public BlacklistSource Source { get; set; }
    public string? Note { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
}

/// <summary>本地黑名单库（独立文件，订阅条目可能上万，不塞进 library.json）</summary>
public sealed class BlacklistDatabase
{
    public int SchemaVersion { get; set; } = 1;
    /// <summary>最近一次更新订阅的时间</summary>
    public DateTime? UpdatedAt { get; set; }
    public List<BlacklistRule> Rules { get; set; } = new();
    /// <summary>被用户删掉的订阅条目，下次更新订阅时不再拉回来</summary>
    public List<string> Ignored { get; set; } = new();
}

/// <summary>单个地址的检测结果</summary>
public sealed class ProbeRecord
{
    public string Url { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public ProbeState State { get; set; }
    public int? StatusCode { get; set; }
    public int ElapsedMs { get; set; }
    public string? FinalUrl { get; set; }
    public string? PageTitle { get; set; }
    public string? ServerHeader { get; set; }
    public bool IsHttps { get; set; }
    public bool CertificateValid { get; set; }
    /// <summary>站点类型（中文名）</summary>
    public string SiteType { get; set; } = string.Empty;
    public SecurityLevel Security { get; set; }
    public List<string> SecurityFlags { get; set; } = new();
    public DateTime CheckedAt { get; set; }

    public bool IsFresh(int cacheHours) => DateTime.Now - CheckedAt < TimeSpan.FromHours(cacheHours);
}

/// <summary>侧栏筛选条件（各组里存的都是选项 Key，界面上勾选后写回这里）</summary>
public sealed class FilterState
{
    /// <summary>分类 Id；空字符串代表「未分类」</summary>
    public List<string> CategoryIds { get; set; } = new();
    /// <summary>可访问状态文案，例如「可访问」「打不开」「未检测」</summary>
    public List<string> States { get; set; } = new();
    /// <summary>安全等级文案，例如「安全」「有风险」「未评估」</summary>
    public List<string> Securities { get; set; } = new();
    /// <summary>站点类型文案，例如「开发技术」「未识别」</summary>
    public List<string> SiteTypes { get; set; } = new();
    /// <summary>域名后缀，例如「.com」「.cn」；取不到后缀的归到「其他」</summary>
    public List<string> Tlds { get; set; } = new();
    /// <summary>封面状态，例如「卡片封面」「仅详情页封面」「无封面」</summary>
    public List<string> Covers { get; set; } = new();
    /// <summary>来源，例如「Edge 同步」「手动添加」</summary>
    public List<string> Sources { get; set; } = new();

    [JsonIgnore]
    public bool IsEmpty => CategoryIds.Count == 0 && States.Count == 0 && Securities.Count == 0
                           && SiteTypes.Count == 0 && Tlds.Count == 0 && Covers.Count == 0
                           && Sources.Count == 0;

    [JsonIgnore]
    public int Count => CategoryIds.Count + States.Count + Securities.Count + SiteTypes.Count + Tlds.Count
                        + Covers.Count + Sources.Count;
}

/// <summary>应用设置</summary>
public sealed class AppSettings
{
    /// <summary>Edge 配置目录（留空则自动探测）</summary>
    public string? EdgeProfilePath { get; set; }
    public int ProbeTimeoutSeconds { get; set; } = 8;
    public int ProbeConcurrency { get; set; } = 8;
    public int CacheHours { get; set; } = 24;
    public bool AutoProbeOnStartup { get; set; }
    public bool PromptAccountOnOpen { get; set; } = true;
    /// <summary>「打开网站」用哪个浏览器：exe 完整路径；留空表示跟随系统默认浏览器</summary>
    public string? BrowserPath { get; set; }
    /// <summary>上面那个浏览器的显示名，只用于设置页回显（手动指定的 exe 也有个名字）</summary>
    public string? BrowserName { get; set; }
    /// <summary>当前生效的分类体系（默认项目分类）</summary>
    public CategoryKind CategorySource { get; set; } = CategoryKind.Project;
    /// <summary>详情页 Hero 图显示方式（默认原样式）</summary>
    public HeroDisplayMode HeroDisplay { get; set; } = HeroDisplayMode.Cropped;
    /// <summary>详情页卡片材质（默认原样式）</summary>
    public CardMaterial CardMaterial { get; set; } = CardMaterial.Solid;
    /// <summary>侧栏筛选条件（下次启动继续生效）</summary>
    public FilterState Filter { get; set; } = new();
    /// <summary>侧栏列表排序方式（下次启动继续生效）</summary>
    public string SortMode { get; set; } = "默认排序";
    /// <summary>未选中网址时主区显示什么（默认 Steam 封面墙）</summary>
    public HomeViewMode HomeView { get; set; } = HomeViewMode.CoverWall;
    /// <summary>启动时自动更新外部恶意网址库订阅（URLhaus / OpenPhish）</summary>
    public bool AutoUpdateBlacklist { get; set; }
    /// <summary>
    /// 自定义快捷键：动作 id → 按键文本（如 "Ctrl+F"）。
    /// 只存用户改过的那些；值写成空串表示「这个动作不绑按键」，键名在 ShortcutIds 里。
    /// </summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>本地主数据库</summary>
public sealed class LibraryDatabase
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime? LastEdgeSyncAt { get; set; }
    public List<BookmarkNode> Bookmarks { get; set; } = new();
    public List<CategoryNode> Categories { get; set; } = new();
    public List<AccountEntry> Accounts { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}

/// <summary>检测缓存（可随时删除重建）</summary>
public sealed class CacheDatabase
{
    public Dictionary<string, ProbeRecord> Probes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>URL 辅助方法</summary>
public static class UrlHelper
{
    public static string GetDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;
        return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
    }

    public static string GetRootDomain(string? url)
    {
        var host = GetDomain(url);
        if (string.IsNullOrEmpty(host)) return string.Empty;
        if (Uri.CheckHostName(host) != UriHostNameType.Dns) return host;
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        // 处理 co.uk / com.cn 这类二级后缀
        var twoLevel = new[] { "co", "com", "net", "org", "gov", "edu", "ac" };
        if (parts.Length >= 3 && Array.Exists(twoLevel, p => p == parts[^2]))
            return string.Join('.', parts[^3..]);
        return string.Join('.', parts[^2..]);
    }

    /// <summary>取域名后缀（最后一段，例如 .com / .cn / .cc）。IP、内网主机等取不到的归到「其他」</summary>
    public static string GetTld(string? url)
    {
        var host = GetDomain(url);
        if (string.IsNullOrEmpty(host)) return "其他";
        if (Uri.CheckHostName(host) != UriHostNameType.Dns) return "其他";
        var index = host.LastIndexOf('.');
        return index <= 0 || index == host.Length - 1 ? "其他" : host[index..].ToLowerInvariant();
    }
}
