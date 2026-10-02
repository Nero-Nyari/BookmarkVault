using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using BookmarkVault.Models;

namespace BookmarkVault.Services;

/// <summary>
/// 本地账号库。密码使用 Windows DPAPI（当前用户范围）加密后存盘，
/// 只有同一台机器上的同一个 Windows 账号能解密，明文永不落盘。
/// </summary>
public sealed class AccountVault
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    private readonly LibraryService _library;

    public AccountVault(LibraryService library) => _library = library;

    private LibraryDatabase Database => _library.Database;

    public IReadOnlyList<AccountEntry> All() => Database.Accounts;

    public IReadOnlyList<AccountEntry> Find(string url)
    {
        var domain = UrlHelper.GetDomain(url).ToLowerInvariant();
        if (string.IsNullOrEmpty(domain)) return Array.Empty<AccountEntry>();

        var root = UrlHelper.GetRootDomain(url).ToLowerInvariant();
        return Database.Accounts
            .Where(a => a.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(root) && a.Domain.Equals(root, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>该站点是否配置了账号，用于决定是否弹提示</summary>
    public bool HasAccount(string url) => Find(url).Any(a => a.AutoPrompt);

    public void Save(AccountEntry entry)
    {
        entry.UpdatedAt = DateTime.Now;
        var index = Database.Accounts.FindIndex(a => a.Id == entry.Id);
        if (index >= 0) Database.Accounts[index] = entry;
        else Database.Accounts.Add(entry);
    }

    public void Delete(string id) => Database.Accounts.RemoveAll(a => a.Id == id);

    public static string Encrypt(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plain);
        var cipher = Protect(bytes);
        return Convert.ToBase64String(cipher);
    }

    public static string? Decrypt(string? cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64)) return null;
        try
        {
            var cipher = Convert.FromBase64String(cipherBase64);
            var plain = Unprotect(cipher);
            return plain == null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            JsonStore.Log($"密码解密失败：{ex.Message}");
            return null;
        }
    }

    private static byte[] Protect(byte[] data)
    {
        var input = ToBlob(data);
        var entropy = IntPtr.Zero;
        try
        {
            if (!CryptProtectData(ref input, IntPtr.Zero, entropy, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var output))
                throw new InvalidOperationException("DPAPI 加密失败：" + Marshal.GetLastWin32Error());

            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData);
        }
    }

    private static byte[]? Unprotect(byte[] data)
    {
        var input = ToBlob(data);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var output))
                return null;

            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            if (input.pbData != IntPtr.Zero) Marshal.FreeHGlobal(input.pbData);
        }
    }

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var blob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, IntPtr szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
