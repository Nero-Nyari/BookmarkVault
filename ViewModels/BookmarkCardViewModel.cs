using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BookmarkVault.Models;
using BookmarkVault.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BookmarkVault.ViewModels;

/// <summary>卡片视图模型：包裹一条收藏 + 它的检测结果</summary>
public sealed partial class BookmarkCardViewModel : ObservableObject
{
    public BookmarkNode Model { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(StateColor))]
    [NotifyPropertyChangedFor(nameof(StateGlyph))]
    [NotifyPropertyChangedFor(nameof(SecurityText))]
    [NotifyPropertyChangedFor(nameof(SecurityColor))]
    [NotifyPropertyChangedFor(nameof(TypeText))]
    [NotifyPropertyChangedFor(nameof(ElapsedText))]
    [NotifyPropertyChangedFor(nameof(TooltipText))]
    [NotifyPropertyChangedFor(nameof(HasProbe))]
    [NotifyPropertyChangedFor(nameof(HttpStatusText))]
    [NotifyPropertyChangedFor(nameof(HttpsText))]
    [NotifyPropertyChangedFor(nameof(CertText))]
    [NotifyPropertyChangedFor(nameof(CheckedText))]
    [NotifyPropertyChangedFor(nameof(ServerText))]
    [NotifyPropertyChangedFor(nameof(HasSecurityFlags))]
    private ProbeRecord? _probe;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    [NotifyPropertyChangedFor(nameof(IconUri))]
    private bool _iconReady;

    public BookmarkCardViewModel(BookmarkNode model)
    {
        Model = model;
        // 启动时直接判断图标缓存是否已存在，避免全部先显示色块再闪一下
        _iconReady = IconCache.HasIcon(model.Domain);
        ReloadCover();
    }

    public string Title => string.IsNullOrWhiteSpace(Model.Title) ? Model.Url : Model.Title;
    public string Domain => Model.Domain;
    public string Url => Model.Url;
    public int VisitCount => Model.VisitCount;
    public bool IsFavorite => Model.IsFavorite;

    public bool HasIcon => IconReady;
    public string IconUri => IconCache.IconPathFor(Domain);
    public string AvatarColor => IconCache.AvatarColor(Domain);
    public string AvatarText => IconCache.AvatarText(Domain);

    // ---------- 封面图 ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private ImageSource? _coverSource;

    public bool HasCover => CoverSource != null;

    /// <summary>重新读取封面图文件（换图或删除后调用）</summary>
    public void ReloadCover()
    {
        CoverSource = LoadCover(Model.CoverPath);
        OnPropertyChanged(nameof(HasCover));
        // 卡片没有专属封面时会沿用详情页封面，这里要一起刷新
        ReloadCardCover();
    }

    // ---------- 卡片封面（2:3，未设置时沿用详情页封面） ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCardCover))]
    private ImageSource? _cardCoverSource;

    public bool HasCardCover => CardCoverSource != null;

    /// <summary>卡片实际使用的封面路径：有专属封面就用它，否则沿用详情页封面</summary>
    public string? EffectiveCardCoverPath =>
        string.IsNullOrWhiteSpace(Model.CardCoverPath) ? Model.CoverPath : Model.CardCoverPath;

    /// <summary>重新读取卡片封面（裁剪换图或删除后调用）</summary>
    public void ReloadCardCover()
    {
        CardCoverSource = LoadCover(EffectiveCardCoverPath);
        OnPropertyChanged(nameof(HasCardCover));
    }

    private static ImageSource? LoadCover(string? coverPath)
    {
        var full = CoverStore.Resolve(coverPath);
        if (string.IsNullOrEmpty(full) || !File.Exists(full)) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            // 换图后文件名可能不变，必须绕开 WPF 的图片缓存，否则会一直显示旧图
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1280;
            image.UriSource = new Uri(full);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            JsonStore.Log($"读取封面图失败 {full}：{ex.Message}");
            return null;
        }
    }

    private Brush? _heroFallbackBrush;

    /// <summary>没有封面图时 Hero 的兜底背景：域名主色 → 深色的斜向渐变</summary>
    public Brush HeroFallbackBrush => _heroFallbackBrush ??= BuildHeroFallbackBrush();

    private Brush BuildHeroFallbackBrush()
    {
        var baseColor = Color.FromRgb(0x0B, 0x12, 0x18);
        Color accent;
        try
        {
            accent = (Color)ColorConverter.ConvertFromString(AvatarColor);
        }
        catch
        {
            accent = Color.FromRgb(0x2E, 0x6A, 0x94);
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.05, 0),
            EndPoint = new Point(0.95, 1)
        };
        brush.GradientStops.Add(new GradientStop(Blend(accent, baseColor, 0.35), 0));
        brush.GradientStops.Add(new GradientStop(Blend(accent, baseColor, 0.68), 0.55));
        brush.GradientStops.Add(new GradientStop(baseColor, 1));
        brush.Freeze();
        return brush;
    }

    private static Color Blend(Color from, Color to, double amount)
        => Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * amount),
            (byte)(from.G + (to.G - from.G) * amount),
            (byte)(from.B + (to.B - from.B) * amount));

    // ---------- 时间与统计 ----------

    public DateTime AddedAt => Model.EdgeDateAdded ?? Model.AddedAt;

    public string AddedText => AddedAt.ToString("yyyy-MM-dd");

    public string LastOpenedText => Model.LastOpenedAt is { } time
        ? time.ToString("yyyy-MM-dd HH:mm")
        : "从未打开";

    public string VisitCountText => Model.VisitCount == 0 ? "0 次" : $"{Model.VisitCount} 次";

    /// <summary>打开一次后刷新统计相关的显示</summary>
    public void RaiseStatsChanged()
    {
        OnPropertyChanged(nameof(VisitCount));
        OnPropertyChanged(nameof(VisitCountText));
        OnPropertyChanged(nameof(LastOpenedText));
    }

    /// <summary>备注（空的时候给个占位，详情页直接绑它）</summary>
    public string NoteText => string.IsNullOrWhiteSpace(Model.Note) ? "（还没有备注）" : Model.Note!;

    /// <summary>标题 / 备注改过之后刷新界面（详情页的备注绑的是卡片属性，不是 Model）</summary>
    public void RaiseContentChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(NoteText));
        OnPropertyChanged(nameof(TooltipText));
    }

    public bool HasProbe => Probe != null;

    // ---------- 分类（一个网址可以属于多个） ----------

    /// <summary>当前分类体系下挂在这条收藏上的分类，可能不止一个（跨侧的归属不算）</summary>
    public ObservableCollection<CategoryNode> Categories { get; } = new();

    public bool HasCategory => Categories.Count > 0;

    public string CategoryColor => Categories.Count > 0 ? Categories[0].Color : "#8F98A0";

    /// <summary>搜索时能按任意一个分类名命中</summary>
    public string CategorySearchText => string.Join(' ', Categories.Select(c => c.Name));

    /// <summary>重新贴分类标签（归类变化、切换分类体系、切隐私模式后调用）</summary>
    public void SetCategories(IEnumerable<CategoryNode> categories)
    {
        Categories.Clear();
        foreach (var category in categories) Categories.Add(category);

        OnPropertyChanged(nameof(HasCategory));
        OnPropertyChanged(nameof(CategoryColor));
        OnPropertyChanged(nameof(CategorySearchText));
    }

    public string TypeText => string.IsNullOrEmpty(Probe?.SiteType) ? "未识别" : Probe!.SiteType;

    public string ElapsedText =>
        Probe == null || Probe.ElapsedMs <= 0 ? string.Empty : $"{Probe.ElapsedMs} ms";

    // ---------- 安全评估明细 ----------

    public string HttpStatusText => Probe?.StatusCode is int code ? $"HTTP {code}" : "—";

    public string HttpsText => Probe == null ? "未检测" : Probe.IsHttps ? "HTTPS 加密" : "HTTP 明文";

    public string CertText => Probe == null
        ? "未检测"
        : !Probe.IsHttps ? "不适用" : Probe.CertificateValid ? "证书有效" : "证书异常";

    public string ServerText => string.IsNullOrWhiteSpace(Probe?.ServerHeader) ? "未知" : Probe!.ServerHeader!;

    public string CheckedText => Probe == null ? "尚未检测" : Probe.CheckedAt.ToString("yyyy-MM-dd HH:mm");

    public bool HasSecurityFlags => Probe is { SecurityFlags.Count: > 0 };

    public string StateText => Probe?.State switch
    {
        Models.ProbeState.Online => "可访问",
        Models.ProbeState.Redirected => "可访问",
        Models.ProbeState.Unauthorized => "需登录",
        Models.ProbeState.Offline => "打不开",
        Models.ProbeState.Timeout => "超时",
        Models.ProbeState.Error => "连接失败",
        Models.ProbeState.Pending => "检测中",
        _ => "未检测"
    };

    public string StateGlyph => Probe?.State switch
    {
        Models.ProbeState.Online => "●",
        Models.ProbeState.Redirected => "●",
        Models.ProbeState.Unauthorized => "◐",
        Models.ProbeState.Offline => "●",
        Models.ProbeState.Timeout => "◌",
        Models.ProbeState.Error => "●",
        Models.ProbeState.Pending => "◌",
        _ => "○"
    };

    public string StateColor => Probe?.State switch
    {
        Models.ProbeState.Online => "#8BC34A",
        Models.ProbeState.Redirected => "#8BC34A",
        Models.ProbeState.Unauthorized => "#FFB74D",
        Models.ProbeState.Offline => "#EF5350",
        Models.ProbeState.Timeout => "#FFB74D",
        Models.ProbeState.Error => "#EF5350",
        Models.ProbeState.Pending => "#66C0F4",
        _ => "#6B7A8C"
    };

    public string SecurityText => Probe?.Security switch
    {
        SecurityLevel.Safe => "安全",
        SecurityLevel.Caution => "注意",
        SecurityLevel.Risk => "有风险",
        _ => "未评估"
    };

    public string SecurityColor => Probe?.Security switch
    {
        SecurityLevel.Safe => "#8BC34A",
        SecurityLevel.Caution => "#FFB74D",
        SecurityLevel.Risk => "#EF5350",
        _ => "#6B7A8C"
    };

    public string TooltipText
    {
        get
        {
            var lines = new List<string> { Model.Url };
            if (!string.IsNullOrEmpty(Model.SourceFolder)) lines.Add("来源：" + Model.SourceFolder);
            if (Probe != null)
            {
                lines.Add($"状态：{StateText}" +
                          (Probe.StatusCode is int code ? $"（HTTP {code}）" : string.Empty));
                lines.Add($"类型：{TypeText}");
                lines.Add($"安全：{SecurityText}");
                foreach (var flag in Probe.SecurityFlags) lines.Add("· " + flag);
                if (!string.IsNullOrEmpty(Probe.PageTitle)) lines.Add("标题：" + Probe.PageTitle);
                lines.Add("检测时间：" + Probe.CheckedAt.ToString("yyyy-MM-dd HH:mm"));
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>标记变化后通知界面刷新</summary>
    public void RaiseFavoriteChanged()
    {
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteButtonText));
    }

    public string FavoriteButtonText => IsFavorite ? "取消常用" : "标记为常用";

    /// <summary>搜索匹配</summary>
    public bool Matches(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;
        return Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || Url.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || (Model.Note?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
               || Model.Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase))
               || CategorySearchText.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}
