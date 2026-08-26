using System.Diagnostics;
using System.IO;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HelpDesk.Contracts;

namespace HelpDesk.Plugins.SystemDiagnostics;

public class SystemDiagnosticsPlugin : IPlugin
{
    public PluginMetadata Metadata => new()
    {
        Id = "system-diagnostics",
        Name = "系统诊断",
        Description = "检测 CPU、内存、磁盘使用状态，监控温度，评估系统健康度",
        Version = "1.0.0",
        Author = "HelpDesk",
        GitHubFolder = "SystemDiagnostics",
        Tags = ["诊断", "性能", "硬件", "监控"]
    };
    
    private IPluginContext _context = null!;
    
    public void Init(IPluginContext context)
    {
        _context = context;
    }
    
    public UserControl GetView() => new DiagnosticsView();
    
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void Dispose() { }
}

/// <summary>
/// 系统诊断视图
/// </summary>
public partial class DiagnosticsView : UserControl
{
    private DispatcherTimer? _refreshTimer;
    
    public DiagnosticsView()
    {
        InitializeComponent();
        Loaded += (_, _) => StartMonitoring();
        Unloaded += (_, _) => StopMonitoring();
    }
    
    private void StartMonitoring()
    {
        RefreshData();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshData();
        _refreshTimer.Start();
    }
    
    private void StopMonitoring()
    {
        _refreshTimer?.Stop();
    }
    
    private void RefreshData()
    {
        try
        {
            // CPU 使用率
            var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            cpuCounter.NextValue();
            Thread.Sleep(100);
            var cpuUsage = cpuCounter.NextValue();
            CpuProgressBar.Value = cpuUsage;
            CpuText.Text = $"{cpuUsage:F1}%";
            CpuStatus.Text = GetStatusText(cpuUsage);
            CpuStatus.Foreground = GetStatusBrush(cpuUsage);
            
            // 内存使用
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get())
            {
                var totalKB = Convert.ToDouble(obj["TotalVisibleMemorySize"]);
                var freeKB = Convert.ToDouble(obj["FreePhysicalMemory"]);
                var usedKB = totalKB - freeKB;
                var usagePercent = (usedKB / totalKB) * 100;
                
                MemoryProgressBar.Value = usagePercent;
                MemoryText.Text = $"{usedKB / 1024 / 1024:F1} GB / {totalKB / 1024 / 1024:F1} GB";
                MemoryStatus.Text = GetStatusText(usagePercent);
                MemoryStatus.Foreground = GetStatusBrush(usagePercent);
            }
            
            // 磁盘使用率
            var drive = new DriveInfo("C");
            var diskUsedPercent = ((drive.TotalSize - drive.AvailableFreeSpace) / (double)drive.TotalSize) * 100;
            DiskProgressBar.Value = diskUsedPercent;
            DiskText.Text = $"{(drive.TotalSize - drive.AvailableFreeSpace) / 1024 / 1024 / 1024:F1} GB / {drive.TotalSize / 1024 / 1024 / 1024:F1} GB";
            DiskStatus.Text = GetStatusText(diskUsedPercent);
            DiskStatus.Foreground = GetStatusBrush(diskUsedPercent);
            
            // 健康评分
            var healthScore = CalculateHealthScore(cpuUsage, memoryProgressBarValue, diskUsedPercent);
            HealthScoreText.Text = healthScore.ToString();
            HealthScoreText.Foreground = healthScore switch
            {
                >= 80 => new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                >= 60 => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
                _ => new SolidColorBrush(Color.FromRgb(239, 68, 68))
            };
            HealthStatus.Text = healthScore switch
            {
                >= 80 => "良好",
                >= 60 => "一般",
                _ => "需要关注"
            };
        }
        catch { }
    }
    
    private double memoryProgressBarValue => MemoryProgressBar.Value;
    
    private int CalculateHealthScore(double cpu, double memory, double disk)
    {
        var cpuScore = cpu < 50 ? 100 : cpu < 80 ? 70 : 40;
        var memScore = memory < 60 ? 100 : memory < 80 ? 70 : 40;
        var diskScore = disk < 70 ? 100 : disk < 90 ? 70 : 40;
        return (cpuScore + memScore + diskScore) / 3;
    }
    
    private string GetStatusText(double value) => value switch
    {
        < 50 => "正常",
        < 80 => "偏高",
        _ => "警告"
    };
    
    private Brush GetStatusBrush(double value) => value switch
    {
        < 50 => new SolidColorBrush(Color.FromRgb(16, 185, 129)),
        < 80 => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
        _ => new SolidColorBrush(Color.FromRgb(239, 68, 68))
    };
}
