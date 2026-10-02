using System.Windows;
using System.Windows.Controls;
using BookmarkVault.ViewModels;

namespace BookmarkVault.Views;

public partial class BlacklistView : UserControl
{
    private BlacklistPanelViewModel? _panel;

    public BlacklistView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => _panel = e.NewValue as BlacklistPanelViewModel;
    }

    private void New_Click(object sender, RoutedEventArgs e) => _panel?.NewCommand.Execute(null);

    /// <summary>Ctrl+F 落在这一页时把光标放进本页搜索框</summary>
    public void FocusSearch()
    {
        PageSearch.Focus();
        PageSearch.SelectAll();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var row = _panel?.SelectedRow;
        if (row == null) return;

        var extra = row.IsManual
            ? "此操作不可撤销。"
            : "它会被记入忽略名单，下次更新订阅不会再拉回来。";
        var answer = MessageBox.Show($"确定要删除黑名单条目 {row.Pattern} 吗？{extra}",
            "BookmarkVault", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK) _panel!.DeleteCommand.Execute(row);
    }
}