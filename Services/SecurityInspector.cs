using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkVault.Models;

namespace BookmarkVault.Services;

/// <summary>
/// 外部安全数据源接口。当前只实现了本地规则，
/// 后续接入 Google Safe Browsing / VirusTotal 时实现此接口并注册即可。
/// </summary>
public interface ISecurityProvider
{
    string Name { get; }
    Task<IReadOnlyList<string>> CheckAsync(string url, CancellationToken ct);
}

/// <summary>安全检测结论</summary>
public sealed record SecurityVerdict(SecurityLevel Level, List<string> Flags);

/// <summary>本地安全规则 + 本地黑名单 + 可插拔的外部数据源</summary>
public sealed class SecurityInspector
{
    private readonly List<ISecurityProvider> _providers = new();
    private readonly BlacklistService? _blacklist;

    public SecurityInspector(BlacklistService? blacklist = null) => _blacklist = blacklist;

    /// <summary>常见易被滥用的免费顶级域</summary>
    private static readonly string[] RiskyTlds = { ".tk", ".ml", ".ga", ".cf", ".gq", ".top", ".buzz", ".loan" };

    /// <summary>短链服务：只说明目标不透明，不算危险</summary>
    private static readonly string[] Shorteners =
    {
        "bit.ly", "tinyurl.com", "t.co", "goo.gl", "is.gd", "buff.ly", "ow.ly",
        "t.cn", "dwz.cn", "url.cn", "suo.im", "sourl.cn", "mrw.so"
    };

    /// <summary>高可信白名单（命中即直接判定安全，不再叠加其它提示）</summary>
    private static readonly string[] TrustedSuffixes =
    {
        "github.com", "gitlab.com", "gitee.com", "microsoft.com", "apple.com", "google.com",
        "mozilla.org", "wikipedia.org", "baidu.com", "qq.com", "taobao.com", "tmall.com",
        "jd.com", "bilibili.com", "zhihu.com", "weibo.com", "aliyun.com", "tencent.com",
        "douban.com", "163.com", "sina.com.cn", "sohu.com", "gov.cn", "edu.cn", "openai.com",
        "anthropic.com", "cloudflare.com", "amazon.com", "adobe.com", "jetbrains.com",
        "stackoverflow.com", "python.org", "nodejs.org", "docker.com", "notion.so", "feishu.cn"
    };

    /// <summary>疑似邀请/钓鱼的词，仅当出现在域名中且不在白名单时提示</summary>
    private static readonly string[] PhishingWords = { "login", "verify", "secure-", "account-update", "signin", "wallet" };

    public IReadOnlyList<ISecurityProvider> Providers => _providers;

    public void Register(ISecurityProvider provider) => _providers.Add(provider);

    public async Task<SecurityVerdict> InspectAsync(
        string url,
        bool isHttps,
        bool certificateValid,
        string? finalUrl,
        string? pageTitle,
        CancellationToken ct)
    {
        var flags = new List<string>();
        var level = SecurityLevel.Safe;
        var domain = UrlHelper.GetDomain(url).ToLowerInvariant();
        var full = url.ToLowerInvariant();

        if (string.IsNullOrEmpty(domain))
            return new SecurityVerdict(SecurityLevel.Unknown, new List<string> { "地址格式无法识别" });

        var isTrusted = TrustedSuffixes.Any(s => domain == s || domain.EndsWith("." + s, StringComparison.Ordinal));

        if (isTrusted)
        {
            flags.Add("命中常用可信站点");
        }
        else
        {
            if (!isHttps)
            {
                flags.Add("未使用 HTTPS 加密，数据传输可能被窃听");
                level = SecurityLevel.Caution;
            }
            else if (!certificateValid)
            {
                flags.Add("HTTPS 证书无效或不受信任");
                level = SecurityLevel.Risk;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.Host) == false)
            {
                // IP 直连
                if (Uri.CheckHostName(uri.Host) == UriHostNameType.IPv4 ||
                    Uri.CheckHostName(uri.Host) == UriHostNameType.IPv6)
                {
                    flags.Add("直接使用 IP 地址访问，无法核实站点身份");
                    level = Max(level, SecurityLevel.Caution);
                }
                // 非标准端口
                if (!uri.IsDefaultPort && uri.Port != 80 && uri.Port != 443)
                {
                    flags.Add($"使用非标准端口 {uri.Port}");
                    level = Max(level, SecurityLevel.Caution);
                }
            }

            if (RiskyTlds.Any(t => domain.EndsWith(t, StringComparison.Ordinal)))
            {
                flags.Add("使用被滥用较多的免费域名后缀");
                level = Max(level, SecurityLevel.Caution);
            }

            if (domain.StartsWith("xn--", StringComparison.Ordinal) || domain.Contains(".xn--", StringComparison.Ordinal))
            {
                flags.Add("域名包含 Punycode 编码，注意是否为仿冒站点");
                level = Max(level, SecurityLevel.Caution);
            }

            if (PhishingWords.Any(w => domain.Contains(w, StringComparison.Ordinal)))
            {
                flags.Add("域名含登录/验证类敏感词，请确认是否为官方地址");
                level = Max(level, SecurityLevel.Caution);
            }

            if (Shorteners.Any(s => domain == s || domain.EndsWith("." + s, StringComparison.Ordinal)))
            {
                flags.Add("短链接服务，跳转目标不透明");
                level = Max(level, SecurityLevel.Caution);
            }

            if (!string.IsNullOrEmpty(finalUrl) &&
                UrlHelper.GetRootDomain(finalUrl) != UrlHelper.GetRootDomain(url) &&
                !string.IsNullOrEmpty(UrlHelper.GetRootDomain(finalUrl)))
            {
                flags.Add($"实际跳转到 {UrlHelper.GetRootDomain(finalUrl)}");
            }
        }

        // 本地黑名单（URLhaus / OpenPhish / 自定义），命中即判为风险，可信站点也要查
        if (_blacklist != null)
        {
            var hits = _blacklist.Match(url);
            const int maxShown = 3;
            for (var i = 0; i < hits.Count && i < maxShown; i++)
            {
                var hit = hits[i];
                var name = BlacklistService.SourceName(hit.Source);
                var detail = string.IsNullOrWhiteSpace(hit.Note) ? hit.Pattern : $"{hit.Pattern}（{hit.Note}）";
                flags.Add($"[{name}] 命中已知恶意网址：{detail}");
                level = Max(level, SecurityLevel.Risk);
            }
            if (hits.Count > maxShown)
            {
                flags.Add($"另有 {hits.Count - maxShown} 条黑名单命中未列出");
                level = Max(level, SecurityLevel.Risk);
            }
        }

        // 外部数据源（当前为空，接入后自动生效）
        foreach (var provider in _providers)
        {
            try
            {
                var extra = await provider.CheckAsync(url, ct).ConfigureAwait(false);
                if (extra.Count == 0) continue;
                flags.AddRange(extra.Select(f => $"[{provider.Name}] {f}"));
                level = Max(level, SecurityLevel.Risk);
            }
            catch (Exception ex)
            {
                JsonStore.Log($"安全数据源 {provider.Name} 调用失败：{ex.Message}");
            }
        }

        if (flags.Count == 0) flags.Add("未发现明显风险");
        return new SecurityVerdict(level, flags);
    }

    private static SecurityLevel Max(SecurityLevel a, SecurityLevel b) => a > b ? a : b;
}
