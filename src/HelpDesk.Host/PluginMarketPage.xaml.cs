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
    private readonly PluginContext _pluginContext;
    private readonly StackPanel _pluginsList;
    private readonly TextBlock _statusText;
    private readonly TextBlock _sourceText;
    private readonly ProgressBar _progressBar;

    private bool _loading;

    public PluginMarketPage(PluginManager pluginManager, PluginContext pluginContext)
    {
        _pluginManager = pluginManager;
        _pluginContext = pluginContext;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var mainStack = new StackPanel { Margin = new Thickness(24) };

        // 标题和刷新按钮
        var headerPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        headerPanel.Children.Add(new TextBlock
        {
            Text = "插件市场",
            FontSize = 20, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        var refreshBtn = new Button
        {
            Content = "刷新列表",
            Style = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(14, 6, 14, 6)
        };
        refreshBtn.Click += async (_, _) => await LoadPluginsAsync();
        DockPanel.SetDock(refreshBtn, Dock.Right);
        headerPanel.Children.Add(refreshBtn);
        mainStack.Children.Add(headerPanel);

        // 插件来源提示（显示真实配置，而不是写死的说明文字）
        _sourceText = new TextBlock
        {
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        mainStack.Children.Add(_sourceText);

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
            Text = "正在获取插件列表…",
            FontSize = 13,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        mainStack.Children.Add(_statusText);

        // 插件列表
        _pluginsList = new StackPanel();
        mainStack.Children.Add(_pluginsList);

        scroll.Content = mainStack;
        Content = scroll;

        // 进入市场就自动拉取一次，否则用户看到的永远是「点击刷新列表获取可用插件」
        Loaded += async (_, _) => { if (!_loading) await LoadPluginsAsync(); };
    }

    private async Task LoadPluginsAsync()
    {
        _loading = true;
        _progressBar.Visibility = Visibility.Visible;
        _progressBar.IsIndeterminate = true;
        _sourceText.Text = $"插件来源：{_pluginManager.GitHubOwner}/{_pluginManager.GitHubRepo}（分支 {_pluginManager.GitHubBranch}）· " +
                           "可在「设置」里修改";
        _statusText.Text = "正在获取插件列表…";
        _pluginsList.Children.Clear();

        try
        {
            var plugins = await _pluginManager.GetAvailablePluginsAsync();

            if (plugins.Count == 0)
            {
                _statusText.Text = "没有获取到可用插件";
                _pluginsList.Children.Add(BuildEmptyState());
            }
            else
            {
                _statusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
                _statusText.Text = $"共 {plugins.Count} 个可用插件";
                foreach (var plugin in plugins)
                    _pluginsList.Children.Add(CreatePluginCard(plugin));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("获取插件列表失败", ex);
            _statusText.Text = $"加载失败：{ex.Message}";
            _pluginsList.Children.Add(BuildEmptyState());
        }
        finally
        {
            _progressBar.IsIndeterminate = false;
            _progressBar.Visibility = Visibility.Collapsed;
            _loading = false;
        }
    }

    private UIElement BuildEmptyState()
    {
        var src = $"{_pluginManager.GitHubOwner}/{_pluginManager.GitHubRepo}";
        return new TextBlock
        {
            Text = "可能的原因：\n" +
                   $"1. 仓库 {src} 不存在，或分支 {_pluginManager.GitHubBranch} 写错了（可在「设置」里改）\n" +
                   "2. 插件目录里缺少 metadata.json\n" +
                   "3. 当前网络无法访问 GitHub\n\n" +
                   "网络正常的话，先点右上角「刷新列表」再试一次。",
            FontSize = 13,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
    }

    private UIElement CreatePluginCard(GitHubPluginInfo plugin)
    {
        var installed = _pluginManager.InstalledPlugins.FirstOrDefault(p => p.Id == plugin.Id);

        var card = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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
        if (!string.IsNullOrWhiteSpace(plugin.Author))
            metaPanel.Children.Add(CreateTag(plugin.Author, "#DBEAFE", "#1E40AF"));
        foreach (var tag in plugin.Tags.Take(3))
            metaPanel.Children.Add(CreateTag(tag, "#F0FDF4", "#166534"));
        infoStack.Children.Add(metaPanel);

        // 完整性校验状态：没有清单就明确告诉用户「装是能装，但验不了」
        infoStack.Children.Add(new TextBlock
        {
            Text = plugin.HasManifest
                ? $"✓ 提供文件校验清单（{plugin.Files.Count} 个文件），安装时会逐个校验 SHA-256"
                : "⚠ 该插件未提供文件校验清单，安装时无法校验文件完整性",
            FontSize = 11,
            Foreground = plugin.HasManifest
                ? (Brush)FindResource("SuccessBrush")
                : (Brush)FindResource("WarningBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });

        Grid.SetColumn(infoStack, 0);
        grid.Children.Add(infoStack);

        var btnStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0)
        };

        if (installed != null && installed.Version == plugin.Version)
        {
            btnStack.Children.Add(new TextBlock
            {
                Text = "已安装最新版",
                FontSize = 13,
                Foreground = (Brush)FindResource("SuccessBrush"),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var reinstallBtn = new Button
            {
                Content = "重新安装",
                Padding = new Thickness(16, 5, 16, 5),
                FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = plugin
            };
            reinstallBtn.Click += async (s, _) =>
                await InstallAsync((Button)s, plugin, reinstall: true);
            btnStack.Children.Add(reinstallBtn);
        }
        else
        {
            if (installed != null)
            {
                btnStack.Children.Add(new TextBlock
                {
                    Text = $"有新版本（当前 v{installed.Version}）",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("WarningBrush"),
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }

            var installBtn = new Button
            {
                Content = installed != null ? "更新到最新版" : "安装",
                Style = (Style)FindResource("PrimaryButton"),
                Padding = new Thickness(20, 6, 20, 6),
                Tag = plugin
            };
            installBtn.Click += async (s, _) => await InstallAsync((Button)s, plugin, reinstall: false);
            btnStack.Children.Add(installBtn);
        }

        Grid.SetColumn(btnStack, 1);
        grid.Children.Add(btnStack);

        card.Child = grid;
        return card;
    }

    private async Task InstallAsync(Button button, GitHubPluginInfo plugin, bool reinstall)
    {
        button.IsEnabled = false;
        var original = button.Content;
        button.Content = reinstall ? "重新安装中…" : "安装中…";
        _statusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
        _statusText.Text = $"正在安装「{plugin.Name}」…";

        try
        {
            var result = await _pluginManager.InstallPluginAsync(plugin);

            if (result.Succeeded)
            {
                _statusText.Foreground = (Brush)FindResource("SuccessBrush");
                _statusText.Text = result.Message is null
                    ? $"「{plugin.Name}」安装成功，可在「已安装」页打开"
                    : $"「{plugin.Name}」安装完成，但请注意：{result.Message}";

                if (result.Message != null)
                {
                    _pluginContext.ShowNotification("安装完成但有警告", result.Message, NotificationType.Warning);
                }
            }
            else
            {
                _statusText.Foreground = (Brush)FindResource("ErrorBrush");
                _statusText.Text = $"「{plugin.Name}」安装失败：{result.Message}";
                _pluginContext.ShowNotification("安装失败", result.Message ?? "未知原因", NotificationType.Error);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"安装插件 {plugin.Id} 失败", ex);
            _statusText.Foreground = (Brush)FindResource("ErrorBrush");
            _statusText.Text = $"「{plugin.Name}」安装失败：{ex.Message}";
        }
        finally
        {
            button.Content = original;
            button.IsEnabled = true;
        }
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
