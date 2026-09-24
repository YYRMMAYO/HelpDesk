using System.IO;
using System.Text;

namespace HelpDesk.Host;

/// <summary>
/// 极简日志。面向普通用户的软件出问题时，用户只会说「它坏了」，
/// 所以把异常落到 %LOCALAPPDATA%\HelpDesk\logs 下，方便远程求助时直接发日志文件。
/// 任何写日志的失败都不会抛出——日志绝不能反过来成为崩溃源。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HelpDesk", "logs");

    public static string CurrentLogFile
        => Path.Combine(LogDirectory, $"helpdesk-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var sb = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(level).Append("] ")
                    .AppendLine(message);

                if (ex != null) sb.AppendLine(ex.ToString());

                File.AppendAllText(CurrentLogFile, sb.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // 忽略：日志失败不影响程序
        }
    }
}
