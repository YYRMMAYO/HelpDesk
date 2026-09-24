using Microsoft.Win32;

namespace HelpDesk.Plugins.SoftwareUninstaller;

/// <summary>
/// 一个已安装软件。全部字段都直接来自注册表，我们不做任何名称推断。
/// </summary>
public sealed class InstalledProgram
{
    public string DisplayName { get; init; } = string.Empty;
    public string DisplayVersion { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;

    /// <summary>注册表记录的占用空间（KB）。</summary>
    public long EstimatedSizeKb { get; init; }

    /// <summary>官方卸载命令（原样保存，绝不改写）。</summary>
    public string? UninstallString { get; init; }

    /// <summary>官方静默卸载命令（如果有）。</summary>
    public string? QuietUninstallString { get; init; }

    /// <summary>官方记录的安装目录。残留清理<b>只</b>允许针对这个路径。</summary>
    public string? InstallLocation { get; init; }

    /// <summary>安装日期，注册表里是 yyyyMMdd 形式。</summary>
    public string? InstallDate { get; init; }

    /// <summary>系统组件（Windows 自己的组件或补丁），默认隐藏。</summary>
    public bool SystemComponent { get; init; }

    /// <summary>注册表明确标记为「不允许卸载」。</summary>
    public bool NoRemove { get; init; }

    /// <summary>是否为 MSI 安装（此时子键名就是 ProductCode）。</summary>
    public bool WindowsInstaller { get; init; }

    /// <summary>该软件属于整机（HKLM）还是仅当前用户（HKCU）；决定卸载是否需要管理员权限。</summary>
    public bool IsMachineWide { get; init; }

    /// <summary>注册表子键的完整路径，例如 <c>HKEY_LOCAL_MACHINE\SOFTWARE\...\Uninstall\{GUID}</c>。</summary>
    public string RegistryKeyPath { get; init; } = string.Empty;

    /// <summary>界面上显示的大小文本。</summary>
    public string SizeText => EstimatedSizeKb <= 0 ? "未知" : FormatSize(EstimatedSizeKb * 1024);

    /// <summary>界面上显示的安装日期。</summary>
    public string InstallDateText =>
        InstallDate is { Length: 8 }
            && int.TryParse(InstallDate[..4], out var year)
            && int.TryParse(InstallDate[4..6], out var month)
            && int.TryParse(InstallDate[6..8], out var day)
            ? $"{year:D4}-{month:D2}-{day:D2}"
            : "未知";

    /// <summary>安装位置文本。</summary>
    public string InstallLocationText =>
        string.IsNullOrWhiteSpace(InstallLocation) ? "注册表未记录安装位置" : InstallLocation;

    public override string ToString() => DisplayName;

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB"
    };
}

/// <summary>
/// 已安装软件扫描器。读取 Windows「程序和功能」所用的同一批注册表位置，
/// 因此列表与系统自带的功能保持一致（不会漏掉，也不会凭空多出条目）。
/// </summary>
public static class InstalledProgramScanner
{
    /// <summary>与「程序和功能」一致的三个卸载信息根键。</summary>
    private static readonly (RegistryKey Root, string Path, bool MachineWide)[] Roots =
    [
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", true),
        (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", true),
        (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false),
        (Registry.CurrentUser, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", false)
    ];

    public static IReadOnlyList<InstalledProgram> Scan(
        bool includeSystemComponents,
        CancellationToken ct = default)
    {
        var result = new List<InstalledProgram>();

        foreach (var (root, path, machineWide) in Roots)
        {
            ct.ThrowIfCancellationRequested();

            RegistryKey? parent = null;
            try
            {
                parent = root.OpenSubKey(path);
                if (parent == null) continue;

                foreach (var subKeyName in parent.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var program = ReadProgram(parent, subKeyName, machineWide, path);
                        if (program == null) continue;
                        if (program.SystemComponent && !includeSystemComponents) continue;
                        result.Add(program);
                    }
                    catch
                    {
                        // 单个条目损坏不应该让整个扫描失败
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // 该根键无权限或无内容，跳过
            }
            finally
            {
                parent?.Dispose();
            }
        }

        return result
            .GroupBy(p => p.RegistryKeyPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static InstalledProgram? ReadProgram(
        RegistryKey parent,
        string subKeyName,
        bool machineWide,
        string parentPath)
    {
        using var key = parent.OpenSubKey(subKeyName);
        if (key == null) return null;

        var displayName = (key.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        var windowsInstaller = ToBool(key.GetValue("WindowsInstaller"));

        var uninstallString = (key.GetValue("UninstallString") as string)?.Trim();
        if (string.IsNullOrWhiteSpace(uninstallString) && windowsInstaller && IsProductCode(subKeyName))
        {
            // MSI 产品即使没写 UninstallString，也可以用 ProductCode 卸载，
            // 这是 Windows 自身的做法，不是我们猜的。
            uninstallString = $"MsiExec.exe /X{subKeyName}";
        }

        return new InstalledProgram
        {
            DisplayName = displayName,
            DisplayVersion = (key.GetValue("DisplayVersion") as string)?.Trim() ?? string.Empty,
            Publisher = (key.GetValue("Publisher") as string)?.Trim() ?? string.Empty,
            EstimatedSizeKb = ToLong(key.GetValue("EstimatedSize")),
            UninstallString = uninstallString,
            QuietUninstallString = (key.GetValue("QuietUninstallString") as string)?.Trim(),
            InstallLocation = (key.GetValue("InstallLocation") as string)?.Trim(),
            InstallDate = (key.GetValue("InstallDate") as string)?.Trim(),
            SystemComponent = ToBool(key.GetValue("SystemComponent")) || key.GetValue("ParentKeyName") != null,
            NoRemove = ToBool(key.GetValue("NoRemove")),
            WindowsInstaller = windowsInstaller,
            IsMachineWide = machineWide,
            RegistryKeyPath = $"{RootName(machineWide)}\\{parentPath}\\{subKeyName}"
        };
    }

    private static string RootName(bool machineWide)
        => machineWide ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";

    private static bool IsProductCode(string name)
        => name.Length == 38 && name[0] == '{' && name[^1] == '}';

    private static bool ToBool(object? value) => value switch
    {
        int i => i != 0,
        long l => l != 0,
        string s => s is "1" or "true" or "True",
        _ => false
    };

    private static long ToLong(object? value) => value switch
    {
        int i => i,
        long l => l,
        string s when long.TryParse(s, out var parsed) => parsed,
        _ => 0
    };
}
