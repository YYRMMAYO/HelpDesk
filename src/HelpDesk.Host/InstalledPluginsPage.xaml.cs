using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HelpDesk.Contracts;

namespace HelpDesk.Host;

/// <summary>
/// 已安装插件页面 - 管理并打开已安装的插件
/// </summary>
public class InstalledPluginsPage : UserControl
{
    private readonly PluginManager _pluginManager;
    private readonly PluginContext _pluginContext;
    private readonly string? _initialPluginId;
    private readonly StackPanel _pluginsList;
    private readonly ContentControl _pluginContentArea;
    private readonly TextBlock _loadedTitle;

    /// <summary>当前已加载到内容区的插件 Id，用于停用上一个插件。</summary>
    private string? _activePluginId;

    public InstalledPluginsPage(
        PluginManager pluginManager,
        PluginContext pluginContext,
        string? initialPluginId = null)
    {
        _pluginManager = pluginManager;
        _pluginContext = pluginContext;
        _initialPluginId = initialPluginId;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 顶部插件列表
        var listBorder = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(24, 16, 24, 0),
            MaxHeight = 240
        };

        var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var listStack = new StackPanel();
        listStack.Children.Add(new TextBlock
        {
            Text = "已安装插件",
            FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        listStack.Children.Add(new TextBlock
        {
            Text = "点「打开」即可使用该功能；卸载只会删除插件本身，不影响你的个人文件",
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });

        _pluginsList = new StackPanel();
        listStack.Children.Add(_pluginsList);
        listScroll.Content = listStack;
        listBorder.Child = listScroll;
        Grid.SetRow(listBorder, 0);
        grid.Children.Add(listBorder);

        // 插件内容区域
        var contentBorder = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(24, 16, 24, 16)
        };
        var contentStack = new StackPanel();
        _loadedTitle = new TextBlock
        {
            Text = "尚未打开任何功能",
            FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        contentStack.Children.Add(_loadedTitle);

        _pluginContentArea = new ContentControl();
        contentStack.Children.Add(_pluginContentArea);
        contentBorder.Child = contentStack;
        Grid.SetRow(contentBorder, 1);
        grid.Children.Add(contentBorder);

        Content = grid;

        Loaded += (_, _) => OnLoadedOnce();
        Unloaded += (_, _) => DeactivateActivePlugin();
    }

    private void OnLoadedOnce()
    {
        RefreshPlugins();

        if (!string.IsNullOrWhiteSpace(_initialPluginId))
            LoadPluginIntoView(_initialPluginId);
    }

    private void RefreshPlugins()
    {
        _pluginsList.Children.Clear();

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
            _pluginsList.Children.Add(CreatePluginItem(plugin));
    }

    private UIElement CreatePluginItem(InstalledPluginInfo pluginInfo)
    {
        var stack = new StackPanel();

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var infoStack = new StackPanel();
        infoStack.Children.Add(new TextBlock
        {
            Text = pluginInfo.Name,
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush")
        });

        var meta = $"v{pluginInfo.Version} · 安装于 {pluginInfo.InstalledAt:yyyy-MM-dd}";
        if (pluginInfo.Verified) meta += " · 已校验";
        else meta += " · 未提供校验清单";

        infoStack.Children.Add(new TextBlock
        {
            Text = meta,
            FontSize = 12,
            Foreground = pluginInfo.Verified
                ? (Brush)FindResource("SuccessBrush")
                : (Brush)FindResource("WarningBrush"),
            Margin = new Thickness(0, 2, 0, 0)
        });

        Grid.SetColumn(infoStack, 0);
        grid.Children.Add(infoStack);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var openBtn = new Button
        {
            Content = "打开",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(14, 5, 14, 5),
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Tag = pluginInfo.Id
        };
        openBtn.Click += (_, _) => LoadPluginIntoView(pluginInfo.Id);
        btnPanel.Children.Add(openBtn);

        var uninstallBtn = new Button
        {
            Content = "卸载",
            Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 5, 14, 5),
            FontSize = 12,
            Cursor = Cursors.Hand,
            Tag = pluginInfo.Id
        };
        uninstallBtn.Click += (_, _) => UninstallPlugin(pluginInfo);
        btnPanel.Children.Add(uninstallBtn);

        Grid.SetColumn(btnPanel, 1);
        grid.Children.Add(btnPanel);

        card.Child = grid;
        stack.Children.Add(card);

        // 加载失败的具体原因直接显示在列表里，而不是只弹一句「加载失败」
        if (_pluginManager.LoadErrors.TryGetValue(pluginInfo.Id, out var error))
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"⚠ 上次加载失败：{error.Reason}",
                FontSize = 12,
                Foreground = (Brush)FindResource("ErrorBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 0, 0, 8)
            });
        }

        return stack;
    }

    private void LoadPluginIntoView(string pluginId)
    {
        if (_activePluginId == pluginId && _pluginContentArea.Content != null)
            return;

        DeactivateActivePlugin();

        // 加载可能因为插件文件缺失/依赖不全而失败，这里把原因原样告诉用户
        if (!_pluginManager.TryLoadPlugin(pluginId, out var plugin, out var error) || plugin == null)
        {
            _loadedTitle.Text = "未能打开该功能";
            _pluginContentArea.Content = new TextBlock
            {
                Text = $"插件加载失败：{error}\n\n" +
                       "可以尝试在「已安装插件」里卸载后重新从插件市场安装。",
                FontSize = 13,
                Foreground = (Brush)FindResource("ErrorBrush"),
                TextWrapping = TextWrapping.Wrap
            };
            _pluginContext.ShowStatus($"插件加载失败：{error}");
            return;
        }

        try
        {
            plugin.Init(_pluginContext);
            _pluginContentArea.Content = plugin.GetView();
            plugin.OnActivated();
            _activePluginId = pluginId;

            var installed = _pluginManager.InstalledPlugins.FirstOrDefault(p => p.Id == pluginId);
            _loadedTitle.Text = installed != null ? installed.Name : plugin.Metadata.Name;
            _pluginContext.ShowStatus($"已打开：{_loadedTitle.Text}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"初始化插件 {pluginId} 失败", ex);
            _loadedTitle.Text = "未能打开该功能";
            _pluginContentArea.Content = new TextBlock
            {
                Text = $"插件启动失败：{ex.Message}",
                FontSize = 13,
                Foreground = (Brush)FindResource("ErrorBrush"),
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

    private void DeactivateActivePlugin()
    {
        if (_activePluginId == null) return;

        if (_pluginManager.LoadedPlugins.TryGetValue(_activePluginId, out var previous))
        {
            try { previous.OnDeactivated(); } catch { /* 插件自身异常不应影响宿主 */ }
        }

        _activePluginId = null;
        _pluginContentArea.Content = null;
    }

    private void UninstallPlugin(InstalledPluginInfo pluginInfo)
    {
        var result = MessageBox.Show(
            $"确定要卸载「{pluginInfo.Name}」吗？\n\n" +
            "删除的只是这个功能模块本身，不会动你的个人文件。",
            "确认卸载",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        if (_activePluginId == pluginInfo.Id) DeactivateActivePlugin();

        var uninstallResult = _pluginManager.UninstallPlugin(pluginInfo.Id);
        if (!uninstallResult.Succeeded)
        {
            MessageBox.Show(
                $"卸载失败：{uninstallResult.Message}",
                "卸载失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (uninstallResult.Message != null)
        {
            // 例如「文件仍被占用，已安排下次启动时清理」——不能假装什么都没发生
            MessageBox.Show(uninstallResult.Message, "卸载完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        _loadedTitle.Text = "尚未打开任何功能";
        RefreshPlugins();
        _pluginContext.ShowStatus($"已卸载：{pluginInfo.Name}");
    }
}
