using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BookmarkVault.Models;

namespace BookmarkVault.Services;

/// <summary>批量检测进度</summary>
public sealed record ProbeProgress(int Done, int Total, string CurrentUrl);

/// <summary>
/// 可访问性 + 页面信息探测。
/// 结果由调用方写入缓存，这里只负责发请求和判定。
/// </summary>
public sealed class ProbeService
{
    private readonly HttpClient _client;
    private readonly SiteClassifier _classifier;
    private readonly SecurityInspector _security;

    private static readonly Regex TitleRegex = new(
        @"<title[^>]*>(?<t>.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex MetaRegex = new(
        @"<meta[^>]+(?:name|property)\s*=\s*[""'](?<k>[^""']+)[""'][^>]+content\s*=\s*[""'](?<v>[^""']*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0";

    public ProbeService(SiteClassifier classifier, SecurityInspector security)
    {
        _classifier = classifier;
        _security = security;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 6,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        _client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan // 逐请求用 CancellationToken 控制
        };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language",
            "zh-CN,zh;q=0.9,en;q=0.8");
    }

    /// <summary>探测单个地址</summary>
    public async Task<ProbeRecord> ProbeAsync(string url, int timeoutSeconds, CancellationToken ct)
    {
        var record = new ProbeRecord
        {
            Url = url,
            Domain = UrlHelper.GetDomain(url),
            CheckedAt = DateTime.Now,
            IsHttps = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
            CertificateValid = true
        };

        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, timeoutSeconds)));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            record.StatusCode = (int)response.StatusCode;
            record.FinalUrl = response.RequestMessage?.RequestUri?.ToString();
            record.ServerHeader = response.Headers.TryGetValues("Server", out var servers)
                ? string.Join(",", servers)
                : null;

            // 读取正文用于提取标题和关键词，限制 128KB 防止大文件
            string? body = null;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(mediaType))
            {
                body = await ReadLimitedAsync(response, 128 * 1024, timeoutCts.Token).ConfigureAwait(false);
            }

            record.PageTitle = ExtractTitle(body);
            var hints = ExtractMeta(body);

            var code = (int)response.StatusCode;
            var redirected = !string.IsNullOrEmpty(record.FinalUrl) &&
                             !string.Equals(record.FinalUrl, url, StringComparison.OrdinalIgnoreCase);

            if (code is >= 200 and < 300)
                record.State = redirected ? ProbeState.Redirected : ProbeState.Online;
            else if (code is 401 or 403 or 407)
                record.State = ProbeState.Unauthorized;
            else if (redirected && code < 400)
                record.State = ProbeState.Redirected;
            else
                record.State = ProbeState.Offline;

            record.ElapsedMs = (int)sw.ElapsedMilliseconds;
            record.SiteType = _classifier.Classify(url, record.PageTitle, hints).Name;

            var verdict = await _security.InspectAsync(
                url, record.IsHttps, record.CertificateValid,
                record.FinalUrl, record.PageTitle, ct).ConfigureAwait(false);
            record.Security = verdict.Level;
            record.SecurityFlags = verdict.Flags;

            return record;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            record.State = ProbeState.Timeout;
        }
        catch (OperationCanceledException)
        {
            record.State = ProbeState.Error;
        }
        catch (HttpRequestException ex)
        {
            // 区分证书问题与普通连接失败
            if (ContainsAuthError(ex))
            {
                record.State = ProbeState.Error;
                record.CertificateValid = false;
            }
            else
            {
                record.State = ProbeState.Error;
            }
        }
        catch (Exception)
        {
            record.State = ProbeState.Error;
        }

        record.ElapsedMs = (int)sw.ElapsedMilliseconds;
        record.FinalUrl ??= url;

        var fallback = await _security.InspectAsync(
            url, record.IsHttps, record.CertificateValid, record.FinalUrl, null, ct).ConfigureAwait(false);
        record.Security = fallback.Level;
        record.SecurityFlags = fallback.Flags;
        record.SiteType = _classifier.Classify(url).Name;
        return record;
    }

    /// <summary>批量探测，带并发限制与进度回调</summary>
    public async Task<IReadOnlyList<ProbeRecord>> ProbeManyAsync(
        IEnumerable<string> urls,
        int timeoutSeconds,
        int concurrency,
        IProgress<ProbeProgress>? progress,
        CancellationToken ct)
    {
        var list = urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = new ConcurrentBag<ProbeRecord>();
        var done = 0;
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 32));

        var tasks = list.Select(async url =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var record = await ProbeAsync(url, timeoutSeconds, ct).ConfigureAwait(false);
                results.Add(record);
                var current = Interlocked.Increment(ref done);
                progress?.Report(new ProbeProgress(current, list.Count, url));
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，返回已完成的部分
        }

        return results.ToList();
    }

    private static bool ContainsAuthError(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is AuthenticationException) return true;
        return false;
    }

    private static async Task<string?> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[Math.Min(limit, 16384)];
            using var ms = new System.IO.MemoryStream();
            int total = 0, read;
            while (total < limit &&
                   (read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - total)), ct)
                       .ConfigureAwait(false)) > 0)
            {
                ms.Write(buffer, 0, read);
                total += read;
            }
            var charset = response.Content.Headers.ContentType?.CharSet;
            var encoding = System.Text.Encoding.UTF8;
            if (!string.IsNullOrEmpty(charset))
            {
                try { encoding = System.Text.Encoding.GetEncoding(charset.Trim('"', '\'')); }
                catch { /* 未知编码，退回 UTF-8 */ }
            }
            return encoding.GetString(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractTitle(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var m = TitleRegex.Match(html);
        if (!m.Success) return null;
        var title = WebUtility.HtmlDecode(m.Groups["t"].Value).Trim();
        title = Regex.Replace(title, @"\s+", " ");
        return string.IsNullOrEmpty(title) ? null : (title.Length > 160 ? title[..160] : title);
    }

    private static string? ExtractMeta(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var parts = new List<string>();
        foreach (Match m in MetaRegex.Matches(html))
        {
            var key = m.Groups["k"].Value.ToLowerInvariant();
            if (key is "description" or "keywords" or "og:title" or "og:description" or "og:site_name")
            {
                var value = WebUtility.HtmlDecode(m.Groups["v"].Value);
                if (!string.IsNullOrWhiteSpace(value)) parts.Add(value);
            }
        }
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
