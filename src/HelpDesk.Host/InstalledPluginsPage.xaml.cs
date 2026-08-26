using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HelpDesk.Contracts;

namespace HelpDesk.Host;

/// <summary>
/// 已安装插件页面 - 管理已安装的插件
/// </summary>
public class InstalledPluginsPage : UserControl
{
    private readonly PluginManager _pluginManager;
    private readonly PluginContext _pluginContext;
    private readonly StackPanel _pluginsList;
    private readonly ContentControl _pluginContentArea;
    
    public InstalledPluginsPage(PluginManager pluginManager, PluginContext pluginContext)
    {
        _pluginManager = pluginManager;
        _pluginContext = pluginContext;
        
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        
        // 顶部插件列表
        var listBorder = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(24, 16, 24, 0),
            MaxHeight = 250
        };
        
        var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var listStack = new StackPanel();
        listStack.Children.Add(new TextBlock
        {
            Text = "已安装插件",
            FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });
        
        _pluginsList = new StackPanel();
        listStack.Children.Add(_pluginsList);
        listScroll.Content = listStack;
        listBorder.Child = listScroll;
        Grid.SetRow(listBorder, 0);
        grid.Children.Add(listBorder);
        
        // 插件内容区域
        _pluginContentArea = new ContentControl
        {
            Margin = new Thickness(24, 16, 24, 16)
        };
        Grid.SetRow(_pluginContentArea, 1);
        grid.Children.Add(_pluginContentArea);
        
        Content = grid;
        
        Loaded += (_, _) => RefreshPlugins();
    }
    
    private void RefreshPlugins()
    {
        _pluginsList.Children.Clear();
        _pluginContentArea.Content = null;
        
        if (_pluginManager.InstalledPlugins.Count == 0)
        {
            _pluginsList.Children.Add(new TextBlock
            {
                Text = "暂无已安装的插件，请前往插件市场下载",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                FontSize = 13,
                Margin = new Thickness(0, 4, 0, 0)
            });
            return;
        }
        
        foreach (var plugin in _pluginManager.InstalledPlugins)
        {
            _pluginsList.Children.Add(CreatePluginItem(plugin));
        }
    }
    
    private Border CreatePluginItem(InstalledPluginInfo pluginInfo)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = Cursors.Hand
        };
        
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        
        // 左侧信息
        var infoStack = new StackPanel();
        infoStack.Children.Add(new TextBlock
        {
            Text = pluginInfo.Name,
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush")
        });
        infoStack.Children.Add(new TextBlock
        {
            Text = $"v{pluginInfo.Version} | 安装于 {pluginInfo.InstalledAt:yyyy-MM-dd}",
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 2, 0, 0)
        });
        
        Grid.SetColumn(infoStack, 0);
        grid.Children.Add(infoStack);
        
        // 右侧操作按钮
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        
        // 加载按钮
        var loadBtn = new Button
        {
            Content = "加载",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(12, 4, 12, 4),
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Tag = pluginInfo.Id
        };
        loadBtn.Click += async (s, e) =>
        {
            if (s is Button btn && btn.Tag is string id)
            {
                btn.IsEnabled = false;
                btn.Content = "加载中...";
                
                var plugin = _pluginManager.LoadPlugin(id);
                if (plugin != null)
                {
                    plugin.Init(_pluginContext);
                    var view = plugin.GetView();
                    _pluginContentArea.Content = view;
                    plugin.OnActivated();
                    btn.Content = "已加载";
                }
                else
                {
                    btn.Content = "加载失败";
                    MessageBox.Show("插件加载失败，请检查插件文件是否完整", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        };
        btnPanel.Children.Add(loadBtn);
        
        // 卸载按钮
        var uninstallBtn = new Button
        {
            Content = "卸载",
            Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 4, 12, 4),
            FontSize = 12,
            Cursor = Cursors.Hand,
            Tag = pluginInfo.Id
        };
        uninstallBtn.Click += (s, e) =>
        {
            if (s is Button btn && btn.Tag is string id)
            {
                var result = MessageBox.Show($"确定要卸载插件 {pluginInfo.Name} 吗？", "确认卸载",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    _pluginManager.UninstallPlugin(id);
                    RefreshPlugins();
                }
            }
        };
        btnPanel.Children.Add(uninstallBtn);
        
        Grid.SetColumn(btnPanel, 1);
        grid.Children.Add(btnPanel);
        
        card.Child = grid;
        return card;
    }
}
