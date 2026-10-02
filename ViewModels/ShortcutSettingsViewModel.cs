using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using BookmarkVault.Models;
using BookmarkVault.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BookmarkVault.ViewModels;

/// <summary>设置页「快捷键」列表里的一行</summary>
public sealed partial class ShortcutRowViewModel : ObservableObject
{
    private readonly ShortcutSettingsViewModel _owner;

    public ShortcutRowViewModel(ShortcutDefinition definition, ShortcutSettingsViewModel owner)
    {
        Definition = definition;
        _owner = owner;
    }

    public ShortcutDefinition Definition { get; }

    public string Id => Definition.Id;
    public string Title => Definition.Title;
    public string Detail => Definition.Detail;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool IsCore => Definition.IsCore;

    /// <summary>当前生效的按键；null 表示这个动作没绑按键</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGesture))]
    [NotifyPropertyChangedFor(nameof(GestureText))]
    private KeyGesture? _gesture;

    /// <summary>正在等用户按下组合键</summary>
    [ObservableProperty] private bool _isCapturing;

    public bool HasGesture => Gesture != null;

    public string GestureText => Gesture == null
        ? "未设置"
        : ShortcutCatalog.Format(Gesture.Key, Gesture.Modifiers);

    [RelayCommand]
    private void BeginCapture() => _owner.BeginCapture(this);

    [RelayCommand]
    private void Clear() => _owner.ClearGesture(this);
}

/// <summary>
/// 快捷键设置：动作目录、当前按键、录制改键与冲突检测。
/// 只负责数据，真正的按键派发在 MainWindow 里（那里才拿得到焦点）。
/// </summary>
public sealed partial class ShortcutSettingsViewModel : ObservableObject
{
    private readonly Func<AppSettings> _settings;
    private readonly Action _save;
    private readonly Action<string> _status;

    public ShortcutSettingsViewModel(Func<AppSettings> settings, Action save, Action<string> status)
    {
        _settings = settings;
        _save = save;
        _status = status;
    }

    public ObservableCollection<ShortcutRowViewModel> Rows { get; } = new();

    /// <summary>正在录制快捷键：MainWindow 会把按键交给 CaptureKey 而不是去执行命令</summary>
    [ObservableProperty] private bool _isCapturing;

    [ObservableProperty] private ShortcutRowViewModel? _capturingRow;

    /// <summary>录制浮层上的提示语（含冲突 / 按键不合法的原因）</summary>
    [ObservableProperty] private string _captureHint = string.Empty;

    /// <summary>按库里的设置重新铺一遍列表。要在主库 Load 之后调用，否则读到的是空的默认值</summary>
    public void Reload()
    {
        CancelCapture();
        Rows.Clear();
        foreach (var definition in ShortcutCatalog.All)
            Rows.Add(new ShortcutRowViewModel(definition, this) { Gesture = StoredGesture(definition) });
    }

    /// <summary>按键落在哪个动作上；没绑到这个组合就返回 null</summary>
    public ShortcutRowViewModel? Resolve(Key key, ModifierKeys modifiers)
        => Rows.FirstOrDefault(r => r.Gesture != null && r.Gesture.Key == key && r.Gesture.Modifiers == modifiers);

    public void BeginCapture(ShortcutRowViewModel row)
    {
        foreach (var other in Rows) other.IsCapturing = false;

        CapturingRow = row;
        row.IsCapturing = true;
        IsCapturing = true;
        CaptureHint = $"请按下用于「{row.Title}」的组合键（Esc 取消，Delete 清除）";
    }

    /// <summary>把一次真实按键记到正在录制的动作上</summary>
    public void CaptureKey(Key key, ModifierKeys modifiers)
    {
        var row = CapturingRow;
        if (row == null) return;

        if (key == Key.Escape)
        {
            CancelCapture();
            _status("已取消设置快捷键");
            return;
        }

        if (key is Key.Delete or Key.Back)
        {
            CancelCapture();
            ClearGesture(row);
            return;
        }

        // 只按住修饰键不放，继续等他按真正的那个键
        if (ShortcutCatalog.IsModifierKey(key)) return;

        var text = ShortcutCatalog.Format(key, modifiers);
        if (string.IsNullOrEmpty(text))
        {
            CaptureHint = "单独一个普通键不能当快捷键，请按住 Ctrl / Alt / Shift，或改用 F1~F12";
            return;
        }

        // 「清除」用的空串不算占用，只管真正绑了键的动作
        var conflict = Rows.FirstOrDefault(r => r != row && r.HasGesture && r.GestureText == text);
        if (conflict != null)
        {
            CaptureHint = $"「{text}」已经被「{conflict.Title}」占用，换一个组合键";
            _status($"快捷键冲突：{text} 已被「{conflict.Title}」占用");
            return;
        }

        row.Gesture = new KeyGesture(key, modifiers);
        Persist(row);
        CancelCapture();
        _status($"「{row.Title}」的快捷键已设为 {text}");
    }

    public void ClearGesture(ShortcutRowViewModel row)
    {
        row.Gesture = null;
        Persist(row);
        _status($"已清除「{row.Title}」的快捷键");
    }

    [RelayCommand]
    private void CancelCapture()
    {
        if (CapturingRow != null) CapturingRow.IsCapturing = false;

        CapturingRow = null;
        IsCapturing = false;
        CaptureHint = string.Empty;
    }

    /// <summary>清掉所有自定义改键，回到「四个常用键 + 其余留空」</summary>
    [RelayCommand]
    private void ResetDefaults()
    {
        CancelCapture();
        _settings().Shortcuts.Clear();

        foreach (var row in Rows) row.Gesture = StoredGesture(row.Definition);

        _save();
        _status("快捷键已恢复默认");
    }

    /// <summary>库里存了这个动作就用存的，否则用目录里的默认值；「没绑」返回 null</summary>
    private KeyGesture? StoredGesture(ShortcutDefinition definition)
    {
        var map = _settings().Shortcuts;
        var text = map.TryGetValue(definition.Id, out var stored) ? stored : definition.DefaultGesture;

        if (!ShortcutCatalog.TryParse(text, out var key, out var modifiers)) return null;
        return new KeyGesture(key, modifiers);
    }

    private void Persist(ShortcutRowViewModel row)
    {
        var text = row.Gesture == null
            ? string.Empty
            : ShortcutCatalog.Format(row.Gesture.Key, row.Gesture.Modifiers);

        _settings().Shortcuts[row.Id] = text;
        _save();
    }
}