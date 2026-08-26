using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HelpDesk.Contracts;

namespace HelpDesk.Plugins.SystemCleaner;

public class SystemCleanerPlugin : IPlugin
{
    public PluginMetadata Metadata => new()
    {
        Id = "system-cleaner",
        Name = "系统清理",
        Description = "清理临时文件、管理启动项、释放磁盘空间",
        Version = "1.0.0",
        Author = "HelpDesk",
        GitHubFolder = "SystemCleaner",
        Tags = ["清理", "临时文件", "启动项", "磁盘"]
    };
    
    private IPluginContext _context = null!;
    
    public void Init(IPluginContext context) => _context = context;
    public UserControl GetView() => new CleanerView();
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void Dispose() { }
}

public partial class CleanerView : UserControl
{
    private long _tempSize;
    
    public CleanerView()
    {
        InitializeComponent();
        Loaded += (_, _) => ScanTempFiles();
    }
    
    private void ScanTempFiles()
    {
        try
        {
            _tempSize = 0;
            var tempPath = Path.GetTempPath();
            
            if (Directory.Exists(tempPath))
            {
                foreach (var file in Directory.GetFiles(tempPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        _tempSize += new FileInfo(file).Length;
                    }
                    catch { }
                }
            }
            
            TempSize.Text = FormatSize(_tempSize);
            TempStatus.Text = _tempSize > 100 * 1024 * 1024 ? "建议清理" : "正常";
            TempStatus.Foreground = new SolidColorBrush(_tempSize > 100 * 1024 * 1024 ? 
                Color.FromRgb(245, 158, 11) : Color.FromRgb(16, 185, 129));
        }
        catch (Exception ex)
        {
            TempStatus.Text = $"扫描失败: {ex.Message}";
        }
    }
    
    private async void CleanTempFiles_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("确定要清理临时文件吗？", "确认", 
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        
        if (result != MessageBoxResult.Yes) return;
        
        try
        {
            var tempPath = Path.GetTempPath();
            var deleted = 0;
            
            foreach (var file in Directory.GetFiles(tempPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch { }
            }
            
            MessageBox.Show($"已清理 {deleted} 个临时文件", "完成", 
                MessageBoxButton.OK, MessageBoxImage.Information);
            ScanTempFiles();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"清理失败: {ex.Message}", "错误", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    
    private string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB"
    };
}
