using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BookmarkVault.Models;
using BookmarkVault.Services;
using BookmarkVault.ViewModels;

namespace BookmarkVault;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        InitializeComponent();
        DataContext = _vm;
        _vm.Initialize();
    }

    private static BookmarkCardViewModel? CardOf(object sender)
        => (sender as FrameworkElement)?.DataContext as BookmarkCardViewModel;

    // ---------- 网址右键菜单 ----------

    private void Card_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is not ContextMenu menu) return;
        if (fe.DataContext is not BookmarkCardViewModel card) return;

        // 「移动到分类」的子菜单是动态生成的
        foreach (var item in menu.Items)
        {
            if (item is not MenuItem mi || !Equals(mi.Tag, "MoveMenu")) continue;
            mi.Items.Clear();
            mi.Items.Add(BuildMenuItem("＋ 新建分类…", () => _vm.BeginCategoryEdit(null)));

            // 「分类」子菜单里勾选/取消勾选，一个网址可以同时勾多个
            foreach (var category in _vm.ProjectCategories)
            {
                var captured = category;
                var entry = BuildMenuItem(category.Name, () => _vm.ToggleCategory(card, captured));
                entry.IsChecked = card.Model.CategoryIds.Contains(category.Id);
                mi.Items.Add(entry);
            }

            if (_vm.ProjectCategories.Count == 0)
                mi.Items.Add(BuildMenuItem("（还没有项目分类，去设置页新建）", null));
        }

        // 「用其他浏览器打开」：临时换个浏览器，不动设置里的默认选择
        foreach (var item in menu.Items)
        {
            if (item is not MenuItem mi || !Equals(mi.Tag, "BrowserMenu")) continue;
            mi.Items.Clear();
            mi.Items.Add(BuildMenuItem("系统默认浏览器", () => _vm.OpenWith(card, null)));

            foreach (var browser in _vm.Browsers)
            {
                var captured = browser;
                mi.Items.Add(BuildMenuItem(browser.Name, () => _vm.OpenWith(card, captured.Path)));
            }

            if (_vm.Browsers.Count == 0)
                mi.Items.Add(BuildMenuItem("（没扫到浏览器，去设置页手动指定）", null));
        }

        // 「移入隐私」和「移出隐私」按当前状态二选一
        foreach (var item in menu.Items)
        {
            if (item is not MenuItem mi) continue;
            if (Equals(mi.Tag, "PrivacyIn"))
                mi.Visibility = card.Model.IsPrivate ? Visibility.Collapsed : Visibility.Visible;
            else if (Equals(mi.Tag, "PrivacyOut"))
                mi.Visibility = card.Model.IsPrivate ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static MenuItem BuildMenuItem(string header, Action? action)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = Application.Current.Resources["DarkMenuItem"] as Style,
            IsEnabled = action != null
        };
        if (action != null) item.Click += (_, _) => action();
        return item;
    }

    private void CardOpen_Click(object sender, RoutedEventArgs e)
    {
        var card = CardOf(sender);
        if (card != null) _vm.OpenCommand.Execute(card);
    }

    private void CardFavorite_Click(object sender, RoutedEventArgs e)
    {
        var card = CardOf(sender);
        if (card != null) _vm.ToggleFavoriteCommand.Execute(card);
    }

    private void CardCopy_Click(object sender, RoutedEventArgs e)
    {
        var card = CardOf(sender);
        if (card != null) _vm.CopyUrlCommand.Execute(card);
    }

    private async void CardProbe_Click(object sender, RoutedEventArgs e)
    {
        var card = CardOf(sender);
        if (card != null) await _vm.ProbeOneCommand.ExecuteAsync(card);
    }

    private void CardClearCategory_Click(object sender, RoutedEventArgs e)
    {
        var card = CardOf(sender);
        if (card != null) _vm.MoveTo(card, null);
    }

    private void CardEditAccount_Click(object sender, RoutedEventArgs e)
    {
        var card = CardOf(sender);
        if (card != null) _vm.EditAccountCommand.Execute(card);
    }

    private void CardEdit_Click(object sender, RoutedEventArgs e)
    {
        var card = CardOf(sender);
        if (card != null) _vm.BeginBookmarkEdit(card);
    }

    private void CardDelete_Click(object sender, RoutedEventArgs e)
    {
        var card = CardOf(sender);
        if (card != null) _vm.DeleteBookmarkRequested(card);
    }

    // ---------- 隐私 ----------

    private void CardPrivacyIn_Click(object sender, RoutedEventArgs e)
    {
        var card = CardOf(sender);
        if (card != null) _vm.MoveToPrivacyCommand.Execute(card);
    }

    private void CardPrivacyOut_Click(object sender, RoutedEventArgs e)
    {
        var card = CardOf(sender);
        if (card != null) _vm.RemoveFromPrivacyCommand.Execute(card);
    }

    // ---------- 封面图 ----------

    private void ImportCover_Click(object sender, RoutedEventArgs e)
    {
        var card = CardOf(sender);
        if (card == null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择详情页封面图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            _vm.ImportCover(card, dialog.FileName);
    }

    // ---------- 卡片封面：选来源 → 裁剪 ----------

    private void ImportCardCover_Click(object sender, RoutedEventArgs e)
    {
        var card = CardOf(sender);
        if (card != null) _vm.BeginCardCoverSetup(card);
    }

    private void PickNewCardCover_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择封面源图",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            _vm.BeginCardCoverCropFromFile(dialog.FileName);
    }

    private Point _cropDragStart;
    private bool _cropDragging;

    private void CropCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _cropDragging = true;
        _cropDragStart = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse();
    }

    private void CropCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_cropDragging) return;

        var point = e.GetPosition(this);
        _vm.DragCrop(point.X - _cropDragStart.X, point.Y - _cropDragStart.Y);
        _cropDragStart = point;
    }

    private void CropCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _cropDragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void CropCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _vm.ZoomCrop(e.Delta);
        e.Handled = true;
    }

    // ---------- 归类看板：从侧栏拖到分类文件夹 ----------

    /// <summary>拖拽载荷的类型就是书签卡片 VM 本身，落点直接用它判断收不收</summary>
    private static readonly Type CardDragFormat = typeof(BookmarkCardViewModel);

    private Point _dragStart;
    private BookmarkCardViewModel? _dragCandidate;

    private void SidebarList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = FindCard(e.OriginalSource as DependencyObject);
    }

    private void SidebarList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null) return;
        // 只有停在归类看板时才拖得动，别的视图没有落点
        if (_vm.CurrentHomeView != HomeViewMode.Organize) return;

        var now = e.GetPosition(null);
        // 没超过系统拖拽阈值就还当成普通点击，别把选中操作抢了
        if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var card = _dragCandidate;
        _dragCandidate = null;

        // 按下侧栏时看板已经被选中动作顶掉了，先把选中清掉让它回来，再开始拖
        _vm.BeginOrganizeDrag();
        UpdateLayout();

        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(CardDragFormat, card), DragDropEffects.Move);
        }
        finally
        {
            // DoDragDrop 是同步的，走到这里说明拖拽已经结束（含中途 Esc / 落在空白处），
            // 统一清一次高亮，免得最后一个悬停过的文件夹一直亮着
            _vm.SetDropTarget(null);
        }
    }

    private void Folder_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (!e.Data.GetDataPresent(CardDragFormat))
        {
            e.Effects = DragDropEffects.None;
            _vm.SetDropTarget(null);
            return;
        }

        e.Effects = DragDropEffects.Move;
        _vm.SetDropTarget((sender as FrameworkElement)?.DataContext as CategoryFolderViewModel);
    }

    private void Folder_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _vm.SetDropTarget(null);
    }

    private void Folder_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        var folder = (sender as FrameworkElement)?.DataContext as CategoryFolderViewModel;
        var card = e.Data.GetData(CardDragFormat) as BookmarkCardViewModel;

        if (folder == null) return;
        if (card == null)
        {
            _vm.ReportUnsupportedDrop();
            return;
        }

        _vm.DropIntoFolder(card, folder);
    }

    /// <summary>从命中的元素往上找到所属的侧栏条目</summary>
    private static BookmarkCardViewModel? FindCard(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is ListBoxItem item) return item.DataContext as BookmarkCardViewModel;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    // ---------- 筛选浮层：再点一次筛选按钮 / 按 Esc 关闭 ----------

    private void FilterButton_Click(object sender, RoutedEventArgs e)
        => _vm.ToggleFilterCommand.Execute(null);

    /// <summary>
    /// 浮层用 StaysOpen=True，点外部要自己关。
    /// 这样打开时不会抢走鼠标捕获，筛选按钮的 Click 才能正常触发，来回点都不会打架。
    /// </summary>
    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsFilterOpen) return;
        if (e.OriginalSource is not DependencyObject source) return;

        // 浮层内部（包括浮层里下拉框弹出的那一层）不算外部
        if (IsInTree(source, FilterPopup)) return;
        // 点筛选按钮：开关交给按钮自己的 Click，这里不要抢
        if (IsInTree(source, FilterButton)) return;

        _vm.CloseFilterCommand.Execute(null);
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // StaysOpen=True 的浮层不会自己跟着失焦关掉，切换程序时手动收起来
        if (_vm.IsFilterOpen) _vm.CloseFilterCommand.Execute(null);
    }

    /// <summary>沿视觉树往上找，视觉父级取不到时退回逻辑树（Popup 的内容就挂在这条链上）</summary>
    private static bool IsInTree(DependencyObject? node, DependencyObject root)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, root)) return true;

            var parent = node is Visual ? VisualTreeHelper.GetParent(node) : null;
            node = parent ?? LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    // ---------- 键盘：快捷键派发 + Esc 逐层关闭 ----------

    /// <summary>
    /// 所有快捷键都在这里派发（不是 InputBindings）。
    /// PreviewKeyDown 从窗口往下钻，比绑定更早拿到按键，焦点在输入框里也照样生效。
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = RealKey(e);

        // 正在录快捷键：这一次按键交给设置页，不去执行命令
        if (_vm.Shortcuts.IsCapturing && _vm.MainView == MainViewMode.Settings)
        {
            _vm.Shortcuts.CaptureKey(key, Keyboard.Modifiers);
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            // 依次：关最上层的浮层 → 清空搜索 → 取消选中
            if (TryCloseTopLayer()) { e.Handled = true; return; }
            if (!string.IsNullOrEmpty(_vm.SearchText)) { _vm.SearchText = string.Empty; e.Handled = true; return; }
            if (_vm.SelectedCard != null) { _vm.SelectedCard = null; e.Handled = true; }
            return;
        }

        var row = _vm.Shortcuts.Resolve(key, Keyboard.Modifiers);
        if (row == null) return;

        if (row.Id == ShortcutIds.FocusSearch) FocusSearch();
        else if (!_vm.RunShortcut(row.Id)) return;

        e.Handled = true;
    }

    /// <summary>按住 Alt 时 e.Key 会是 Key.System，真正的键在 SystemKey 里</summary>
    private static Key RealKey(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

    /// <summary>Esc：按层级从最上面那个浮层开始关；一层都没开返回 false</summary>
    private bool TryCloseTopLayer()
    {
        if (_vm.IsCoverCropOpen) { _vm.CancelCoverCropCommand.Execute(null); return true; }
        if (_vm.IsCoverSourcePickerOpen) { _vm.CancelCoverPickerCommand.Execute(null); return true; }
        if (_vm.IsAddBookmarkOpen) { _vm.CancelAddBookmarkCommand.Execute(null); return true; }
        if (_vm.IsEditBookmarkOpen) { _vm.CancelBookmarkEditCommand.Execute(null); return true; }
        if (_vm.IsConfirmOpen) { _vm.ConfirmCancelCommand.Execute(null); return true; }
        if (_vm.IsPromptOpen) { _vm.ClosePromptCommand.Execute(null); return true; }
        if (_vm.IsCategoryEditorOpen) { _vm.CancelCategoryEditorCommand.Execute(null); return true; }
        if (_vm.IsMessageOpen) { _vm.CloseMessageCommand.Execute(null); return true; }
        if (_vm.IsFilterOpen) { _vm.CloseFilterCommand.Execute(null); return true; }

        return false;
    }

    /// <summary>Ctrl+F：光标跳到当前这一页该用的搜索框</summary>
    private void FocusSearch()
    {
        if (_vm.MainView == MainViewMode.Blacklist)
        {
            BlacklistHost.FocusSearch();
            return;
        }

        SidebarSearch.Focus();
        SidebarSearch.SelectAll();
    }

    private void RemoveCover_Click(object sender, RoutedEventArgs e)
    {
        var card = CardOf(sender);
        if (card != null) _vm.RemoveCover(card);
    }
}
