using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HelpDesk.Contracts;

namespace HelpDesk.Plugins.SystemDiagnostics;

public class SystemDiagnosticsPlugin : IPlugin
{
    public PluginMetadata Metadata => new()
    {
        Id = "system-diagnostics",
        Name = "系统诊断",
        Description = "检测 CPU、内存、磁盘使用状态，给出直观的系统健康评分",
        Version = "1.1.0",
        Author = "HelpDesk",
        GitHubFolder = "SystemDiagnostics",
        Tags = ["诊断", "性能", "硬件", "监控"],
        MinHostVersion = 1.1
    };

    private IPluginContext _context = null!;
    private DiagnosticsView? _view;

    public void Init(IPluginContext context) => _context = context;

    public UserControl GetView() => _view ??= new DiagnosticsView();

    public void OnActivated() { }

    /// <summary>离开页面就停止采样，不要留一个后台循环继续跑。</summary>
    public void OnDeactivated() => _view?.StopMonitoring();

    public void Dispose() => _view?.StopMonitoring();
}

/// <summary>
/// 系统诊断视图。
///
/// <para><b>相比旧实现改了什么、为什么</b></para>
/// <list type="bullet">
/// <item>旧实现在 UI 线程的定时器里做 <c>Thread.Sleep(100)</c> 并同步查询 WMI，
/// 界面每 2 秒卡一次；现在采样全部在后台线程，UI 只负责显示结果。</item>
/// <item>旧实现每一拍都 <c>new PerformanceCounter(...)</c> 且从不释放，长时间开着会持续泄漏
/// 句柄；现在改用 <c>GetSystemTimes</c> / <c>GlobalMemoryStatusEx</c> 两个系统调用，
/// 既没有句柄泄漏，也不再依赖 System.Management（插件包因此小了一个数量级）。</item>
/// </list>
/// </summary>
public partial class DiagnosticsView : UserControl
{
    private const int SampleIntervalMs = 1500;

    private CancellationTokenSource? _cts;

    public DiagnosticsView()
    {
        InitializeComponent();
        Loaded += (_, _) => StartMonitoring();
        Unloaded += (_, _) => StopMonitoring();
    }

