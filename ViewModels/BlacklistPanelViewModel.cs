using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using BookmarkVault.Models;
using BookmarkVault.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BookmarkVault.ViewModels;

/// <summary>黑名单列表里的一行</summary>
public sealed class BlacklistRowViewModel
{
    public BlacklistRule Model { get; }

    public BlacklistRowViewModel(BlacklistRule model) => Model = model;

    public string Pattern => Model.Pattern;
    public string SourceName => BlacklistService.SourceName(Model.Source);

    public string SourceColor => Model.Source switch
    {
        BlacklistSource.Urlhaus => "#FF8A65",
        BlacklistSource.OpenPhish => "#FFD54F",
        _ => "#66C0F4"
    };

    public bool IsManual => Model.Source == BlacklistSource.Manual;
    public string OriginText => IsManual ? "手工添加" : "订阅更新";
    public bool HasNote => !string.IsNullOrWhiteSpace(Model.Note);
    public string Note => HasNote ? Model.Note! : "—";
    public string AddedText => Model.AddedAt.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>黑名单管理面板：增删改查 + 更新订阅</summary>
public sealed partial class BlacklistPanelViewModel : ObservableObject
{
    /// <summary>列表最多渲染这么多行，再多就让用户先筛选（避免一次铺几万条）</summary>
    private const int MaxRows = 3000;

    private readonly BlacklistService _service;
    private readonly Action<string> _setStatus;
    private BlacklistRule? _editing;

    public ObservableCollection<BlacklistRowViewModel> Rows { get; } = new();

    public const string AllSourcesLabel = "全部来源";
    public const string UrlhausLabel = "URLhaus";
    public const string OpenPhishLabel = "OpenPhish";
    public const string ManualLabel = "自定义";

    public IReadOnlyList<string> SourceOptions { get; } =
        new[] { AllSourcesLabel, UrlhausLabel, OpenPhishLabel, ManualLabel };

    [ObservableProperty] private string? _filterText;
    [ObservableProperty] private string _sourceFilter = AllSourcesLabel;
    [ObservableProperty] private BlacklistRowViewModel? _selectedRow;
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private bool _isPatternEditable = true;
    [ObservableProperty] private string _editorTitle = "新增黑名单条目";
    [ObservableProperty] private string _draftPattern = string.Empty;
    [ObservableProperty] private string _draftNote = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>没在更新订阅（按钮可用状态）</summary>
    public bool IsIdle => !IsBusy;

    [ObservableProperty] private string _countText = string.Empty;
    /// <summary>设置页用的简明统计，不带列表分页信息</summary>
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private string _updatedText = string.Empty;
    [ObservableProperty] private string _ignoredText = string.Empty;

    public BlacklistPanelViewModel(BlacklistService service, Action<string> setStatus)
    {
        _service = service;
        _setStatus = setStatus;
        // 订阅更新可能发生在后台线程（启动时自动更新），集合改动必须切回 UI 线程
        _service.Changed += (_, _) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(new Action(Reload));
            else
                Reload();
        };

        Reload();
    }

    partial void OnFilterTextChanged(string? value) => Reload();

    partial void OnSourceFilterChanged(string value) => Reload();

    public void Reload()
    {
        var selectedPattern = SelectedRow?.Pattern;
        Rows.Clear();

        var filter = FilterText?.Trim();
        var matched = _service.Rules.Where(r =>
            (string.IsNullOrEmpty(filter) ||
             r.Pattern.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
             (r.Note?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)) &&
            MatchesSource(r));

        var total = 0;
        foreach (var rule in matched.OrderBy(r => r.Source).ThenBy(r => r.Pattern))
        {
            total++;
            if (Rows.Count < MaxRows) Rows.Add(new BlacklistRowViewModel(rule));
        }

        SelectedRow = Rows.FirstOrDefault(r => r.Pattern == selectedPattern) ?? Rows.FirstOrDefault();

        var suffix = total > Rows.Count ? $"，只显示前 {Rows.Count} 条，可先筛选或搜索" : string.Empty;
        CountText = _service.Count == 0
            ? "还没有任何黑名单条目，点「更新订阅」拉取外部恶意网址库"
            : $"共 {_service.Count} 条（订阅 {_service.SubscriptionCount} · 自定义 {_service.ManualCount}），"
              + $"当前匹配 {total} 条{suffix}";

        SummaryText = _service.Count == 0
            ? "还没有任何黑名单条目"
            : $"共 {_service.Count} 条（订阅 {_service.SubscriptionCount} · 自定义 {_service.ManualCount}）";

        IgnoredText = _service.IgnoredCount == 0
            ? "忽略了 0 条订阅条目"
            : $"已忽略 {_service.IgnoredCount} 条订阅条目，下次更新订阅不会再拉回来";

        UpdatedText = _service.LastUpdatedAt == null
            ? "从未更新过订阅"
            : $"上次更新：{_service.LastUpdatedAt:yyyy-MM-dd HH:mm}";
    }

