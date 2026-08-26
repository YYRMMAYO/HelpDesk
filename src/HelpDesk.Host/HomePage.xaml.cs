using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HelpDesk.Contracts;

namespace HelpDesk.Host;

/// <summary>
/// 首页 - 显示已安装插件的快速入口
/// </summary>
public class HomePage : UserControl
{
    private readonly PluginManager _pluginManager;
    private readonly PluginContext _pluginContext;
    private readonly WrapPanel _pluginsPanel;
    
    public HomePage(PluginManager pluginManager, PluginContext pluginContext)
    {
        _pluginManager = pluginManager;
        _pluginContext = pluginContext;
        
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var mainStack = new StackPanel { Margin = new Thickness(24) };
        
        // 欢迎区域
        var welcomeCard = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 0, 24),
            Background = new SolidColorBrush(Color.FromRgb(37, 99, 235))
        };
        var welcomeStack = new StackPanel();
        welcomeStack.Children.Add(new TextBlock
        {
            Text = "欢迎使用 HelpDesk",
            FontSize = 24, FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        });
        welcomeStack.Children.Add(new TextBlock
        {
            Text = "模块化的 PC 帮助平台 - 选择你需要的功能模块",
            FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(191, 219, 254)),
            Margin = new Thickness(0, 8, 0, 0)
        });
        welcomeCard.Child = welcomeStack;
        mainStack.Children.Add(welcomeCard);
        
        // 快速操作
        var actionsCard = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 0, 24)
        };
        var actionsPanel = new StackPanel();
        actionsPanel.Children.Add(new TextBlock
        {
            Text = "快速操作",
            FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        
        var buttonsPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        buttonsPanel.Children.Add(CreateActionButton("系统诊断", "检测 CPU、内存、磁盘状态", "#2563EB"));
        buttonsPanel.Children.Add(CreateActionButton("网络工具", "WiFi 诊断、速度测试", "#10B981"));
        buttonsPanel.Children.Add(CreateActionButton("系统清理", "清理临时文件、管理启动项", "#F59E0B"));
        buttonsPanel.Children.Add(CreateActionButton("故障指南", "常见问题修复指南", "#8B5CF6"));
        actionsPanel.Children.Add(buttonsPanel);
        actionsCard.Child = actionsPanel;
        mainStack.Children.Add(actionsCard);
        
        // 已安装插件
        var installedCard = new Border
        {
            Style = (Style)FindResource("CardBorder")
        };
        var installedStack = new StackPanel();
        installedStack.Children.Add(new TextBlock
        {
            Text = "已安装插件",
            FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        
        _pluginsPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        installedStack.Children.Add(_pluginsPanel);
        installedCard.Child = installedStack;
        mainStack.Children.Add(installedCard);
        
        scroll.Content = mainStack;
        Content = scroll;
        
        Loaded += (_, _) => RefreshPlugins();
    }
    
    private Border CreateActionButton(string title, string desc, string colorHex)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorHex);
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 12, 8),
            Width = 200,
            Cursor = Cursors.Hand
        };
        
        var stack = new StackPanel();
        var iconBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8)
        };
        iconBorder.Child = new TextBlock
        {
            Text = title.Substring(0, 1),
            FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(iconBorder);
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush")
        });
        stack.Children.Add(new TextBlock
        {
            Text = desc,
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        
        border.Child = stack;
        return border;
    }
    
    private void RefreshPlugins()
    {
        _pluginsPanel.Children.Clear();
        
        if (_pluginManager.InstalledPlugins.Count == 0)
        {
            _pluginsPanel.Children.Add(new TextBlock
            {
                Text = "暂无已安装的插件，前往插件市场下载",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                FontSize = 13
            });
            return;
        }
        
        foreach (var plugin in _pluginManager.InstalledPlugins)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 12, 8),
                Width = 200
            };
            
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = plugin.Name,
                FontSize = 14, FontWeight = FontWeights.SemiBold,
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
            _pluginsPanel.Children.Add(card);
        }
    }
}
