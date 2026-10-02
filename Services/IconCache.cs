using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BookmarkVault.Models;

namespace BookmarkVault.Services;

/// <summary>
/// 站点图标：优先从站点自身抓 favicon，失败则回退到「首字母色块」。
/// 统一缩放为 64px PNG 缓存到本地，避免每次启动重新下载。
/// </summary>
public sealed class IconCache
{
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(6);

    private static readonly string[] CandidatePaths =
    {
        "/favicon.ico",
        "/apple-touch-icon.png",
        "/apple-touch-icon-precomposed.png"
    };

    private static readonly string[] AvatarPalette =
    {
        "#4FC3F7", "#B388FF", "#FF8A65", "#FF5252", "#4DD0E1", "#FFB74D",
        "#81C784", "#7E57C2", "#64B5F6", "#FFD54F", "#4DB6AC", "#7986CB"
    };

    public IconCache()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 4,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Edg/124.0.0.0");
    }

    public static string IconPathFor(string domain) =>
        Path.Combine(AppPaths.IconDir, SafeName(domain) + ".png");

    public static bool HasIcon(string domain) =>
        !string.IsNullOrEmpty(domain) && File.Exists(IconPathFor(domain));

    /// <summary>下载并缓存图标，返回是否成功</summary>
    public async Task<bool> EnsureIconAsync(string url, CancellationToken ct)
    {
        var domain = UrlHelper.GetDomain(url);
        if (string.IsNullOrEmpty(domain)) return false;

        var target = IconPathFor(domain);
        if (File.Exists(target)) return true;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(target)) return true;

            var site = GetSiteRoot(url);
            if (string.IsNullOrEmpty(site)) return false;

            foreach (var path in CandidatePaths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var bytes = await _client.GetByteArrayAsync(site + path, ct).ConfigureAwait(false);
                    if (bytes.Length < 32) continue;
                    if (!LooksLikeImage(bytes)) continue;
                    if (TrySaveAsPng(bytes, target)) return true;
                }
                catch
                {
                    // 单个候选地址失败就试下一个
                }
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>把任意图片字节解码、缩放后存为 PNG</summary>
    private static bool TrySaveAsPng(byte[] bytes, string target)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return false;

            // 多帧 ICO 取面积最大的一帧，避免拿到 16x16
            var frame = decoder.Frames
                .OrderByDescending(f => f.PixelWidth * f.PixelHeight)
                .First();

            BitmapSource source = frame;
            const int max = 64;
            if (frame.PixelWidth > max || frame.PixelHeight > max)
            {
                var scale = Math.Min((double)max / frame.PixelWidth, (double)max / frame.PixelHeight);
                source = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            Directory.CreateDirectory(AppPaths.IconDir);
            var temp = target + ".tmp";
            using (var output = File.Create(temp))
                encoder.Save(output);
            File.Move(temp, target, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeImage(byte[] b)
    {
        if (b.Length < 8) return false;
        if (b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01 && b[3] == 0x00) return true; // ICO
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true; // PNG
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;                 // JPEG
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return true;                 // GIF
        if (b[0] == 0x42 && b[1] == 0x4D) return true;                                 // BMP
        if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46) return true; // WEBP
        // SVG：文本开头
        var head = Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 512));
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSiteRoot(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;
        return uri.Scheme + "://" + uri.Authority;
    }

    private static string SafeName(string domain)
    {
        var sb = new StringBuilder(domain.Length);
        foreach (var c in domain)
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        var name = sb.ToString();
        if (name.Length > 80)
        {
            using var md5 = MD5.Create();
            var hash = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(domain)))[..12];
            name = name[..60] + "_" + hash;
        }
        return name;
    }

    /// <summary>首字母色块：同一域名始终得到同一颜色</summary>
    public static string AvatarColor(string domain)
    {
        if (string.IsNullOrEmpty(domain)) return AvatarPalette[0];
        var sum = 0;
        foreach (var c in domain) sum = (sum * 31 + c) & 0x7FFFFFFF;
        return AvatarPalette[sum % AvatarPalette.Length];
    }

    /// <summary>首字母：优先取域名主体首字，中文域名取首字</summary>
    public static string AvatarText(string domain)
    {
        if (string.IsNullOrEmpty(domain)) return "?";
        var core = domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? domain[4..] : domain;
        return core.Length > 0 ? core[..1].ToUpperInvariant() : "?";
    }
}
