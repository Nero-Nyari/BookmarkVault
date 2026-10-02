using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace BookmarkVault.Services;

/// <summary>一个可用来打开网址的浏览器</summary>
public sealed record BrowserEntry(string Name, string Path);

/// <summary>
/// 找出系统里有哪些浏览器能拿来打开网址。
/// 主要来源是注册表里「默认应用」登记的那批（HKCU / HKLM 的 Clients\StartMenuInternet），
/// 扫完之后再补一遍常见安装路径，这样便携版、没登记的也能捞到。
/// </summary>
public static class BrowserCatalog
{
    /// <summary>注册表里「默认应用」登记的浏览器，默认值是显示名，shell\open\command 是启动命令</summary>
    private static readonly (RegistryKey Hive, string Path)[] RegistryRoots =
    {
        (Registry.CurrentUser, @"SOFTWARE\Clients\StartMenuInternet"),
        (Registry.LocalMachine, @"SOFTWARE\Clients\StartMenuInternet"),
        (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet")
    };

    public static IReadOnlyList<BrowserEntry> Detect()
    {
        var found = new List<BrowserEntry>();

        foreach (var (hive, path) in RegistryRoots)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) continue;

                foreach (var sub in key.GetSubKeyNames())
                {
                    using var browserKey = key.OpenSubKey(sub);
                    var command = browserKey?.OpenSubKey(@"shell\open\command")?.GetValue(null) as string;
                    var exe = ParseExecutable(command);
                    if (exe == null) continue;

                    var name = browserKey?.GetValue(null) as string;
                    Add(found, string.IsNullOrWhiteSpace(name) ? NameFor(exe) : name!.Trim(), exe);
                }
            }
            catch (Exception ex)
            {
                JsonStore.Log($"读取浏览器注册表失败 {path}：{ex.Message}");
            }
        }

        foreach (var exe in FallbackPaths())
            if (File.Exists(exe)) Add(found, NameFor(exe), exe);

        return found.OrderBy(b => b.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>注册表里扫不到时兜底找的几个常见安装位置</summary>
    private static IEnumerable<string> FallbackPaths()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(programsX86, @"Microsoft\Edge\Application\msedge.exe");
        yield return Path.Combine(programs, @"Microsoft\Edge\Application\msedge.exe");
        yield return Path.Combine(programs, @"Google\Chrome\Application\chrome.exe");
        yield return Path.Combine(programsX86, @"Google\Chrome\Application\chrome.exe");
        yield return Path.Combine(local, @"Google\Chrome\Application\chrome.exe");
        yield return Path.Combine(programs, @"Mozilla Firefox\firefox.exe");
        yield return Path.Combine(programsX86, @"Mozilla Firefox\firefox.exe");
        yield return Path.Combine(local, @"BraveSoftware\Brave-Browser\Application\brave.exe");
    }

    /// <summary>
    /// 从启动命令里拆出 exe 路径。命令形如
    /// "C:\Program Files\Google\Chrome\Application\chrome.exe" --single-argument %1
    /// 带引号和不带引号都要认。
    /// </summary>
    private static string? ParseExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        string exe;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return null;
            exe = text[1..end];
        }
        else
        {
            var end = text.IndexOf(' ');
            exe = end < 0 ? text : text[..end];
        }

        return File.Exists(exe) ? exe : null;
    }

    /// <summary>按 exe 路径去重：同一个浏览器会在多个注册表位置出现</summary>
    private static void Add(List<BrowserEntry> found, string name, string path)
    {
        if (found.Any(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        found.Add(new BrowserEntry(name, path));
    }

    private static string NameFor(string exePath) => Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant() switch
    {
        "msedge" => "Microsoft Edge",
        "chrome" => "Google Chrome",
        "firefox" => "Mozilla Firefox",
        "brave" => "Brave",
        "opera" => "Opera",
        _ => Path.GetFileNameWithoutExtension(exePath)
    };
}