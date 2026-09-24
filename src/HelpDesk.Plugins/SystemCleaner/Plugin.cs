using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HelpDesk.Contracts;

namespace HelpDesk.Plugins.SystemCleaner;

public class SystemCleanerPlugin : IPlugin
{
    public PluginMetadata Metadata => new()
    {
        Id = "system-cleaner",
        Name = "系统清理",
        Description = "扫描并清理当前账户的临时文件，释放磁盘空间",
        Version = "1.1.0",
        Author = "HelpDesk",
        GitHubFolder = "SystemCleaner",
        Tags = ["清理", "临时文件", "磁盘"],
        MinHostVersion = 1.1
    };

    private IPluginContext _context = null!;
    private CleanerView? _view;

    public void Init(IPluginContext context) => _context = context;

    public UserControl GetView() => _view ??= new CleanerView();

    public void OnActivated() { }

    public void OnDeactivated() { }

    public void Dispose() { }
}

/// <summary>
/// 临时文件清理视图。
///
/// <para><b>相比旧实现改了什么、为什么</b></para>
/// <list type="bullet">
/// <item>旧实现直接在 <c>Loaded</c> 里同步递归扫描临时目录，文件多的时候窗口会卡住；
/// 现在扫描在后台线程进行。</item>
/// <item>旧实现用 <c>Directory.GetFiles(temp, "*", AllDirectories)</c>，只要遇到<b>一个</b>
/// 没有权限的子目录就整体抛异常，界面上显示「扫描失败」，实际什么都没扫到；
/// 现在逐目录容错，坏目录只跳过自己。</item>
/// <item>清理范围明确限定为「当前账户的临时目录」，并且默认跳过最近 5 分钟修改过的文件，
/// 避免把正在安装、正在导出的程序的工作文件删掉。</item>
/// </list>
/// </summary>
public partial class CleanerView : UserControl
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(16, 185, 129));
    private static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(245, 158, 11));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(239, 68, 68));
    private static readonly Brush Slate = new SolidColorBrush(Color.FromRgb(100, 116, 139));

    /// <summary>最近这段时间内修改过的文件视为「可能正在被使用」，默认不清理。</summary>
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(5);

    private readonly string _tempRoot = Path.GetTempPath();
    private bool _busy;

    public CleanerView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ScanAsync();
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        CleanButton.IsEnabled = false;
        RescanButton.IsEnabled = false;
        TempStatus.Text = "正在扫描…";
        TempStatus.Foreground = Slate;
        CleanResult.Text = string.Empty;

        try
        {
            var skipRecent = SkipRecent.IsChecked == true;
            var files = await Task.Run(() => CollectTempFiles(skipRecent));

            var totalBytes = await Task.Run(() => SumSize(files));

            TempSize.Text = FormatSize(totalBytes);
            TempFileCount.Text = $"{files.Count} 个文件 · 位置：{_tempRoot}";

            var suggest = totalBytes > 100L * 1024 * 1024;
            TempStatus.Text = totalBytes == 0 ? "没有可清理的文件" : suggest ? "建议清理" : "空间占用正常";
            TempStatus.Foreground = totalBytes == 0 ? Green : suggest ? Orange : Green;
        }
        catch (Exception ex)
        {
            TempStatus.Text = $"扫描失败：{ex.Message}";
            TempStatus.Foreground = Red;
        }
        finally
        {
            _busy = false;
            CleanButton.IsEnabled = true;
            RescanButton.IsEnabled = true;
        }
    }

    private async void CleanTempFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var skipRecent = SkipRecent.IsChecked == true;
        var confirm = MessageBox.Show(
            Window.GetWindow(this),
            "将删除当前账户临时文件夹里"
            + (skipRecent ? " 5 分钟以前创建的" : "全部")
            + "文件。\n\n"
            + "正在被程序使用的文件会自动跳过；你的文档、图片、桌面等个人文件不在清理范围内。\n\n"
            + "确定要清理吗？",
            "确认清理", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        _busy = true;
        CleanButton.IsEnabled = false;
        RescanButton.IsEnabled = false;
        TempStatus.Text = "正在清理…";
        TempStatus.Foreground = Slate;

        try
        {
            var result = await Task.Run(() => DeleteFiles(CollectTempFiles(skipRecent)));

            CleanResult.Text =
                $"清理完成：成功删除 {result.Deleted} 个文件，释放 {FormatSize(result.FreedBytes)}"
                + (result.Skipped > 0 ? $"，{result.Skipped} 个文件正在被占用已自动跳过" : "")
                + "。";

            TempStatus.Text = "清理完成";
            TempStatus.Foreground = Green;
        }
        catch (Exception ex)
        {
            CleanResult.Text = $"清理失败：{ex.Message}";
            TempStatus.Foreground = Red;
        }
        finally
        {
            _busy = false;
            await ScanAsync();
        }
    }

    // ──────────────────────────────────────────────── 扫描与删除

    /// <summary>
    /// 逐个目录容错地收集临时文件。任何一个子目录读取失败只跳过它自己，
    /// 不会让整轮扫描失败（旧实现正是在这里「整轮失败、界面显示扫描失败」）。
    /// </summary>
    private List<string> CollectTempFiles(bool skipRecent)
    {
        var cutoff = DateTime.Now - RecentWindow;
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(_tempRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            // 防御性检查：任何原因导致队列里出现临时目录之外的路径，都直接丢弃
            if (!IsInsideTempRoot(directory)) continue;

            string[] files;
            try { files = Directory.GetFiles(directory); }
            catch { files = []; }

            foreach (var file in files)
            {
                try
                {
                    if (skipRecent && File.GetLastWriteTime(file) > cutoff) continue;
                    result.Add(file);
                }
                catch
                {
                    // 单个文件读取失败不影响其余文件
                }
            }

            string[] subdirectories;
            try { subdirectories = Directory.GetDirectories(directory); }
            catch { subdirectories = []; }

            foreach (var subdirectory in subdirectories)
            {
                // 不跟随符号链接/交叉点：它们指向的位置可能不在临时目录里
                try
                {
                    if ((File.GetAttributes(subdirectory) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch
                {
                    continue;
                }

                pending.Push(subdirectory);
            }
        }

        return result;
    }

    private bool IsInsideTempRoot(string path)
    {
        try
        {
            var root = Path.GetFullPath(_tempRoot);
            var candidate = Path.GetFullPath(path);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                   || candidate.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static long SumSize(List<string> files)
    {
        long total = 0;
        foreach (var file in files)
        {
            try { total += new FileInfo(file).Length; } catch { }
        }
        return total;
    }

    private (int Deleted, int Skipped, long FreedBytes) DeleteFiles(List<string> files)
    {
        var deleted = 0;
        var skipped = 0;
        long freed = 0;

        foreach (var file in files)
        {
            if (!IsInsideTempRoot(file)) continue;

            try
            {
                var size = new FileInfo(file).Length;
                File.Delete(file);
                deleted++;
                freed += size;
            }
            catch
            {
                // 被占用 / 只读 / 权限不足 —— 跳过，绝不强行处理
                skipped++;
            }
        }

        // 顺手收掉被清空的子目录（只删空目录，删不掉就算了）
        try
        {
            foreach (var directory in Directory.GetDirectories(_tempRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.GetFileSystemEntries(directory).Length == 0)
                        Directory.Delete(directory);
                }
                catch { }
            }
        }
        catch { }

        return (deleted, skipped, freed);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB"
    };
}
