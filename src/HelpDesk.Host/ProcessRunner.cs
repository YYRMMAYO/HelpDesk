using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using HelpDesk.Contracts;

namespace HelpDesk.Host;

/// <summary>
/// 外部进程执行与 UAC 提权的<b>唯一</b>实现处。
///
/// <para><b>为什么提权要走临时文件而不是命令行参数</b></para>
/// <para>
/// 以管理员权限启动进程必须使用 <c>ShellExecute</c>（<c>Verb=runas</c>），而
/// ShellExecute 会先解析命令行字符串再交给目标程序——任何被拼进去的用户数据
/// （例如注册表里的 UninstallString、文件路径）都可能改变命令的语义。
/// 因此本实现把脚本内容写进宿主生成的临时 .ps1 文件，命令行上只出现宿主自己
/// 生成的路径，从结构上消除命令行注入面。
/// </para>
///
/// <para><b>为什么用结果文件而不是重定向</b></para>
/// <para>
/// ShellExecute 启动的进程由 Windows 的提权服务创建，我们拿不到它的标准输出管道，
/// 也取不到 ExitCode（.NET 在该模式下访问 ExitCode 会抛异常）。所以脚本把
/// 输出与退出码写进宿主指定的临时文件，宿主读文件为准——这也是它能稳定工作的原因。
/// </para>
/// </summary>
public static class ProcessRunner
{
    private const int ErrorCancelled = 1223;

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 执行一段 PowerShell 脚本。脚本以 <c>-File</c> 方式运行（见类注释）。
    /// </summary>
    public static async Task<ProcessResult> RunPowerShellAsync(
        string script,
        bool elevated,
        int timeoutMs,
        CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "HelpDesk", "ps");
        var id = Guid.NewGuid().ToString("N");
        var scriptPath = Path.Combine(workDir, $"task-{id}.ps1");
        var resultPath = Path.Combine(workDir, $"result-{id}.txt");
        var exitPath = Path.Combine(workDir, $"exit-{id}.txt");

        try
        {
            Directory.CreateDirectory(workDir);

            // 用拼接而非插值构造，避免 PowerShell 的 { } 与 C# 插值语法互相干扰。
            // 两个 {0} 位置只会填入宿主生成的临时路径，不含任何第三方数据。
            var content =
                "$ErrorActionPreference = 'Stop'\r\n" +
                "$ProgressPreference = 'SilentlyContinue'\r\n" +
                "$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8\r\n" +
                "$__exit = 0\r\n" +
                "$__text = ''\r\n" +
                "try {\r\n" +
                "    $__text = ( & {\r\n" +
                script + "\r\n" +
                "    } 2>&1 | Out-String )\r\n" +
                "} catch {\r\n" +
                "    $__text = $__text + [Environment]::NewLine + ( $_ | Out-String )\r\n" +
                "    $__exit = 1\r\n" +
                "}\r\n" +
                "Set-Content -LiteralPath " + Contracts.PowerShellText.Literal(resultPath) + " -Value $__text -Encoding UTF8\r\n" +
                "Set-Content -LiteralPath " + Contracts.PowerShellText.Literal(exitPath) + " -Value $__exit -Encoding UTF8\r\n";

            // BOM 是必需的：Windows PowerShell 5.1 在无 BOM 时按系统 ANSI 代码页
            // 读取 .ps1，脚本里的中文会变成乱码甚至语法错误。
            await File.WriteAllTextAsync(scriptPath, content, new UTF8Encoding(true), ct);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    $"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass " +
                    $"-File {Quote(scriptPath)}",
                UseShellExecute = elevated,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false
            };
            if (elevated) psi.Verb = "runas";

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return new ProcessResult { Started = false, ElevationDenied = true };
            }
            catch (Exception ex)
            {
                return new ProcessResult { Started = false, Error = ex.Message };
            }

            if (process == null)
                return new ProcessResult { Started = false, Error = "无法启动 powershell.exe" };

            var timedOut = false;
            var canceled = false;
            using (process)
            {
                using var timeoutCts = new CancellationTokenSource(timeoutMs > 0 ? timeoutMs : Timeout.Infinite);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
                try
                {
                    await process.WaitForExitAsync(linked.Token);
                }
                catch (OperationCanceledException)
                {
                    timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
                    canceled = ct.IsCancellationRequested;
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
            }

            var stdout = await ReadTextIfExistsAsync(resultPath, ct);
            var exitText = await ReadTextIfExistsAsync(exitPath, ct);
            int? exitCode = int.TryParse(exitText.Trim(), out var code) ? code : null;

            var result = new ProcessResult
            {
                Started = true,
                StdOut = stdout.Trim(),
                ExitCode = exitCode,
                TimedOut = timedOut,
                Canceled = canceled
            };

            // 走完流程但脚本被 catch 捕获过，说明执行出错：把输出挪到 StdErr 更贴合语义
            if (exitCode is > 0) result.StdErr = result.StdOut;

            return result;
        }
        catch (OperationCanceledException)
        {
            return new ProcessResult { Started = false, Canceled = true };
        }
        catch (Exception ex)
        {
            return new ProcessResult { Started = false, Error = ex.Message };
        }
        finally
        {
            // 临时文件可能含注册表/路径信息，用完即删
            foreach (var f in new[] { scriptPath, resultPath, exitPath })
            {
                try
                {
                    if (File.Exists(f)) File.Delete(f);
                }
                catch { /* 清理失败不影响结果 */ }
            }
        }
    }

    /// <summary>
    /// 启动一个外部程序并立即返回，不接管它的界面（如官方卸载向导）。
    /// </summary>
    public static Task<ProcessResult> StartProcessAsync(
        string fileName,
        string arguments,
        bool elevated,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = true
                };
                if (!string.IsNullOrWhiteSpace(arguments)) psi.Arguments = arguments;
                if (elevated) psi.Verb = "runas";

                using var process = Process.Start(psi);
                // 这里必须用 using 释放 Process 句柄，但 Dispose 不会结束已启动的进程，
                // 卸载向导会继续独立运行——这正是「程序和功能」的行为。
                return new ProcessResult { Started = process != null, ExitCode = null };
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return new ProcessResult { Started = false, ElevationDenied = true };
            }
            catch (Exception ex)
            {
                return new ProcessResult { Started = false, Error = ex.Message };
            }
        }, ct);
    }

    private static async Task<string> ReadTextIfExistsAsync(string path, CancellationToken ct)
    {
        try
        {
            // 提权进程刚退出时文件句柄可能还没完全释放，给一点重试
            for (var i = 0; i < 5; i++)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        return await File.ReadAllTextAsync(path, ct);
                    }
                    catch (IOException)
                    {
                        await Task.Delay(60, ct);
                        continue;
                    }
                }
                await Task.Delay(60, ct);
            }
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