    public void StartMonitoring()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = MonitorLoopAsync(_cts.Token);
    }

    public void StopMonitoring()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        // 记一下磁盘是哪个盘，界面标题要显示真实盘符而不是写死 C:
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        await Dispatcher.InvokeAsync(() => DiskTitle.Text = $"磁盘使用 ({systemDrive.TrimEnd('\\')})");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snapshot = Sample(systemDrive);
                if (snapshot != null)
                    await Dispatcher.InvokeAsync(() => Render(snapshot));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 采样失败（例如权限受限）不应该让循环退出
            }

            try
            {
                await Task.Delay(SampleIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private sealed class Snapshot
    {
        public double? CpuPercent { get; init; }
        public double MemoryPercent { get; init; }
        public double UsedMemoryGb { get; init; }
        public double TotalMemoryGb { get; init; }
        public double DiskPercent { get; init; }
        public double UsedDiskGb { get; init; }
        public double TotalDiskGb { get; init; }
    }

    /// <summary>采集一次数据（在后台线程执行，不碰任何 UI 元素）。</summary>
    private Snapshot? Sample(string systemDrive)
    {
        try
        {
            var memory = ReadMemory();
            var disk = ReadDisk(systemDrive);

            return new Snapshot
            {
                CpuPercent = ReadCpuPercent(),
                MemoryPercent = memory.Percent,
                UsedMemoryGb = memory.UsedGb,
                TotalMemoryGb = memory.TotalGb,
                DiskPercent = disk.Percent,
                UsedDiskGb = disk.UsedGb,
                TotalDiskGb = disk.TotalGb
            };
        }
        catch
        {
            return null;
        }
    }

    private void Render(Snapshot snapshot)
    {
        // CPU：第一次采样只用来建立基线，所以要等一拍才有数据
        if (snapshot.CpuPercent is { } cpu)
        {
            CpuProgressBar.Value = cpu;
            CpuText.Text = $"{cpu:F1}%";
            CpuStatus.Text = GetStatusText(cpu);
            CpuStatus.Foreground = GetStatusBrush(cpu);
        }
        else
        {
            CpuText.Text = "--";
            CpuStatus.Text = "正在采样…";
            CpuStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
        }

        MemoryProgressBar.Value = snapshot.MemoryPercent;
        MemoryText.Text = $"{snapshot.UsedMemoryGb:F1} GB / {snapshot.TotalMemoryGb:F1} GB";
        MemoryStatus.Text = GetStatusText(snapshot.MemoryPercent);
        MemoryStatus.Foreground = GetStatusBrush(snapshot.MemoryPercent);

        DiskProgressBar.Value = snapshot.DiskPercent;
        DiskText.Text = $"{snapshot.UsedDiskGb:F1} GB / {snapshot.TotalDiskGb:F1} GB";
        DiskStatus.Text = GetStatusText(snapshot.DiskPercent);
        DiskStatus.Foreground = GetStatusBrush(snapshot.DiskPercent);

        // 健康评分：只用确实采到的项，避免 CPU 还没基线时就算出一个假的分数
        var scores = new List<int>();
        if (snapshot.CpuPercent is { } cpuValue) scores.Add(ScoreOf(cpuValue, 50, 80));
        scores.Add(ScoreOf(snapshot.MemoryPercent, 60, 80));
        scores.Add(ScoreOf(snapshot.DiskPercent, 70, 90));

        var score = (int)Math.Round(scores.Average());
        HealthScoreText.Text = score.ToString();
        HealthScoreText.Foreground = score switch
        {
            >= 80 => new SolidColorBrush(Color.FromRgb(16, 185, 129)),
            >= 60 => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
            _ => new SolidColorBrush(Color.FromRgb(239, 68, 68))
        };
        HealthStatus.Text = score switch
        {
            >= 80 => "状态良好，可以正常使用",
            >= 60 => "一般，建议清理一下磁盘空间",
            _ => "需要关注，至少有一项长期偏高"
        };
    }

    private static int ScoreOf(double value, double goodBelow, double warnBelow)
        => value < goodBelow ? 100 : value < warnBelow ? 70 : 40;

    private static string GetStatusText(double value) => value switch
    {
        < 50 => "正常",
        < 80 => "偏高",
        _ => "警告"
    };

    private static Brush GetStatusBrush(double value) => value switch
    {
        < 50 => new SolidColorBrush(Color.FromRgb(16, 185, 129)),
        < 80 => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
        _ => new SolidColorBrush(Color.FromRgb(239, 68, 68))
    };

    // ──────────────────────────────────────────────── 采样原语

    private (ulong Idle, ulong Total)? _previousCpuSample;

    /// <summary>
    /// CPU 使用率：两次 GetSystemTimes 之间的 (总时间 - 空闲时间) / 总时间。
    /// 与任务管理器同源，不需要 WMI 也不需要性能计数器。
    /// </summary>
    private double? ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return null;

        var idle = ToUInt64(idleTime);
        var total = ToUInt64(kernelTime) + ToUInt64(userTime);

        if (_previousCpuSample is not { } previous)
        {
            _previousCpuSample = (idle, total);
            return null;
        }

        var idleDelta = idle - previous.Idle;
        var totalDelta = total - previous.Total;
        _previousCpuSample = (idle, total);

        if (totalDelta == 0) return null;
        return Math.Clamp((1.0 - idleDelta / (double)totalDelta) * 100.0, 0, 100);
    }

    private static (double Percent, double UsedGb, double TotalGb) ReadMemory()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return (0, 0, 0);

        var total = status.ullTotalPhys;
        var used = total - status.ullAvailPhys;
        var percent = total == 0 ? 0 : used * 100.0 / total;

        return (percent, used / 1024.0 / 1024 / 1024, total / 1024.0 / 1024 / 1024);
    }

    private static (double Percent, double UsedGb, double TotalGb) ReadDisk(string systemDrive)
    {
        try
        {
            var drive = new DriveInfo(systemDrive);
            if (!drive.IsReady || drive.TotalSize == 0) return (0, 0, 0);

            var used = drive.TotalSize - drive.AvailableFreeSpace;
            return (used * 100.0 / drive.TotalSize,
                    used / 1024.0 / 1024 / 1024,
                    drive.TotalSize / 1024.0 / 1024 / 1024);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private static ulong ToUInt64(SystemFileTime time) => ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemFileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out SystemFileTime lpIdleTime,
        out SystemFileTime lpKernelTime,
        out SystemFileTime lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
