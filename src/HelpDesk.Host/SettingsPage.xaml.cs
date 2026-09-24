using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HelpDesk.Contracts;

namespace HelpDesk.Host;

/// <summary>
/// 设置页面
/// </summary>
public class SettingsPage : UserControl
{
    private readonly PluginManager _pluginManager;

    public SettingsPage(PluginManager pluginManager)
    {
        _pluginManager = pluginManager;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var mainStack = new StackPanel { Margin = new Thickness(24) };

        // ── 插件来源配置 ───────────────────────────────────────────
        var githubCard = CreateCard("插件来源（GitHub 仓库）");
        var githubStack = (StackPanel)githubCard.Child;

        githubStack.Children.Add(new TextBlock
        {
            Text = "插件从这个仓库下载。修改后会自动记住，重启程序不会丢。",
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        githubStack.Children.Add(CreateLabel("仓库所有者 (Owner)"));
        var ownerBox = CreateTextBox(_pluginManager.GitHubOwner);
        githubStack.Children.Add(ownerBox);

        githubStack.Children.Add(CreateLabel("仓库名称 (Repo)"));
        var repoBox = CreateTextBox(_pluginManager.GitHubRepo);
        githubStack.Children.Add(repoBox);

        githubStack.Children.Add(CreateLabel("分支 (Branch)"));
        var branchBox = CreateTextBox(_pluginManager.GitHubBranch);
        githubStack.Children.Add(branchBox);

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var saveBtn = new Button
        {
            Content = "保存配置",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(20, 8, 20, 8)
        };
        saveBtn.Click += (_, _) =>
        {
            _pluginManager.GitHubOwner = ownerBox.Text.Trim();
            _pluginManager.GitHubRepo = repoBox.Text.Trim();
            _pluginManager.GitHubBranch = branchBox.Text.Trim();
            _pluginManager.SaveSettings();
            MessageBox.Show("配置已保存，进入插件市场点「刷新列表」即可生效。", "HelpDesk",
                MessageBoxButton.OK, MessageBoxImage.Information);
        };
        buttonPanel.Children.Add(saveBtn);

        var restoreBtn = new Button
        {
            Content = "恢复默认",
            Padding = new Thickness(16, 8, 16, 8),
            FontSize = 13,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        restoreBtn.Click += (_, _) =>
        {
            ownerBox.Text = "YYRMMAYO";
            repoBox.Text = "HelpDesk-Plugins";
            branchBox.Text = "main";
        };
        buttonPanel.Children.Add(restoreBtn);

        githubStack.Children.Add(buttonPanel);
        mainStack.Children.Add(githubCard);

        // ── 系统信息 ───────────────────────────────────────────────
        var systemCard = CreateCard("系统信息");
        var systemStack = (StackPanel)systemCard.Child;

        systemStack.Children.Add(CreateInfoRow("程序版本", $"v{PluginManager.HostVersion:F1}.0"));
        systemStack.Children.Add(CreateInfoRow("操作系统", Environment.OSVersion.VersionString));
        systemStack.Children.Add(CreateInfoRow("运行环境", $".NET {Environment.Version}"));
        systemStack.Children.Add(CreateInfoRow("管理员权限", ProcessRunner.IsElevated ? "已获取" : "未获取（需要时会单独提示）"));
        systemStack.Children.Add(CreateInfoRow("已安装插件", $"{_pluginManager.InstalledPlugins.Count} 个"));
        systemStack.Children.Add(CreateInfoRow("插件目录", _pluginManager.PluginsDirectory));
        mainStack.Children.Add(systemCard);

        // ── 帮助与排错 ─────────────────────────────────────────────
        var helpCard = CreateCard("出问题了？");
        var helpStack = (StackPanel)helpCard.Child;

        helpStack.Children.Add(new TextBlock
        {
            Text = "如果某个功能打不开或报错，把日志文件发给帮你排查的人即可。",
            FontSize = 13,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var helpButtons = new StackPanel { Orientation = Orientation.Horizontal };
        helpButtons.Children.Add(CreateOpenFolderButton("打开日志文件夹", Path.GetDirectoryName(AppLog.CurrentLogFile)));
        helpButtons.Children.Add(CreateOpenFolderButton("打开插件文件夹", _pluginManager.PluginsDirectory));
        helpStack.Children.Add(helpButtons);

        helpStack.Children.Add(new TextBlock
        {
            Text = AppLog.CurrentLogFile,
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        });

        mainStack.Children.Add(helpCard);

        scroll.Content = mainStack;
        Content = scroll;
    }

    private Button CreateOpenFolderButton(string text, string? folder)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(16, 8, 16, 8),
            FontSize = 13,
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            IsEnabled = !string.IsNullOrWhiteSpace(folder)
        };
        button.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(folder!);
                Process.Start(new ProcessStartInfo { FileName = folder!, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开文件夹：{ex.Message}", "HelpDesk",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        return button;
    }

    /// <summary>创建带标题的空卡片，并把内容面板返回给调用方填充。</summary>
    private Border CreateCard(string title)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });

        return new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 0, 20),
            Child = stack
        };
    }

    private TextBox CreateTextBox(string value) => new()
    {
        Text = value,
        Padding = new Thickness(8, 6, 8, 6),
        FontSize = 13,
        Margin = new Thickness(0, 0, 0, 12)
    };

    private TextBlock CreateLabel(string text) => new()
    {
        Text = text,
        FontSize = 13, FontWeight = FontWeights.Medium,
        Foreground = (Brush)FindResource("TextPrimaryBrush"),
        Margin = new Thickness(0, 0, 0, 4)
    };

    private Border CreateInfoRow(string label, string value)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 13,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            FontWeight = FontWeights.Medium,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(valueBlock);

        return new Border
        {
            Padding = new Thickness(0, 8, 0, 8),
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
    }
}
