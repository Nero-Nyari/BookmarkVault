using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace BookmarkVault.Services;

/// <summary>
/// 封面图仓库：把用户手动导入的图片复制进数据目录统一管理，
/// 库里只记录相对路径，换机器 / 移动目录都不会断链。
/// </summary>
public static class CoverStore
{
    private const string FolderName = "covers";

    /// <summary>导入一张封面图，返回相对数据目录的路径</summary>
    public static string Import(string bookmarkId, string sourceFile)
    {
        AppPaths.EnsureCreated();

        var ext = Path.GetExtension(sourceFile);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
        var fileName = SafeName(bookmarkId) + ext.ToLowerInvariant();
        var target = Path.Combine(AppPaths.CoverDir, fileName);

        // 同名文件（同一书签重新导入）先删掉，避免个别环境不允许覆盖
        if (File.Exists(target)) File.Delete(target);
        File.Copy(sourceFile, target);

        return FolderName + "/" + fileName;
    }

    /// <summary>删除封面图文件</summary>
    public static void Delete(string? coverPath)
    {
        var full = Resolve(coverPath);
        if (string.IsNullOrEmpty(full)) return;
        try
        {
            if (File.Exists(full)) File.Delete(full);
        }
        catch (Exception ex)
        {
            JsonStore.Log($"删除封面图失败 {full}：{ex.Message}");
        }
    }

    /// <summary>卡片墙封面固定的宽高比 2:3（Steam 风格竖版）</summary>
    public const double CardCoverRatio = 2.0 / 3.0;

    /// <summary>
    /// 把位图编码成 png 写进 covers 目录，返回相对数据目录的路径。
    /// 文件名带后缀，避免和详情页封面互相覆盖（详情页封面后缀为空，卡片封面用 "_card"）。
    /// </summary>
    public static string SaveBitmap(string bookmarkId, string suffix, BitmapSource image)
    {
        AppPaths.EnsureCreated();

        var fileName = SafeName(bookmarkId) + suffix + ".png";
        var target = Path.Combine(AppPaths.CoverDir, fileName);
        if (File.Exists(target)) File.Delete(target);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(target);
        encoder.Save(stream);

        return FolderName + "/" + fileName;
    }

    /// <summary>
    /// 取中心区域裁成 2:3。抓来的封面多半是横版，直接塞进竖版卡片会被拉变形，
    /// 所以自动抓取时额外落一张裁切版。已经是 2:3 的原图直接返回，不重复编码。
    /// </summary>
    public static BitmapSource CropToCardRatio(BitmapSource source)
    {
        var w = source.PixelWidth;
        var h = source.PixelHeight;
        if (w <= 0 || h <= 0) return source;

        var ratio = (double)w / h;
        if (Math.Abs(ratio - CardCoverRatio) < 0.01) return source;

        int cropW, cropH, x, y;
        if (ratio > CardCoverRatio)
        {
            // 太宽：高度铺满，左右各切掉一点
            cropH = h;
            cropW = Math.Max(1, (int)Math.Round(h * CardCoverRatio));
            x = (w - cropW) / 2;
            y = 0;
        }
        else
        {
            // 太窄：宽度铺满，上下各切掉一点
            cropW = w;
            cropH = Math.Max(1, (int)Math.Round(w / CardCoverRatio));
            x = 0;
            y = (h - cropH) / 2;
        }

        var rect = new Int32Rect(x, y, Math.Min(cropW, w - x), Math.Min(cropH, h - y));
        var cropped = new CroppedBitmap(source, rect);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>读取整张原图用于裁剪（不缩放解码，保留全分辨率）</summary>
    public static BitmapSource? LoadFull(string? coverPath)
    {
        var full = Resolve(coverPath);
        if (string.IsNullOrEmpty(full) || !File.Exists(full)) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            // 同一路径重新导入时文件名不变，必须绕开 WPF 的图片缓存
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(full);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            JsonStore.Log($"读取封面原图失败 {full}：{ex.Message}");
            return null;
        }
    }

    /// <summary>相对路径 → 绝对路径</summary>
    public static string Resolve(string? coverPath)
    {
        if (string.IsNullOrWhiteSpace(coverPath)) return string.Empty;
        if (Path.IsPathRooted(coverPath)) return coverPath;
        return Path.Combine(AppPaths.Root, coverPath.Replace('/', Path.DirectorySeparatorChar));
    }

    public static bool Exists(string? coverPath)
    {
        var full = Resolve(coverPath);
        return !string.IsNullOrEmpty(full) && File.Exists(full);
    }

    private static string SafeName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Guid.NewGuid().ToString("N");
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.ToString();
    }
}
