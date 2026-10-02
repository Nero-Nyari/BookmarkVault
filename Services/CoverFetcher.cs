using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BookmarkVault.Services;

/// <summary>要抓封面的一条目标</summary>
public sealed record CoverTarget(string BookmarkId, string Url);

/// <summary>抓取结果：详情页封面 + 顺带裁好的 2:3 卡片封面（相对数据目录）</summary>
public sealed record FetchedCover(string CoverPath, string CardCoverPath);

/// <summary>抓取进度</summary>
public sealed record CoverFetchProgress(int Done, int Total, string CurrentUrl);

/// <summary>
/// 自动抓封面：打开网址首页，从 og:image / twitter:image 里找配图，
/// 找不到就退回到正文前几张 img。抓到后落两版文件：原图（详情页）+ 2:3 裁切版（卡片墙）。
/// 只负责抓和存，标记位与列表刷新交给 ViewModel。
/// </summary>
public sealed class CoverFetcher
{
    /// <summary>单张图片最大 6MB，超过直接跳过</summary>
    private const int MaxImageBytes = 6 * 1024 * 1024;

    /// <summary>首页 HTML 最多读 256KB，够 meta 标签出现的位置了</summary>
    private const int MaxHtmlBytes = 256 * 1024;

    /// <summary>小于这个尺寸的多半是 logo、图标、占位图，不算封面</summary>
    private const int MinImageWidth = 300;
    private const int MinImageHeight = 150;

    /// <summary>存入数据目录前先缩到最长边 1280，免得 png 转出来比原图还大</summary>
    private const int MaxStoredDimension = 1280;

    /// <summary>兜底方案里最多试前 8 个 img</summary>
    private const int MaxImgFallbacks = 8;

    /// <summary>卡片封面的文件后缀，和 CoverStore 约定一致</summary>
    private const string CardSuffix = "_card";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0";

    private static readonly Regex MetaTagRegex = new(
        @"<meta\s[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NameAttrRegex = new(
        @"(?:property|name|itemprop)\s*=\s*[""'](?<v>[^""']*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ContentAttrRegex = new(
        @"content\s*=\s*[""'](?<v>[^""']*)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ImgTagRegex = new(
        @"<img\s[^>]*src\s*=\s*[""'](?<v>[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _client;

    public CoverFetcher()
    {
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
            Timeout = Timeout.InfiniteTimeSpan // 逐目标用 CancellationToken 控制
        };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
    }

