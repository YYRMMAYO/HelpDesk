using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Newtonsoft.Json;

namespace HelpDesk.Contracts;

/// <summary>
/// GitHub 上的插件仓库信息
/// </summary>
public class GitHubPluginInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string[] Tags { get; set; } = [];
    public string DllName { get; set; } = string.Empty;
    public long Size { get; set; }
}

/// <summary>
/// 本地已安装插件信息
/// </summary>
public class InstalledPluginInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime InstalledAt { get; set; } = DateTime.Now;
    public DateTime? LastUpdated { get; set; }
    public string LocalPath { get; set; } = string.Empty;
}

/// <summary>
/// 插件管理器 - 负责从 GitHub 发现、下载、加载和卸载插件
/// </summary>
public class PluginManager
{
    private readonly string _pluginsDirectory;
    private readonly string _cacheDirectory;
    private readonly string _installedPluginsFile;
    
    private readonly Dictionary<string, PluginLoadContext> _loadContexts = new();
    private readonly Dictionary<string, IPlugin> _loadedPlugins = new();
    private List<InstalledPluginInfo> _installedPlugins = new();
    
    /// <summary>
    /// GitHub 仓库配置
    /// </summary>
    public string GitHubOwner { get; set; } = "YYRMMAYO";
    public string GitHubRepo { get; set; } = "HelpDesk-Plugins";
    public string GitHubBranch { get; set; } = "main";
    
    public IReadOnlyList<InstalledPluginInfo> InstalledPlugins => _installedPlugins.AsReadOnly();
    public IReadOnlyDictionary<string, IPlugin> LoadedPlugins => _loadedPlugins;
    
    public PluginManager()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _pluginsDirectory = Path.Combine(appData, "HelpDesk", "Plugins");
        _cacheDirectory = Path.Combine(appData, "HelpDesk", "Cache");
        _installedPluginsFile = Path.Combine(appData, "HelpDesk", "installed_plugins.json");
        
        Directory.CreateDirectory(_pluginsDirectory);
        Directory.CreateDirectory(_cacheDirectory);
        
