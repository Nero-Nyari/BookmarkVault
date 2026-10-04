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

    // ---------- 导出 ----------

    [ObservableProperty] private bool _isExportOpen;
    /// <summary>是否把明文密码写进表格，默认关闭（导出后 DPAPI 加密就失效了）</summary>
    [ObservableProperty] private bool _exportIncludePassword;

    public string CountText => Rows.Count == 0 ? "还没有记录任何账号" : $"共 {Rows.Count} 个账号";

    /// <summary>勾选了几条 / 共几条。全选框的文案和导出的范围判定都用它</summary>
    public int SelectedCount => Rows.Count(r => r.IsExportSelected);

    public string SelectionText => $"已勾选 {SelectedCount} / {Rows.Count} 个账号";

    public bool CanExport => Rows.Count > 0;

    /// <summary>列表里有没有内容（勾选工具条按它显示）</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>没勾选任何一条时，导出弹层里说明会导出全部</summary>
    public string ExportScopeText => Rows.Count == 0
        ? "账号库是空的，没有可导出的内容"
        : SelectedCount > 0
            ? $"将导出已勾选的 {SelectedCount} 个账号"
            : $"没有勾选任何账号，将导出全部 {Rows.Count} 个账号";

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
        // 重建列表会换掉所有行对象，先把勾选过的 id 记下来，重建后再贴回去
        var checkedIds = Rows.Where(r => r.IsExportSelected).Select(r => r.Model.Id).ToHashSet();

        foreach (var row in Rows) row.PropertyChanged -= OnRowPropertyChanged;
        Rows.Clear();

        IEnumerable<AccountEntry> source = _vault.All();
        var filter = FilterText?.Trim();
        if (!string.IsNullOrEmpty(filter))
            source = source.Where(a =>
                a.Domain.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                a.SiteName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                a.Username.Contains(filter, StringComparison.OrdinalIgnoreCase));

        foreach (var entry in source.OrderBy(a => a.Domain))
        {
            var row = new AccountRowViewModel(entry) { IsExportSelected = checkedIds.Contains(entry.Id) };
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }

        SelectedRow = Rows.FirstOrDefault(r => r.Model.Id == selectedId) ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(CountText));
        NotifySelectionChanged();
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountRowViewModel.IsExportSelected)) NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ExportScopeText));
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

    // ---------- 勾选与导出 ----------

    /// <summary>全选 / 全不选：只作用于当前列表（搜索后就是「所见即所选」）</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        if (Rows.Count == 0) return;
        var target = SelectedCount < Rows.Count;
        foreach (var row in Rows) row.IsExportSelected = target;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var row in Rows) row.IsExportSelected = false;
    }

    [RelayCommand]
    private void InvertSelection()
    {
        foreach (var row in Rows) row.IsExportSelected = !row.IsExportSelected;
    }

    [RelayCommand]
    private void OpenExport()
    {
        if (Rows.Count == 0)
        {
            _setStatus("账号库是空的，没有可导出的内容");
            return;
        }
        ExportIncludePassword = false;
        IsExportOpen = true;
    }

    [RelayCommand]
    private void CancelExport()
    {
        IsExportOpen = false;
        ExportIncludePassword = false;
    }

    /// <summary>把勾选的账号写到 path（一条都没勾就导出全部）。成功返回 true</summary>
    public bool Export(string path, AccountExportFormat format)
    {
        var checkedRows = Rows.Where(r => r.IsExportSelected).ToList();
        if (checkedRows.Count == 0) checkedRows = Rows.ToList();
        if (checkedRows.Count == 0)
        {
            _setStatus("账号库是空的，没有可导出的内容");
            return false;
        }

        try
        {
            AccountExporter.Export(checkedRows.Select(r => r.Model), path, format, ExportIncludePassword);
        }
        catch (Exception ex)
        {
            _setStatus("导出失败：" + ex.Message);
            return false;
        }

        IsExportOpen = false;
        ExportIncludePassword = false;
        _setStatus($"已导出 {checkedRows.Count} 个账号到 {System.IO.Path.GetFileName(path)}");
        return true;
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
