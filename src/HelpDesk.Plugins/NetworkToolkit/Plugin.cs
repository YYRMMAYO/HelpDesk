using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HelpDesk.Contracts;

namespace HelpDesk.Plugins.NetworkToolkit;

public class NetworkToolkitPlugin : IPlugin
{
    public PluginMetadata Metadata => new()
    {
        Id = "network-toolkit",
        Name = "网络工具",
        Description = "网速检测（下载/上传/延迟/抖动）、WiFi 状态、DNS 诊断、Ping 连通性",
        Version = "1.1.0",
        Author = "HelpDesk",
        GitHubFolder = "NetworkToolkit",
        Tags = ["网络", "WiFi", "DNS", "网速", "测速"],
        MinHostVersion = 1.1
    };

    private IPluginContext _context = null!;
    private NetworkView? _view;

    public void Init(IPluginContext context) => _context = context;

    public UserControl GetView() => _view ??= new NetworkView(_context);

    /// <summary>
    /// 用户切走时取消正在进行的测速——否则一次千兆测速会在后台继续拉上百兆流量。
    /// </summary>
    public void OnDeactivated() => _view?.CancelRunningTest();

    public void OnActivated() { }

    public void Dispose() => _view?.CancelRunningTest();
}

public partial class NetworkView : UserControl
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(16, 185, 129));
    private static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(245, 158, 11));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(239, 68, 68));
    private static readonly Brush Slate = new SolidColorBrush(Color.FromRgb(100, 116, 139));
    private static readonly Brush Dark = new SolidColorBrush(Color.FromRgb(30, 41, 59));

    private readonly IPluginContext? _context;
    private CancellationTokenSource? _speedTestCts;

    public NetworkView() : this(null) { }

    public NetworkView(IPluginContext? context)
    {
        _context = context;
        InitializeComponent();

        Loaded += async (_, _) => await LoadNetworkInfoAsync();
        Unloaded += (_, _) => CancelRunningTest();
    }

    /// <summary>取消正在进行的测速（离开页面或插件被停用时调用）。</summary>
    public void CancelRunningTest() => _speedTestCts?.Cancel();

    // ──────────────────────────────────────────────────── 基础网络信息

    private async Task LoadNetworkInfoAsync()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            // 「有默认网关」才说明这个网卡真的在承担上网通道；只判断 Up 会把
            // VPN / WSL / 虚拟网卡之类的假接口选中，用户就会看到一堆没意义的地址。
            var active = interfaces
                .Where(i => i.OperationalStatus == OperationalStatus.Up)
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(i => new { Interface = i, Props = SafeGetProperties(i) })
                .Where(x => x.Props != null)
                .Where(x => x.Props!.GatewayAddresses
                    .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                .OrderByDescending(x => x.Interface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .Select(x => x.Interface)
                .FirstOrDefault();

            var hasNetwork = active != null || NetworkInterface.GetIsNetworkAvailable();
            StatusIndicator.Fill = hasNetwork ? Green : Red;
            StatusText.Text = active != null
                ? "网络连接正常，可以正常上网"
                : hasNetwork ? "检测到网络，但没有找到能上网的网卡" : "未检测到网络连接";
            StatusText.Foreground = Dark;

            if (active != null)
            {
                AdapterName.Text = active.Name;
                AdapterType.Text = DescribeInterfaceType(active.NetworkInterfaceType);
                Speed.Text = active.Speed > 0
                    ? $"{active.Speed / 1_000_000} Mbps（协商速率）"
                    : "未知";

                var props = SafeGetProperties(active);
                var ipv4 = props?.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                IPAddress.Text = ipv4?.Address.ToString() ?? "未获取到 IP";

                if (ipv4 != null && ipv4.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    // APIPA 意味着没拿到 DHCP 地址，用户看到「有 IP」会一头雾水
                    IPAddress.Text = $"{ipv4.Address}（169.254 开头，说明没从路由器拿到地址）";
                    IPAddress.Foreground = Orange;
                }
                else
                {
                    IPAddress.Foreground = Dark;
                }
            }
            else
            {
                AdapterName.Text = "未找到";
                AdapterType.Text = "--";
                Speed.Text = "--";
                IPAddress.Text = "--";
            }

            await TestDnsAsync();
            await TestPingAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"检测失败：{ex.Message}";
            StatusText.Foreground = Red;
        }
    }

    /// <summary>
    /// <see cref="NetworkInterface.GetIPProperties"/> 在网卡被拔掉/驱动异常时会抛异常，
    /// 直接调用会让整页检测失败，所以包一层。
    /// </summary>
    private static IPInterfaceProperties? SafeGetProperties(NetworkInterface ni)
    {
        try { return ni.GetIPProperties(); }
        catch { return null; }
    }

    private static string DescribeInterfaceType(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => "无线网卡 (WiFi)",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "有线网卡",
        NetworkInterfaceType.Ppp => "拨号 / VPN 连接",
        _ => type.ToString()
    };

    private async Task TestDnsAsync()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var addresses = await Dns.GetHostAddressesAsync("www.baidu.com");
            sw.Stop();

            DnsResult.Text = $"解析成功（{sw.ElapsedMilliseconds} ms）";
            DnsResult.Foreground = Green;
            DnsDetail.Text = addresses.Length > 0 ? $"解析结果：{addresses[0]}" : "未返回结果";
        }
        catch (Exception ex)
        {
            DnsResult.Text = "解析失败（打不开网页通常是因为这个）";
            DnsResult.Foreground = Red;
            DnsDetail.Text = $"建议把 DNS 改成 223.5.5.5（阿里）或 119.29.29.29（腾讯）。原始错误：{ex.Message}";
        }
    }

    private async Task TestPingAsync()
    {
        try
        {
            using var ping = new Ping();
            var sw = Stopwatch.StartNew();
            var reply = await ping.SendPingAsync("www.baidu.com", 5000);
            sw.Stop();

            if (reply.Status == IPStatus.Success)
            {
                PingResult.Text = $"Ping 成功（{reply.RoundtripTime} ms）";
                PingResult.Foreground = Green;
                PingDetail.Text = $"TTL：{reply.Options?.Ttl} · 往返时间：{sw.ElapsedMilliseconds} ms";
            }
            else
            {
                PingResult.Text = $"Ping 失败：{DescribePingStatus(reply.Status)}";
                PingResult.Foreground = Orange;
                PingDetail.Text = "部分网络会屏蔽 Ping，这不代表一定上不了网，请以「网速检测」和「DNS」结果为准。";
            }
        }
        catch (Exception ex)
        {
            PingResult.Text = "Ping 测试无法执行";
            PingResult.Foreground = Orange;
            PingDetail.Text = "部分网络会屏蔽 Ping，这不代表一定上不了网。原始错误：" + ex.Message;
        }
    }

    private static string DescribePingStatus(IPStatus status) => status switch
    {
        IPStatus.TimedOut => "超时（对方没有响应）",
        IPStatus.DestinationHostUnreachable => "目标主机不可达",
        IPStatus.DestinationNetworkUnreachable => "目标网络不可达",
        _ => status.ToString()
    };

    // ──────────────────────────────────────────────────── 网速检测

    private async void StartSpeedTest_Click(object sender, RoutedEventArgs e)
    {
        if (_speedTestCts != null) return;

        _speedTestCts = new CancellationTokenSource();
        var ct = _speedTestCts.Token;

        StartSpeedTestButton.IsEnabled = false;
        CancelSpeedTestButton.IsEnabled = true;
        CustomSpeedUrl.IsEnabled = false;
        SpeedProgress.Value = 0;
        SpeedGrade.Text = "测速中…";
        SpeedGrade.Foreground = Slate;
        SpeedStatus.Text = "正在准备测速…";
        SpeedDetail.Text = "";
        ResetMetricValues();

        try
        {
            var customUrl = string.IsNullOrWhiteSpace(CustomSpeedUrl.Text) ? null : CustomSpeedUrl.Text.Trim();
            if (customUrl != null && !IsHttpUrl(customUrl))
            {
                SpeedStatus.Text = "自定义测速地址必须是 http:// 或 https:// 开头的网址";
                return;
            }

            var progress = new Progress<SpeedTestProgress>(p =>
            {
                SpeedProgress.Value = Math.Clamp(p.Percent, 0, 100);
                SpeedStatus.Text = p.Detail;
            });

            var report = await SpeedTestService.RunAsync(customUrl, progress, ct);
            RenderReport(report);
        }
        catch (OperationCanceledException)
        {
            SpeedGrade.Text = "已取消";
            SpeedGrade.Foreground = Slate;
            SpeedStatus.Text = "测速已取消";
        }
        catch (Exception ex)
        {
            SpeedGrade.Text = "测速失败";
            SpeedGrade.Foreground = Red;
            SpeedStatus.Text = $"测速失败：{ex.Message}";
            _context?.ShowStatus("网速检测失败");
        }
        finally
        {
            _speedTestCts?.Dispose();
            _speedTestCts = null;
            StartSpeedTestButton.IsEnabled = true;
            CancelSpeedTestButton.IsEnabled = false;
            CustomSpeedUrl.IsEnabled = true;
        }
    }

    private void CancelSpeedTest_Click(object sender, RoutedEventArgs e)
    {
        CancelRunningTest();
        CancelSpeedTestButton.IsEnabled = false;
        SpeedStatus.Text = "正在取消测速…";
    }

    private void ResetMetricValues()
    {
        LatencyValue.Text = "--";
        LatencyValue.Foreground = Dark;
        JitterValue.Text = "--";
        DownloadValue.Text = "--";
        DownloadValue.Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235));
        UploadValue.Text = "--";
        UploadValue.Foreground = new SolidColorBrush(Color.FromRgb(139, 92, 246));
    }

    private void RenderReport(SpeedTestReport report)
    {
        LatencyValue.Text = report.LatencyMs is { } latency ? $"{latency:F0}" : "--";
        LatencyValue.Foreground = report.LatencyMs switch
        {
            null => Slate,
            <= 60 => Green,
            <= 150 => Orange,
            _ => Red
        };

        JitterValue.Text = report.JitterMs is { } jitter ? $"{jitter:F0}" : "--";
        JitterValue.Foreground = report.JitterMs switch
        {
            null => Slate,
            <= 20 => Green,
            <= 60 => Orange,
            _ => Red
        };

        DownloadValue.Text = report.DownloadMbps is { } download ? $"{download:F1}" : "不可用";
        DownloadValue.Foreground = report.DownloadMbps switch
        {
            null => Red,
            >= 20 => new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            _ => Orange
        };

        UploadValue.Text = report.UploadMbps is { } upload ? $"{upload:F1}" : "不可用";
        UploadValue.Foreground = report.UploadMbps is null ? Red : new SolidColorBrush(Color.FromRgb(139, 92, 246));

        var grade = GradeOf(report.DownloadMbps);
        SpeedGrade.Text = grade.Text;
        SpeedGrade.Foreground = grade.Brush;
        SpeedStatus.Text = report.DownloadMbps is null
            ? "没有测出下载速度：当前网络可能访问不了测速节点，可以试试在下面填入自定义测速地址"
            : $"下载 {report.DownloadMbps:F1} Mbps · 上传 "
              + (report.UploadMbps is { } up ? $"{up:F1} Mbps" : "不可用");

        var lines = new List<string> { $"延迟来源：{report.LatencySource}" };
        if (report.DownloadEndpoint != null) lines.Add($"下载节点：{report.DownloadEndpoint}");
        if (report.UploadEndpoint != null) lines.Add($"上传节点：{report.UploadEndpoint}");
        lines.AddRange(report.Notes);
        SpeedDetail.Text = string.Join("\n", lines);

        SpeedProgress.Value = 100;
        _context?.ShowStatus(report.DownloadMbps is { } mbps
            ? $"测速完成：下载 {mbps:F1} Mbps"
            : "测速完成：未测出下载速度");
    }

    private static (string Text, Brush Brush) GradeOf(double? mbps) => mbps switch
    {
        null => ("测速失败", Red),
        >= 100 => ("极快", Green),
        >= 50 => ("良好", Green),
        >= 20 => ("一般", Orange),
        _ => ("偏慢", Red)
    };

    private static bool IsHttpUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