        LoadInstalledPlugins();
    }
    
    /// <summary>
    /// 从 GitHub 获取可用插件列表
    /// </summary>
    public async Task<List<GitHubPluginInfo>> GetAvailablePluginsAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "HelpDesk-Client");
            
            var url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/contents/plugins?ref={GitHubBranch}";
            var response = await http.GetStringAsync(url);
            
            var folders = JsonConvert.DeserializeObject<List<GitHubFolderItem>>(response);
            var plugins = new List<GitHubPluginInfo>();
            
            if (folders == null) return plugins;
            
            foreach (var folder in folders.Where(f => f.type == "dir"))
            {
                try
                {
                    var metaUrl = $"https://raw.githubusercontent.com/{GitHubOwner}/{GitHubRepo}/{GitHubBranch}/plugins/{folder.name}/metadata.json";
                    var metaJson = await http.GetStringAsync(metaUrl);
                    var pluginInfo = JsonConvert.DeserializeObject<GitHubPluginInfo>(metaJson);
                    if (pluginInfo != null)
                    {
                        pluginInfo.Folder = folder.name;
                        plugins.Add(pluginInfo);
                    }
                }
                catch
                {
                    // 跳过没有 metadata.json 的文件夹
                }
            }
            
            return plugins;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"获取插件列表失败: {ex.Message}");
            return new List<GitHubPluginInfo>();
        }
    }
    
    /// <summary>
    /// 下载并安装插件
    /// </summary>
    public async Task<bool> InstallPluginAsync(GitHubPluginInfo pluginInfo, IProgress<double>? progress = null)
    {
        try
        {
            var pluginDir = Path.Combine(_pluginsDirectory, pluginInfo.Folder);
            Directory.CreateDirectory(pluginDir);
            
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "HelpDesk-Client");
            
            // 下载 DLL
            var dllUrl = $"https://raw.githubusercontent.com/{GitHubOwner}/{GitHubRepo}/{GitHubBranch}/plugins/{pluginInfo.Folder}/{pluginInfo.DllName}";
            var dllBytes = await http.GetByteArrayAsync(dllUrl);
            var dllPath = Path.Combine(pluginDir, pluginInfo.DllName);
            await File.WriteAllBytesAsync(dllPath, dllBytes);
            
            progress?.Report(50);
            
            // 下载 metadata.json
            var metaUrl = $"https://raw.githubusercontent.com/{GitHubOwner}/{GitHubRepo}/{GitHubBranch}/plugins/{pluginInfo.Folder}/metadata.json";
            var metaJson = await http.GetStringAsync(metaUrl);
            await File.WriteAllTextAsync(Path.Combine(pluginDir, "metadata.json"), metaJson);
            
            progress?.Report(80);
            
            // 下载依赖文件（如果有 deps.json）
            try
            {
                var depsUrl = $"https://raw.githubusercontent.com/{GitHubOwner}/{GitHubRepo}/{GitHubBranch}/plugins/{pluginInfo.Folder}/deps.json";
                var depsJson = await http.GetStringAsync(depsUrl);
                await File.WriteAllTextAsync(Path.Combine(pluginDir, "deps.json"), depsJson);
            }
            catch { }
            
            progress?.Report(90);
            
            // 记录已安装信息
            var installed = new InstalledPluginInfo
            {
                Id = pluginInfo.Id,
                Name = pluginInfo.Name,
                Version = pluginInfo.Version,
                InstalledAt = DateTime.Now,
                LocalPath = pluginDir
            };
            
            var existing = _installedPlugins.FirstOrDefault(p => p.Id == pluginInfo.Id);
            if (existing != null)
                _installedPlugins.Remove(existing);
            
            _installedPlugins.Add(installed);
            SaveInstalledPlugins();
            
            progress?.Report(100);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"安装插件失败: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// 卸载插件
    /// </summary>
    public bool UninstallPlugin(string pluginId)
    {
        var plugin = _installedPlugins.FirstOrDefault(p => p.Id == pluginId);
        if (plugin == null) return false;
        
        // 先卸载已加载的插件
        if (_loadedPlugins.ContainsKey(pluginId))
        {
            UnloadPlugin(pluginId);
        }
        
        // 删除本地文件
        if (Directory.Exists(plugin.LocalPath))
        {
            try
            {
                Directory.Delete(plugin.LocalPath, true);
            }
            catch
            {
                return false;
            }
        }
        
        _installedPlugins.Remove(plugin);
        SaveInstalledPlugins();
        return true;
    }
    
    /// <summary>
    /// 加载已安装的插件
    /// </summary>
    public IPlugin? LoadPlugin(string pluginId)
    {
        var installed = _installedPlugins.FirstOrDefault(p => p.Id == pluginId);
        if (installed == null) return null;
        
        if (_loadedPlugins.ContainsKey(pluginId))
            return _loadedPlugins[pluginId];
        
        string? dllPath = null;
        
        // 优先从 metadata.json 读取 DllName
        var metaFile = Path.Combine(installed.LocalPath, "metadata.json");
        if (File.Exists(metaFile))
        {
            var metaJson = File.ReadAllText(metaFile);
            var meta = JsonConvert.DeserializeObject<GitHubPluginInfo>(metaJson);
            if (meta != null && !string.IsNullOrEmpty(meta.DllName))
            {
                var candidate = Path.Combine(installed.LocalPath, meta.DllName);
                if (File.Exists(candidate)) dllPath = candidate;
            }
        }
        
        // 回退：加载第一个非 Contracts 的 DLL
        dllPath ??= Directory.GetFiles(installed.LocalPath, "*.dll")
            .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("HelpDesk.Contracts"));
        
        if (dllPath == null) return null;
        
        try
        {
            var loadContext = new PluginLoadContext(dllPath);
            var assembly = loadContext.LoadFromAssemblyPath(dllPath);
            
            var pluginType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
            
            if (pluginType == null) return null;
            
            var plugin = (IPlugin)Activator.CreateInstance(pluginType)!;
            _loadContexts[pluginId] = loadContext;
            _loadedPlugins[pluginId] = plugin;
            
            return plugin;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"加载插件失败 [{pluginId}]: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// 卸载插件（从内存中）
    /// </summary>
    public void UnloadPlugin(string pluginId)
    {
        if (_loadedPlugins.TryGetValue(pluginId, out var plugin))
        {
            plugin.Dispose();
            _loadedPlugins.Remove(pluginId);
        }
        
        if (_loadContexts.TryGetValue(pluginId, out var context))
        {
            context.Unload();
            _loadContexts.Remove(pluginId);
        }
    }
    
    /// <summary>
    /// 检查插件是否有更新
    /// </summary>
    public async Task<bool> CheckForUpdateAsync(string pluginId)
    {
        var installed = _installedPlugins.FirstOrDefault(p => p.Id == pluginId);
        if (installed == null) return false;
        
        try
        {
            var available = await GetAvailablePluginsAsync();
            var remote = available.FirstOrDefault(p => p.Id == pluginId);
            if (remote == null) return false;
            
            return remote.Version != installed.Version;
        }
        catch
        {
            return false;
        }
    }
    
    private void LoadInstalledPlugins()
    {
        if (File.Exists(_installedPluginsFile))
        {
            var json = File.ReadAllText(_installedPluginsFile);
            _installedPlugins = JsonConvert.DeserializeObject<List<InstalledPluginInfo>>(json) ?? new();
        }
    }
    
    private void SaveInstalledPlugins()
    {
        var json = JsonConvert.SerializeObject(_installedPlugins, Formatting.Indented);
        File.WriteAllText(_installedPluginsFile, json);
    }
}

internal class GitHubFolderItem
{
    public string name { get; set; } = string.Empty;
    public string type { get; set; } = string.Empty;
    public string path { get; set; } = string.Empty;
}

/// <summary>
/// 插件专用的 AssemblyLoadContext
/// </summary>
public class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _pluginDirectory;
    
    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _pluginDirectory = Path.GetDirectoryName(pluginPath) ?? string.Empty;
    }
    
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 优先从插件目录加载
        var assemblyPath = Path.Combine(_pluginDirectory, $"{assemblyName.Name}.dll");
        if (File.Exists(assemblyPath))
        {
            return LoadFromAssemblyPath(assemblyPath);
        }
        
        // 如果是 HelpDesk.Contracts，使用默认上下文（避免类型不匹配）
        if (assemblyName.Name?.StartsWith("HelpDesk.Contracts") == true)
        {
            return null;
        }
        
        return null;
    }
    
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = Path.Combine(_pluginDirectory, unmanagedDllName);
        if (File.Exists(libraryPath))
        {
            return LoadUnmanagedDllFromPath(libraryPath);
        }
        
        return IntPtr.Zero;
    }
}
