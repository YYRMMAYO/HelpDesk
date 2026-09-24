using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HelpDesk.Contracts;

namespace HelpDesk.Plugins.TroubleshootGuide;

public class TroubleshootGuidePlugin : IPlugin
{
    public PluginMetadata Metadata => new()
    {
        Id = "troubleshoot-guide",
        Name = "故障排除指南",
        Description = "15 类常见 PC 故障的步骤化修复指南",
        Version = "1.1.0",
        Author = "HelpDesk",
        GitHubFolder = "TroubleshootGuide",
        Tags = ["故障", "修复", "指南", "蓝屏", "启动"],
        MinHostVersion = 1.1
    };
    
    private IPluginContext _context = null!;
    
    public void Init(IPluginContext context) => _context = context;
    public UserControl GetView() => new GuideView();
    public void OnActivated() { }
    public void OnDeactivated() { }
    public void Dispose() { }
}

public partial class GuideView : UserControl
{
    public GuideView()
    {
        InitializeComponent();
        LoadGuides();
    }
    
    private void LoadGuides()
    {
        var guides = new[]
        {
            new { Title = "电脑运行缓慢", Icon = "🐌", Color = "#F59E0B",
                  Steps = new[] { "检查任务管理器，找出占用资源最高的进程", "清理磁盘空间，保持至少 15% 可用空间", "禁用不必要的启动项", "运行 Windows 更新", "检查是否有恶意软件" } },
            new { Title = "蓝屏死机 (BSOD)", Icon = "💥", Color = "#EF4444",
                  Steps = new[] { "记录蓝屏停止代码 (STOP Code)", "检查是否在更新后发生", "进入安全模式排查", "更新或回滚驱动程序", "运行系统文件检查器 (sfc /scannow)" } },
            new { Title = "无法开机", Icon = "电源", Color = "#64748B",
                  Steps = new[] { "检查电源线和插座", "尝试不同电源插座", "检查显示器连接", "移除所有外部设备", "尝试重置 CMOS" } },
            new { Title = "WiFi 连接问题", Icon = "📶", Color = "#2563EB",
                  Steps = new[] { "重启路由器和电脑", "忘记网络后重新连接", "更新网卡驱动", "重置网络设置 (netsh winsock reset)", "检查 DNS 设置" } },
            new { Title = "电脑过热", Icon = "🌡️", Color = "#EF4444",
                  Steps = new[] { "清理电脑内部灰尘", "检查风扇是否正常运转", "确保通风口未被遮挡", "检查散热膏是否需要更换", "使用硬件监控软件检查温度" } },
            new { Title = "声音问题", Icon = "🔊", Color = "#8B5CF6",
                  Steps = new[] { "检查音量是否被静音", "检查音频输出设备选择", "重启 Windows Audio 服务", "更新音频驱动", "运行音频疑难解答" } },
            new { Title = "USB 端口失灵", Icon = "🔌", Color = "#10B981",
                  Steps = new[] { "尝试不同的 USB 端口", "重启电脑", "检查设备管理器中的 USB 控制器", "更新 USB 驱动", "卸载并重新安装 USB 控制器" } },
            new { Title = "Windows 更新失败", Icon = "🔄", Color = "#2563EB",
                  Steps = new[] { "运行 Windows 更新疑难解答", "清除更新缓存", "检查磁盘空间", "运行 DISM 和 SFC", "手动下载更新包安装" } },
            new { Title = "软件崩溃", Icon = "⚡", Color = "#F59E0B",
                  Steps = new[] { "检查软件是否有更新", "以管理员身份运行", "检查兼容性设置", "重新安装软件", "检查系统日志获取错误详情" } },
            new { Title = "硬盘噪音", Icon = "💾", Color = "#EF4444",
                  Steps = new[] { "立即备份重要数据", "使用 CrystalDiskInfo 检查健康状态", "检查硬盘连接线", "运行 chkdsk 命令", "考虑更换硬盘" } },
            new { Title = "电脑自动重启", Icon = "🔁", Color = "#8B5CF6",
                  Steps = new[] { "检查系统日志中的错误", "检查是否过热", "运行内存诊断工具", "检查电源供应", "更新驱动程序" } },
            new { Title = "显示器问题", Icon = "🖥️", Color = "#2563EB",
                  Steps = new[] { "检查视频线连接", "尝试不同的显示器", "更新显卡驱动", "检查分辨率设置", "检查显卡是否松动" } },
            new { Title = "打印机问题", Icon = "🖨️", Color = "#64748B",
                  Steps = new[] { "检查打印机电源和连接", "清除打印队列", "重启打印服务", "更新打印机驱动", "重新添加打印机" } },
            new { Title = "键盘问题", Icon = "⌨️", Color = "#10B981",
                  Steps = new[] { "检查键盘连接", "禁用筛选键和粘滞键", "更新键盘驱动", "尝试不同的 USB 端口", "测试键盘在 BIOS 中是否工作" } },
            new { Title = "系统文件损坏", Icon = "📁", Color = "#F59E0B",
                  Steps = new[] { "运行 DISM /Online /Cleanup-Image /RestoreHealth", "运行 sfc /scannow", "检查事件日志", "使用系统还原", "考虑重装系统" } },
        };
        
        foreach (var guide in guides)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20),
                Margin = new Thickness(0, 0, 0, 12)
            };
            
            var stack = new StackPanel();
            
            // 标题
            var color = (Color)ColorConverter.ConvertFromString(guide.Color);
            var headerPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            headerPanel.Children.Add(new TextBlock
            {
                Text = $"{guide.Icon} {guide.Title}",
                FontSize = 16, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59))
            });
            stack.Children.Add(headerPanel);
            
            // 步骤
            for (int i = 0; i < guide.Steps.Length; i++)
            {
                var stepPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                stepPanel.Children.Add(new TextBlock
                {
                    Text = $"{i + 1}.",
                    FontSize = 13, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(color),
                    Width = 24
                });
                stepPanel.Children.Add(new TextBlock
                {
                    Text = guide.Steps[i],
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                    TextWrapping = TextWrapping.Wrap
                });
                stack.Children.Add(stepPanel);
            }
            
            card.Child = stack;
            GuidesPanel.Children.Add(card);
        }
    }
}
