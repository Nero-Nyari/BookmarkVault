using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BookmarkVault.Models;
using BookmarkVault.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BookmarkVault.ViewModels;

/// <summary>主区当前展示的内容</summary>
public enum MainViewMode
{
    Detail,
    Accounts,
    Blacklist,
    Settings
}

/// <summary>主界面视图模型</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly LibraryService _library;
    private readonly AccountVault _vault;
    private readonly IconCache _icons;
    private readonly ProbeService _probe;
    private readonly CoverFetcher _covers;

    private readonly List<BookmarkCardViewModel> _allCards = new();
    private CancellationTokenSource? _probeCts;
    private bool _loaded;

    public ObservableCollection<BookmarkCardViewModel> SidebarItems { get; } = new();
    public ObservableCollection<NavItemViewModel> PinnedNav { get; } = new();

    /// <summary>当前生效的那一套分类（项目分类或浏览器分类）</summary>
    public ObservableCollection<CategoryNode> Categories { get; } = new();

    /// <summary>右键菜单用：只有项目分类可以手工移动，所以始终列这一套</summary>
    public ObservableCollection<CategoryNode> ProjectCategories { get; } = new();

    /// <summary>归类看板上的文件夹：开头固定一个「未分类」，后面是全部项目分类</summary>
    public ObservableCollection<CategoryFolderViewModel> CategoryFolders { get; } = new();

    public const string ProjectCategoryLabel = "项目分类";
    public const string BrowserCategoryLabel = "浏览器分类";

    public IReadOnlyList<string> CategorySourceOptions { get; } =
        new[] { ProjectCategoryLabel, BrowserCategoryLabel };

    public ObservableCollection<string> SortModes { get; } = new()
    {
        "默认排序", "最近添加", "访问最多", "名称", "状态", "安全等级"
    };

    [ObservableProperty] private bool _isMessageOpen;
    [ObservableProperty] private string _messageTitle = string.Empty;
    [ObservableProperty] private string _messageBody = string.Empty;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private NavItemViewModel? _selectedNav;
    [ObservableProperty] private string _sortMode = "默认排序";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "正在启动…";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMax = 100;
    [ObservableProperty] private bool _isProgressVisible;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _totalBookmarks;
    [ObservableProperty] private int _probedCount;
    [ObservableProperty] private string _onlineRateText = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCard))]
    [NotifyPropertyChangedFor(nameof(IsOverview))]
    [NotifyPropertyChangedFor(nameof(IsCardWall))]
    [NotifyPropertyChangedFor(nameof(IsCoverWall))]
    [NotifyPropertyChangedFor(nameof(ShowHeroFallback))]
    private BookmarkCardViewModel? _selectedCard;

    /// <summary>主区当前展示的内容</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAuxView))]
    private MainViewMode _mainView = MainViewMode.Detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPromptOpen))]
    private BookmarkCardViewModel? _promptCard;

    public ObservableCollection<AccountRowViewModel> PromptAccounts { get; } = new();

    /// <summary>概览页的「最近打开」小卡片</summary>
    public ObservableCollection<BookmarkCardViewModel> RecentCards { get; } = new();

    /// <summary>卡片墙当前已铺开的卡片（只放前 N 条，滚动到底再追加）</summary>
    public ObservableCollection<BookmarkCardViewModel> WallCards { get; } = new();

    /// <summary>卡片墙每次追加的条数</summary>
    private const int WallPageSize = 120;

    /// <summary>卡片墙的数据源（当前筛选结果，追加时从这里往后取）</summary>
    private IReadOnlyList<BookmarkCardViewModel> _wallSource = Array.Empty<BookmarkCardViewModel>();

    /// <summary>还有没铺开的收藏</summary>
    public bool HasMoreWallCards => WallCards.Count < _wallSource.Count;

    /// <summary>卡片墙一条都没有</summary>
    public bool IsCardWallEmpty => WallCards.Count == 0;

    [RelayCommand]
    private void LoadMoreWallCards() => AppendWallPage();

    private void AppendWallPage()
    {
        foreach (var card in _wallSource.Skip(WallCards.Count).Take(WallPageSize))
            WallCards.Add(card);

        OnPropertyChanged(nameof(HasMoreWallCards));
        OnPropertyChanged(nameof(IsCardWallEmpty));
    }

    /// <summary>筛选条件变化后，卡片墙从第一页重新铺</summary>
    private void ResetCardWall(IReadOnlyList<BookmarkCardViewModel> filtered)
    {
        // 数据源没变（搜索、检测回填等触发的刷新）就不重铺，免得卡片被反复重建
        if (_wallSource.Count == filtered.Count && _wallSource.SequenceEqual(filtered))
        {
            if (WallCards.Count == 0) AppendWallPage();
            return;
        }

        _wallSource = filtered;
        WallCards.Clear();
        AppendWallPage();
    }

    /// <summary>详情页右栏的「相关收藏推荐」</summary>
    public ObservableCollection<BookmarkCardViewModel> RelatedCards { get; } = new();

    /// <summary>详情页右栏的「该站账号」</summary>
    public ObservableCollection<AccountRowViewModel> SelectedAccounts { get; } = new();

    public bool HasSelectedAccounts => SelectedAccounts.Count > 0;

    public bool HasRelatedCards => RelatedCards.Count > 0;

    [ObservableProperty] private bool _isCategoryEditorOpen;
    [ObservableProperty] private string _categoryEditorTitle = "新建分类";
    [ObservableProperty] private string _categoryDraftName = string.Empty;
    private CategoryNode? _editingCategory;

    public bool IsPromptOpen => PromptCard != null;

    public bool HasSelectedCard => SelectedCard != null;

    /// <summary>
    /// 主区当前实际展示的视图。
    /// 设置页里的「主界面」决定启动时用哪个（会落盘），底部栏按钮只改这个值，重启后回到设置里的那个。
    /// </summary>
    [ObservableProperty]
    private HomeViewMode _currentHomeView;

    partial void OnCurrentHomeViewChanged(HomeViewMode value)
    {
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsCardWall));
        OnPropertyChanged(nameof(IsCoverWall));
        OnPropertyChanged(nameof(IsOrganizeBoard));
        OnPropertyChanged(nameof(HomeViewButtonText));
        OnPropertyChanged(nameof(HomeViewButtonTooltip));
    }

    /// <summary>底部栏按钮循环切换的顺序</summary>
    private static readonly HomeViewMode[] HomeViewCycle =
        { HomeViewMode.CoverWall, HomeViewMode.CardWall, HomeViewMode.Overview, HomeViewMode.Organize };

    /// <summary>没有选中任何网址、且主区选的是概览页时</summary>
    public bool IsOverview => SelectedCard == null && CurrentHomeView == HomeViewMode.Overview;

    /// <summary>没有选中任何网址、且主区选的是简洁卡片墙时</summary>
    public bool IsCardWall => SelectedCard == null && CurrentHomeView == HomeViewMode.CardWall;

    /// <summary>没有选中任何网址、且主区选的是 Steam 封面墙时</summary>
    public bool IsCoverWall => SelectedCard == null && CurrentHomeView == HomeViewMode.CoverWall;

    /// <summary>没有选中任何网址、且主区选的是归类看板时</summary>
    public bool IsOrganizeBoard => SelectedCard == null && CurrentHomeView == HomeViewMode.Organize;

    /// <summary>底部栏切换按钮上的文字：当前视图</summary>
    public string HomeViewButtonText => CurrentHomeView switch
    {
        HomeViewMode.Overview => "📊 概览",
        HomeViewMode.CardWall => "🗂 卡片墙",
        HomeViewMode.Organize => "🗃 归类",
        _ => "🖼 封面墙"
    };

    public string HomeViewButtonTooltip =>
        $"当前显示{ShortHomeViewName(CurrentHomeView)}，点击切换到{ShortHomeViewName(NextHomeView(CurrentHomeView))}。" +
        "这里只是临时切换，启动时默认显示哪个由设置页的「主界面」决定。";

    /// <summary>底部栏视图切换按钮：在三种主区视图之间循环</summary>
    [RelayCommand]
    private void CycleHomeView() => GoToHomeView(NextHomeView(CurrentHomeView));

    /// <summary>直接切到指定主区视图（设置页里配的「切到某视图」快捷键也走这里）</summary>
    private void GoToHomeView(HomeViewMode mode)
    {
        CurrentHomeView = mode;

        // 正在看详情或停在设置页时顺手回到主区，让切换立刻看得见
        MainView = MainViewMode.Detail;
        SelectedCard = null;

        StatusText = "主区切换为" + ShortHomeViewName(CurrentHomeView);
    }

    private static HomeViewMode NextHomeView(HomeViewMode mode)
    {
        var index = Array.IndexOf(HomeViewCycle, mode);
        return HomeViewCycle[(index + 1) % HomeViewCycle.Length];
    }

    private static string ShortHomeViewName(HomeViewMode mode) => mode switch
    {
        HomeViewMode.Overview => "概览页",
        HomeViewMode.CardWall => "卡片墙",
        HomeViewMode.Organize => "归类看板",
        _ => "封面墙"
    };

    /// <summary>当前是否停在账号库 / 设置这类非详情页</summary>
    public bool IsAuxView => MainView != MainViewMode.Detail;

    // ---------- 隐私模式 ----------

    /// <summary>是否处于隐私模式：侧栏只显示被移入隐私的网站</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNavVisible))]
    [NotifyPropertyChangedFor(nameof(ShowFilterBar))]
    [NotifyPropertyChangedFor(nameof(FilterSummaryText))]
    [NotifyPropertyChangedFor(nameof(PrivacyButtonText))]
    [NotifyPropertyChangedFor(nameof(PrivacyButtonTooltip))]
    private bool _isPrivacyMode;

    /// <summary>隐私模式下把上方的导航项藏起来</summary>
    public bool IsNavVisible => !IsPrivacyMode;

    public string PrivacyButtonText => IsPrivacyMode ? "🕶 退出隐私" : "🕶 隐私";

    public string PrivacyButtonTooltip => IsPrivacyMode
        ? "退出隐私模式，回到常规列表"
        : "只看被移入隐私的网站；在侧栏网址上点右键可以「移入隐私」";

    /// <summary>这条书签在当前模式下是否可见（隐私的只看隐私，常规的只看非隐私）</summary>
    private bool InScope(BookmarkNode bookmark) => bookmark.IsPrivate == IsPrivacyMode;

    /// <summary>
    /// 这个分类在当前模式下是否可见。
    /// 浏览器分类是从 Edge 文件夹结构读出来的、两边共用，不参与隐私划分；只有项目分类分常规/隐私。
    /// </summary>
    private bool CategoryInScope(CategoryNode category) =>
        category.Kind != CategoryKind.Project || category.IsPrivate == IsPrivacyMode;

    [RelayCommand]
    private void TogglePrivacy()
    {
        IsPrivacyMode = !IsPrivacyMode;
        MainView = MainViewMode.Detail;
        // 选中的多半是另一侧的网站，退出/进入后不该继续停在详情页
        SelectedCard = null;
        Refresh();

        StatusText = IsPrivacyMode
            ? TotalCount == 0
                ? "隐私模式：还没有网站被移入隐私，在常规列表里右键即可添加"
                : $"隐私模式：{TotalCount} 条隐私收藏"
            : "已退出隐私模式";
    }

    [RelayCommand]
    private void MoveToPrivacy(BookmarkCardViewModel card)
    {
        if (card.Model.IsPrivate) return;
        card.Model.IsPrivate = true;
        _library.Save();

        if (SelectedCard == card) SelectedCard = null;
        Refresh();
        StatusText = $"已移入隐私：{card.Title}";
    }

    [RelayCommand]
    private void RemoveFromPrivacy(BookmarkCardViewModel card)
    {
        if (!card.Model.IsPrivate) return;
        card.Model.IsPrivate = false;
        _library.Save();

        if (SelectedCard == card) SelectedCard = null;
        Refresh();
        StatusText = $"已移出隐私：{card.Title}";
    }

    // ---------- 外观美化（只作用于网址详情页） ----------

    public const string HeroCroppedLabel = "原样式（240px 裁剪铺满）";
    public const string HeroFullLabel = "完整显示（按图片比例加高）";
    public const string CardSolidLabel = "原样式（不透明深色）";
    public const string CardTranslucentLabel = "半透明";
    public const string CardFrostedLabel = "毛玻璃";

    public IReadOnlyList<string> HeroDisplayOptions { get; } =
        new[] { HeroCroppedLabel, HeroFullLabel };

    public IReadOnlyList<string> CardMaterialOptions { get; } =
        new[] { CardSolidLabel, CardTranslucentLabel, CardFrostedLabel };

    /// <summary>Hero 图显示方式（美化面板的下拉框绑定这个）</summary>
    public string HeroDisplayLabel
    {
        get => Settings.HeroDisplay == HeroDisplayMode.FullHeight ? HeroFullLabel : HeroCroppedLabel;
        set
        {
            var mode = value == HeroFullLabel ? HeroDisplayMode.FullHeight : HeroDisplayMode.Cropped;
            if (Settings.HeroDisplay == mode) return;
            Settings.HeroDisplay = mode;
            _library.Save();

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFullHero));
            OnPropertyChanged(nameof(IsCroppedHero));
            OnPropertyChanged(nameof(ShowHeroFallback));
            StatusText = mode == HeroDisplayMode.FullHeight
                ? "详情页 Hero 图改为完整显示"
                : "详情页 Hero 图恢复原样式";
        }
    }

    // ---------- 主区默认视图（Steam 封面墙 / 简洁卡片墙 / 概览页 / 归类看板） ----------

    public const string HomeCoverWallLabel = "Steam 封面墙（2:3 封面大图）";
    public const string HomeCardWallLabel = "简洁卡片墙（图标 + 标题）";
    public const string HomeOverviewLabel = "概览页（统计 + 最近打开）";
    public const string HomeOrganizeLabel = "归类看板（拖拽到分类文件夹）";

    public IReadOnlyList<string> HomeViewOptions { get; } =
        new[] { HomeCoverWallLabel, HomeCardWallLabel, HomeOverviewLabel, HomeOrganizeLabel };

    /// <summary>未选中网址时主区显示什么（设置页「主界面」卡片的下拉框绑定这个）</summary>
    public string HomeViewLabel
    {
        get => Settings.HomeView switch
        {
            HomeViewMode.Overview => HomeOverviewLabel,
            HomeViewMode.CardWall => HomeCardWallLabel,
            HomeViewMode.Organize => HomeOrganizeLabel,
            _ => HomeCoverWallLabel
        };
        set
        {
            var mode = value switch
            {
                HomeOverviewLabel => HomeViewMode.Overview,
                HomeCardWallLabel => HomeViewMode.CardWall,
                HomeOrganizeLabel => HomeViewMode.Organize,
                _ => HomeViewMode.CoverWall
            };
            if (Settings.HomeView == mode) return;
            Settings.HomeView = mode;
            _library.Save();

            // 设置页定的是「启动时默认显示哪个」，改完顺手让主区也切过去，立刻能看见效果
            CurrentHomeView = mode;
            OnPropertyChanged();
            StatusText = "启动默认视图改为" + value.Split('（')[0];
        }
    }

    /// <summary>卡片材质（美化面板的下拉框绑定这个）</summary>
    public string CardMaterialLabel
    {
        get => Settings.CardMaterial switch
        {
            CardMaterial.Translucent => CardTranslucentLabel,
            CardMaterial.Frosted => CardFrostedLabel,
            _ => CardSolidLabel
        };
        set
        {
            var material = value switch
            {
                CardTranslucentLabel => CardMaterial.Translucent,
                CardFrostedLabel => CardMaterial.Frosted,
                _ => CardMaterial.Solid
            };
            if (Settings.CardMaterial == material) return;
            Settings.CardMaterial = material;
            _library.Save();

            OnPropertyChanged();
            OnPropertyChanged(nameof(CardFillColor));
            OnPropertyChanged(nameof(CardInnerFillColor));
            OnPropertyChanged(nameof(CardStrokeColor));
            OnPropertyChanged(nameof(IsFrostedCard));
            StatusText = $"详情页卡片材质：{value}";
        }
    }

    /// <summary>Hero 图是否按原始比例完整显示（图片可以延伸到下方卡片背后）</summary>
    public bool IsFullHero => Settings.HeroDisplay == HeroDisplayMode.FullHeight;

    public bool IsCroppedHero => !IsFullHero;

    /// <summary>完整显示且当前条目有封面图时，大图就是背景，不需要域名渐变兜底</summary>
    public bool ShowHeroFallback => !(IsFullHero && SelectedCard?.HasCover == true);

    /// <summary>毛玻璃：需要把背景里的 Hero 图整体模糊</summary>
    public bool IsFrostedCard => Settings.CardMaterial == CardMaterial.Frosted;

    /// <summary>详情页卡片底色</summary>
    public string CardFillColor => Settings.CardMaterial switch
    {
        CardMaterial.Translucent => "#B8111C27",
        CardMaterial.Frosted => "#8C111C27",
        _ => "#111C27"
    };

    /// <summary>详情页卡片里嵌套的小色块底色</summary>
    public string CardInnerFillColor => Settings.CardMaterial switch
    {
        CardMaterial.Translucent => "#7A16232F",
        CardMaterial.Frosted => "#5916232F",
        _ => "#16232F"
    };

    /// <summary>详情页卡片描边</summary>
    public string CardStrokeColor => Settings.CardMaterial switch
    {
        CardMaterial.Translucent => "#4D7FA8C8",
        CardMaterial.Frosted => "#59E8F4FF",
        _ => "#24384F"
    };

    // ---------- 侧栏筛选 ----------

    /// <summary>分类组里代表「未分类」的 Key</summary>
    private const string UncategorizedKey = "";

    // 封面筛选的三种状态（同时也是持久化用的 Key）
    private const string CoverCardKey = "卡片封面";
    private const string CoverDetailKey = "仅详情页封面";
    private const string CoverNoneKey = "无封面";

    // 来源筛选的两个选项（同时也是持久化用的 Key）
    private const string SourceEdgeKey = "Edge 同步";
    private const string SourceManualKey = "手动添加";

    /// <summary>一条收藏的封面状态：设置了专属卡片封面 / 只有详情页封面 / 都没有</summary>
    private static string CoverStateOf(BookmarkCardViewModel card)
    {
        if (!string.IsNullOrWhiteSpace(card.Model.CardCoverPath)) return CoverCardKey;
        return card.HasCover ? CoverDetailKey : CoverNoneKey;
    }

    private readonly FilterGroupViewModel _categoryFilter;
    private readonly FilterGroupViewModel _coverFilter;
    private readonly FilterGroupViewModel _stateFilter;
    private readonly FilterGroupViewModel _securityFilter;
    private readonly FilterGroupViewModel _typeFilter;
    private readonly FilterGroupViewModel _tldFilter;
    private readonly FilterGroupViewModel _sourceFilter;

    /// <summary>批量取消勾选时不逐条刷新</summary>
    private bool _suppressFilterRefresh;

    /// <summary>SelectedNav 与 chip IsSelected 双向同步时防递归</summary>
    private bool _navChipSync;

    public ObservableCollection<FilterGroupViewModel> FilterGroups { get; } = new();

    /// <summary>筛选浮层是否展开</summary>
    [ObservableProperty] private bool _isFilterOpen;

    public bool HasActiveFilter => !Settings.Filter.IsEmpty;

    public int ActiveFilterCount => Settings.Filter.Count;

    /// <summary>提示条可见性：有筛选条件，或当前视图不是「全部」</summary>
    public bool ShowFilterBar => HasActiveFilter || (!IsPrivacyMode && SelectedNav is { Kind: not NavKind.All });

    /// <summary>搜索框下方那一行提示（侧栏不再显示当前视图，这里是唯一持久指示器）</summary>
    public string FilterSummaryText
    {
        get
        {
            var view = !IsPrivacyMode && SelectedNav is { Kind: not NavKind.All } nav ? $"视图：{nav.Name} · " : "";
            return $"{view}已按 {ActiveFilterCount} 项条件筛选，列表剩 {TotalCount} 条";
        }
    }

    [RelayCommand]
    private void ToggleFilter() => IsFilterOpen = !IsFilterOpen;

    [RelayCommand]
    private void CloseFilter() => IsFilterOpen = false;

    [RelayCommand]
    private void ClearFilters()
    {
        _suppressFilterRefresh = true;
        foreach (var option in FilterGroups.SelectMany(g => g.Options)) option.IsChecked = false;
        if (!ReferenceEquals(SelectedNav, PinnedNav[0]))
        {
            _navChipSync = true;
            SelectedNav = PinnedNav[0]; // 视图一并还原为「全部」
            _navChipSync = false;
            SyncNavChips(SelectedNav);
        }
        SortMode = "默认排序"; // 排序一并还原：落盘在 OnSortModeChanged，刷新统一交给 OnFilterChanged
        _suppressFilterRefresh = false;
        OnFilterChanged();
        StatusText = "已清除全部筛选条件";
    }

    /// <summary>任意一个条件被勾选 / 取消后：落盘 + 刷新列表</summary>
    private void OnFilterChanged()
    {
        if (_suppressFilterRefresh) return;

        foreach (var group in FilterGroups) group.Persist(Settings.Filter);
        _library.Save();
        Refresh();
        StatusText = $"筛选：{ActiveFilterCount} 项条件";
    }

    /// <summary>
    /// 按当前可见范围统计出各组的候选项，并尽量保留已有的勾选状态。
    /// 候选项来自数据本身，所以「未检测 / 未评估 / 未识别」会自然成为单独一项。
    /// </summary>
    private void BuildFilterOptions()
    {
        var pool = _allCards.Where(c => InScope(c.Model)).ToList();

        // 封面三态：专属卡片封面 → 只有详情页封面 → 什么都没有
        MergeGroup(_coverFilter, new List<(string Key, string Label, int Count)>
        {
            (CoverCardKey, "卡片封面", pool.Count(c => CoverStateOf(c) == CoverCardKey)),
            (CoverDetailKey, "仅详情页封面", pool.Count(c => CoverStateOf(c) == CoverDetailKey)),
            (CoverNoneKey, "无封面", pool.Count(c => CoverStateOf(c) == CoverNoneKey))
        });

        var categories = _library.Database.Categories
            .Where(c => c.Kind == Settings.CategorySource && CategoryInScope(c))
            .OrderBy(c => c.Order).ThenBy(c => c.Name)
            .Select(c => (Key: c.Id, Label: c.Name, Count: pool.Count(x => x.Categories.Any(g => g.Id == c.Id))))
            .ToList();
        categories.Add((UncategorizedKey, "未分类", pool.Count(x => x.Categories.Count == 0)));
        MergeGroup(_categoryFilter, categories);

        MergeGroup(_sourceFilter, new List<(string Key, string Label, int Count)>
        {
            (SourceEdgeKey, SourceEdgeKey, pool.Count(c => SourceKeyOf(c) == SourceEdgeKey)),
            (SourceManualKey, SourceManualKey, pool.Count(c => SourceKeyOf(c) == SourceManualKey))
        });

        MergeGroup(_stateFilter, Tally(pool, c => c.StateText));
        MergeGroup(_securityFilter, Tally(pool, c => c.SecurityText));
        MergeGroup(_typeFilter, Tally(pool, c => c.TypeText));
        MergeGroup(_tldFilter, Tally(pool, c => UrlHelper.GetTld(c.Url)));
    }

    private static List<(string Key, string Label, int Count)> Tally(
        IEnumerable<BookmarkCardViewModel> pool, Func<BookmarkCardViewModel, string> selector)
    {
        var counts = new Dictionary<string, int>();
        foreach (var card in pool)
        {
            var value = selector(card);
            counts[value] = counts.TryGetValue(value, out var n) ? n + 1 : 1;
        }

        return counts
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.CurrentCulture)
            .Select(p => (Key: p.Key, Label: p.Key, Count: p.Value))
            .ToList();
    }

    /// <summary>候选项没变时只刷新计数；变了则重建，但保留已勾选状态</summary>
    private void MergeGroup(FilterGroupViewModel group, List<(string Key, string Label, int Count)> items)
    {
        var sameKeys = group.Options.Count == items.Count
                       && group.Options.Select(o => o.Key).SequenceEqual(items.Select(i => i.Key));

        if (sameKeys)
        {
            for (var i = 0; i < items.Count; i++) group.Options[i].Count = items[i].Count;
            return;
        }

        var checkedKeys = group.Options.Where(o => o.IsChecked).Select(o => o.Key).ToHashSet();
        group.Options.Clear();
        foreach (var (key, label, count) in items)
            group.Options.Add(new FilterOptionViewModel(group, key, label, count, checkedKeys.Contains(key)));
    }

    /// <summary>一条收藏是否满足当前全部筛选条件（组内多选是「或」，组间是「且」）</summary>
    private bool MatchesFilter(BookmarkCardViewModel card)
    {
        var filter = Settings.Filter;

        // 分类是「或」：挂在任意一个被勾选的分类下就算命中；一条分类都没有的走「未分类」
        if (filter.CategoryIds.Count > 0 &&
            !card.Categories.Any(c => filter.CategoryIds.Contains(c.Id)) &&
            !(card.Categories.Count == 0 && filter.CategoryIds.Contains(UncategorizedKey)))
            return false;
        if (filter.States.Count > 0 && !filter.States.Contains(card.StateText)) return false;
        if (filter.Securities.Count > 0 && !filter.Securities.Contains(card.SecurityText)) return false;
        if (filter.SiteTypes.Count > 0 && !filter.SiteTypes.Contains(card.TypeText)) return false;
        if (filter.Tlds.Count > 0 && !filter.Tlds.Contains(UrlHelper.GetTld(card.Url))) return false;
        if (filter.Covers.Count > 0 && !filter.Covers.Contains(CoverStateOf(card))) return false;
        if (filter.Sources.Count > 0 && !filter.Sources.Contains(SourceKeyOf(card))) return false;

        return true;
    }

    /// <summary>来源筛选用的 Key，同时也是筛选选项的文案</summary>
    private static string SourceKeyOf(BookmarkCardViewModel card) =>
        card.Model.Source == BookmarkSource.Manual ? SourceManualKey : SourceEdgeKey;

    // ---------- 分类体系切换 ----------

    /// <summary>当前用的是项目分类还是浏览器分类（设置页下拉框绑定这个）</summary>
    public string CategorySourceLabel
    {
        get => Settings.CategorySource == CategoryKind.Browser ? BrowserCategoryLabel : ProjectCategoryLabel;
        set => SetCategorySource(value == BrowserCategoryLabel ? CategoryKind.Browser : CategoryKind.Project);
    }

    public bool IsProjectCategorySource => Settings.CategorySource == CategoryKind.Project;

    public bool IsBrowserCategorySource => Settings.CategorySource == CategoryKind.Browser;

    private void SetCategorySource(CategoryKind kind)
    {
        if (Settings.CategorySource == kind) return;
        Settings.CategorySource = kind;
        _library.Save();

        OnPropertyChanged(nameof(CategorySourceLabel));
        OnPropertyChanged(nameof(IsProjectCategorySource));
        OnPropertyChanged(nameof(IsBrowserCategorySource));
        OnPropertyChanged(nameof(ShowCategorySourceHint));
        OnPropertyChanged(nameof(CategorySourceHint));

        Refresh();
        StatusText = kind == CategoryKind.Browser
            ? "已切换到浏览器分类（只读，跟随 Edge 文件夹）"
            : "已切换到项目分类";
    }

    /// <summary>当前分类体系下，这条书签归属的分类 Id（项目分类可以有多个，浏览器分类只有一个）</summary>
    private List<string> ActiveCategoryIds(BookmarkNode bookmark)
    {
        if (Settings.CategorySource == CategoryKind.Browser)
            return string.IsNullOrEmpty(bookmark.BrowserCategoryId)
                ? new List<string>()
                : new List<string> { bookmark.BrowserCategoryId! };

        return bookmark.CategoryIds;
    }

    public AccountsPanelViewModel AccountsPanel { get; }

    /// <summary>恶意网址库（黑名单）管理面板</summary>
    public BlacklistPanelViewModel BlacklistPanel { get; }

    /// <summary>快捷键设置：动作、当前按键、录制改键</summary>
    public ShortcutSettingsViewModel Shortcuts { get; }

    public AppSettings Settings => _library.Database.Settings;

    public MainViewModel(LibraryService library, AccountVault vault, IconCache icons,
        ProbeService probe, CoverFetcher covers, BlacklistService blacklist)
    {
        _library = library;
        _vault = vault;
        _icons = icons;
        _probe = probe;
        _covers = covers;
        AccountsPanel = new AccountsPanelViewModel(vault, () => _library.Save(), text => StatusText = text);
        BlacklistPanel = new BlacklistPanelViewModel(blacklist, text => StatusText = text);
        Shortcuts = new ShortcutSettingsViewModel(() => Settings, () => _library.Save(), text => StatusText = text);

        // 每次勾选直接写回 AppSettings.Filter 里的对应列表
        _coverFilter = new FilterGroupViewModel("封面", s => s.Covers, OnFilterChanged);
        _categoryFilter = new FilterGroupViewModel("分类", s => s.CategoryIds, OnFilterChanged);
        _stateFilter = new FilterGroupViewModel("可访问状态", s => s.States, OnFilterChanged);
        _securityFilter = new FilterGroupViewModel("安全等级", s => s.Securities, OnFilterChanged);
        _typeFilter = new FilterGroupViewModel("站点类型", s => s.SiteTypes, OnFilterChanged);
        _tldFilter = new FilterGroupViewModel("域名后缀", s => s.Tlds, OnFilterChanged);
        _sourceFilter = new FilterGroupViewModel("来源", s => s.Sources, OnFilterChanged);
        foreach (var group in new[] { _coverFilter, _categoryFilter, _sourceFilter, _stateFilter, _securityFilter, _typeFilter, _tldFilter })
            FilterGroups.Add(group);
    }

    public void Initialize()
    {
        if (_loaded) return;

        _library.Load();

        // 快捷键要等主库读完再铺，否则读到的还是默认设置
        Shortcuts.Reload();

        // 恢复上次选择的排序方式；库里的值无效就回退默认。
        // 放在 _loaded 置位之前：OnSortModeChanged 会因 !_loaded 直接返回，不落盘不提前刷新
        if (SortModes.Contains(Settings.SortMode)) SortMode = Settings.SortMode;
        else Settings.SortMode = SortModes[0];

        // 启动时用设置里的默认视图填充运行时视图
        CurrentHomeView = Settings.HomeView;

        _loaded = true;

        _allCards.Clear();
        foreach (var bookmark in _library.Database.Bookmarks.OrderBy(b => b.Title))
            _allCards.Add(new BookmarkCardViewModel(bookmark));

        BuildNav();
        RefreshBrowsers();
        Refresh();

        var lastSync = _library.Database.LastEdgeSyncAt;
        StatusText = lastSync == null
            ? "还没有导入书签，点击「同步 Edge」开始"
            : $"上次同步：{lastSync:yyyy-MM-dd HH:mm} · 共 {_allCards.Count} 条";

        // 首次启动且本地还是空的：自动导入一次，免得看到一个空界面
        if (_allCards.Count == 0 && !string.IsNullOrEmpty(_library.EdgeProfilePath))
        {
            _ = SyncEdgeAsync();
            return;
        }

        if (Settings.AutoProbeOnStartup)
            _ = ProbeAsync(GetFilteredCards().Take(60).ToList());
    }

    // ---------- 导航 ----------

    private void BuildNav()
    {
        PinnedNav.Add(new NavItemViewModel(NavKind.All, "全部", "▤", "#66C0F4"));
        PinnedNav.Add(new NavItemViewModel(NavKind.Favorite, "常用", "★", "#FFD54F"));
        PinnedNav.Add(new NavItemViewModel(NavKind.Unchecked, "待检测", "◌", "#8F98A0"));
        PinnedNav.Add(new NavItemViewModel(NavKind.Broken, "打不开", "⚠", "#EF5350"));

        // chip 勾选变化统一走这里，与 SelectedNav 保持互斥同步
        foreach (var item in PinnedNav)
            item.PropertyChanged += OnNavItemPropertyChanged;

        SelectedNav = PinnedNav[0];
    }

    private void RefreshCategories()
    {
        // 只列当前这一侧的分类，隐私分类不会漏到常规模式的筛选浮层和右键菜单里
        var kind = Settings.CategorySource;
        var source = _library.Database.Categories
            .Where(c => c.Kind == kind && CategoryInScope(c))
            .OrderBy(c => c.Order).ThenBy(c => c.Name).ToList();

        if (Categories.Count != source.Count ||
            Categories.Where((c, i) => !ReferenceEquals(c, source[i])).Any())
        {
            Categories.Clear();
            foreach (var category in source) Categories.Add(category);
        }

        // 右键菜单始终列项目分类（浏览器分类只读，不能手工移动）
        var projects = _library.Database.Categories
            .Where(c => c.Kind == CategoryKind.Project && CategoryInScope(c))
            .OrderBy(c => c.Order).ThenBy(c => c.Name).ToList();

        if (ProjectCategories.Count != projects.Count ||
            ProjectCategories.Where((c, i) => !ReferenceEquals(c, projects[i])).Any())
        {
            ProjectCategories.Clear();
            foreach (var category in projects) ProjectCategories.Add(category);
        }
    }

    /// <summary>
    /// 分类被删掉、或者切到隐私/常规另一侧之后，筛选里残留的「分类」勾选会让列表一条都匹配不上，
    /// 所以每次刷新前先把指向「当前这一侧不存在的分类」的勾选清掉。「未分类」始终有效。
    /// </summary>
    private void PruneStaleCategoryFilter()
    {
        var ids = Settings.Filter.CategoryIds;
        if (ids.Count == 0) return;

        var valid = _library.Database.Categories.Where(CategoryInScope).Select(c => c.Id).ToHashSet();
        ids.RemoveAll(id => id != UncategorizedKey && !valid.Contains(id));
    }

    /// <summary>按当前分类体系，把分类对象贴到每张卡片上（详情页标签、搜索、相关推荐都靠它）</summary>
    private void LinkCardCategories()
    {
        var map = new Dictionary<string, CategoryNode>();
        foreach (var category in _library.Database.Categories) map[category.Id] = category;

        foreach (var card in _allCards)
        {
            // 一个网址可以有多个分类；跨侧的归属只在原来那一侧生效，这里不贴标签，
            // 筛选里也就自然算成「未分类」
            card.SetCategories(ActiveCategoryIds(card.Model)
                .Select(id => map.TryGetValue(id, out var category) ? category : null)
                .Where(category => category != null && CategoryInScope(category))
                .Select(category => category!)
                .Distinct());
        }
    }

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        if (_navChipSync) return; // 由触发方负责同步 chips 并刷新

        SyncNavChips(value);
        ActivateNav();
    }

    /// <summary>让浮层里的 chip 选中态与 SelectedNav 保持一致（互斥单选）</summary>
    private void SyncNavChips(NavItemViewModel? selected)
    {
        _navChipSync = true;
        foreach (var item in PinnedNav) item.IsSelected = ReferenceEquals(item, selected);
        _navChipSync = false;
    }

    /// <summary>切换视图后回到详情页并刷新列表</summary>
    private void ActivateNav()
    {
        MainView = MainViewMode.Detail;
        if (_suppressFilterRefresh) return; // 清除全部时刷新统一交给 OnFilterChanged

        Refresh();
        OnPropertyChanged(nameof(ShowFilterBar));
        OnPropertyChanged(nameof(FilterSummaryText));
    }

    /// <summary>浮层里 chip 被点击：选中它，或再点一次取消回到「全部」</summary>
    private void OnNavItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NavItemViewModel.IsSelected) || sender is not NavItemViewModel item) return;
        if (_navChipSync || item.IsSelected == ReferenceEquals(SelectedNav, item)) return;

        _navChipSync = true;
        SelectedNav = item.IsSelected ? item : PinnedNav[0]; // 取消勾选时回「全部」
        _navChipSync = false;
        SyncNavChips(SelectedNav);
        ActivateNav();
    }

    partial void OnSelectedCardChanged(BookmarkCardViewModel? value)
    {
        if (value != null) MainView = MainViewMode.Detail;
        RefreshSelection();
    }

    /// <summary>切换选中网址后，刷新右侧的账号与推荐</summary>
    private void RefreshSelection()
    {
        SelectedAccounts.Clear();
        if (SelectedCard != null)
            foreach (var entry in _vault.Find(SelectedCard.Url))
                SelectedAccounts.Add(new AccountRowViewModel(entry));
        OnPropertyChanged(nameof(HasSelectedAccounts));

        RelatedCards.Clear();
        if (SelectedCard != null)
            foreach (var card in FindRelated(SelectedCard)) RelatedCards.Add(card);
        OnPropertyChanged(nameof(HasRelatedCards));
    }

    /// <summary>相关推荐：同域名优先，不够再补同分类，排除自己；只在本模式可见的网站里找</summary>
    private IEnumerable<BookmarkCardViewModel> FindRelated(BookmarkCardViewModel current)
    {
        const int max = 6;
        var picked = new List<BookmarkCardViewModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.Url };
        var pool = _allCards.Where(c => InScope(c.Model)).ToList();

        foreach (var card in pool.Where(c => c != current)
                     .Where(c => string.Equals(c.Domain, current.Domain, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(c => c.VisitCount))
        {
            if (seen.Add(card.Url)) picked.Add(card);
            if (picked.Count >= max) return picked;
        }

        // 多分类后：和当前这条共享任意一个分类的都算相关
        var categoryIds = current.Categories.Select(c => c.Id).ToHashSet();
        if (categoryIds.Count > 0)
        {
            foreach (var card in pool.Where(c => c != current && c.Categories.Any(g => categoryIds.Contains(g.Id)))
                         .OrderByDescending(c => c.VisitCount))
            {
                if (seen.Add(card.Url)) picked.Add(card);
                if (picked.Count >= max) break;
            }
        }

        return picked;
    }

    /// <summary>刷新概览页的统计与「最近打开」</summary>
    private void RefreshOverview()
    {
        var scope = _allCards.Where(c => InScope(c.Model)).ToList();

        TotalBookmarks = scope.Count;
        ProbedCount = scope.Count(c => c.Probe != null && c.Probe.State != ProbeState.Pending);

        var online = scope.Count(c => c.Probe?.State is ProbeState.Online or ProbeState.Redirected);
        OnlineRateText = ProbedCount == 0 ? "—" : $"{online * 100.0 / ProbedCount:F0}%";

        RecentCards.Clear();
        foreach (var card in scope
                     .Where(c => c.Model.LastOpenedAt != null)
                     .OrderByDescending(c => c.Model.LastOpenedAt)
                     .Take(8))
            RecentCards.Add(card);
    }

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnSortModeChanged(string value)
    {
        if (!_loaded) return; // 启动时恢复上次的排序，不用落盘或刷新

        Settings.SortMode = value;
        _library.Save();
        if (_suppressFilterRefresh) return; // 清除全部时刷新统一交给 OnFilterChanged

        Refresh();
    }

    // ---------- 过滤与分页 ----------

    private IEnumerable<BookmarkCardViewModel> GetFilteredCards()
    {
        // 隐私的网站只在隐私模式出现，反之亦然
        IEnumerable<BookmarkCardViewModel> query = _allCards.Where(c => InScope(c.Model));

        // 隐私模式下导航项是隐藏的，不再按它过滤
        if (!IsPrivacyMode && SelectedNav is { } nav)
        {
            query = nav.Kind switch
            {
                NavKind.Favorite => query.Where(c => c.Model.IsFavorite),
                NavKind.Unchecked => query.Where(c => c.Probe == null),
                NavKind.Broken => query.Where(c => c.Probe?.State is ProbeState.Offline
                    or ProbeState.Error or ProbeState.Timeout),
                _ => query
            };
        }

        var keyword = SearchText?.Trim();
        if (!string.IsNullOrEmpty(keyword))
            query = query.Where(c => c.Matches(keyword));

        if (HasActiveFilter)
            query = query.Where(MatchesFilter);

        query = SortMode switch
        {
            "最近添加" => query.OrderByDescending(c => c.Model.EdgeDateAdded ?? c.Model.AddedAt),
            "访问最多" => query.OrderByDescending(c => c.Model.VisitCount),
            "名称" => query.OrderBy(c => c.Title, StringComparer.CurrentCulture),
            "状态" => query.OrderBy(c => c.Probe?.State ?? ProbeState.Unknown)
                .ThenBy(c => c.Title, StringComparer.CurrentCulture),
            "安全等级" => query.OrderByDescending(c => c.Probe?.Security ?? SecurityLevel.Unknown)
                .ThenBy(c => c.Title, StringComparer.CurrentCulture),
            _ => query
        };

        return query;
    }

    [RelayCommand]
    private void Refresh()
    {
        PruneStaleCategoryFilter();
        LinkCardCategories();
        var filtered = GetFilteredCards().ToList();
        TotalCount = filtered.Count;

        SyncSidebarItems(filtered);
        ResetCardWall(filtered);
        RefreshCategories();
        BuildCategoryFolders();
        RefreshCounts();
        RefreshOverview();
        RefreshSelection();
        BuildFilterOptions();
        OnPropertyChanged(nameof(IsNormalListEmpty));
        OnPropertyChanged(nameof(IsPrivacyListEmpty));
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(ActiveFilterCount));
        OnPropertyChanged(nameof(ShowFilterBar));
        OnPropertyChanged(nameof(FilterSummaryText));
    }

    /// <summary>按差异更新侧栏列表，避免整体清空把选中项和详情页一起重置掉</summary>
    private void SyncSidebarItems(IReadOnlyList<BookmarkCardViewModel> filtered)
    {
        var keep = new HashSet<BookmarkCardViewModel>(filtered);
        for (var i = SidebarItems.Count - 1; i >= 0; i--)
            if (!keep.Contains(SidebarItems[i])) SidebarItems.RemoveAt(i);

        for (var i = 0; i < filtered.Count; i++)
        {
            var item = filtered[i];
            var index = SidebarItems.IndexOf(item);
            if (index < 0) SidebarItems.Insert(i, item);
            else if (index != i) SidebarItems.Move(index, i);
        }
    }

    /// <summary>常规模式下没有可显示的收藏</summary>
    public bool IsNormalListEmpty => !IsPrivacyMode && TotalCount == 0;

    /// <summary>隐私模式下一个网站都还没移进来</summary>
    public bool IsPrivacyListEmpty => IsPrivacyMode && TotalCount == 0;

    private void RefreshCounts()
    {
        // 导航计数也不把隐私网站算进去
        var scope = _allCards.Where(c => InScope(c.Model)).ToList();
        foreach (var nav in PinnedNav)
        {
            nav.Count = nav.Kind switch
            {
                NavKind.All => scope.Count,
                NavKind.Favorite => scope.Count(c => c.Model.IsFavorite),
                NavKind.Unchecked => scope.Count(c => c.Probe == null),
                NavKind.Broken => scope.Count(c => c.Probe?.State is ProbeState.Offline
                    or ProbeState.Error or ProbeState.Timeout),
                _ => nav.Count
            };
        }
    }

    // ---------- 同步与检测 ----------

    [RelayCommand]
    private async Task SyncEdgeAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在读取 Edge 书签…";
        try
        {
            var (added, updated) = await Task.Run(() => _library.SyncEdge());

            RebuildCards();
            SelectedCard = null;
            Refresh();
            StatusText = added == 0 && updated == 0
                ? _library.LastSyncMessage ?? "同步完成，没有变化"
                : $"同步完成：新增 {added}，更新 {updated}，共 {_allCards.Count} 条";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>按库里的书签重建卡片集合（同步完成、手动添加之后都要走一遍），已有检测结果按 URL 带过去</summary>
    private void RebuildCards()
    {
        var probes = _allCards.ToDictionary(c => c.Url, c => c.Probe, StringComparer.OrdinalIgnoreCase);
        _allCards.Clear();
        foreach (var bookmark in _library.Database.Bookmarks.OrderBy(b => b.Title))
        {
            var card = new BookmarkCardViewModel(bookmark);
            if (probes.TryGetValue(bookmark.Url, out var probe)) card.Probe = probe;
            _allCards.Add(card);
        }
    }

    [RelayCommand]
    private async Task ProbeAllAsync()
    {
        var targets = _allCards
            .Where(c => InScope(c.Model))
            .Where(c => c.Probe == null || !c.Probe.IsFresh(Settings.CacheHours))
            .ToList();
        if (targets.Count == 0)
        {
            ShowMessage("无需检测", "所有收藏都在缓存有效期内，可以先把缓存时长改短再试。");
            return;
        }
        await ProbeAsync(targets);
    }

    public void ShowMessage(string title, string body)
    {
        MessageTitle = title;
        MessageBody = body;
        IsMessageOpen = true;
    }

    [RelayCommand]
    private void CloseMessage() => IsMessageOpen = false;

    [RelayCommand]
    private Task ProbeVisibleAsync() => ProbeAsync(SidebarItems.ToList());

    [RelayCommand]
    private Task ProbeOneAsync(BookmarkCardViewModel? card)
        => card == null ? Task.CompletedTask : ProbeAsync(new[] { card });

    [RelayCommand]
    private void CancelProbe() => _probeCts?.Cancel();

    private async Task ProbeAsync(IReadOnlyList<BookmarkCardViewModel> targets)
    {
        if (IsBusy || targets.Count == 0) return;

        IsBusy = true;
        _probeCts = new CancellationTokenSource();
        var token = _probeCts.Token;
        IsProgressVisible = true;
        ProgressMax = targets.Count;
        ProgressValue = 0;

        foreach (var card in targets) card.Probe = new ProbeRecord { State = ProbeState.Pending };

        try
        {
            var progress = new Progress<ProbeProgress>(p =>
            {
                ProgressValue = p.Done;
                StatusText = $"检测中 {p.Done}/{p.Total}：{UrlHelper.GetDomain(p.CurrentUrl)}";
            });

            var urls = targets.Select(t => t.Url).ToList();
            var results = await _probe.ProbeManyAsync(urls, Settings.ProbeTimeoutSeconds,
                Settings.ProbeConcurrency, progress, token);

            var map = results.ToDictionary(r => r.Url, StringComparer.OrdinalIgnoreCase);
            foreach (var card in targets)
            {
                if (map.TryGetValue(card.Url, out var record))
                {
                    card.Probe = record;
                    _library.ApplyProbe(record);
                }
                else
                {
                    card.Probe = null;
                }
            }
            _library.SaveCache();

            var ok = results.Count(r => r.State is ProbeState.Online or ProbeState.Redirected);
            var bad = results.Count(r => r.State is ProbeState.Offline or ProbeState.Error or ProbeState.Timeout);
            StatusText = token.IsCancellationRequested
                ? $"已取消：完成 {results.Count} 条"
                : $"检测完成：可访问 {ok}，异常 {bad}，共 {results.Count} 条";
        }
        catch (Exception ex)
        {
            JsonStore.Log("批量检测失败：" + ex);
            StatusText = "检测过程出错：" + ex.Message;
        }
        finally
        {
            IsProgressVisible = false;
            IsBusy = false;
            _probeCts?.Dispose();
            _probeCts = null;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task DownloadIconsAsync()
    {
        var targets = SidebarItems.Where(c => !c.HasIcon).ToList();
        if (targets.Count == 0)
        {
            StatusText = "当前列表的图标都已缓存";
            return;
        }

        IsBusy = true;
        IsProgressVisible = true;
        ProgressMax = targets.Count;
        ProgressValue = 0;
        var cts = new CancellationTokenSource();

        try
        {
            var done = 0;
            foreach (var card in targets)
            {
                if (cts.IsCancellationRequested) break;
                StatusText = $"抓取图标 {++done}/{targets.Count}：{card.Domain}";
                ProgressValue = done;
                if (await _icons.EnsureIconAsync(card.Url, cts.Token))
                    card.IconReady = true;
            }
            StatusText = $"图标抓取完成，已缓存 {targets.Count(c => c.HasIcon)} 个";
        }
        catch (Exception ex)
        {
            JsonStore.Log("抓取图标失败：" + ex.Message);
        }
        finally
        {
            cts.Dispose();
            IsProgressVisible = false;
            IsBusy = false;
        }
    }

    // ---------- 自动抓封面 ----------

    /// <summary>单次最多抓多少条，避免一按就对着上千个站点发请求</summary>
    private const int MaxCoverFetchPerRun = 100;

    /// <summary>抓封面的并发数，比检测低一些：每条要下首页 + 一张图</summary>
    private const int CoverFetchConcurrency = 4;

    /// <summary>
    /// 这条能不能自动抓：详情页封面和卡片封面都必须「还没有」或「本来就是自动抓的」。
    /// 手动导入或手工裁剪过的封面一律不动。
    /// </summary>
    private static bool CanAutoFillCover(BookmarkCardViewModel card)
    {
        var model = card.Model;
        var detailOk = string.IsNullOrWhiteSpace(model.CoverPath) || model.CoverAutoFetched;
        var coverOk = string.IsNullOrWhiteSpace(model.CardCoverPath) || model.CardCoverAutoFetched;
        return detailOk && coverOk;
    }

    /// <summary>把抓到的封面写回模型；新旧路径不一样才删旧文件，免得删掉刚写好的那张</summary>
    private static void ApplyFetchedCover(BookmarkCardViewModel card, FetchedCover cover)
    {
        var model = card.Model;

        if (!string.Equals(model.CoverPath, cover.CoverPath, StringComparison.OrdinalIgnoreCase))
            CoverStore.Delete(model.CoverPath);
        model.CoverPath = cover.CoverPath;
        model.CoverAutoFetched = true;
        card.ReloadCover();

        if (!string.Equals(model.CardCoverPath, cover.CardCoverPath, StringComparison.OrdinalIgnoreCase))
            CoverStore.Delete(model.CardCoverPath);
        model.CardCoverPath = cover.CardCoverPath;
        model.CardCoverAutoFetched = true;
        card.ReloadCardCover();
    }

    /// <summary>为当前列表里缺封面的网址自动抓封面，抓到后连卡片封面一起补上</summary>
    [RelayCommand]
    private async Task FetchCoversAsync()
    {
        if (IsBusy) return;

        var pending = SidebarItems.Where(CanAutoFillCover).ToList();
        if (pending.Count == 0)
        {
            StatusText = "当前列表没有需要抓封面的网址";
            return;
        }

        var targets = pending.Take(MaxCoverFetchPerRun).ToList();
        var skipped = pending.Count - targets.Count;

        IsBusy = true;
        _probeCts = new CancellationTokenSource();
        var token = _probeCts.Token;
        IsProgressVisible = true;
        ProgressMax = targets.Count;
        ProgressValue = 0;

        try
        {
            var progress = new Progress<CoverFetchProgress>(p =>
            {
                ProgressValue = p.Done;
                StatusText = $"抓取封面 {p.Done}/{p.Total}：{UrlHelper.GetDomain(p.CurrentUrl)}";
            });

            var list = targets.Select(c => new CoverTarget(c.Model.Id, c.Url)).ToList();
            var fetched = await _covers.FetchManyAsync(list, Settings.ProbeTimeoutSeconds,
                CoverFetchConcurrency, progress, token);

            foreach (var card in targets)
                if (fetched.TryGetValue(card.Model.Id, out var cover))
                    ApplyFetchedCover(card, cover);

            _library.Save();

            if (token.IsCancellationRequested)
                StatusText = $"已取消：抓到 {fetched.Count} 张封面";
            else if (skipped > 0)
                StatusText = $"抓取完成：抓到 {fetched.Count} 张封面，还有 {skipped} 条超单次上限，可以再点一次";
            else
                StatusText = $"抓取完成：抓到 {fetched.Count} 张封面";
        }
        catch (Exception ex)
        {
            JsonStore.Log("抓取封面失败：" + ex);
            StatusText = "抓取封面出错：" + ex.Message;
        }
        finally
        {
            IsProgressVisible = false;
            IsBusy = false;
            _probeCts?.Dispose();
            _probeCts = null;
            RefreshAfterCoverChange();
        }
    }

    // ---------- 打开与账号 ----------

    [RelayCommand]
    private void Open(BookmarkCardViewModel? card)
    {
        if (card == null) return;
        _launchBrowser = null;   // 按钮打开始终走设置里选的那个浏览器
        OpenCore(card);
    }

    /// <summary>右键「用其他浏览器打开」：只影响这一次，不改设置里的选择</summary>
    public void OpenWith(BookmarkCardViewModel card, string? browserPath)
    {
        _launchBrowser = browserPath;
        OpenCore(card);
    }

    private void OpenCore(BookmarkCardViewModel card)
    {
        card.Model.LastOpenedAt = DateTime.Now;
        card.Model.VisitCount++;
        card.RaiseStatsChanged();
        _library.Save();
        RefreshOverview();

        if (Settings.PromptAccountOnOpen && _vault.HasAccount(card.Url))
        {
            PromptAccounts.Clear();
            foreach (var entry in _vault.Find(card.Url))
                PromptAccounts.Add(new AccountRowViewModel(entry));
            PromptCard = card;
            return;   // 等用户在提示里确认，临时浏览器要留到那时候
        }
        LaunchUrl(card.Url);
        _launchBrowser = null;
    }

    /// <summary>从概览 / 相关推荐里点一条，直接切到它的详情页</summary>
    [RelayCommand]
    private void SelectCard(BookmarkCardViewModel? card)
    {
        if (card == null) return;
        SelectedCard = card;
        MainView = MainViewMode.Detail;
    }

    [RelayCommand]
    private void ConfirmPromptOpen()
    {
        var card = PromptCard;
        PromptCard = null;
        if (card != null) LaunchUrl(card.Url);
        _launchBrowser = null;
    }

    [RelayCommand]
    private void ClosePrompt()
    {
        PromptCard = null;
        _launchBrowser = null;
    }

    [RelayCommand]
    private void PromptEditAccount()
    {
        var card = PromptCard;
        PromptCard = null;
        if (card == null) return;
        AccountsPanel.BeginEdit(null, card.Domain);
        OpenAccounts();
    }

    [RelayCommand]
    private void PromptToggleReveal(AccountRowViewModel? row)
    {
        if (row != null) row.Revealed = !row.Revealed;
    }

    [RelayCommand]
    private void PromptCopy(AccountRowViewModel? row)
    {
        if (row == null) return;
        try
        {
            var text = row.Revealed
                ? AccountVault.Decrypt(row.Model.PasswordCipher)
                : row.Model.Username;
            if (string.IsNullOrEmpty(text))
            {
                StatusText = "这条记录没有可复制的内容";
                return;
            }
            System.Windows.Clipboard.SetText(text);
            StatusText = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText = "复制失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenAccounts()
    {
        AccountsPanel.Reload();
        MainView = MainViewMode.Accounts;
    }

    [RelayCommand]
    private void OpenBlacklist()
    {
        BlacklistPanel.Reload();
        MainView = MainViewMode.Blacklist;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        MainView = MainViewMode.Settings;
        // 打开设置页时重扫一次，新装的浏览器能立刻出现在下拉里
        RefreshBrowsers();
    }

    [RelayCommand]
    private void BackToDetail() => MainView = MainViewMode.Detail;

    // ---------- 打开方式（用哪个浏览器打开） ----------

    /// <summary>设置页下拉框的固定首项：不指定浏览器，交给系统默认</summary>
    public const string FollowSystemBrowserLabel = "跟随系统默认浏览器";

    /// <summary>本次打开临时指定的浏览器（右键「用其他浏览器打开」），为空表示用设置里选的</summary>
    private string? _launchBrowser;

    private List<BrowserEntry> _browsers = new();

    /// <summary>扫到的浏览器（右键的「用其他浏览器打开」用这份清单）</summary>
    public IReadOnlyList<BrowserEntry> Browsers => _browsers;

    /// <summary>设置页下拉框的选项：跟随系统默认 + 每个扫到的浏览器。缓存住，只有内容真变了才通知</summary>
    private List<string> _browserOptions = new() { FollowSystemBrowserLabel };
    public IReadOnlyList<string> BrowserOptions => _browserOptions;

    /// <summary>设置页下拉框的选中项（首次进来是「跟随系统默认浏览器」）</summary>
    public string BrowserLabel
    {
        get => CurrentBrowserLabel();
        set
        {
            // 下拉框在 ItemsSource 被重建时，会先把选中项置空再回填；认不出的值也一样。
            // 这两种回调都得忽略，否则会把设置误清掉、还会和下面的通知互相触发成死循环
            if (string.IsNullOrEmpty(value) || value == CurrentBrowserLabel()) return;

            if (value == FollowSystemBrowserLabel) SetBrowserPath(null);
            else
            {
                var entry = _browsers.FirstOrDefault(b => b.Name == value);
                if (entry == null) return;
                SetBrowserPath(entry.Path, entry.Name);
            }
        }
    }

    private string CurrentBrowserLabel() => string.IsNullOrWhiteSpace(Settings.BrowserPath)
        ? FollowSystemBrowserLabel
        : Settings.BrowserName ?? Settings.BrowserPath!;

    /// <summary>当前选择的补充说明，顺便提示路径已经不存在的情况</summary>
    public string BrowserPathHint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Settings.BrowserPath)) return "当前把网址交给系统默认浏览器打开。";
            return File.Exists(Settings.BrowserPath)
                ? $"当前固定在：{Settings.BrowserPath}"
                : $"设置里的路径已经不存在（{Settings.BrowserPath}），暂时回退到系统默认浏览器。";
        }
    }

    /// <summary>重新扫描系统里的浏览器（启动、打开设置页、手动指定之后都会调）</summary>
    public void RefreshBrowsers()
    {
        _browsers = BrowserCatalog.Detect().ToList();
        OnPropertyChanged(nameof(Browsers));
        RefreshBrowserOptions();
        OnPropertyChanged(nameof(BrowserLabel));
        OnPropertyChanged(nameof(BrowserPathHint));
    }

    private void RefreshBrowserOptions()
    {
        var names = new List<string> { FollowSystemBrowserLabel };
        names.AddRange(_browsers.Select(b => b.Name));

        // 手动指定的 exe 可能不在扫描结果里，补一项免得下拉框选了个空白
        var current = CurrentBrowserLabel();
        if (!names.Contains(current)) names.Add(current);

        // 内容没变就别通知：下拉框每次重建 ItemsSource 都会把选中项置空再回填，白折腾一轮
        if (_browserOptions.SequenceEqual(names)) return;
        _browserOptions = names;
        OnPropertyChanged(nameof(BrowserOptions));
    }

    /// <summary>指定打开网址用的浏览器；path 为空表示跟随系统默认</summary>
    public void SetBrowserPath(string? path, string? name = null)
    {
        Settings.BrowserPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        Settings.BrowserName = Settings.BrowserPath == null
            ? null
            : string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(Settings.BrowserPath) : name;

        // 手动指定的 exe 也塞进清单里，右键菜单里就能直接点它
        if (Settings.BrowserPath != null &&
            !_browsers.Any(b => string.Equals(b.Path, Settings.BrowserPath, StringComparison.OrdinalIgnoreCase)))
        {
            _browsers = _browsers
                .Append(new BrowserEntry(Settings.BrowserName!, Settings.BrowserPath))
                .OrderBy(b => b.Name, StringComparer.CurrentCulture)
                .ToList();
            OnPropertyChanged(nameof(Browsers));
        }

        _library.Save();
        RefreshBrowserOptions();
        OnPropertyChanged(nameof(BrowserLabel));
        OnPropertyChanged(nameof(BrowserPathHint));

        StatusText = Settings.BrowserPath == null
            ? "已改为跟随系统默认浏览器"
            : $"已改用「{Settings.BrowserName}」打开网址";
    }

    /// <summary>打开网址：优先用这次临时指定的浏览器，其次用设置里选的，都没有就交给系统默认</summary>
    private void LaunchUrl(string url)
    {
        var browser = _launchBrowser ?? Settings.BrowserPath;

        try
        {
            if (!string.IsNullOrWhiteSpace(browser) && File.Exists(browser))
                // 直接把网址当参数传给浏览器的 exe；浏览器已经开着时会开新标签页
                Process.Start(new ProcessStartInfo(browser) { Arguments = $"\"{url}\"", UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            JsonStore.Log($"打开网址失败 {url}：{ex.Message}");
            StatusText = "打开网址失败，检查一下设置里的浏览器路径";
        }
    }

    [RelayCommand]
    private void EditAccount(BookmarkCardViewModel? card)
    {
        AccountsPanel.BeginEdit(null, card?.Domain);
        OpenAccounts();
    }

    [RelayCommand]
    private void ToggleFavorite(BookmarkCardViewModel? card)
    {
        if (card == null) return;
        card.Model.IsFavorite = !card.Model.IsFavorite;
        card.RaiseFavoriteChanged();
        _library.Save();
        RefreshCounts();
        Refresh();
    }

    [RelayCommand]
    private void CopyUrl(BookmarkCardViewModel? card)
    {
        if (card == null) return;
        try
        {
            System.Windows.Clipboard.SetText(card.Url);
            StatusText = "已复制链接：" + card.Url;
        }
        catch (Exception ex)
        {
            JsonStore.Log("复制失败：" + ex.Message);
        }
    }

    // ---------- 封面图 ----------

    /// <summary>导入一张本地图片作为该网址的封面（复制进数据目录）</summary>
    public void ImportCover(BookmarkCardViewModel card, string sourceFile)
    {
        try
        {
            var previous = card.Model.CoverPath;
            var relative = CoverStore.Import(card.Model.Id, sourceFile);
            card.Model.CoverPath = relative;
            card.Model.CoverAutoFetched = false; // 手工导入的封面，自动抓取不再覆盖
            if (!string.IsNullOrEmpty(previous) &&
                !string.Equals(previous, relative, StringComparison.OrdinalIgnoreCase))
                CoverStore.Delete(previous);

            card.ReloadCover();
            card.ReloadCardCover(); // 卡片没有专属封面时要跟着详情页封面走
            _library.Save();
            RefreshAfterCoverChange();
            StatusText = "已更新封面图：" + card.Title;
        }
        catch (Exception ex)
        {
            JsonStore.Log("导入封面图失败：" + ex.Message);
            ShowMessage("导入封面图失败", ex.Message);
        }
    }

    /// <summary>封面变化后：正在按封面筛选就重过一遍列表，否则至少把筛选计数刷新掉</summary>
    private void RefreshAfterCoverChange()
    {
        if (Settings.Filter.Covers.Count > 0) Refresh();
        else BuildFilterOptions();
    }

    /// <summary>移除封面：先摘卡片专属封面，没有的话再摘详情页封面</summary>
    public void RemoveCover(BookmarkCardViewModel card)
    {
        // 卡片专属封面优先移除，卡片会自动回退到沿用详情页封面
        if (!string.IsNullOrWhiteSpace(card.Model.CardCoverPath))
        {
            CoverStore.Delete(card.Model.CardCoverPath);
            card.Model.CardCoverPath = null;
            card.Model.CardCoverAutoFetched = false;
            card.ReloadCardCover();
            _library.Save();
            RefreshAfterCoverChange();
            StatusText = "已移除卡片封面，改用详情页封面：" + card.Title;
            return;
        }

        CoverStore.Delete(card.Model.CoverPath);
        card.Model.CoverPath = null;
        card.Model.CoverAutoFetched = false;
        card.ReloadCover();
        _library.Save();
        RefreshAfterCoverChange();
        StatusText = "已移除详情页封面：" + card.Title;
    }

    // ---------- 卡片封面：选来源 → 裁剪成 2:3 ----------

    /// <summary>取景框尺寸，固定 2:3</summary>
    public const double CropWindowWidth = 300;
    public const double CropWindowHeight = 450;

    /// <summary>裁剪结果文件名的后缀，和详情页封面区分开</summary>
    private const string CardCoverSuffix = "_card";

    /// <summary>正在设置封面的卡片</summary>
    private BookmarkCardViewModel? _coverTarget;

    /// <summary>裁剪用的原图（全分辨率）</summary>
    private BitmapSource? _cropBitmap;

    /// <summary>相对「铺满取景框」的额外缩放倍数，1 表示刚好铺满</summary>
    private double _cropScale = 1;

    /// <summary>铺满取景框时的基准尺寸</summary>
    private double _cropBaseWidth;
    private double _cropBaseHeight;

    [ObservableProperty]
    private bool _isCoverSourcePickerOpen;

    [ObservableProperty]
    private bool _isCoverCropOpen;

    [ObservableProperty]
    private string _coverPickerTitle = string.Empty;

    /// <summary>该网址有没有详情页封面，「使用详细页封面」是否可点</summary>
    [ObservableProperty]
    private bool _coverPickerHasDetailCover;

    [ObservableProperty]
    private ImageSource? _cropSourceImage;

    [ObservableProperty]
    private double _cropDisplayWidth;

    [ObservableProperty]
    private double _cropDisplayHeight;

    [ObservableProperty]
    private double _cropLeft;

    [ObservableProperty]
    private double _cropTop;

    /// <summary>右键「设置卡片封面」：先问封面从哪来</summary>
    public void BeginCardCoverSetup(BookmarkCardViewModel card)
    {
        _coverTarget = card;
        CoverPickerTitle = card.Title;
        CoverPickerHasDetailCover = CoverStore.Exists(card.Model.CoverPath);
        IsCoverSourcePickerOpen = true;
    }

    [RelayCommand]
    private void CancelCoverPicker()
    {
        IsCoverSourcePickerOpen = false;
        _coverTarget = null;
    }

    /// <summary>拿现有的详情页封面当源图，直接进裁剪</summary>
    [RelayCommand]
    private void UseDetailCoverForCard()
    {
        var card = _coverTarget;
        if (card == null) return;

        var bitmap = CoverStore.LoadFull(card.Model.CoverPath);
        if (bitmap == null)
        {
            ShowMessage("找不到详情页封面", "这张封面图的文件已经不在数据目录里了，请重新设置详情页封面。");
            return;
        }

        IsCoverSourcePickerOpen = false;
        BeginCoverCrop(bitmap);
    }

    /// <summary>选一张新图片当源图，再进裁剪</summary>
    public void BeginCardCoverCropFromFile(string sourceFile)
    {
        var bitmap = CoverStore.LoadFull(sourceFile);
        if (bitmap == null)
        {
            ShowMessage("读取图片失败", "没法读取这张图片，换一张试试。");
            return;
        }

        IsCoverSourcePickerOpen = false;
        BeginCoverCrop(bitmap);
    }

    private void BeginCoverCrop(BitmapSource bitmap)
    {
        _cropBitmap = bitmap;
        CropSourceImage = bitmap;

        // 先按「刚好铺满取景框」算基准尺寸，保证取景框内不会露白
        var fit = Math.Max(CropWindowWidth / bitmap.PixelWidth, CropWindowHeight / bitmap.PixelHeight);
        _cropBaseWidth = bitmap.PixelWidth * fit;
        _cropBaseHeight = bitmap.PixelHeight * fit;
        _cropScale = 1;

        CropDisplayWidth = _cropBaseWidth;
        CropDisplayHeight = _cropBaseHeight;
        CropLeft = (CropWindowWidth - CropDisplayWidth) / 2;
        CropTop = (CropWindowHeight - CropDisplayHeight) / 2;

        IsCoverCropOpen = true;
    }

    /// <summary>裁剪时拖动图片（dx/dy 是屏幕像素位移）</summary>
    public void DragCrop(double dx, double dy)
    {
        CropLeft = ClampCropLeft(CropLeft + dx, CropDisplayWidth);
        CropTop = ClampCropTop(CropTop + dy, CropDisplayHeight);
    }

    /// <summary>裁剪时滚轮缩放，以取景框中心为锚点</summary>
    public void ZoomCrop(int wheelDelta)
    {
        var factor = wheelDelta > 0 ? 1.1 : 1 / 1.1;
        var newScale = Math.Clamp(_cropScale * factor, 1, 6);
        if (Math.Abs(newScale - _cropScale) < 0.0001) return;

        var centerX = CropWindowWidth / 2;
        var centerY = CropWindowHeight / 2;
        var ratio = newScale / _cropScale;

        _cropScale = newScale;
        var newWidth = _cropBaseWidth * _cropScale;
        var newHeight = _cropBaseHeight * _cropScale;

        CropDisplayWidth = newWidth;
        CropDisplayHeight = newHeight;
        CropLeft = ClampCropLeft(centerX - (centerX - CropLeft) * ratio, newWidth);
        CropTop = ClampCropTop(centerY - (centerY - CropTop) * ratio, newHeight);
    }

    // 图片必须始终盖满取景框：左边不能大于 0，右边不能小于取景框宽度
    private static double ClampCropLeft(double left, double width)
        => Math.Min(0, Math.Max(CropWindowWidth - width, left));

    private static double ClampCropTop(double top, double height)
        => Math.Min(0, Math.Max(CropWindowHeight - height, top));

    [RelayCommand]
    private void ConfirmCoverCrop()
    {
        var card = _coverTarget;
        var bitmap = _cropBitmap;

        try
        {
            if (card == null || bitmap == null) return;

            // 取景框对应到原图上的区域（原图像素坐标）
            var sourceX = -CropLeft / CropDisplayWidth * bitmap.PixelWidth;
            var sourceY = -CropTop / CropDisplayHeight * bitmap.PixelHeight;
            var sourceW = CropWindowWidth / CropDisplayWidth * bitmap.PixelWidth;
            var sourceH = CropWindowHeight / CropDisplayHeight * bitmap.PixelHeight;

            var rect = new Int32Rect(
                Math.Clamp((int)Math.Round(sourceX), 0, Math.Max(0, bitmap.PixelWidth - 1)),
                Math.Clamp((int)Math.Round(sourceY), 0, Math.Max(0, bitmap.PixelHeight - 1)),
                Math.Max(1, (int)Math.Round(sourceW)),
                Math.Max(1, (int)Math.Round(sourceH)));
            rect.Width = Math.Min(rect.Width, bitmap.PixelWidth - rect.X);
            rect.Height = Math.Min(rect.Height, bitmap.PixelHeight - rect.Y);

            var cropped = new CroppedBitmap(bitmap, rect);
            cropped.Freeze();

            var previous = card.Model.CardCoverPath;
            var relative = CoverStore.SaveBitmap(card.Model.Id, CardCoverSuffix, cropped);
            card.Model.CardCoverPath = relative;
            card.Model.CardCoverAutoFetched = false; // 手工裁剪过就是用户自己的封面，自动抓取不再覆盖
            if (!string.IsNullOrEmpty(previous) &&
                !string.Equals(previous, relative, StringComparison.OrdinalIgnoreCase))
                CoverStore.Delete(previous);

            card.ReloadCardCover();
            _library.Save();
            RefreshAfterCoverChange();
            StatusText = "已更新卡片封面：" + card.Title;
        }
        catch (Exception ex)
        {
            JsonStore.Log("裁剪卡片封面失败：" + ex.Message);
            ShowMessage("裁剪卡片封面失败", ex.Message);
        }
        finally
        {
            CloseCoverCrop();
        }
    }

    [RelayCommand]
    private void CancelCoverCrop() => CloseCoverCrop();

    private void CloseCoverCrop()
    {
        IsCoverCropOpen = false;
        _cropBitmap = null;
        CropSourceImage = null;
        _coverTarget = null;
    }

    // ---------- 手动添加网址 ----------

    /// <summary>手动添加的书签在「来源文件夹」里统一显示这个，和 Edge 同步来的区分开</summary>
    public const string ManualSourceFolder = "手动添加";

    [ObservableProperty] private bool _isAddBookmarkOpen;
    /// <summary>网址输入框，支持一行一个批量粘贴</summary>
    [ObservableProperty] private string _addBookmarkUrl = string.Empty;
    /// <summary>标题，留空就取域名（只填一条网址时才有意义）</summary>
    [ObservableProperty] private string _addBookmarkTitle = string.Empty;
    [ObservableProperty] private string _addBookmarkNote = string.Empty;
    /// <summary>校验失败提示，有内容时在弹层里显示，弹层不会关掉</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddBookmarkError))]
    private string _addBookmarkError = string.Empty;

    public bool HasAddBookmarkError => AddBookmarkError.Length > 0;

    /// <summary>当前会把网址加到哪一侧（打开弹层时按隐私模式算好）</summary>
    [ObservableProperty] private string _addBookmarkScopeText = string.Empty;

    /// <summary>弹层里可勾选的分类（只列当前隐私侧的项目分类）</summary>
    public ObservableCollection<SelectableCategoryViewModel> AddBookmarkCategories { get; } = new();

    public bool HasAddBookmarkCategories => AddBookmarkCategories.Count > 0;

    [RelayCommand]
    private void OpenAddBookmark()
    {
        AddBookmarkUrl = string.Empty;
        AddBookmarkTitle = string.Empty;
        AddBookmarkNote = string.Empty;
        AddBookmarkError = string.Empty;
        AddBookmarkScopeText = IsPrivacyMode
            ? "会加到隐私侧，只在隐私模式下看得到。"
            : "会加到常规侧。";

        AddBookmarkCategories.Clear();
        foreach (var category in _library.Database.Categories
                     .Where(c => c.Kind == CategoryKind.Project && CategoryInScope(c))
                     .OrderBy(c => c.Order).ThenBy(c => c.Name))
            AddBookmarkCategories.Add(new SelectableCategoryViewModel(category));
        OnPropertyChanged(nameof(HasAddBookmarkCategories));

        IsAddBookmarkOpen = true;
    }

    [RelayCommand]
    private void CancelAddBookmark()
    {
        IsAddBookmarkOpen = false;
        AddBookmarkError = string.Empty;
    }

    [RelayCommand]
    private void ConfirmAddBookmark()
    {
        // 一行一个网址，空行忽略；同一批里重复的只算一条
        var rawLines = AddBookmarkUrl
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (rawLines.Count == 0)
        {
            AddBookmarkError = "请先填一个网址。";
            return;
        }

        var invalid = new List<string>();
        var urls = new List<string>();
        foreach (var line in rawLines)
        {
            var url = NormalizeUrl(line);
            if (url == null) invalid.Add(line);
            else if (!urls.Contains(url, StringComparer.OrdinalIgnoreCase)) urls.Add(url);
        }

        if (invalid.Count > 0)
        {
            AddBookmarkError = $"这些看起来不是网址：{string.Join("、", invalid.Take(3))}"
                               + (invalid.Count > 3 ? $" 等 {invalid.Count} 条" : "");
            return;
        }

        // 库里已有的按同一条网址算，不重复添加
        var existing = _library.Database.Bookmarks
            .Where(b => urls.Contains(b.Url, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var fresh = urls
            .Where(u => !existing.Any(b => string.Equals(b.Url, u, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (fresh.Count == 0)
        {
            IsAddBookmarkOpen = false;
            var jumped = FocusUrl(existing[0].Url);
            StatusText = (urls.Count == 1 ? "这个网址已经在库里了" : $"{urls.Count} 条网址都已经在库里了")
                         + (jumped ? "，已跳到它的详情页" : "（在另一侧的隐私列表里，切到那一侧才能看到）");
            return;
        }

        var title = AddBookmarkTitle.Trim();
        var note = AddBookmarkNote.Trim();
        var categoryIds = AddBookmarkCategories.Where(c => c.IsChecked).Select(c => c.Category.Id).ToList();
        var single = urls.Count == 1;

        foreach (var url in fresh)
        {
            _library.Database.Bookmarks.Add(new BookmarkNode
            {
                Url = url,
                // 只有填一条网址时，标题和备注才有意义；批量时按域名兜底
                Title = single && title.Length > 0 ? title : UrlHelper.GetDomain(url),
                Note = single && note.Length > 0 ? note : null,
                CategoryIds = new List<string>(categoryIds),
                SourceFolder = ManualSourceFolder,
                Source = BookmarkSource.Manual,
                IsPrivate = IsPrivacyMode,
                AddedAt = DateTime.Now
            });
        }

        _library.Save();
        RebuildCards();
        Refresh();

        IsAddBookmarkOpen = false;
        FocusUrl(fresh[0]);

        var skipped = urls.Count - fresh.Count;
        StatusText = $"已添加 {fresh.Count} 条网址"
                     + (skipped > 0 ? $"，跳过 {skipped} 条已存在的" : "")
                     + (categoryIds.Count > 0 ? $"（归入 {categoryIds.Count} 个分类）" : "");
    }

    /// <summary>把用户输入整理成规范 URL；补 https:// 前缀，非 http/https 或解析不出来就返回 null</summary>
    private static string? NormalizeUrl(string text)
    {
        var value = text;
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(uri.Host) || !uri.Host.Contains('.')) return null;

        return uri.AbsoluteUri;
    }

    /// <summary>
    /// 切到某条收藏的详情页（手动添加撞上重复条目时用）。
    /// 不在当前隐私侧就返回 false —— 隐私条目不该在常规模式里露面。
    /// </summary>
    private bool FocusUrl(string url)
    {
        var card = _allCards.FirstOrDefault(c => string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase));
        if (card == null || !InScope(card.Model)) return false;

        SelectedCard = card;
        MainView = MainViewMode.Detail;
        return true;
    }

    // ---------- 确认弹层（目前只有「删除收藏」用） ----------

    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private string _confirmTitle = string.Empty;
    [ObservableProperty] private string _confirmBody = string.Empty;
    [ObservableProperty] private string _confirmOkText = "确定";

    private Action? _confirmAction;

    /// <summary>弹一个「确定 / 取消」的确认框，确定后执行 onConfirm</summary>
    public void ShowConfirm(string title, string body, Action onConfirm, string okText = "确定")
    {
        ConfirmTitle = title;
        ConfirmBody = body;
        ConfirmOkText = okText;
        _confirmAction = onConfirm;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void ConfirmOk()
    {
        IsConfirmOpen = false;
        var action = _confirmAction;
        _confirmAction = null;
        action?.Invoke();
    }

    [RelayCommand]
    private void ConfirmCancel()
    {
        IsConfirmOpen = false;
        _confirmAction = null;
    }

    // ---------- 编辑标题 / 备注 ----------

    [ObservableProperty] private bool _isEditBookmarkOpen;
    [ObservableProperty] private string _editBookmarkTitle = string.Empty;
    [ObservableProperty] private string _editBookmarkNote = string.Empty;
    /// <summary>弹层里显示的网址，只作提示（改不了，网址是去重的键）</summary>
    [ObservableProperty] private string _editBookmarkUrl = string.Empty;

    private BookmarkCardViewModel? _editTarget;

    public void BeginBookmarkEdit(BookmarkCardViewModel card)
    {
        _editTarget = card;
        EditBookmarkTitle = card.Model.Title;
        EditBookmarkNote = card.Model.Note ?? string.Empty;
        EditBookmarkUrl = card.Url;
        IsEditBookmarkOpen = true;
    }

    [RelayCommand]
    private void CancelBookmarkEdit()
    {
        IsEditBookmarkOpen = false;
        _editTarget = null;
    }

    [RelayCommand]
    private void ConfirmBookmarkEdit()
    {
        var card = _editTarget;
        IsEditBookmarkOpen = false;
        _editTarget = null;
        if (card == null) return;

        var title = EditBookmarkTitle.Trim();
        var note = EditBookmarkNote.Trim();
        card.Model.Title = title;
        card.Model.Note = note.Length == 0 ? null : note;
        card.RaiseContentChanged();

        _library.Save();
        Refresh();
        StatusText = $"已更新「{(title.Length == 0 ? card.Url : title)}」的标题和备注";
    }

    // ---------- 删除收藏 ----------

    /// <summary>右键「删除这条收藏」：先确认再删（删掉的 Edge 条目下次同步还会回来）</summary>
    public void DeleteBookmarkRequested(BookmarkCardViewModel card)
    {
        var hint = card.Model.Source == BookmarkSource.Edge
            ? "它来自 Edge 同步：如果 Edge 里还留着这条，下次同步会重新出现。"
            : "它是手动添加的，删掉之后不会自动恢复。";

        ShowConfirm($"删除「{card.Title}」？",
            $"只从本应用移除，不会动 Edge 的书签文件，它的封面图也会一起清掉。{hint}",
            () => DeleteBookmark(card), "删除");
    }

    private void DeleteBookmark(BookmarkCardViewModel card)
    {
        if (!_library.Database.Bookmarks.Remove(card.Model)) return;

        CoverStore.Delete(card.Model.CoverPath);
        CoverStore.Delete(card.Model.CardCoverPath);

        var wasSelected = ReferenceEquals(SelectedCard, card);
        _allCards.Remove(card);
        _library.Save();

        // 删的正好是当前详情页那条，就退回墙视图
        if (wasSelected) SelectedCard = null;
        MainView = MainViewMode.Detail;
        Refresh();
        StatusText = $"已删除「{card.Title}」";
    }

    // ---------- 分类管理 ----------

    [RelayCommand]
    private void OpenCategoryEditor() => BeginCategoryEdit(null);

    public void BeginCategoryEdit(CategoryNode? category)
    {
        _editingCategory = category;
        CategoryEditorTitle = category == null ? "新建分类" : "重命名分类";
        CategoryDraftName = category?.Name ?? string.Empty;
        IsCategoryEditorOpen = true;
    }

    [RelayCommand]
    private void CancelCategoryEditor()
    {
        IsCategoryEditorOpen = false;
        _editingCategory = null;
    }

    [RelayCommand]
    private void ConfirmCategoryEditor()
    {
        var name = CategoryDraftName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            StatusText = "分类名不能为空";
            return;
        }

        if (_editingCategory != null)
        {
            var old = _editingCategory.Name;
            RenameCategory(_editingCategory, name, _editingCategory.Glyph, _editingCategory.Color);
            StatusText = $"分类「{old}」已重命名为「{name}」";
        }
        else
        {
            var palette = new[] { "#66C0F4", "#4FC3F7", "#B388FF", "#FF8A65", "#FF5252", "#4DD0E1",
                "#FFB74D", "#81C784", "#7E57C2", "#64B5F6", "#FFD54F", "#4DB6AC" };
            AddCategory(name, "•", palette[_library.Database.Categories.Count % palette.Length]);
            StatusText = $"已创建分类「{name}」，可右键书签移动过来";
        }

        IsCategoryEditorOpen = false;
        _editingCategory = null;
    }

    public void DeleteCategoryRequested(CategoryNode category)
    {
        DeleteCategory(category);
    }

    /// <summary>
    /// 把一个网址归入某个分类（追加，已经在里面就不动）；category 传 null 表示移出全部分类。
    /// 归类看板拖拽和右键的「移出全部分类」都走这里。
    /// </summary>
    public void MoveTo(BookmarkCardViewModel card, CategoryNode? category)
    {
        if (category is { Kind: CategoryKind.Browser })
        {
            StatusText = "浏览器分类跟随 Edge 文件夹，不能手工归入";
            return;
        }

        if (category == null)
        {
            if (card.Model.CategoryIds.Count == 0)
            {
                StatusText = $"「{card.Title}」本来就没归类";
                return;
            }

            card.Model.CategoryIds.Clear();
            _library.Save();
            Refresh();
            StatusText = $"已移出全部分类：{card.Title}";
            return;
        }

        if (card.Model.CategoryIds.Contains(category.Id))
        {
            StatusText = $"「{card.Title}」已经在「{category.Name}」里了";
            return;
        }

        card.Model.CategoryIds.Add(category.Id);
        _library.Save();
        Refresh();
        StatusText = $"已归入「{category.Name}」：{card.Title}";
    }

    /// <summary>右键菜单：勾上就归入这个分类，再点一下就从这里移出（别的分类不受影响）</summary>
    public void ToggleCategory(BookmarkCardViewModel card, CategoryNode category)
    {
        if (category.Kind == CategoryKind.Browser)
        {
            StatusText = "浏览器分类跟随 Edge 文件夹，不能手工归入";
            return;
        }

        var owned = card.Model.CategoryIds.Contains(category.Id);
        if (owned) card.Model.CategoryIds.Remove(category.Id);
        else card.Model.CategoryIds.Add(category.Id);

        _library.Save();
        Refresh();
        StatusText = owned
            ? $"已从「{category.Name}」移出：{card.Title}"
            : $"已归入「{category.Name}」：{card.Title}";
    }

    // ---------- 归类看板 ----------

    /// <summary>设置里「分类来源」选了浏览器分类时的提示，说明看板操作的是项目分类</summary>
    public bool ShowCategorySourceHint => Settings.CategorySource == CategoryKind.Browser;

    public string CategorySourceHint =>
        "当前「分类来源」是浏览器分类（跟随 Edge 文件夹，只读）。归类看板里操作的是项目分类，" +
        "浏览器分类的归属由 Edge 决定，同步时会被重算。";

    /// <summary>重建看板：先钉一个「未分类」，再跟全部项目分类，条数按当前隐私范围统计</summary>
    private void BuildCategoryFolders()
    {
        var projects = _library.Database.Categories
            .Where(c => c.Kind == CategoryKind.Project && CategoryInScope(c))
            .OrderBy(c => c.Order).ThenBy(c => c.Name).ToList();

        // 索引对得上就只更新条数，避免每次刷新都把文件夹卡片重建一遍
        var sameShape = CategoryFolders.Count == projects.Count + 1 &&
                        CategoryFolders[0].IsUncategorized &&
                        !CategoryFolders.Skip(1)
                            .Where((f, i) => !ReferenceEquals(f.Category, projects[i])).Any();

        if (!sameShape)
        {
            CategoryFolders.Clear();
            CategoryFolders.Add(new CategoryFolderViewModel(null));
            foreach (var category in projects) CategoryFolders.Add(new CategoryFolderViewModel(category));
        }

        var counts = new Dictionary<string, int> { [UncategorizedKey] = 0 };
        foreach (var category in projects) counts[category.Id] = 0;

        foreach (var card in _allCards)
        {
            if (!InScope(card.Model)) continue;
            // 看板按项目分类算，跟「分类来源」设置无关。
            // 一条书签可以挂在多个分类里，所以每个分类各计一次，各文件夹条数相加会大于总数；
            // 一个都没挂上（或者归属指向另一侧的分类，跨侧失效）才算「未分类」
            var hit = false;
            foreach (var id in card.Model.CategoryIds)
            {
                if (!counts.TryGetValue(id, out var n)) continue;
                counts[id] = n + 1;
                hit = true;
            }
            if (!hit) counts[UncategorizedKey]++;
        }

        CategoryFolders[0].Count = counts[UncategorizedKey];
        for (var i = 0; i < projects.Count; i++)
            CategoryFolders[i + 1].Count = counts[projects[i].Id];

        OnPropertyChanged(nameof(HasNoProjectCategory));
    }

    /// <summary>一个项目分类都还没有时，看板上只剩「未分类」，需要提示去哪建分类</summary>
    public bool HasNoProjectCategory => CategoryFolders.Count <= 1;

    /// <summary>
    /// 开始从侧栏往看板拖时调用。
    /// 鼠标一按下侧栏，ListBox 就会选中条目、主区跳到详情页，看板跟着消失、落点也没了，
    /// 所以这里把选中清掉，让看板重新露出来。
    /// </summary>
    public void BeginOrganizeDrag()
    {
        MainView = MainViewMode.Detail;
        SelectedCard = null;
    }

    /// <summary>拖拽过程中高亮某个文件夹（传 null 清掉全部高亮）</summary>
    public void SetDropTarget(CategoryFolderViewModel? folder)
    {
        foreach (var item in CategoryFolders)
            item.IsDropTarget = ReferenceEquals(item, folder);
    }

    /// <summary>把一条收藏放到某个文件夹里；文件夹为「未分类」时等于移出分类</summary>
    public void DropIntoFolder(BookmarkCardViewModel card, CategoryFolderViewModel folder)
    {
        SetDropTarget(null);
        MoveTo(card, folder.Category);
    }

    /// <summary>桌面拖进来的文件不是书签时用不着，但拖拽悬停要高亮，所以统一给个提示</summary>
    public void ReportUnsupportedDrop()
    {
        SetDropTarget(null);
        StatusText = "这里只接收左侧收藏列表里的网址";
    }

    public CategoryNode AddCategory(string name, string glyph, string color)
    {
        var category = new CategoryNode
        {
            Name = name,
            Glyph = glyph,
            Color = color,
            Kind = CategoryKind.Project,
            // 在哪个模式下建的，就归哪一侧：常规模式建的是常规分类，隐私模式建的只有隐私模式看得到
            IsPrivate = IsPrivacyMode,
            Order = _library.Database.Categories.Count
        };
        _library.Database.Categories.Add(category);
        _library.Save();
        Refresh();
        return category;
    }

    public void DeleteCategory(CategoryNode category)
    {
        _library.DeleteCategory(category.Id);
        Refresh();
        StatusText = $"已删除分类「{category.Name}」，该分类下的书签已从它移出";
    }

    public void RenameCategory(CategoryNode category, string name, string glyph, string color)
    {
        category.Name = name;
        category.Glyph = glyph;
        category.Color = color;
        category.IsAuto = false;
        _library.Save();
        Refresh();
    }

    [RelayCommand]
    private void RegroupByType()
    {
        var changed = _library.RegroupBySiteType();
        Refresh();
        StatusText = $"智能归类完成，调整了 {changed} 条（需要先做检测才能准确识别类型）";
    }

    [RelayCommand]
    private void RegroupByFolder()
    {
        var count = _library.RegroupByEdgeFolder();
        Refresh();
        StatusText = $"已把 Edge 文件夹结构导入项目分类，覆盖 {count} 条";
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo(AppPaths.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            JsonStore.Log("打开数据目录失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _library.Save();
        StatusText = "设置已保存";
    }

    /// <summary>
    /// 执行一个快捷键动作。返回 false 表示这个动作不归 ViewModel 管（目前只有「聚焦搜索」，
    /// 它要操作控件焦点，由 MainWindow 自己处理）。
    /// </summary>
    public bool RunShortcut(string id)
    {
        switch (id)
        {
            case ShortcutIds.SyncEdge: _ = SyncEdgeAsync(); return true;
            case ShortcutIds.ProbeVisible: _ = ProbeVisibleAsync(); return true;
            case ShortcutIds.ProbeAll: _ = ProbeAllAsync(); return true;
            case ShortcutIds.CycleView: CycleHomeView(); return true;
            case ShortcutIds.TogglePrivacy: TogglePrivacy(); return true;
            case ShortcutIds.ToggleFilter: ToggleFilter(); return true;
            case ShortcutIds.ClearFilters: ClearFilters(); return true;
            case ShortcutIds.OpenAddBookmark: OpenAddBookmark(); return true;
            case ShortcutIds.OpenSettings: OpenSettings(); return true;
            case ShortcutIds.OpenAccounts: OpenAccounts(); return true;
            case ShortcutIds.OpenBlacklist: OpenBlacklist(); return true;
            case ShortcutIds.ViewCoverWall: GoToHomeView(HomeViewMode.CoverWall); return true;
            case ShortcutIds.ViewCardWall: GoToHomeView(HomeViewMode.CardWall); return true;
            case ShortcutIds.ViewOverview: GoToHomeView(HomeViewMode.Overview); return true;
            case ShortcutIds.ViewOrganize: GoToHomeView(HomeViewMode.Organize); return true;
            default: return false;
        }
    }

    public void SaveAll()
    {
        _library.Save();
        _library.SaveCache();
    }

    public IReadOnlyList<AccountEntry> Accounts => _library.Database.Accounts;

    public AccountVault Vault => _vault;

    public void NotifyAccountsChanged()
    {
        _library.Save();
        Refresh();
        RefreshSelection();
    }

    public void SetStatus(string text) => StatusText = text;
}

/// <summary>「添加网址」弹层里一个可勾选的分类（勾几个就归入几个，和右键菜单同一套规则）</summary>
public sealed partial class SelectableCategoryViewModel : ObservableObject
{
    public SelectableCategoryViewModel(CategoryNode category) => Category = category;

    public CategoryNode Category { get; }

    public string Name => Category.Name;

    [ObservableProperty] private bool _isChecked;
}