    private bool MatchesSource(BlacklistRule rule) => SourceFilter switch
    {
        UrlhausLabel => rule.Source == BlacklistSource.Urlhaus,
        OpenPhishLabel => rule.Source == BlacklistSource.OpenPhish,
        ManualLabel => rule.Source == BlacklistSource.Manual,
        _ => true
    };

    // ---------- 增删改 ----------

    [RelayCommand]
    private void New()
    {
        _editing = null;
        EditorTitle = "新增黑名单条目";
        IsPatternEditable = true;
        DraftPattern = string.Empty;
        DraftNote = string.Empty;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void Edit(BlacklistRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row == null) return;

        _editing = row.Model;
        EditorTitle = "编辑黑名单条目";
        // 订阅条目的表达式是数据源给的原样数据，只允许改备注
        IsPatternEditable = row.IsManual;
        DraftPattern = row.Model.Pattern;
        DraftNote = row.Model.Note ?? string.Empty;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void Cancel()
    {
        IsEditorOpen = false;
        _editing = null;
    }

    [RelayCommand]
    private void Save()
    {
        if (_editing == null)
        {
            var created = _service.AddManual(DraftPattern, DraftNote);
            if (created == null)
            {
                _setStatus("表达式不合法，请填 example.com、https://example.com/bad 或 *.example.com");
                return;
            }
            _setStatus($"已添加黑名单条目 {created.Pattern}");
        }
        else
        {
            var pattern = IsPatternEditable ? DraftPattern : _editing.Pattern;
            if (!_service.UpdateRule(_editing, pattern, DraftNote))
            {
                _setStatus("表达式不合法，请填 example.com、https://example.com/bad 或 *.example.com");
                return;
            }
            _setStatus($"已更新黑名单条目 {_editing.Pattern}");
        }

        IsEditorOpen = false;
        _editing = null;
    }

    [RelayCommand]
    private void Delete(BlacklistRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row == null) return;

        var wasManual = row.IsManual;
        var pattern = row.Pattern;
        _service.RemoveRule(row.Model);
        if (_editing == row.Model) IsEditorOpen = false;

        _setStatus(wasManual
            ? $"已删除黑名单条目 {pattern}"
            : $"已删除 {pattern}，并加入忽略名单（下次更新订阅不会再拉回来）");
    }

    [RelayCommand]
    private void ClearIgnored()
    {
        var count = _service.IgnoredCount;
        if (count == 0)
        {
            _setStatus("忽略名单本来就是空的");
            return;
        }
        foreach (var pattern in _service.Ignored.ToList())
            _service.Unignore(pattern);
        _setStatus($"已清空忽略名单（{count} 条），下次更新订阅会把它们重新拉回来");
    }

    // ---------- 更新订阅 ----------

    [RelayCommand]
    private async Task RefreshFeedsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _setStatus("正在更新恶意网址库订阅…");
        try
        {
            var result = await _service.RefreshFeedsAsync();
            if (result.Error != null && result.TotalCount == 0)
            {
                _setStatus("订阅更新失败：" + result.Error);
                return;
            }

            _setStatus($"订阅更新完成：URLhaus {result.UrlhausCount} 条，OpenPhish {result.OpenPhishCount} 条"
                       + (result.Error != null ? $"（部分失败：{result.Error}）" : string.Empty));
        }
        catch (Exception ex)
        {
            _setStatus("订阅更新失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            Reload();
        }
    }
}