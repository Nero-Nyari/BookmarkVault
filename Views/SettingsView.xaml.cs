using System;
using System.Windows;
using System.Windows.Controls;
using BookmarkVault.Models;
using BookmarkVault.Services;
using BookmarkVault.ViewModels;

namespace BookmarkVault.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void RenameCategory_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if ((sender as FrameworkElement)?.DataContext is not CategoryNode category) return;
        vm.BeginCategoryEdit(category);
    }

    private void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if ((sender as FrameworkElement)?.DataContext is not CategoryNode category) return;

        var answer = MessageBox.Show(
            $"确定要删除分类「{category.Name}」吗？\n\n分类里的网址不会被删除，只是移到「未分类」。",
            "BookmarkVault", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK) vm.DeleteCategoryRequested(category);
    }

    /// <summary>手动指定用来打开网址的浏览器 exe（扫描不到时兜底）</summary>
    private void BrowseBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择浏览器的可执行文件",
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            var current = vm.Settings.BrowserPath;
            if (!string.IsNullOrWhiteSpace(current) && System.IO.File.Exists(current))
                dialog.InitialDirectory = System.IO.Path.GetDirectoryName(current);

            if (dialog.ShowDialog() == true) vm.SetBrowserPath(dialog.FileName);
        }
        catch (Exception ex)
        {
            JsonStore.Log("选择浏览器失败：" + ex.Message);
            vm.ShowMessage("无法打开文件选择器",
                "请手动把浏览器 exe 的路径填进设置文件。\n\n系统返回：" + ex.Message);
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择 Edge 配置目录（包含 Bookmarks 文件）",
                Multiselect = false
            };

            var current = vm.Settings.EdgeProfilePath;
            if (!string.IsNullOrWhiteSpace(current) && System.IO.Directory.Exists(current))
                dialog.InitialDirectory = current;

            if (dialog.ShowDialog() == true)
            {
                vm.Settings.EdgeProfilePath = dialog.FolderName;
                vm.SaveSettingsCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            JsonStore.Log("选择目录失败：" + ex.Message);
            vm.ShowMessage("无法打开目录选择器",
                "请手动把路径填进输入框。\n\n系统返回：" + ex.Message);
        }
    }
}
