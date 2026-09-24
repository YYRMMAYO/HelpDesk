using System.IO;
using System.Text;

namespace HelpDesk.Plugins.SoftwareUninstaller;

/// <summary>残留路径的判定结论。</summary>
public enum ResidueVerdict
{
    /// <summary>可以删除。</summary>
    Allowed,

    /// <summary>无需处理（例如路径本来就不存在），不是错误。</summary>
    Skipped,

    /// <summary>被安全规则拦下，禁止删除。</summary>
    Blocked
}

/// <summary>残留路径判定结果。</summary>
public sealed record ResiduePathCheck(ResidueVerdict Verdict, string Reason)
{
    public bool CanDelete => Verdict == ResidueVerdict.Allowed;
    public static ResiduePathCheck Allow(string reason = "") => new(ResidueVerdict.Allowed, reason);
    public static ResiduePathCheck Skip(string reason) => new(ResidueVerdict.Skipped, reason);
    public static ResiduePathCheck Block(string reason) => new(ResidueVerdict.Blocked, reason);
}

/// <summary>卸载命令的检查结果。</summary>
public sealed record UninstallCommandCheck(
    bool Allowed,
    string Reason,
    string? ExecutablePath,
    string Arguments)
{
    public static UninstallCommandCheck Deny(string reason) => new(false, reason, null, string.Empty);
    public static UninstallCommandCheck Allow(string exe, string args) => new(true, string.Empty, exe, args);
}