    /// <summary>
    /// 批量抓取，带并发限制与进度回调。抓不到的条目直接不出现在结果里，由调用方按原样保留。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, FetchedCover>> FetchManyAsync(
        IReadOnlyList<CoverTarget> targets,
        int timeoutSeconds,
        int concurrency,
        IProgress<CoverFetchProgress>? progress,
        CancellationToken ct)
    {
        var results = new ConcurrentDictionary<string, FetchedCover>(StringComparer.Ordinal);
        var total = targets.Count;
        var done = 0;
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 16));

        var tasks = targets.Select(async target =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var fetched = await FetchOneAsync(target, timeoutSeconds, ct).ConfigureAwait(false);
                if (fetched != null) results[target.BookmarkId] = fetched;

                var current = Interlocked.Increment(ref done);
                progress?.Report(new CoverFetchProgress(current, total, target.Url));
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

        return results;
    }

    private async Task<FetchedCover?> FetchOneAsync(CoverTarget target, int timeoutSeconds, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
        var token = timeoutCts.Token;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
            request.Headers.TryAddWithoutValidation("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var baseUri = response.RequestMessage?.RequestUri ?? new Uri(target.Url);
            var html = await ReadLimitedAsync(response, MaxHtmlBytes, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(html)) return null;

            foreach (var candidate in ExtractCandidates(html, baseUri))
            {
                var image = await DownloadImageAsync(candidate, token).ConfigureAwait(false);
                if (image == null) continue;

                var stored = ScaleDown(image, MaxStoredDimension);
                var cardBitmap = CoverStore.CropToCardRatio(stored);

                var coverPath = CoverStore.SaveBitmap(target.BookmarkId, string.Empty, stored);
                var cardPath = CoverStore.SaveBitmap(target.BookmarkId, CardSuffix, cardBitmap);
                return new FetchedCover(coverPath, cardPath);
            }
        }
        catch (OperationCanceledException)
        {
            // 超时或被取消，当作抓不到
        }
        catch (Exception ex)
        {
            JsonStore.Log($"抓封面失败 {target.Url}：{ex.Message}");
        }

        return null;
    }

    private async Task<BitmapSource?> DownloadImageAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept",
                "image/avif,image/webp,image/png,image/jpeg,image/*;q=0.8,*/*;q=0.5");

            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Length > 0 && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;

            var bytes = await ReadBytesLimitedAsync(response, MaxImageBytes, ct).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0) return null;

            var image = Decode(bytes);
            if (image == null) return null;
            if (image.PixelWidth < MinImageWidth || image.PixelHeight < MinImageHeight) return null;
            return image;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 单张图取不到就换下一张候选
            return null;
        }
    }

    /// <summary>按出现顺序收集候选图：优先 og / twitter 的分享图，再退回正文里的 img</summary>
    private static List<string> ExtractCandidates(string html, Uri baseUri)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? raw)
        {
            var resolved = ResolveUrl(raw, baseUri);
            if (resolved == null) return;
            if (seen.Add(resolved)) found.Add(resolved);
        }

        foreach (Match tag in MetaTagRegex.Matches(html))
        {
            var name = NameAttrRegex.Match(tag.Value);
            if (!name.Success) continue;

            var key = name.Groups["v"].Value.Trim().ToLowerInvariant();
            if (key is not ("og:image" or "og:image:url" or "og:image:secure_url"
                or "twitter:image" or "twitter:image:src")) continue;

            var content = ContentAttrRegex.Match(tag.Value);
            if (content.Success) Add(content.Groups["v"].Value);
        }

        var taken = 0;
        foreach (Match tag in ImgTagRegex.Matches(html))
        {
            if (taken >= MaxImgFallbacks) break;
            var before = found.Count;
            Add(tag.Groups["v"].Value);
            if (found.Count > before) taken++;
        }

        return found;
    }

    private static string? ResolveUrl(string? raw, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = WebUtility.HtmlDecode(raw).Trim();
        // data: 内联图、协议相对之外的奇怪 scheme 一律不要
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;

        if (!Uri.TryCreate(baseUri, value, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.ToString();
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // WPF 解不了的格式（webp / avif 等）直接算作没有
            return null;
        }
    }

    private static BitmapSource ScaleDown(BitmapSource source, int maxDimension)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= maxDimension) return source;

        var scale = (double)maxDimension / longest;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static async Task<string?> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[Math.Min(limit, 16384)];
            using var ms = new MemoryStream();
            int total = 0, read;
            while (total < limit &&
                   (read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - total)), ct)
                       .ConfigureAwait(false)) > 0)
            {
                ms.Write(buffer, 0, read);
                total += read;
            }

            var charset = response.Content.Headers.ContentType?.CharSet;
            var encoding = Encoding.UTF8;
            if (!string.IsNullOrEmpty(charset))
            {
                try { encoding = Encoding.GetEncoding(charset.Trim('"', '\'')); }
                catch { /* 未知编码，退回 UTF-8 */ }
            }
            return encoding.GetString(ms.ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadBytesLimitedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        try
        {
            if (response.Content.Headers.ContentLength is long length && length > limit) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[16384];
            using var ms = new MemoryStream();
            int total = 0, read;
            while (total < limit &&
                   (read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - total)), ct)
                       .ConfigureAwait(false)) > 0)
            {
                ms.Write(buffer, 0, read);
                total += read;
            }
            return ms.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}