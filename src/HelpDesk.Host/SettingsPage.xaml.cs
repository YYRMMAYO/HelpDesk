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
        
        // GitHub 配置
        var githubCard = CreateCard("GitHub 仓库配置");
        var githubStack = new StackPanel();
        
        githubStack.Children.Add(CreateLabel("仓库所有者 (Owner)"));
        var ownerBox = new TextBox
        {
            Text = _pluginManager.GitHubOwner,
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12)
        };
        githubStack.Children.Add(ownerBox);
        
        githubStack.Children.Add(CreateLabel("仓库名称 (Repo)"));
        var repoBox = new TextBox
        {
            Text = _pluginManager.GitHubRepo,
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12)
        };
        githubStack.Children.Add(repoBox);
        
        githubStack.Children.Add(CreateLabel("分支 (Branch)"));
        var branchBox = new TextBox
        {
            Text = _pluginManager.GitHubBranch,
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 16)
        };
        githubStack.Children.Add(branchBox);
        
        var saveBtn = new Button
        {
            Content = "保存配置",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(20, 8, 20, 8)
        };
        saveBtn.Click += (_, _) =>
        {
            _pluginManager.GitHubOwner = ownerBox.Text;
            _pluginManager.GitHubRepo = repoBox.Text;
            _pluginManager.GitHubBranch = branchBox.Text;
            MessageBox.Show("配置已保存", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        githubStack.Children.Add(saveBtn);
        
        githubCard.Child = githubStack;
        mainStack.Children.Add(githubCard);
        
        // 系统信息
        var systemCard = CreateCard("系统信息");
        var systemStack = new StackPanel();
        
        systemStack.Children.Add(CreateInfoRow("操作系统", Environment.OSVersion.ToString()));
        systemStack.Children.Add(CreateInfoRow(".NET 版本", Environment.Version.ToString()));
        systemStack.Children.Add(CreateInfoRow("插件目录", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HelpDesk", "Plugins")));
        systemStack.Children.Add(CreateInfoRow("已安装插件数", _pluginManager.InstalledPlugins.Count.ToString()));
        
        systemCard.Child = systemStack;
        mainStack.Children.Add(systemCard);
        
        scroll.Content = mainStack;
        Content = scroll;
    }
    
    private Border CreateCard(string title)
    {
        var card = new Border
        {
            Style = (Style)FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 0, 20)
        };
        
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 16)
        });
        
        card.Child = stack;
        return card;
    }
    
    private TextBlock CreateLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 13, FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        };
    }
    
    private Border CreateInfoRow(string label, string value)
    {
        var border = new Border
        {
            Padding = new Thickness(0, 8, 0, 8),
            BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        
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
        
        border.Child = grid;
        return border;
    }
}
