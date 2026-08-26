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
        Description = "WiFi 状态检测、DNS 诊断、网络速度测试",
        Version = "1.0.0",
        Author = "HelpDesk",
        GitHubFolder = "NetworkToolkit",
        Tags = ["网络", "WiFi", "DNS", "速度测试"]
    };
    
    private IPluginContext _context = null!;
    
    public void Init(IPluginContext context) => _context = context;
    public UserControl GetView() => new NetworkView();
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void Dispose() { }
}

public partial class NetworkView : UserControl
{
    public NetworkView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadNetworkInfoAsync();
    }
    
    private async Task LoadNetworkInfoAsync()
    {
        // 连接状态
        var hasNetwork = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        StatusIndicator.Fill = new SolidColorBrush(hasNetwork ? Color.FromRgb(16, 185, 129) : Color.FromRgb(239, 68, 68));
        StatusText.Text = hasNetwork ? "网络连接正常" : "未检测到网络连接";
        
        // 网络适配器信息
        var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
        var activeInterface = interfaces.FirstOrDefault(i => i.OperationalStatus == OperationalStatus.Up && 
            i.NetworkInterfaceType != NetworkInterfaceType.Loopback);
        
        if (activeInterface != null)
        {
            AdapterName.Text = activeInterface.Name;
            AdapterType.Text = activeInterface.NetworkInterfaceType.ToString();
            Speed.Text = $"{activeInterface.Speed / 1_000_000} Mbps";
            
            var ipProps = activeInterface.GetIPProperties();
            var ipv4 = ipProps.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            IPAddress.Text = ipv4?.Address.ToString() ?? "未获取";
        }
        
        // DNS 测试
        await TestDnsAsync();
        
        // Ping 测试
        await TestPingAsync();
    }
    
    private async Task TestDnsAsync()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var addresses = await Dns.GetHostAddressesAsync("www.baidu.com");
            sw.Stop();
            
            DnsResult.Text = $"解析成功 ({sw.ElapsedMilliseconds}ms)";
            DnsResult.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            DnsDetail.Text = addresses.Length > 0 ? addresses[0].ToString() : "无结果";
        }
        catch (Exception ex)
        {
            DnsResult.Text = "解析失败";
            DnsResult.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            DnsDetail.Text = ex.Message;
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
                PingResult.Text = $"Ping 成功 ({sw.ElapsedMilliseconds}ms)";
                PingResult.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                PingDetail.Text = $"TTL: {reply.Options?.Ttl}, 大小: {reply.Buffer.Length} bytes";
            }
            else
            {
                PingResult.Text = $"Ping 失败: {reply.Status}";
                PingResult.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
        }
        catch (Exception ex)
        {
            PingResult.Text = "Ping 失败";
            PingResult.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            PingDetail.Text = ex.Message;
        }
    }
}
