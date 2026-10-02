using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BookmarkVault.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BookmarkVault.ViewModels;

/// <summary>
/// 筛选面板里的一个条件组（分类 / 可访问状态 / 安全等级 / 站点类型 / 域名后缀）。
/// 每个组对应 <see cref="FilterState"/> 里的一个字符串列表，勾选结果直接写回那里。
/// </summary>
public sealed class FilterGroupViewModel
{
    private readonly Func<FilterState, List<string>> _selector;
    private readonly Action _onChanged;

    public FilterGroupViewModel(string title, Func<FilterState, List<string>> selector, Action onChanged)
    {
        Title = title;
        _selector = selector;
        _onChanged = onChanged;
    }

    public string Title { get; }

    public ObservableCollection<FilterOptionViewModel> Options { get; } = new();

    public bool HasChecked => Options.Any(o => o.IsChecked);

    /// <summary>把本组勾选的 Key 写回设置</summary>
    internal void Persist(FilterState state)
    {
        var target = _selector(state);
        target.Clear();
        foreach (var option in Options.Where(o => o.IsChecked)) target.Add(option.Key);
    }

    internal void NotifyChanged() => _onChanged();
}

/// <summary>筛选面板里的一个可勾选项，Key 是参与比较的值，Label 是展示文案</summary>
public sealed partial class FilterOptionViewModel : ObservableObject
{
    private readonly FilterGroupViewModel _group;

    public FilterOptionViewModel(FilterGroupViewModel group, string key, string label, int count, bool isChecked)
    {
        _group = group;
        Key = key;
        Label = label;
        // 直接写字段，避免构造时触发一次无谓的刷新
        _count = count;
        _isChecked = isChecked;
    }

    public string Key { get; }
    public string Label { get; }

    /// <summary>命中这个选项的收藏条数，只用来提示</summary>
    [ObservableProperty] private int _count;

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _group.NotifyChanged();
}
