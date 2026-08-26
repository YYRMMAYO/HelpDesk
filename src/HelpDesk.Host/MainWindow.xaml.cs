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
                MainContent.Content = new HomePage(_pluginManager, _pluginContext);
                break;
            case "Plugins":
                PageTitle.Text = "插件市场";
                MainContent.Content = new PluginMarketPage(_pluginManager);
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
}

/// <summary>
/// 插件上下文实现
/// </summary>
public class PluginContext : IPluginContext
{
    public string DataDirectory { get; }
    public double HostVersion => 1.0;
    
    public PluginContext()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HelpDesk", "Data");
        Directory.CreateDirectory(DataDirectory);
    }
    
    public void ShowStatus(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (Application.Current.MainWindow is MainWindow main)
                main.StatusBarText.Text = message;
        });
    }
    
    public void ShowNotification(string title, string message, NotificationType type = NotificationType.Info)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            MessageBox.Show($"{title}\n\n{message}", "HelpDesk", MessageBoxButton.OK, 
                type switch
                {
                    NotificationType.Error => MessageBoxImage.Error,
                    NotificationType.Warning => MessageBoxImage.Warning,
                    NotificationType.Success => MessageBoxImage.Information,
                    _ => MessageBoxImage.Information
                });
        });
    }
    
    public async Task<string> RunPowerShellAsync(string command)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-Command \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return "无法启动 PowerShell";
            
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            
            return string.IsNullOrEmpty(error) ? output : $"{output}\n错误: {error}";
        }
        catch (Exception ex)
        {
            return $"执行失败: {ex.Message}";
        }
    }
}
