using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using HelpDesk.Contracts;

namespace HelpDesk.Host;

public partial class MainWindow : Window
{
    private readonly PluginManager _pluginManager;
    private readonly PluginContext _pluginContext;

    public MainWindow()
    {
        InitializeComponent();

        _pluginManager = new PluginManager();
        _pluginContext = new PluginContext();

        VersionText.Text = $"v{PluginManager.HostVersion:F1}.0";

        // 默认显示首页
        ShowPage("Home");
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            ShowPage(tag);
        }
    }

    private void ShowPage(string pageTag)
    {
        switch (pageTag)
        {
            case "Home":
                PageTitle.Text = "首页";
                MainContent.Content = new HomePage(_pluginManager, _pluginContext, ShowPlugin, ShowMarket);
                break;
            case "Plugins":
                PageTitle.Text = "插件市场";
                MainContent.Content = new PluginMarketPage(_pluginManager, _pluginContext);
                break;
            case "Installed":
                PageTitle.Text = "已安装插件";
                MainContent.Content = new InstalledPluginsPage(_pluginManager, _pluginContext);
                break;
            case "Settings":
                PageTitle.Text = "设置";
                MainContent.Content = new SettingsPage(_pluginManager);
                break;
        }
    }

    /// <summary>
    /// 打开指定插件：切到「已安装」页并直接加载它。首页的快捷入口用它。
    /// </summary>
    private void ShowPlugin(string pluginId)
    {
        // 先把左侧导航切过去：RadioButton 的 Checked 事件会同步重建一次页面，
        // 因此必须在它之后再赋我们这份「带初始插件」的页面，否则会被覆盖掉。
        NavInstalled.IsChecked = true;

        PageTitle.Text = "已安装插件";
        MainContent.Content = new InstalledPluginsPage(_pluginManager, _pluginContext, pluginId);
    }

    /// <summary>跳到插件市场（首页里点了「去安装」的卡片）。</summary>
    private void ShowMarket() => NavPlugins.IsChecked = true;
}

/// <summary>
/// 插件上下文实现：把宿主能力暴露给插件，所有高危能力都收敛到 ProcessRunner。
/// </summary>
public class PluginContext : IPluginContext
{
    public string DataDirectory { get; }

    /// <summary>宿主版本号，与 <see cref="PluginManager.HostVersion"/> 保持一致。</summary>
    public double HostVersion => PluginManager.HostVersion;

    public bool IsElevated => ProcessRunner.IsElevated;

    public PluginContext()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HelpDesk", "Data");
        Directory.CreateDirectory(DataDirectory);
    }

    public void ShowStatus(string message)
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.Invoke(() =>
        {
            if (app.MainWindow is MainWindow main)
                main.StatusBarText.Text = message;
        });
    }

    public void ShowNotification(string title, string message, NotificationType type = NotificationType.Info)
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.Invoke(() =>
        {
            MessageBox.Show(app.MainWindow, $"{message}", title, MessageBoxButton.OK,
                type switch
                {
                    NotificationType.Error => MessageBoxImage.Error,
                    NotificationType.Warning => MessageBoxImage.Warning,
                    NotificationType.Success => MessageBoxImage.Information,
                    _ => MessageBoxImage.Information
                });
        });
    }

    public Task<ProcessResult> RunPowerShellAsync(
        string script,
        bool elevated = false,
        int timeoutMs = 60000,
        CancellationToken ct = default)
        => ProcessRunner.RunPowerShellAsync(script, elevated, timeoutMs, ct);

    public Task<ProcessResult> StartProcessAsync(
        string fileName,
        string arguments,
        bool elevated = false,
        CancellationToken ct = default)
        => ProcessRunner.StartProcessAsync(fileName, arguments, elevated, ct);
}