/// <summary>
/// 卸载与残留清理的安全规则合集。
///
/// <para><b>核心原则</b></para>
/// <list type="number">
/// <item><b>只信注册表，不按名字猜。</b>清理目标只能是该软件在注册表里自己写下的
/// <c>InstallLocation</c>，以及它自己的卸载注册表键。绝不使用「目录名包含软件名」
/// 这类模糊匹配——那正是误删同类同名文件的根源。</item>
/// <item><b>删除前把路径的合法性验到底。</b>盘符必须真实存在、必须是绝对路径、
/// 不能是 UNC、不能是系统/用户关键目录、不能包含其他软件的安装目录、
/// 不能包含本程序自己所在的目录、不能是符号链接。</item>
/// <item><b>卸载命令必须自己起进程，不经解释器。</b>cmd / powershell / wscript /
/// rundll32 / certutil 这类命令一旦来自被篡改的注册表，就等于任意代码执行。</item>
/// </list>
/// </summary>
public static class UninstallSafety
{
    /// <summary>
    /// 禁止作为卸载程序启动的可执行文件。
    /// <para>
    /// 这些程序本身没问题，问题是它们的作用是「执行别人给的命令或脚本」：
    /// 正常软件的卸载命令几乎从不经过它们，而被篡改过的注册表项几乎一定会。
    /// 拦下来之后会把命令原文展示给用户，用户可以自行判断要不要手动执行。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ForbiddenExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        // 命令解释器
        "cmd", "cmd.exe",
        "powershell", "powershell.exe",
        "pwsh", "pwsh.exe",
        "conhost.exe",
        // 脚本宿主
        "wscript.exe", "cscript.exe", "mshta.exe",
        // 加载任意 DLL 的宿主
        "rundll32.exe", "regsvr32.exe", "installutil.exe",
        // 下载器（把「卸载」变成「下载并运行」）
        "bitsadmin.exe", "certutil.exe", "curl.exe", "wget.exe",
        // 其他 shell
        "bash.exe", "wsl.exe", "sh.exe"
    };

    /// <summary>命令中出现这些片段说明它想联网取东西，正常的卸载命令不需要。</summary>
    private static readonly string[] RemoteMarkers = ["http://", "https://", "ftp://", "\\\\?\\", "\\\\.\\"];

    /// <summary>调用方未显式传入「正在运行的程序目录」时用它。</summary>
    public static string RunningDirectory => AppContext.BaseDirectory;

    // ══════════════════════════════════════════════════════════════
    // 一、卸载命令检查
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 检查并解析某个软件注册表里登记的卸载命令。
    /// </summary>
    /// <param name="program">目标软件。</param>
    /// <param name="preferQuiet">是否优先使用 QuietUninstallString（静默卸载）。</param>
    public static UninstallCommandCheck ValidateUninstallCommand(InstalledProgram program, bool preferQuiet)
    {
        if (program.NoRemove)
            return UninstallCommandCheck.Deny(
                "注册表明确把这程序标记为「不允许卸载」（NoRemove），请到「控制面板 → 程序和功能」里处理。");

        // 选命令：优先用用户指定的那种，另一种作为兜底
        var chosen = preferQuiet
            ? FirstNonEmpty(program.QuietUninstallString, program.UninstallString)
            : FirstNonEmpty(program.UninstallString, program.QuietUninstallString);

        if (string.IsNullOrWhiteSpace(chosen))
            return UninstallCommandCheck.Deny(
                "注册表里没有记录卸载命令，无法自动卸载。可以改用「扫描残留」清理它的安装目录。");

        var command = chosen.Trim();

        // 命令里出现远程地址 / 设备路径：正常的卸载命令不会这样
        foreach (var marker in RemoteMarkers)
        {
            if (command.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return UninstallCommandCheck.Deny(
                    $"命令里出现了网络地址或设备路径（{marker}），正常的卸载命令不会这样，已阻止执行。");
        }

        var (executable, arguments) = SplitExecutableAndArguments(command);
        if (string.IsNullOrWhiteSpace(executable))
            return UninstallCommandCheck.Deny("无法从命令中解析出可执行文件路径。");

        var expanded = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));

        // UNC 路径：可能指向局域网共享上的程序
        if (expanded.StartsWith(@"\\", StringComparison.Ordinal) || expanded.StartsWith("//", StringComparison.Ordinal))
            return UninstallCommandCheck.Deny("卸载程序位于网络路径（UNC）上，已阻止执行。");

        var fileName = Path.GetFileName(expanded);
        if (ForbiddenExecutables.Contains(fileName) || ForbiddenExecutables.Contains(expanded))
            return UninstallCommandCheck.Deny(
                $"卸载命令要通过「{fileName}」这类解释器/脚本宿主执行，可能包含被隐藏的操作，已阻止执行。" +
                "正常的卸载命令会直接指向软件自己的 uninstall.exe 或 msiexec。");

        // 解析到真实文件：既验证了路径确实存在（避免对着错误路径操作），
        // 也让界面上可以明确告诉用户「将要运行的是这个程序」
        var resolved = ResolveExecutable(expanded);
        if (resolved == null)
            return UninstallCommandCheck.Deny(
                $"找不到卸载程序：{expanded}\n" +
                "它可能已经被删除或移动了。这种情况下建议改用「扫描残留」清理它的安装目录。");

        return UninstallCommandCheck.Allow(resolved, arguments);
    }

    /// <summary>
    /// 把注册表里的卸载命令拆成「可执行文件」与「参数」两部分。
    /// <para>
    /// 难点在于历史上大量安装程序写的是不带引号、但路径里含空格的命令，例如
    /// <c>C:\Program Files\Foo\uninst.exe /S</c>。按第一个空格切会得到
    /// <c>C:\Program</c>，后面所有判断就都建立在一个错误路径上了。
    /// 因此这里从最右侧开始，逐段扩大候选路径，取第一个真实存在的文件；
    /// 全都不存在时才退回「按第一个空格切分」。
    /// </para>
    /// </summary>
    public static (string Executable, string Arguments) SplitExecutableAndArguments(string command)
    {
        var text = (command ?? string.Empty).Trim();
        if (text.Length == 0) return (string.Empty, string.Empty);

        if (text[0] == '"')
        {
            var closing = text.IndexOf('"', 1);
            if (closing > 1)
                return (text[1..closing], text[(closing + 1)..].Trim());
            // 引号没闭合：把去掉开头引号的剩余部分当作路径
            return (text[1..], string.Empty);
        }

        var spaceIndexes = new List<int>();
        for (var i = 0; i < text.Length; i++)
            if (text[i] == ' ') spaceIndexes.Add(i);

        // 从最长的前缀开始试：C:\Program Files\Foo\uninst.exe /S 优先命中完整路径
        for (var i = spaceIndexes.Count - 1; i >= 0; i--)
        {
            var candidate = text[..spaceIndexes[i]];
            if (TryFileExists(candidate))
                return (candidate, text[(spaceIndexes[i] + 1)..].Trim());
        }

        if (spaceIndexes.Count == 0) return (text, string.Empty);
        return (text[..spaceIndexes[0]], text[(spaceIndexes[0] + 1)..].Trim());
    }

    private static bool TryFileExists(string path)
    {
        try
        {
            if (File.Exists(path)) return true;
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return expanded != path && File.Exists(expanded);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把可执行文件（可能是裸文件名，如 <c>MsiExec.exe</c>）解析为磁盘上的真实路径。</summary>
    public static string? ResolveExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;

        string expanded;
        try
        {
            expanded = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));
        }
        catch
        {
            return null;
        }

        try
        {
            if (Path.IsPathRooted(expanded))
                return File.Exists(expanded) ? Path.GetFullPath(expanded) : null;

            // 裸文件名：先 System32（msiexec.exe 等系统工具都在这里）
            var system32 = Path.Combine(Environment.SystemDirectory, expanded);
            if (File.Exists(system32)) return system32;

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows))
            {
                var inWindows = Path.Combine(windows, expanded);
                if (File.Exists(inWindows)) return inWindows;
            }

            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    var candidate = Path.Combine(directory, expanded);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch
                {
                    // 忽略非法 PATH 项
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    /// <summary>整机安装（HKLM）的软件通常需要管理员权限才能卸载。</summary>
    public static bool NeedsElevationForUninstall(InstalledProgram program)
    {
        if (program.IsMachineWide) return true;

        // 装在 Program Files 下但登记在 HKCU 的情况也存在，检查一下路径
        var location = program.InstallLocation;
        if (string.IsNullOrWhiteSpace(location)) return false;

        var normalized = Normalize(location);
        return normalized != null && ProgramFilesRoots.Any(root => IsSameOrInside(normalized, root));
    }

    // ══════════════════════════════════════════════════════════════
    // 二、残留路径检查
    // ══════════════════════════════════════════════════════════════

    private static readonly Lazy<IReadOnlyList<string>> ProtectedRoots = new(CollectProtectedRoots);

    private static readonly IReadOnlyList<string> ProgramFilesRoots = new[]
        {
            Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            Normalize(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))
        }
        .Where(root => !string.IsNullOrEmpty(root))
        .Select(root => root!)
        .ToList();

    /// <summary>系统与用户的关键目录：绝不允许被当作「残留」删除。</summary>
    private static IReadOnlyList<string> CollectProtectedRoots()
    {
        var roots = new List<string?>();

        void Add(string? path) => roots.Add(Normalize(path));
        void AddFolder(Environment.SpecialFolder folder)
        {
            try { Add(Environment.GetFolderPath(folder)); } catch { }
        }

        // 系统
        Add(Path.GetPathRoot(Environment.SystemDirectory));          // C:\
        Add(Environment.GetEnvironmentVariable("SystemDrive") is { } drive ? drive + "\\" : null);
        Add(Environment.GetEnvironmentVariable("windir"));
        Add(Environment.SystemDirectory);                             // C:\Windows\System32
        AddFolder(Environment.SpecialFolder.Windows);
        AddFolder(Environment.SpecialFolder.System);
        AddFolder(Environment.SpecialFolder.SystemX86);
        AddFolder(Environment.SpecialFolder.Fonts);
        AddFolder(Environment.SpecialFolder.Resources);

        // 程序目录
        AddFolder(Environment.SpecialFolder.ProgramFiles);
        AddFolder(Environment.SpecialFolder.ProgramFilesX86);
        AddFolder(Environment.SpecialFolder.CommonProgramFiles);
        AddFolder(Environment.SpecialFolder.CommonProgramFilesX86);
        AddFolder(Environment.SpecialFolder.CommonApplicationData);    // C:\ProgramData

        // 用户目录
        AddFolder(Environment.SpecialFolder.UserProfile);              // C:\Users\<你>
        Add(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        AddFolder(Environment.SpecialFolder.LocalApplicationData);
        AddFolder(Environment.SpecialFolder.ApplicationData);
        AddFolder(Environment.SpecialFolder.Desktop);
        AddFolder(Environment.SpecialFolder.DesktopDirectory);
        AddFolder(Environment.SpecialFolder.MyDocuments);
        AddFolder(Environment.SpecialFolder.MyMusic);
        AddFolder(Environment.SpecialFolder.MyPictures);
        AddFolder(Environment.SpecialFolder.MyVideos);
        AddFolder(Environment.SpecialFolder.Favorites);
        AddFolder(Environment.SpecialFolder.Templates);
        AddFolder(Environment.SpecialFolder.Recent);
        AddFolder(Environment.SpecialFolder.SendTo);
        AddFolder(Environment.SpecialFolder.History);
        AddFolder(Environment.SpecialFolder.Cookies);
        AddFolder(Environment.SpecialFolder.InternetCache);
        AddFolder(Environment.SpecialFolder.CommonDocuments);
        AddFolder(Environment.SpecialFolder.CommonDesktopDirectory);
        AddFolder(Environment.SpecialFolder.Programs);
        AddFolder(Environment.SpecialFolder.CommonPrograms);
        AddFolder(Environment.SpecialFolder.StartMenu);
        AddFolder(Environment.SpecialFolder.CommonStartMenu);
        AddFolder(Environment.SpecialFolder.Startup);
        AddFolder(Environment.SpecialFolder.CommonStartup);
        AddFolder(Environment.SpecialFolder.AdminTools);
        AddFolder(Environment.SpecialFolder.CommonAdminTools);
        AddFolder(Environment.SpecialFolder.NetworkShortcuts);
        AddFolder(Environment.SpecialFolder.PrinterShortcuts);
        AddFolder(Environment.SpecialFolder.CDBurning);

        // 临时目录
        Add(Path.GetTempPath());

        return roots.Where(r => !string.IsNullOrEmpty(r))
                    .Select(r => r!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    /// <summary>
    /// 判断一个候选路径能否作为「残留」删除。
    /// </summary>
    /// <param name="candidate">候选路径（唯一来源：注册表的 InstallLocation）。</param>
    /// <param name="otherInstallLocations">其他软件的安装目录，用于防止删掉共用父目录。</param>
    /// <param name="runningDirectory">本程序所在目录，防止把自己删掉。</param>
    public static ResiduePathCheck ValidateResiduePath(
        string? candidate,
        IEnumerable<string> otherInstallLocations,
        string runningDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return ResiduePathCheck.Skip("注册表未记录安装位置，无法定位残留");

        // 控制字符会破坏「一行一个路径」的批量删除清单
        if (candidate.Any(char.IsControl))
            return ResiduePathCheck.Block("路径里包含控制字符，已阻止。");

        // 必须是绝对路径
        if (!Path.IsPathRooted(candidate))
            return ResiduePathCheck.Block($"不是绝对路径（{candidate}），已阻止。");

        // 排除 UNC 与设备路径
        if (candidate.StartsWith(@"\\", StringComparison.Ordinal)
            || candidate.StartsWith("//", StringComparison.Ordinal))
            return ResiduePathCheck.Block("是网络路径（UNC），已阻止。");

        if (candidate.StartsWith(@"\\?\", StringComparison.Ordinal)
            || candidate.StartsWith(@"\\.\", StringComparison.Ordinal))
            return ResiduePathCheck.Block("是设备路径，已阻止。");

        var normalized = Normalize(candidate);
        if (normalized == null)
            return ResiduePathCheck.Block("路径格式无法解析，已阻止。");

        // 盘符必须真实存在（外接盘没插、光驱没盘的情况很常见）
        var root = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(root))
            return ResiduePathCheck.Block("无法识别盘符，已阻止。");

        try
        {
            if (!Directory.Exists(root))
                return ResiduePathCheck.Block($"盘符 {root} 不存在（可能磁盘未接入），已阻止。");
        }
        catch
        {
            return ResiduePathCheck.Block($"盘符 {root} 无法访问，已阻止。");
        }

        // 根目录本身永远不能删
        if (Normalize(root) is { } normalizedRoot && normalizedRoot.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            return ResiduePathCheck.Block($"这是磁盘根目录 {root}，已阻止。");

        // 系统 / 用户关键目录
        foreach (var protectedRoot in ProtectedRoots.Value)
        {
            if (protectedRoot.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return ResiduePathCheck.Block($"这是系统或用户的关键目录（{protectedRoot}），已阻止。");

            // 候选路径把某个关键目录包在里面 → 删它会连带删掉系统目录
            if (IsSameOrInside(protectedRoot, normalized))
                return ResiduePathCheck.Block(
                    $"这个目录里包含系统或用户目录（{protectedRoot}），删它会伤及系统，已阻止。");
        }

        // 本程序自己的目录
        var running = Normalize(runningDirectory);
        if (running != null && IsSameOrInside(running, normalized))
            return ResiduePathCheck.Block("这个目录里包含 HelpDesk 自己，已阻止。");

        // 系统目录内部（C:\Windows\...）：正常软件不会把安装目录放在这里，
        // 出现在这里就说明注册表数据不可信，宁可不清理。
        var windowsDirectory = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (windowsDirectory != null
            && !windowsDirectory.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            && IsSameOrInside(normalized, windowsDirectory))
            return ResiduePathCheck.Block($"位于系统目录 {windowsDirectory} 内部，已阻止。");

        // 其他软件的安装目录 → 这是「不误删其他软件」的关键一条
        foreach (var other in otherInstallLocations)
        {
            var otherNormalized = Normalize(other);
            if (otherNormalized == null) continue;
            if (IsSameOrInside(otherNormalized, normalized))
                return ResiduePathCheck.Block(
                    $"这个目录里还装着别的软件（{other}），删除会误伤它，已阻止。");
        }

        if (!Directory.Exists(normalized) && !File.Exists(normalized))
            return ResiduePathCheck.Skip("该路径已经不存在，无需清理");

        // 符号链接 / 交叉点：删除可能顺着链接影响到它指向的真实位置
        if (ContainsReparsePoint(normalized))
            return ResiduePathCheck.Block(
                "这个目录（或它的子目录）里有符号链接或交叉点，删除可能影响它们指向的位置，已阻止。请手动处理。");

        return ResiduePathCheck.Allow("注册表记录的安装目录，且通过了全部安全检查");
    }

    // ══════════════════════════════════════════════════════════════
    // 三、注册表键检查
    // ══════════════════════════════════════════════════════════════

    private static readonly string[] AllowedRegistryPrefixes =
    [
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\",
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\",
        @"HKEY_CURRENT_USER\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\"
    ];

    /// <summary>
    /// 只有「卸载信息根键 + 恰好一层子键」才允许删除。
    /// 这样即使注册表数据被构造成 <c>...\Uninstall\Foo\Bar\..</c> 之类也删不出这个范围。
    /// </summary>
    public static bool IsUninstallRegistryKey(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        foreach (var prefix in AllowedRegistryPrefixes)
        {
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var leaf = fullPath[prefix.Length..];
            return leaf.Length > 0
                   && leaf.IndexOf('\\') < 0
                   && leaf is not "." and not "..";
        }

        return false;
    }

    /// <summary>把 <c>HKEY_LOCAL_MACHINE\...</c> 转成 PowerShell 提供程序路径 <c>HKLM:\...</c>。</summary>
    public static string? ToPowerShellRegistryPath(string fullPath)
    {
        string? hive = null;
        if (fullPath.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)) hive = "HKLM";
        else if (fullPath.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)) hive = "HKCU";
        if (hive == null) return null;

        var separator = fullPath.IndexOf('\\');
        if (separator < 0) return null;

        return $"{hive}:\\{fullPath[(separator + 1)..]}";
    }

    // ══════════════════════════════════════════════════════════════
    // 四、公共小工具
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 规范化为「绝对路径 + 末尾无分隔符」；盘符根目录保留为 <c>C:\</c> 形式。
    /// <para>
    /// 同时展开 8.3 短名：<c>C:\PROGRA~1</c> 与 <c>C:\Program Files</c> 是同一个目录，
    /// 不展开的话「系统关键目录」和「别的软件的安装目录」这两道防线都能用短名绕过
    /// （注册表里的 InstallLocation 完全可能是短名写法）。
    /// </para>
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            return TrimSeparators(ExpandShortName(full));
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferLength);

    /// <summary>把 8.3 短名换算成长名；路径不存在或换算失败时原样返回。</summary>
    private static string ExpandShortName(string path)
    {
        try
        {
            var buffer = new StringBuilder(512);
            var length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
            if (length == 0) return path;

            if (length > buffer.Capacity)
            {
                buffer = new StringBuilder((int)length + 1);
                length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
                if (length == 0) return path;
            }

            var result = buffer.ToString();
            return string.IsNullOrEmpty(result) ? path : result;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    /// 目录树里是否存在符号链接 / 交叉点（含目录自身）。
    /// <para>
    /// 为什么必须拦：删除使用的是 PowerShell 的 <c>Remove-Item -Recurse</c>，
    /// 而 Windows PowerShell 5.1 在遇到交叉点时可能顺着链接<b>删到链接指向的真实目录</b>里去。
    /// 只要发现链接就整条候选作废——这是重装系统的代价，不是「少清理一点垃圾」的代价。
    /// </para>
    /// </summary>
    public static bool ContainsReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
        }
        catch
        {
            // 读不到属性时按「有风险」处理
            return true;
        }

        if (!Directory.Exists(path)) return false;

        var pending = new Stack<string>();
        pending.Push(path);
        var visited = 0;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            // 目录大到不正常的程度：宁可不清理，也不冒顺着链接删出去的风险
            if (++visited > 200_000) return true;

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                try
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) return true;
                }
                catch
                {
                    continue;
                }

                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }

        return false;
    }

    private static string TrimSeparators(string fullPath)
    {
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0) return fullPath;
        // "C:" → "C:\"，让「等于某盘根目录」的判断能正常工作
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + Path.DirectorySeparatorChar;
        return trimmed;
    }

    /// <summary><paramref name="candidate"/> 是否等于 <paramref name="container"/> 或位于其内部。</summary>
    public static bool IsSameOrInside(string candidate, string container)
    {
        var inner = TrimSeparators(candidate);
        var outer = TrimSeparators(container);
        if (inner.Equals(outer, StringComparison.OrdinalIgnoreCase)) return true;

        var withSeparator = outer.EndsWith(Path.DirectorySeparatorChar)
            ? outer
            : outer + Path.DirectorySeparatorChar;

        return inner.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>删除这些路径是否需要管理员权限。</summary>
    public static bool NeedsElevation(IEnumerable<string> filePaths, IEnumerable<string> registryKeys)
    {
        if (registryKeys.Any(k => k.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)))
            return true;

        var userScopes = new List<string>();
        void Add(string? p) { var n = Normalize(p); if (n != null) userScopes.Add(n); }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add(Path.GetTempPath());

        foreach (var path in filePaths)
        {
            var normalized = Normalize(path);
            if (normalized == null) continue;
            if (!userScopes.Any(scope => IsSameOrInside(normalized, scope))) return true;
        }

        return false;
    }

    private static string? FirstNonEmpty(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first : second;
}
