using CommunityToolkit.Mvvm.ComponentModel;

namespace BookmarkVault.ViewModels;

/// <summary>视图快捷入口的类型</summary>
public enum NavKind
{
    All,
    Favorite,
    Unchecked,
    Broken
}

/// <summary>视图快捷入口条目</summary>
public sealed partial class NavItemViewModel : ObservableObject
{
    public NavKind Kind { get; init; }
    public string Name { get; set; } = string.Empty;
    public string Glyph { get; set; } = string.Empty;
    public string Color { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _count;

    /// <summary>筛选浮层里 chip 的选中态（与 SelectedNav 双向同步）</summary>
    [ObservableProperty]
    private bool _isSelected;

    public NavItemViewModel(NavKind kind, string name, string glyph, string color)
    {
        Kind = kind;
        Name = name;
        Glyph = glyph;
        Color = color;
    }

    public string CountText => Count > 0 ? Count.ToString() : string.Empty;
}
