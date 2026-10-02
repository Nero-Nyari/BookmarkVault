using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using BookmarkVault.ViewModels;

namespace BookmarkVault.Views;

public partial class AccountsView : UserControl
{
    private AccountsPanelViewModel? _panel;

    public AccountsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_panel != null) _panel.PropertyChanged -= OnPanelPropertyChanged;

        _panel = e.NewValue as AccountsPanelViewModel;

        if (_panel != null) _panel.PropertyChanged += OnPanelPropertyChanged;
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // PasswordBox 的 Password 不能双向绑定，打开表单时手动回填
        if (e.PropertyName == nameof(AccountsPanelViewModel.IsEditorOpen) && _panel?.IsEditorOpen == true)
            PasswordInput.Password = _panel.DraftPassword;
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        if (_panel != null) _panel.DraftPassword = PasswordInput.Password;
    }

    private void New_Click(object sender, RoutedEventArgs e) => _panel?.NewCommand.Execute(null);

    private void Save_Click(object sender, RoutedEventArgs e) => _panel?.SaveCommand.Execute(null);

    private void Edit_Click(object sender, RoutedEventArgs e) => _panel?.EditCommand.Execute(_panel.SelectedRow);

    private void Reveal_Click(object sender, RoutedEventArgs e)
        => _panel?.ToggleRevealCommand.Execute(_panel.SelectedRow);

    private void CopyUser_Click(object sender, RoutedEventArgs e)
        => _panel?.CopyUsernameCommand.Execute(_panel.SelectedRow);

    private void CopyPass_Click(object sender, RoutedEventArgs e)
        => _panel?.CopyPasswordCommand.Execute(_panel.SelectedRow);

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var row = _panel?.SelectedRow;
        if (row == null) return;

        var answer = MessageBox.Show($"确定要删除 {row.Domain} 的账号记录吗？此操作不可撤销。",
            "BookmarkVault", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK) _panel!.DeleteCommand.Execute(row);
    }
}
