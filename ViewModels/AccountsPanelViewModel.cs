using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BookmarkVault.Models;
using BookmarkVault.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BookmarkVault.ViewModels;

/// <summary>账号库面板：列表 + 编辑表单</summary>
public sealed partial class AccountsPanelViewModel : ObservableObject
{
    private readonly AccountVault _vault;
    private readonly Action _persist;
    private readonly Action<string> _setStatus;
    private string? _editingId;

    public ObservableCollection<AccountRowViewModel> Rows { get; } = new();

    [ObservableProperty] private AccountRowViewModel? _selectedRow;
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private string _editorTitle = "新增账号";
    [ObservableProperty] private string _draftDomain = string.Empty;
    [ObservableProperty] private string _draftSiteName = string.Empty;
    [ObservableProperty] private string _draftUsername = string.Empty;
    [ObservableProperty] private string _draftPassword = string.Empty;
    [ObservableProperty] private string _draftNote = string.Empty;
    [ObservableProperty] private bool _draftAutoPrompt = true;
    [ObservableProperty] private string? _filterText;

    public string CountText => Rows.Count == 0 ? "还没有记录任何账号" : $"共 {Rows.Count} 个账号";

    public AccountsPanelViewModel(AccountVault vault, Action persist, Action<string> setStatus)
    {
        _vault = vault;
        _persist = persist;
        _setStatus = setStatus;
    }

    partial void OnFilterTextChanged(string? value) => Reload();

    public void Reload()
    {
        var selectedId = SelectedRow?.Model.Id;
        Rows.Clear();

        IEnumerable<AccountEntry> source = _vault.All();
        var filter = FilterText?.Trim();
        if (!string.IsNullOrEmpty(filter))
            source = source.Where(a =>
                a.Domain.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                a.SiteName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                a.Username.Contains(filter, StringComparison.OrdinalIgnoreCase));

        foreach (var entry in source.OrderBy(a => a.Domain))
            Rows.Add(new AccountRowViewModel(entry));

        SelectedRow = Rows.FirstOrDefault(r => r.Model.Id == selectedId) ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>打开编辑表单；domain 不为空时预填域名</summary>
    public void BeginEdit(AccountRowViewModel? row, string? domain = null)
    {
        if (row != null)
        {
            _editingId = row.Model.Id;
            EditorTitle = "编辑账号";
            DraftDomain = row.Model.Domain;
            DraftSiteName = row.Model.SiteName;
            DraftUsername = row.Model.Username;
            DraftPassword = AccountVault.Decrypt(row.Model.PasswordCipher) ?? string.Empty;
            DraftNote = row.Model.Note ?? string.Empty;
            DraftAutoPrompt = row.Model.AutoPrompt;
        }
        else
        {
            _editingId = null;
            EditorTitle = "新增账号";
            DraftDomain = domain ?? string.Empty;
            DraftSiteName = string.Empty;
            DraftUsername = string.Empty;
            DraftPassword = string.Empty;
            DraftNote = string.Empty;
            DraftAutoPrompt = true;
        }
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void New() => BeginEdit(null);

    [RelayCommand]
    private void Edit(AccountRowViewModel? row) => BeginEdit(row ?? SelectedRow);

    [RelayCommand]
    private void Cancel() => IsEditorOpen = false;

    [RelayCommand]
    private void Save()
    {
        var domain = UrlHelper.GetDomain(DraftDomain.Trim());
        if (string.IsNullOrEmpty(domain))
            domain = DraftDomain.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(domain))
        {
            _setStatus("请填写域名，例如 github.com");
            return;
        }

        AccountEntry entry;
        if (_editingId != null)
        {
            entry = _vault.All().First(a => a.Id == _editingId);
        }
        else
        {
            entry = new AccountEntry();
        }

        entry.Domain = domain;
        entry.SiteName = DraftSiteName.Trim();
        entry.Username = DraftUsername.Trim();
        entry.Note = string.IsNullOrWhiteSpace(DraftNote) ? null : DraftNote.Trim();
        entry.AutoPrompt = DraftAutoPrompt;
        entry.PasswordCipher = string.IsNullOrEmpty(DraftPassword)
            ? null
            : AccountVault.Encrypt(DraftPassword);

        _vault.Save(entry);
        _persist();
        IsEditorOpen = false;
        DraftPassword = string.Empty;
        Reload();
        _setStatus($"已保存 {domain} 的账号信息");
    }

    [RelayCommand]
    private void Delete(AccountRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row == null) return;
        _vault.Delete(row.Model.Id);
        _persist();
        if (_editingId == row.Model.Id) IsEditorOpen = false;
        Reload();
        _setStatus($"已删除 {row.Domain} 的账号信息");
    }

    [RelayCommand]
    private void ToggleReveal(AccountRowViewModel? row)
    {
        if (row == null) return;
        row.Revealed = !row.Revealed;
    }

    [RelayCommand]
    private void CopyUsername(AccountRowViewModel? row)
    {
        if (row == null || string.IsNullOrEmpty(row.Model.Username)) return;
        TrySetClipboard(row.Model.Username, "账号已复制");
    }

    [RelayCommand]
    private void CopyPassword(AccountRowViewModel? row)
    {
        if (row == null) return;
        var plain = AccountVault.Decrypt(row.Model.PasswordCipher);
        if (string.IsNullOrEmpty(plain))
        {
            _setStatus("这条记录没有保存密码");
            return;
        }
        TrySetClipboard(plain, "密码已复制到剪贴板");
    }

    private void TrySetClipboard(string text, string status)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            _setStatus(status);
        }
        catch (Exception ex)
        {
            _setStatus("复制失败：" + ex.Message);
        }
    }
}
