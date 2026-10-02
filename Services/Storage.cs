using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BookmarkVault.Services;

/// <summary>应用数据存放路径</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BookmarkVault");

    public static string LibraryFile => Path.Combine(Root, "library.json");
    public static string CacheFile => Path.Combine(Root, "probe-cache.json");
    public static string IconDir => Path.Combine(Root, "icons");
    public static string CoverDir => Path.Combine(Root, "covers");
    /// <summary>安全数据目录（黑名单等，条目可能上万，单独放一个目录）</summary>
    public static string SecurityDir => Path.Combine(Root, "security");
    public static string BlacklistFile => Path.Combine(SecurityDir, "blacklist.json");
    public static string LogFile => Path.Combine(Root, "app.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(IconDir);
        Directory.CreateDirectory(CoverDir);
        Directory.CreateDirectory(SecurityDir);
    }
}

/// <summary>JSON 读写（原子写入 + 损坏自愈）</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static T Load<T>(string path, Func<T> factory) where T : class
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var value = JsonSerializer.Deserialize<T>(json, Options);
                    if (value != null) return value;
                }
            }
        }
        catch (Exception ex)
        {
            // 文件损坏时备份后重建，避免应用打不开
            TryBackupCorrupt(path);
            Log($"读取 {Path.GetFileName(path)} 失败：{ex.Message}");
        }
        return factory();
    }

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(value, Options);
        var temp = path + ".tmp";

        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            // 少数环境（杀毒实时防护、受限沙箱）会拒绝覆盖式重命名，退回直接写入
            Log($"原子写入失败，改为直接写入 {Path.GetFileName(path)}：{ex.Message}");
            File.WriteAllText(path, json, new UTF8Encoding(false));
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // 临时文件清理失败可以忽略
            }
        }
    }

    private static void TryBackupCorrupt(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
        }
        catch
        {
            // 备份失败不影响主流程
        }
    }

    public static void Log(string message)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.AppendAllText(AppPaths.LogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // 日志失败直接忽略
        }
    }
}
