using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HelpDesk.Contracts;

namespace HelpDesk.Host;

/// <summary>
/// 插件市场页面 - 从 GitHub 浏览和安装插件
/// </summary>
public class PluginMarketPage : UserControl
{
    private readonly PluginManager _pluginManager;
    private readonly StackPanel _pluginsList;
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progressBar;
    
    public PluginMarketPage(PluginManager pluginManager)
    {
        _pluginManager = pluginManager;
        
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var mainStack = new StackPanel { Margin = new Thickness(24) };
        
        // 标题和刷新按钮
        var headerPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var titleBlock = new TextBlock
        {
            Text = "插件市场",
            FontSize = 20, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        headerPanel.Children.Add(titleBlock);
        
        var refreshBtn = new Button
        {
            Content = "刷新列表",
            Style = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(12, 6, 12, 6)
        };
        refreshBtn.Click += async (_, _) => await LoadPluginsAsync();
        DockPanel.SetDock(refreshBtn, Dock.Right);
        headerPanel.Children.Add(refreshBtn);
        mainStack.Children.Add(headerPanel);
        
        // GitHub 配置提示
        var configCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(20, 245, 158, 11)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 245, 158, 11)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 16)
        };
        configCard.Child = new TextBlock
        {
            Text = "提示：请在 PluginManager 中配置 GitHubOwner 和 GitHubRepo，指向你的插件仓库",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(146, 64, 14)),
            TextWrapping = TextWrapping.Wrap
        };
        mainStack.Children.Add(configCard);
        
        // 进度条
        _progressBar = new ProgressBar
        {
            Height = 4,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed
        };
        mainStack.Children.Add(_progressBar);
        
        // 状态文本
        _statusText = new TextBlock
        {
            Text = "点击「刷新列表」获取可用插件",
            FontSize = 13,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        mainStack.Children.Add(_statusText);
        
        // 插件列表
        _pluginsList = new StackPanel();
        mainStack.Children.Add(_pluginsList);
        
        scroll.Content = mainStack;
        Content = scroll;
    }
    
    private async Task LoadPluginsAsync()
    {
        _progressBar.Visibility = Visibility.Visible;
        _progressBar.IsIndeterminate = true;
        _statusText.Text = "正在获取插件列表...";
        _pluginsList.Children.Clear();
        
        try
        {
            var plugins = await _pluginManager.GetAvailablePluginsAsync();
            
            if (plugins.Count == 0)
            {
                _statusText.Text = "未找到可用插件，请检查 GitHub 仓库配置";
                _pluginsList.Children.Add(new TextBlock
                {
                    Text = "可能的原因：\n1. GitHub 仓库未配置或不存在\n2. 插件文件夹中没有 metadata.json\n3. 网络连接问题",
                    FontSize = 13,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                });
            }
            else
            {
                _statusText.Text = $"找到 {plugins.Count} 个可用插件";
                
                foreach (var plugin in plugins)
                {
                    _pluginsList.Children.Add(CreatePluginCard(plugin));
                }
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"加载失败: {ex.Message}";
        }
        finally
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Visibility = Visibility.Collapsed;
        }
    }
    
    private Border CreatePluginCard(GitHubPluginInfo plugin)
    {
        var isInstalled = _pluginManager.InstalledPlugins.Any(p => p.Id == plugin.Id);
        
        var card = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        
        // 左侧信息
        var infoStack = new StackPanel();
        infoStack.Children.Add(new TextBlock
        {
            Text = plugin.Name,
            FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush")
        });
        infoStack.Children.Add(new TextBlock
        {
            Text = plugin.Description,
            FontSize = 13,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8)
        });
        
        var metaPanel = new StackPanel { Orientation = Orientation.Horizontal };
        metaPanel.Children.Add(CreateTag($"v{plugin.Version}", "#E2E8F0", "#475569"));
        metaPanel.Children.Add(CreateTag(plugin.Author, "#DBEAFE", "#1E40AF"));
        foreach (var tag in plugin.Tags.Take(3))
        {
            metaPanel.Children.Add(CreateTag(tag, "#F0FDF4", "#166534"));
        }
        infoStack.Children.Add(metaPanel);
        
        Grid.SetColumn(infoStack, 0);
        grid.Children.Add(infoStack);
        
        // 右侧按钮
        var btnStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0)
        };
        
        if (isInstalled)
        {
            btnStack.Children.Add(new TextBlock
            {
                Text = "已安装",
                FontSize = 13,
                Foreground = (Brush)FindResource("SuccessBrush"),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
        else
        {
            var installBtn = new Button
            {
                Content = "安装",
                Style = (Style)FindResource("PrimaryButton"),
                Padding = new Thickness(20, 6, 20, 6),
                Tag = plugin
            };
            installBtn.Click += async (s, e) =>
            {
                if (s is Button btn && btn.Tag is GitHubPluginInfo info)
                {
                    btn.IsEnabled = false;
                    btn.Content = "安装中...";
                    
                    var success = await _pluginManager.InstallPluginAsync(info);
                    
                    btn.Content = success ? "已安装" : "安装失败";
                    if (success)
                    {
                        _statusText.Text = $"插件 {info.Name} 安装成功";
                    }
                }
            };
            btnStack.Children.Add(installBtn);
        }
        
        Grid.SetColumn(btnStack, 1);
        grid.Children.Add(btnStack);
        
        card.Child = grid;
        return card;
    }
    
    private Border CreateTag(string text, string bgColor, string textColor)
    {
        var bg = (Color)ColorConverter.ConvertFromString(bgColor);
        var fg = (Color)ColorConverter.ConvertFromString(textColor);
        
        return new Border
        {
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new SolidColorBrush(fg)
            }
        };
    }
}
