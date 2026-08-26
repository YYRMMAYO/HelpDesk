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
/// 插件上下文，提供插件与宿主交互的能力
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
    /// 向状态栏发送消息
    /// </summary>
    void ShowStatus(string message);
    
    /// <summary>
    /// 显示通知
    /// </summary>
    void ShowNotification(string title, string message, NotificationType type = NotificationType.Info);
    
    /// <summary>
    /// 执行 PowerShell 命令（需要管理员权限）
    /// </summary>
    Task<string> RunPowerShellAsync(string command);
}

public enum NotificationType
{
    Info,
    Success,
    Warning,
    Error
}
