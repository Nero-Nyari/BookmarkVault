using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BookmarkVault.Services;
using BookmarkVault.ViewModels;

namespace BookmarkVault;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureCreated();
        DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            JsonStore.Log("未观察的任务异常：" + args.Exception);
            args.SetObserved();
        };

        try
        {
            var library = new LibraryService();
            library.Load();

            var vault = new AccountVault(library);
            var classifier = new SiteClassifier();
            var blacklist = new BlacklistService();
            blacklist.Load();
            var security = new SecurityInspector(blacklist);
            var icons = new IconCache();
            var probe = new ProbeService(classifier, security);
            var covers = new CoverFetcher();

            _viewModel = new MainViewModel(library, vault, icons, probe, covers, blacklist);

            // 开了自动更新就在后台悄悄拉一次两个订阅源，不挡启动
            if (library.Database.Settings.AutoUpdateBlacklist)
                _ = blacklist.RefreshFeedsAsync();

            var window = new MainWindow(_viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            JsonStore.Log("启动失败：" + ex);
            MessageBox.Show("应用启动失败：" + ex.Message + "\n\n详细日志：" + AppPaths.LogFile,
                "BookmarkVault", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        JsonStore.Log("未处理异常：" + e.Exception);
        MessageBox.Show("发生了一个错误：" + e.Exception.Message + "\n\n已写入日志：" + AppPaths.LogFile,
            "BookmarkVault", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _viewModel?.SaveAll();
        }
        catch (Exception ex)
        {
            JsonStore.Log("退出保存失败：" + ex.Message);
        }
        base.OnExit(e);
    }
}
