using CommunityToolkit.Mvvm.ComponentModel;
using BookmarkVault.Models;

namespace BookmarkVault.ViewModels;

/// <summary>
/// 归类看板上的一个分类文件夹。
/// CategoryNode 本身不是 ObservableObject，拖拽高亮和条数都得另外挂在外面。
/// </summary>
public sealed partial class CategoryFolderViewModel : ObservableObject
{
    /// <summary>对应的分类；「未分类」文件夹这里是 null</summary>
    public CategoryNode? Category { get; }

    public bool IsUncategorized => Category == null;

    public string Name => Category?.Name ?? "未分类";

    public string Glyph => IsUncategorized ? "📥" : (string.IsNullOrEmpty(Category!.Glyph) ? "📁" : Category.Glyph);

    /// <summary>未分类用中性灰，不走分类自己的配色</summary>
    public string Color => Category?.Color ?? "#6B7A8C";

    /// <summary>当前可见范围内落在这个分类里的条数</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _count;

    /// <summary>拖拽悬停时高亮</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    public CategoryFolderViewModel(CategoryNode? category) => Category = category;

    public string CountText => Count == 1 ? "1 个" : $"{Count} 个";
}