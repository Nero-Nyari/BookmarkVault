using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace BookmarkVault.Services;

/// <summary>
/// 快捷键动作的稳定标识。这些字符串会落盘到 <c>AppSettings.Shortcuts</c> 里当键名，改文案不要改它们。
/// </summary>
public static class ShortcutIds
{
    public const string FocusSearch = "search.focus";
    public const string SyncEdge = "sync.edge";
    public const string ProbeVisible = "probe.visible";
    public const string CycleView = "view.cycle";

    public const string TogglePrivacy = "privacy.toggle";
    public const string OpenSettings = "settings.open";
    public const string OpenAddBookmark = "add.open";
    public const string OpenAccounts = "accounts.open";
    public const string OpenBlacklist = "blacklist.open";
    public const string ProbeAll = "probe.all";
    public const string ToggleFilter = "filter.toggle";
    public const string ClearFilters = "filter.clear";
    public const string ViewCoverWall = "view.coverwall";
    public const string ViewCardWall = "view.cardwall";
    public const string ViewOverview = "view.overview";
    public const string ViewOrganize = "view.organize";
}

/// <summary>一条可以绑按键的动作</summary>
public sealed class ShortcutDefinition
{
    public ShortcutDefinition(string id, string title, string detail, bool isCore, string defaultGesture)
    {
        Id = id;
        Title = title;
        Detail = detail;
        IsCore = isCore;
        DefaultGesture = defaultGesture;
    }

    public string Id { get; }
    public string Title { get; }
    /// <summary>一句话说明这个键干什么，设置页里显示在标题下面</summary>
    public string Detail { get; }
    /// <summary>默认就绑好按键的「常用」组；其余的默认留空，想用再自己设</summary>
    public bool IsCore { get; }
    /// <summary>默认按键文本（如 "Ctrl+F"），空串表示默认不绑</summary>
    public string DefaultGesture { get; }
}

/// <summary>快捷键动作目录，以及按键文本 ↔ Key/ModifierKeys 的互转</summary>
public static class ShortcutCatalog
{
    private static readonly KeyGestureConverter GestureConverter = new();

    /// <summary>设置页里从上到下显示的全部动作：常用组在前，可选组在后</summary>
    public static IReadOnlyList<ShortcutDefinition> All { get; } = new[]
    {
        new ShortcutDefinition(ShortcutIds.FocusSearch, "聚焦搜索",
            "把光标放到侧栏搜索框；停在网址库页时放到那一页的搜索框", true, "Ctrl+F"),
        new ShortcutDefinition(ShortcutIds.SyncEdge, "同步 Edge 书签",
            "把 Edge 里的书签合并进来，等价于设置页的「重新从 Edge 同步」", true, "Ctrl+R"),
        new ShortcutDefinition(ShortcutIds.ProbeVisible, "检测当前筛选结果",
            "只检测左侧列表当前显示的条目，跟着视图、搜索和筛选走", true, "F5"),
        new ShortcutDefinition(ShortcutIds.CycleView, "循环切换主视图",
            "封面墙 → 卡片墙 → 概览 → 归类看板", true, "Ctrl+E"),

        new ShortcutDefinition(ShortcutIds.TogglePrivacy, "进入 / 退出隐私模式", "等价于底部栏的 🕶 按钮", false, ""),
        new ShortcutDefinition(ShortcutIds.ToggleFilter, "打开 / 收起筛选面板", "等价于点侧栏搜索框右边的漏斗按钮", false, ""),
        new ShortcutDefinition(ShortcutIds.ClearFilters, "清除全部筛选条件", "会一并把排序和视图重置回默认", false, ""),
        new ShortcutDefinition(ShortcutIds.ProbeAll, "检测全部收藏", "忽略筛选，检测库里的每一条", false, ""),
        new ShortcutDefinition(ShortcutIds.OpenAddBookmark, "添加网址", "打开「＋ 添加网址」弹层", false, ""),
        new ShortcutDefinition(ShortcutIds.OpenSettings, "打开设置页", "", false, ""),
        new ShortcutDefinition(ShortcutIds.OpenAccounts, "打开账号库", "", false, ""),
        new ShortcutDefinition(ShortcutIds.OpenBlacklist, "打开网址库（黑名单）", "", false, ""),
        new ShortcutDefinition(ShortcutIds.ViewCoverWall, "切到 Steam 封面墙", "只切换本次运行显示的视图，不改设置里的默认值", false, ""),
        new ShortcutDefinition(ShortcutIds.ViewCardWall, "切到简洁卡片墙", "只切换本次运行显示的视图，不改设置里的默认值", false, ""),
        new ShortcutDefinition(ShortcutIds.ViewOverview, "切到概览页", "只切换本次运行显示的视图，不改设置里的默认值", false, ""),
        new ShortcutDefinition(ShortcutIds.ViewOrganize, "切到归类看板", "只切换本次运行显示的视图，不改设置里的默认值", false, "")
    };

    /// <summary>把「Ctrl+F」这样的文本解析成按键和修饰键；解析不了返回 false</summary>
    public static bool TryParse(string? text, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            if (GestureConverter.ConvertFromInvariantString(text) is not KeyGesture gesture) return false;
            if (gesture.Key == Key.None) return false;

            key = gesture.Key;
            modifiers = gesture.Modifiers;
            return true;
        }
        catch
        {
            // KeyGestureConverter 对非法组合会抛异常，当成「没绑」处理
            return false;
        }
    }

    /// <summary>按键 + 修饰键转成「Ctrl+F」这样的显示文本；这个组合不能当快捷键时返回空串</summary>
    public static string Format(Key key, ModifierKeys modifiers)
    {
        if (key == Key.None || IsModifierKey(key)) return string.Empty;

        try
        {
            return GestureConverter.ConvertToInvariantString(new KeyGesture(key, modifiers)) ?? string.Empty;
        }
        catch
        {
            // 比如单独一个字母键：WPF 要求必须带修饰键
            return string.Empty;
        }
    }

    /// <summary>本身只是修饰键 / 不是真实按键的那些，录制时要跳过</summary>
    public static bool IsModifierKey(Key key) => key is
        Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or
        Key.System or Key.ImeProcessed or Key.DeadCharProcessed;
}