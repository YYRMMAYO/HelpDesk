using System.Windows.Controls;

namespace HelpDesk.Contracts;

/// <summary>
/// 插件元数据，描述插件的基本信息
/// </summary>
public class PluginMetadata
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = string.Empty;
    public string GitHubFolder { get; set; } = string.Empty;
    public string[] Tags { get; set; } = [];

    /// <summary>
    /// 宿主最低版本要求。宿主的 <see cref="IPluginContext.HostVersion"/> 低于该值时拒绝加载，
    /// 避免新版插件（依赖新版契约成员）在旧宿主上被反射调用到缺失的方法而崩溃。
    /// </summary>
    public double MinHostVersion { get; set; } = 1.0;
}

/// <summary>
/// 插件接口，所有插件必须实现
/// </summary>
public interface IPlugin : IDisposable
{
    PluginMetadata Metadata { get; }

    /// <summary>
    /// 初始化插件
    /// </summary>
    void Init(IPluginContext context);

    /// <summary>
    /// 获取插件的主界面控件
    /// </summary>
    UserControl GetView();

    /// <summary>
    /// 插件被激活时调用
    /// </summary>
    void OnActivated();

    /// <summary>
    /// 插件被停用时调用
    /// </summary>
    void OnDeactivated();
}

/// <summary>
/// 外部进程 / PowerShell 的执行结果。
/// <para>
/// 说明：以管理员权限启动时走的是 ShellExecute（<c>Verb=runas</c>），Windows 不会把
/// 子进程的句柄交给我们，因此 <see cref="ExitCode"/> 可能为 null；此时以
/// <see cref="Started"/> 判断「是否成功拉起」即可（与「程序和功能」的行为一致）。
/// </para>
/// </summary>
public class ProcessResult
{
    /// <summary>退出代码；未取到时为 null。</summary>
    public int? ExitCode { get; set; }

    /// <summary>标准输出（PowerShell 场景下已合并错误流）。</summary>
    public string StdOut { get; set; } = string.Empty;

    /// <summary>标准错误。</summary>
    public string StdErr { get; set; } = string.Empty;

    /// <summary>是否成功启动。</summary>
    public bool Started { get; set; }

    /// <summary>用户在 UAC 提权提示上点了「否」。</summary>
    public bool ElevationDenied { get; set; }

    /// <summary>执行超时（进程已被终止）。</summary>
    public bool TimedOut { get; set; }

    /// <summary>被调用方取消。</summary>
    public bool Canceled { get; set; }

    /// <summary>启动或执行失败的原因；无失败为 null。</summary>
    public string? Error { get; set; }

    /// <summary>是否按预期成功。</summary>
    public bool Success =>
        Started && !ElevationDenied && !TimedOut && !Canceled && ExitCode is null or 0;

    /// <summary>供界面直接展示的一句话结果。</summary>
    public string Summary => ElevationDenied ? "已取消：用户拒绝了管理员权限请求"
        : Canceled ? "已取消"
        : TimedOut ? "执行超时"
        : !Started ? $"启动失败：{Error}"
        : ExitCode is null ? "已启动"
        : ExitCode == 0 ? "执行成功"
        : $"执行失败（退出代码 {ExitCode}）：{FirstNonEmpty(StdErr, StdOut)}";

    private static string FirstNonEmpty(string a, string b)
    {
        var text = string.IsNullOrWhiteSpace(a) ? b : a;
        text = text.Trim();
        return text.Length > 200 ? text[..200] + "…" : text;
    }
}

/// <summary>
/// 插件上下文，提供插件与宿主交互的能力。
/// <para>
/// 安全设计：所有外部执行能力都收敛到本接口，宿主在唯一一处实现参数转义与提权机制，
/// 插件侧无需（也无法）自行拼接命令行。详见 <see cref="RunPowerShellAsync"/>。
/// </para>
/// </summary>
public interface IPluginContext
{
    /// <summary>
    /// 插件本地数据目录
    /// </summary>
    string DataDirectory { get; }

    /// <summary>
    /// 主程序版本号
    /// </summary>
    double HostVersion { get; }

    /// <summary>
    /// 当前进程是否已以管理员权限运行。
    /// </summary>
    bool IsElevated { get; }

    /// <summary>
    /// 向状态栏发送消息
    /// </summary>
    void ShowStatus(string message);

    /// <summary>
    /// 显示通知
    /// </summary>
    void ShowNotification(string title, string message, NotificationType type = NotificationType.Info);

    /// <summary>
    /// 执行 PowerShell 脚本。
    /// <para>
    /// <paramref name="script"/> 会先写入宿主生成的临时 .ps1 文件，再以
    /// <c>-File</c> 方式执行——脚本正文不会被拼进命令行，因此不会出现命令行注入；
    /// 但脚本作者仍需对其中嵌入的第三方字符串负责，请使用
    /// <see cref="PowerShellText.Literal"/> 转义（或改用 <c>-LiteralPath</c>）。
    /// </para>
    /// <para>
    /// <paramref name="elevated"/> 为 true 时经 UAC 以管理员权限执行；用户拒绝提权时
    /// 返回 <see cref="ProcessResult.ElevationDenied"/> 为 true，宿主不会误判为执行失败。
    /// </para>
    /// </summary>
    Task<ProcessResult> RunPowerShellAsync(
        string script,
        bool elevated = false,
        int timeoutMs = 60000,
        CancellationToken ct = default);

    /// <summary>
    /// 启动一个外部程序并立即返回（不接管其后续界面），例如注册表中登记的官方卸载程序。
    /// </summary>
    /// <param name="fileName">可执行文件路径（不要包含参数）。</param>
    /// <param name="arguments">命令行参数（可为空）。</param>
    /// <param name="elevated">是否通过 UAC 以管理员权限启动。</param>
    Task<ProcessResult> StartProcessAsync(
        string fileName,
        string arguments,
        bool elevated = false,
        CancellationToken ct = default);
}

public enum NotificationType
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// PowerShell 文本处理。把用户/注册表来源的字符串安全地嵌入 PowerShell 脚本的
/// <b>唯一</b>实现，避免每个插件各写一份转义逻辑而出现遗漏。
/// </summary>
public static class PowerShellText
{
    /// <summary>
    /// 生成 PowerShell 单引号字面量。单引号字符串内的 <c>'</c> 通过双写转义，
    /// 因此结果不可能跳出引号边界，可安全用于 <c>-LiteralPath</c> 等参数。
    /// </summary>
    public static string Literal(string? value)
        => "'" + (value ?? string.Empty).Replace("'", "''") + "'";

    /// <summary>
    /// 生成路径数组字面量，如 <c>@('C:\a','C:\b')</c>；空集合返回 <c>@()</c>。
    /// </summary>
    public static string LiteralArray(IEnumerable<string?> values)
        => "@(" + string.Join(",", values.Select(Literal)) + ")";
}
