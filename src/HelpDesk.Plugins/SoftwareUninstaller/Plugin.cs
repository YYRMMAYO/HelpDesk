using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HelpDesk.Contracts;

namespace HelpDesk.Plugins.SoftwareUninstaller;

public class SoftwareUninstallerPlugin : IPlugin
{
    public PluginMetadata Metadata => new()
    {
        Id = "software-uninstaller",
        Name = "软件卸载",
        Description = "安全卸载已安装软件（按注册表登记的官方卸载程序），并清理卸载残留",
        Version = "1.1.0",
        Author = "HelpDesk",
        GitHubFolder = "SoftwareUninstaller",
        Tags = ["卸载", "软件管理", "残留清理", "提权"],
        MinHostVersion = 1.1
    };

    private IPluginContext _context = null!;
    private UninstallerView? _view;

    public void Init(IPluginContext context) => _context = context;

    public UserControl GetView() => _view ??= new UninstallerView(_context);

    public void OnActivated() { }

    public void OnDeactivated() { }

    public void Dispose() { }
}

public partial class UninstallerView : UserControl
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(16, 185, 129));
    private static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(217, 119, 6));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(239, 68, 68));
    private static readonly Brush Slate = new SolidColorBrush(Color.FromRgb(100, 116, 139));
    private static readonly Brush Dark = new SolidColorBrush(Color.FromRgb(30, 41, 59));

    /// <summary>残留清单中的一行。</summary>
    private sealed class ResidueRow
    {
        public required string DeleteTarget { get; init; }
        public required string DisplayPath { get; init; }
        public required ResiduePathCheck Check { get; init; }
        public required CheckBox Toggle { get; init; }
    }

    private readonly IPluginContext _context;
    private readonly List<ResidueRow> _residues = new();
    private IReadOnlyList<InstalledProgram> _allPrograms = [];

    public UninstallerView(IPluginContext context)
    {
        _context = context;
        InitializeComponent();
    }

    private InstalledProgram? Selected => ProgramList.SelectedItem as InstalledProgram;

    // ──────────────────────────────────────────────────── 扫描

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        ScanStatus.Foreground = Slate;
        ScanStatus.Text = "正在读取已安装软件列表…";
        ProgramList.ItemsSource = null;
        ClearSelectionUi();

        try
        {
            // 始终「连系统组件一起扫描」：显示可以过滤，但「哪些目录属于别的软件」这份
            // 保护名单必须是完整的——按显示条件裁剪过的名单会让共享父目录漏检，
            // 那正是我们要防的误删。
            var programs = await Task.Run(() => InstalledProgramScanner.Scan(includeSystemComponents: true));

            _allPrograms = programs;
            ApplyFilter();

            var shown = (ProgramList.ItemsSource as IEnumerable<InstalledProgram>)?.Count() ?? 0;
            ScanStatus.Foreground = programs.Count > 0 ? Green : Orange;
            ScanStatus.Text = programs.Count > 0
                ? $"扫描完成，共 {programs.Count} 个软件"
                  + (shown < programs.Count ? $"（其中 {shown} 个符合当前筛选条件，另有 {programs.Count - shown} 个系统组件已隐藏）" : "")
                  + "。选中一行后即可卸载或扫描残留。"
                : "没有读到任何软件。如果不是权限问题，请点「显示系统组件」再看看。";
        }
        catch (Exception ex)
        {
            ScanStatus.Foreground = Red;
            ScanStatus.Text = $"扫描失败：{ex.Message}";
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private void ShowSystemComponents_Changed(object sender, RoutedEventArgs e)
    {
        // 系统组件是在扫描阶段过滤的，改了开关就重新扫一次
        if (_allPrograms.Count > 0) Scan_Click(sender, e);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var keyword = SearchBox.Text?.Trim() ?? string.Empty;
        var showSystem = ShowSystemComponents.IsChecked == true;

        var filtered = _allPrograms
            // 「显示系统组件」只在显示层面过滤：保护名单用的是完整扫描结果
            .Where(p => showSystem || !p.SystemComponent)
            .Where(p => string.IsNullOrEmpty(keyword)
                        || p.DisplayName.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
                        || p.Publisher.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        ProgramList.ItemsSource = filtered;

        if (_allPrograms.Count > 0)
            ScanStatus.Text = string.IsNullOrEmpty(keyword)
                ? $"显示 {filtered.Count} 个软件（共扫描到 {_allPrograms.Count} 个）"
                : $"匹配到 {filtered.Count} 个软件（共扫描到 {_allPrograms.Count} 个）";
    }

    // ──────────────────────────────────────────────────── 选择

    private void ProgramList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _residues.Clear();
        ResiduePanel.Children.Clear();
        SelectAllResidue.IsEnabled = false;
        SelectAllResidue.IsChecked = false;
        DeleteResidueButton.IsEnabled = false;
        ResidueStatus.Text = "选择上面的软件后点「扫描残留」";
        ActionStatus.Text = string.Empty;

        var program = Selected;
        if (program == null)
        {
            ClearSelectionUi();
            return;
        }

        SelectionTitle.Text = program.DisplayName;
        DetailGrid.Visibility = Visibility.Visible;

        var versionParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(program.DisplayVersion)) versionParts.Add($"版本 {program.DisplayVersion}");
        if (!string.IsNullOrWhiteSpace(program.Publisher)) versionParts.Add(program.Publisher);
        versionParts.Add(program.SizeText);
        versionParts.Add(program.IsMachineWide ? "整机安装（需管理员权限）" : "仅当前用户");
        DetailVersion.Text = string.Join(" · ", versionParts);

        DetailLocation.Text = program.InstallLocationText;
        DetailLocation.Foreground = string.IsNullOrWhiteSpace(program.InstallLocation) ? Orange : Dark;

        DetailCommand.Text = program.UninstallString ?? "（注册表未记录）";
        DetailRegistryPath.Text = program.RegistryKeyPath;

        var check = UninstallSafety.ValidateUninstallCommand(program, QuietUninstall.IsChecked == true);
        UninstallButton.IsEnabled = check.Allowed;
        ScanResidueButton.IsEnabled = true;

        if (check.Allowed)
        {
            ActionStatus.Foreground = _context.IsElevated || UninstallSafety.NeedsElevationForUninstall(program)
                ? Slate
                : Green;
            ActionStatus.Text = UninstallSafety.NeedsElevationForUninstall(program) && !_context.IsElevated
                ? "卸载这个软件需要管理员权限，点「卸载」后会先弹出 Windows 的授权提示，同意即可。"
                : "卸载会运行该软件自己的卸载程序，请按它弹出的提示操作。";
        }
        else
        {
            ActionStatus.Foreground = Red;
            ActionStatus.Text = $"⚠ 已阻止自动卸载：{check.Reason}";
        }
    }

    private void ClearSelectionUi()
    {
        SelectionTitle.Text = "未选择软件";
        DetailGrid.Visibility = Visibility.Collapsed;
        UninstallButton.IsEnabled = false;
        ScanResidueButton.IsEnabled = false;
    }

    // ──────────────────────────────────────────────────── 卸载

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var program = Selected;
        if (program == null) return;

        var check = UninstallSafety.ValidateUninstallCommand(program, QuietUninstall.IsChecked == true);
        if (!check.Allowed || check.ExecutablePath == null)
        {
            // 把命令原文交给用户，让他自己判断——我们只是拒绝代替他执行
            ShowBlockedDialog(program, check.Reason);
            return;
        }

        var needsElevation = UninstallSafety.NeedsElevationForUninstall(program) && !_context.IsElevated;

        var description =
            $"软件：{program.DisplayName}\n" +
            (string.IsNullOrWhiteSpace(program.Publisher) ? "" : $"发布者：{program.Publisher}\n") +
            $"将要运行：\n{check.ExecutablePath}\n" +
            (string.IsNullOrWhiteSpace(check.Arguments) ? "" : $"参数：{check.Arguments}\n") +
            "\n" +
            (needsElevation
                ? "接下来 Windows 会弹出管理员授权提示，同意后才会开始卸载。\n\n"
                : "") +
            "卸载由该软件自己的卸载程序完成（和「控制面板 → 程序和功能」里的动作完全一样），" +
            "不会删除你的个人文件。\n\n确定要卸载吗？";

        var confirmed = MessageBox.Show(
            Window.GetWindow(this), description,
            "确认卸载", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes) return;

        UninstallButton.IsEnabled = false;
        ActionStatus.Foreground = Slate;
        ActionStatus.Text = "正在启动卸载程序…";

        try
        {
            var result = await _context.StartProcessAsync(
                check.ExecutablePath, check.Arguments, needsElevation);

            // 未提权的情况下，某些卸载程序会因为权限不足直接失败，此时再试一次提权
            if (!result.Started && !needsElevation && !result.ElevationDenied)
            {
                ActionStatus.Text = "第一次没有启动成功，正在改用管理员权限重试…";
                result = await _context.StartProcessAsync(check.ExecutablePath, check.Arguments, elevated: true);
                needsElevation = true;
            }

            if (result.ElevationDenied)
            {
                ActionStatus.Foreground = Orange;
                ActionStatus.Text = "你取消了管理员授权，卸载没有开始。";
            }
            else if (result.Started)
            {
                ActionStatus.Foreground = Green;
                ActionStatus.Text =
                    "已启动卸载程序，请在弹出的窗口里按提示完成卸载。\n" +
                    "卸载完成后，建议回到这里点「扫描残留」清理剩下的空目录和注册表项。";
                _context.ShowStatus($"已启动卸载：{program.DisplayName}");
            }
            else
            {
                ActionStatus.Foreground = Red;
                ActionStatus.Text = $"启动失败：{result.Error ?? "未知原因"}";
            }
        }
        catch (Exception ex)
        {
            ActionStatus.Foreground = Red;
            ActionStatus.Text = $"启动失败：{ex.Message}";
        }
        finally
        {
            UninstallButton.IsEnabled = true;
        }
    }

    private void ShowBlockedDialog(InstalledProgram program, string reason)
    {
        var raw = program.UninstallString ?? "（注册表未记录卸载命令）";
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"出于安全考虑，HelpDesk 不会自动运行这条卸载命令：\n\n" +
            $"原因：{reason}\n\n" +
            $"命令原文（可复制，自行判断是否手动执行）：\n{raw}\n\n" +
            "要不要改去「扫描残留」，清理它留下的安装目录？",
            "已阻止自动卸载", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes)
        {
            // 不需要真的点了按钮才有反应，直接调用同一段逻辑
            ScanResidue_Click(this, new RoutedEventArgs());
        }
    }

    // ──────────────────────────────────────────────────── 扫描残留

    private void ScanResidue_Click(object sender, RoutedEventArgs e)
    {
        var program = Selected;
        if (program == null) return;

        ResiduePanel.Children.Clear();
        _residues.Clear();
        DeleteResidueButton.IsEnabled = false;
        SelectAllResidue.IsEnabled = false;
        SelectAllResidue.IsChecked = false;

        // 其余软件的安装目录：用来判断待删目录是否被别人共用
        var otherLocations = _allPrograms
            .Where(p => !ReferenceEquals(p, program) && !string.IsNullOrWhiteSpace(p.InstallLocation))
            .Select(p => p.InstallLocation!)
            .ToList();

        var runningDirectory = UninstallSafety.RunningDirectory;

        // 候选一：注册表记录的安装目录（唯一的目录来源）
        if (!string.IsNullOrWhiteSpace(program.InstallLocation))
        {
            var check = UninstallSafety.ValidateResiduePath(
                program.InstallLocation, otherLocations, runningDirectory);
            AddResidueRow(program.InstallLocation, check);
        }
        else
        {
            AddInfoLine("这个软件的注册表项里没有记录安装位置，因此无法定位它的安装目录。" +
                        "这通常意味着卸载程序会自己清理，或者软件是绿色版。");
        }

        // 候选二：它自己的卸载注册表项
        AddResidueRow(program.RegistryKeyPath, ValidateRegistryKey(program));

        var deletable = _residues.Count(r => r.Check.CanDelete);
        var blocked = _residues.Count(r => r.Check.Verdict == ResidueVerdict.Blocked);

        SelectAllResidue.IsEnabled = deletable > 0;
        DeleteResidueButton.IsEnabled = deletable > 0;

        ResidueStatus.Foreground = blocked > 0 ? Orange : Slate;
        ResidueStatus.Text = $"{_residues.Count} 项候选，其中可删除 {deletable} 项"
                             + (blocked > 0 ? $"，{blocked} 项被安全检查拦下（不会删除）" : "")
                             + "。请确认后再点「删除选中的残留」。";
    }

    private static ResiduePathCheck ValidateRegistryKey(InstalledProgram program)
    {
        if (!UninstallSafety.IsUninstallRegistryKey(program.RegistryKeyPath))
            return ResiduePathCheck.Block("不是标准的卸载信息注册表项，已阻止。");

        return ResiduePathCheck.Allow("该软件自己的卸载注册表项");
    }

    private void AddResidueRow(string deleteTarget, ResiduePathCheck check)
    {
        var isRegistry = deleteTarget.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase);

        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var toggle = new CheckBox
        {
            IsChecked = check.CanDelete,
            IsEnabled = check.CanDelete,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 8, 0)
        };
        Grid.SetColumn(toggle, 0);
        rowGrid.Children.Add(toggle);

        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock
        {
            Text = (isRegistry ? "【注册表项】" : "【目录】") + deleteTarget,
            FontSize = 12,
            FontFamily = isRegistry ? new FontFamily("Consolas") : new FontFamily("Segoe UI"),
            Foreground = check.Verdict switch
            {
                ResidueVerdict.Allowed => Dark,
                ResidueVerdict.Skipped => Slate,
                _ => Red
            },
            TextWrapping = TextWrapping.Wrap
        });

        textStack.Children.Add(new TextBlock
        {
            Text = check.Verdict switch
            {
                ResidueVerdict.Allowed => $"✓ 可删除 — {check.Reason}",
                ResidueVerdict.Skipped => $"— 跳过 — {check.Reason}",
                _ => $"⛔ 已阻止 — {check.Reason}"
            },
            FontSize = 11,
            Foreground = check.Verdict switch
            {
                ResidueVerdict.Allowed => Green,
                ResidueVerdict.Skipped => Slate,
                _ => Red
            },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });

        Grid.SetColumn(textStack, 1);
        rowGrid.Children.Add(textStack);

        ResiduePanel.Children.Add(new Border
        {
            Background = new SolidColorBrush(check.Verdict switch
            {
                ResidueVerdict.Allowed => Color.FromRgb(248, 250, 252),
                ResidueVerdict.Skipped => Color.FromRgb(250, 250, 250),
                _ => Color.FromRgb(254, 242, 242)
            }),
            BorderBrush = new SolidColorBrush(check.Verdict == ResidueVerdict.Blocked
                ? Color.FromRgb(254, 202, 202)
                : Color.FromRgb(226, 232, 240)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 6),
            Child = rowGrid
        });

        _residues.Add(new ResidueRow
        {
            DeleteTarget = deleteTarget,
            DisplayPath = deleteTarget,
            Check = check,
            Toggle = toggle
        });
    }

    private void AddInfoLine(string text)
    {
        ResiduePanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Slate,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
    }

    private void SelectAllResidue_Click(object sender, RoutedEventArgs e)
    {
        var select = SelectAllResidue.IsChecked == true;
        foreach (var row in _residues.Where(r => r.Check.CanDelete))
            row.Toggle.IsChecked = select;

        DeleteResidueButton.IsEnabled = _residues.Any(r => r.Check.CanDelete && r.Toggle.IsChecked == true);
    }

    // ──────────────────────────────────────────────────── 删除残留

    private async void DeleteResidue_Click(object sender, RoutedEventArgs e)
    {
        var selected = _residues
            .Where(r => r.Check.CanDelete && r.Toggle.IsChecked == true)
            .ToList();

        if (selected.Count == 0)
        {
            ResidueStatus.Text = "还没有勾选任何可删除项。";
            return;
        }

        var filePaths = selected
            .Where(r => !r.DeleteTarget.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.DeleteTarget)
            .ToList();

        var registryKeys = selected
            .Where(r => r.DeleteTarget.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.DeleteTarget)
            .ToList();

        var needElevation = !_context.IsElevated && UninstallSafety.NeedsElevation(filePaths, registryKeys);

        var listText = string.Join("\n", selected.Select(r => "  • " + r.DeleteTarget));
        var confirmed = MessageBox.Show(
            Window.GetWindow(this),
            $"即将删除以下 {selected.Count} 项：\n\n{listText}\n\n" +
            "这些路径都经过安全检查（只包含所选软件自己登记的安装目录与卸载注册表项）。\n" +
            "注意：删除后无法通过「回收站」恢复。\n\n" +
            (needElevation ? "接下来会弹出 Windows 管理员授权提示，同意后才会执行。\n\n" : "") +
            "确定要删除吗？",
            "确认删除残留", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes) return;

        DeleteResidueButton.IsEnabled = false;
        SelectAllResidue.IsEnabled = false;
        ResidueStatus.Foreground = Slate;
        ResidueStatus.Text = needElevation ? "等待管理员授权…" : "正在删除…";

        string? listFile = null;
        try
        {
            // 路径通过文件传给 PowerShell，不拼进命令行：
            // 路径里无论有什么字符都不可能改变脚本的行为。
            listFile = Path.Combine(
                Path.GetTempPath(), $"helpdesk-residue-{Guid.NewGuid():N}.txt");

            var lines = ResidueDeletion.BuildListLines(filePaths, registryKeys);
            await File.WriteAllLinesAsync(listFile, lines);

            var script = ResidueDeletion.BuildScript(listFile);
            var result = await _context.RunPowerShellAsync(script, needElevation, timeoutMs: 300_000);

            if (result.ElevationDenied)
            {
                ResidueStatus.Foreground = Orange;
                ResidueStatus.Text = "你取消了管理员授权，残留没有被删除。";
                return;
            }

            if (!result.Started || result.Error != null)
            {
                ResidueStatus.Foreground = Red;
                ResidueStatus.Text = $"删除失败：{result.Error ?? "无法执行删除操作"}";
                return;
            }

            var (ok, fail, errors) = ResidueDeletion.ParseOutput(result.StdOut);
            ResidueStatus.Foreground = fail > 0 ? Orange : Green;
            ResidueStatus.Text = $"删除完成：成功 {ok} 项，失败 {fail} 项。";

            if (errors.Count > 0)
            {
                ResidueStatus.Text += "\n失败明细（通常是文件正在被使用，关掉相关程序后重试）：\n"
                                      + string.Join("\n", errors.Take(8));
            }

            _context.ShowStatus($"残留清理完成：成功 {ok} 项");

            // 重新扫描一次，让列表反映真实结果
            ScanResidue_Click(this, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            ResidueStatus.Foreground = Red;
            ResidueStatus.Text = $"删除失败：{ex.Message}";
        }
        finally
        {
            if (listFile != null)
            {
                try { if (File.Exists(listFile)) File.Delete(listFile); } catch { }
            }
            DeleteResidueButton.IsEnabled = true;
            SelectAllResidue.IsEnabled = _residues.Any(r => r.Check.CanDelete);
        }
    }

}
