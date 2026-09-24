using System.Threading.Tasks;
using System.Windows;

namespace HelpDesk.Host;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        // 目标用户是「不懂电脑」的人：任何未处理异常都不应该变成 Windows 的崩溃对话框，
        // 而应该弹出一句看得懂的话，并把细节写进日志。
        DispatcherUnhandledException += (_, e) =>
        {
            AppLog.Error("界面线程未处理异常", e.Exception);
            ShowFriendlyError(e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Error("后台线程未处理异常", e.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };
    }

    private static void ShowFriendlyError(Exception ex)
    {
        try
        {
            MessageBox.Show(
                "抱歉，程序遇到了一点问题，但已经帮你安全地停下来了。\n\n" +
                $"出错位置：{ex.GetType().Name}\n" +
                $"说明：{ex.Message}\n\n" +
                $"详细日志已保存到：\n{AppLog.CurrentLogFile}",
                "HelpDesk",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // 连提示框都弹不出来时只能放弃
        }
    }
}
