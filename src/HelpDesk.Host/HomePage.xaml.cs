using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HelpDesk.Contracts;

namespace HelpDesk.Host;

/// <summary>
/// 首页 - 显示功能入口与已安装插件的快速入口
/// </summary>
public class HomePage : UserControl
{
    /// <summary>一个内置功能入口的「期望插件」描述。</summary>
    private sealed record FeatureCard(string Title, string Description, string ColorHex, string PluginId);

    /// <summary>
    /// 内置功能清单。注意这些都是「期望」：卡片会根据插件是否已安装切换成
    /// 「打开」或「去插件市场安装」两种状态，所以首页永远反映真实情况，
    /// 不会出现点了没反应的假按钮。
    /// </summary>
    private static readonly FeatureCard[] Features =
    [
        new("网络与网速", "测网速、Ping 延迟、WiFi 与 DNS 诊断", "#10B981", "network-toolkit"),
        new("软件卸载", "安全卸载已装软件、清理卸载残留", "#2563EB", "software-uninstaller"),
        new("系统诊断", "CPU、内存、磁盘实时状态与健康评分", "#8B5CF6", "system-diagnostics"),
        new("系统清理", "扫描并清理临时文件，释放磁盘空间", "#F59E0B", "system-cleaner"),
        new("故障指南", "15 类常见电脑故障的排查步骤", "#0EA5E9", "troubleshoot-guide"),
    ];

    private readonly PluginManager _pluginManager;
    private readonly PluginContext _pluginContext;
    private readonly Action<string> _openPlugin;
    private readonly Action _openMarket;
    private readonly WrapPanel _featuresPanel = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel _installedPanel = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _summaryText = new();

    public HomePage(
        PluginManager pluginManager,
        PluginContext pluginContext,
        Action<string> openPlugin,
        Action openMarket)
    {
        _pluginManager = pluginManager;
        _pluginContext = pluginContext;
        _openPlugin = openPlugin;
        _openMarket = openMarket;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var mainStack = new StackPanel { Margin = new Thickness(24) };

        // ── 欢迎区域 ───────────────────────────────────────────────
        var welcomeCard = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 0, 20),
            Background = new SolidColorBrush(Color.FromRgb(37, 99, 235))
        };
        var welcomeStack = new StackPanel();
        welcomeStack.Children.Add(new TextBlock
        {
            Text = "欢迎使用 HelpDesk 电脑助手",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        });
        welcomeStack.Children.Add(new TextBlock
        {
            Text = "需要什么功能就装什么功能，不需要的不会占用你的电脑",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(191, 219, 254)),
            Margin = new Thickness(0, 8, 0, 0)
        });

        _summaryText.FontSize = 13;
        _summaryText.Foreground = new SolidColorBrush(Color.FromRgb(219, 234, 254));
        _summaryText.Margin = new Thickness(0, 12, 0, 0);
        welcomeStack.Children.Add(_summaryText);

        welcomeCard.Child = welcomeStack;
        mainStack.Children.Add(welcomeCard);

        // ── 功能入口 ───────────────────────────────────────────────
        var featuresCard = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 0, 20)
        };
        var featuresStack = new StackPanel();
        featuresStack.Children.Add(new TextBlock
        {
            Text = "功能入口",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        featuresStack.Children.Add(new TextBlock
        {
            Text = "灰底的表示还没安装，点一下就能去装",
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        featuresStack.Children.Add(_featuresPanel);
        featuresCard.Child = featuresStack;
        mainStack.Children.Add(featuresCard);

        // ── 已安装插件 ─────────────────────────────────────────────
        var installedCard = new Border { Style = (Style)FindResource("CardBorder") };
        var installedStack = new StackPanel();
        installedStack.Children.Add(new TextBlock
        {
            Text = "已安装的插件",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        installedStack.Children.Add(_installedPanel);
        installedCard.Child = installedStack;
        mainStack.Children.Add(installedCard);

        scroll.Content = mainStack;
        Content = scroll;

        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _summaryText.Text = _pluginManager.InstalledPlugins.Count == 0
            ? "还没有安装任何功能模块，去「插件市场」挑一个开始吧"
            : $"已安装 {_pluginManager.InstalledPlugins.Count} 个功能模块 · 管理员权限："
              + (_pluginContext.IsElevated ? "已获取" : "未获取（卸载软件时会单独提示）");

        _featuresPanel.Children.Clear();
        foreach (var feature in Features)
            _featuresPanel.Children.Add(CreateFeatureCard(feature));

        _installedPanel.Children.Clear();
        if (_pluginManager.InstalledPlugins.Count == 0)
        {
            _installedPanel.Children.Add(new TextBlock
            {
                Text = "暂无已安装的插件，点上方任意功能卡片即可前往插件市场下载",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                FontSize = 13
            });
            return;
        }

        foreach (var plugin in _pluginManager.InstalledPlugins)
            _installedPanel.Children.Add(CreateInstalledCard(plugin));
    }

    private Border CreateFeatureCard(FeatureCard feature)
    {
        var installed = _pluginManager.InstalledPlugins.FirstOrDefault(p => p.Id == feature.PluginId);
        var accent = (Color)ColorConverter.ConvertFromString(feature.ColorHex);

        // 未安装 → 用浅灰降级视觉，明确传达「还没装」
        var background = installed != null
            ? new SolidColorBrush(Color.FromRgb(248, 250, 252))
            : new SolidColorBrush(Color.FromRgb(243, 244, 246));
        var border = new SolidColorBrush(installed != null
            ? Color.FromRgb(226, 232, 240)
            : Color.FromRgb(229, 231, 235));

        var card = new Border
        {
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 12, 12),
            Width = 236,
            Cursor = Cursors.Hand,
            ToolTip = installed != null
                ? $"打开「{installed.Name}」"
                : $"「{feature.Title}」尚未安装，点击前往插件市场"
        };

        var stack = new StackPanel();

        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        headerPanel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(installed != null ? (byte)30 : (byte)18, accent.R, accent.G, accent.B)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = feature.Title[..1],
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accent)
            }
        });
        headerPanel.Children.Add(new TextBlock
        {
            Text = installed != null ? "打开" : "去安装",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = installed != null
                ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                : new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(headerPanel);

        stack.Children.Add(new TextBlock
        {
            Text = feature.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 8, 0, 0)
        });
        stack.Children.Add(new TextBlock
        {
            Text = feature.Description,
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        card.Child = stack;
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (installed != null) _openPlugin(installed.Id);
            else _openMarket();
        };

        return card;
    }

    private Border CreateInstalledCard(InstalledPluginInfo plugin)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 12, 12),
            Width = 200,
            Cursor = Cursors.Hand
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = plugin.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush")
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"v{plugin.Version}",
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 4, 0, 0)
        });

        card.Child = stack;
        card.MouseLeftButtonUp += (_, _) => _openPlugin(plugin.Id);
        return card;
    }
}
