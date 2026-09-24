using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace HelpDesk.Contracts;

/// <summary>
/// GitHub 上的插件仓库信息（对应插件目录中的 metadata.json）
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

    /// <summary>
    /// 文件校验清单。非空时宿主<b>只</b>下载清单中列出的文件，并逐个校验
    /// Size 与 Sha256——未列入清单的文件一律拒绝，防止仓库被追加恶意 DLL。
    /// 为空时退回「下载整个目录且不校验」的兼容模式，并在界面上给出警告。
    /// </summary>
    public List<PluginFileInfo> Files { get; set; } = new();

    /// <summary>是否提供可校验的文件清单。</summary>
    [JsonIgnore]
    public bool HasManifest => Files.Count > 0;

    /// <summary>
    /// 该插件在仓库中的布局前缀（<c>"plugins/"</c> 或空串表示仓库根），发现时由宿主填入。
    /// </summary>
    [JsonIgnore]
    public string LayoutPrefix { get; set; } = string.Empty;
}

/// <summary>插件包内的单个文件（相对插件目录的路径）。</summary>
public class PluginFileInfo
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
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

    /// <summary>是否在安装时通过了文件完整性校验。</summary>
    public bool Verified { get; set; }
}

/// <summary>
/// 插件加载失败的明细（供界面展示可读原因，而不是只弹一句「加载失败」）。
/// </summary>
public class PluginLoadError
{
    public string PluginId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// 插件管理器 - 负责从 GitHub 发现、下载、校验、加载和卸载插件
/// </summary>
public class PluginManager
{
    /// <summary>宿主版本号。插件可用 <see cref="PluginMetadata.MinHostVersion"/> 声明显式依赖。</summary>
    public const double HostVersion = 1.1;

    private static readonly string[] GitHubFolderLayouts = ["plugins", ""];

    private readonly string _rootDirectory;
    private readonly string _pluginsDirectory;
    private readonly string _cacheDirectory;
    private readonly string _installedPluginsFile;
    private readonly string _settingsFile;
    private readonly string _pendingDeletionsFile;

    private readonly Dictionary<string, PluginLoadContext> _loadContexts = new();
    private readonly Dictionary<string, IPlugin> _loadedPlugins = new();
    private List<InstalledPluginInfo> _installedPlugins = new();
    private List<string> _pendingDeletions = new();

    /// <summary>无参数构造函数：使用 %LOCALAPPDATA%\HelpDesk。</summary>
    public PluginManager() : this(null) { }

    /// <summary>
    /// 指定数据根目录（便于测试与绿色版部署）；传 null 时使用 %LOCALAPPDATA%\HelpDesk。
    /// </summary>
    public PluginManager(string? rootDirectory)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelpDesk");
        _pluginsDirectory = Path.Combine(_rootDirectory, "Plugins");
        _cacheDirectory = Path.Combine(_rootDirectory, "Cache");
        _installedPluginsFile = Path.Combine(_rootDirectory, "installed_plugins.json");
        _settingsFile = Path.Combine(_rootDirectory, "settings.json");
        _pendingDeletionsFile = Path.Combine(_rootDirectory, "pending_deletions.json");

        Directory.CreateDirectory(_pluginsDirectory);
        Directory.CreateDirectory(_cacheDirectory);

        LoadSettings();
        LoadInstalledPlugins();

        // 上一次没能删掉的插件目录（通常是因为 DLL 还被占用），这次启动时再试一次
        LoadPendingDeletions();
        ProcessPendingDeletions();
    }

    /// <summary>
    /// GitHub 仓库配置
    /// </summary>
    public string GitHubOwner { get; set; } = "YYRMMAYO";
    public string GitHubRepo { get; set; } = "HelpDesk-Plugins";
    public string GitHubBranch { get; set; } = "main";

    public string PluginsDirectory => _pluginsDirectory;
    public string DataRoot => _rootDirectory;

    public IReadOnlyList<InstalledPluginInfo> InstalledPlugins => _installedPlugins.AsReadOnly();
    public IReadOnlyDictionary<string, IPlugin> LoadedPlugins => _loadedPlugins;

    /// <summary>最近一次加载失败的明细，键为插件 Id。</summary>
    public IReadOnlyDictionary<string, PluginLoadError> LoadErrors => _loadErrors;

    private readonly Dictionary<string, PluginLoadError> _loadErrors = new();

    // ────────────────────────────────────────────────────────────────────────
    // 设置持久化
    // ────────────────────────────────────────────────────────────────────────

    private class HostSettings
    {
        public string GitHubOwner { get; set; } = "YYRMMAYO";
        public string GitHubRepo { get; set; } = "HelpDesk-Plugins";
        public string GitHubBranch { get; set; } = "main";
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsFile)) return;
            var s = JsonConvert.DeserializeObject<HostSettings>(File.ReadAllText(_settingsFile));
            if (s == null) return;
            if (!string.IsNullOrWhiteSpace(s.GitHubOwner)) GitHubOwner = s.GitHubOwner.Trim();
            if (!string.IsNullOrWhiteSpace(s.GitHubRepo)) GitHubRepo = s.GitHubRepo.Trim();
            if (!string.IsNullOrWhiteSpace(s.GitHubBranch)) GitHubBranch = s.GitHubBranch.Trim();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取设置失败: {ex.Message}");
        }
    }

    /// <summary>把当前的 GitHub 配置写入 settings.json（否则重启即丢失）。</summary>
    public void SaveSettings()
    {
        try
        {
            var json = JsonConvert.SerializeObject(new HostSettings
            {
                GitHubOwner = GitHubOwner,
                GitHubRepo = GitHubRepo,
                GitHubBranch = GitHubBranch
            }, Formatting.Indented);
            File.WriteAllText(_settingsFile, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"保存设置失败: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 插件市场
    // ────────────────────────────────────────────────────────────────────────

    private HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Add("User-Agent", "HelpDesk-Client");
        // 请求 CDN 重新校验缓存：插件刚发布时边缘节点可能还在发旧内容
        http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return http;
    }

    /// <summary>
    /// 插件文件的下载源。按顺序尝试：
    /// <list type="number">
    /// <item><c>raw.githubusercontent.com</c> —— GitHub 官方源；</item>
    /// <item><c>cdn.jsdelivr.net</c> —— GitHub 的公共镜像。国内网络下 raw 域名经常不可达，
    /// 只依赖官方源会导致「插件市场一直是空的」，所以必须有镜像兜底。</item>
    /// </list>
    /// <para>
    /// 两个源提供的是同一份文件，而安装时会用 metadata.json 里的 SHA-256 逐个校验，
    /// 因此「换源」不会降低完整性保证。
    /// </para>
    /// <para>
    /// 已知局限：校验清单与文件来自同一处，所以只能防「仓库文件被改/下载被截断」，
    /// 不能防「镜像站本身被攻陷」。要覆盖后者需要独立的签名清单（带内置公钥验签），
    /// 那是下一步的事。
    /// </para>
    /// </summary>
    private IEnumerable<string> BuildSourceUrls(string repoRelativePath)
    {
        var path = repoRelativePath.TrimStart('/');
        yield return $"https://raw.githubusercontent.com/{GitHubOwner}/{GitHubRepo}/{GitHubBranch}/{path}";
        yield return $"https://cdn.jsdelivr.net/gh/{GitHubOwner}/{GitHubRepo}@{GitHubBranch}/{path}";
    }

    /// <summary>按源顺序下载，每个源最多重试一次（网络抖动很常见）。</summary>
    /// <param name="cacheBuster">非空时作为查询参数附加到 URL 上，用于绕开边缘缓存。</param>
    private async Task<byte[]?> DownloadBytesAsync(
        HttpClient http,
        string repoRelativePath,
        CancellationToken ct,
        string? cacheBuster = null)
    {
        Exception? lastError = null;

        foreach (var baseUrl in BuildSourceUrls(repoRelativePath))
        {
            var url = cacheBuster == null
                ? baseUrl
                : $"{baseUrl}{(baseUrl.Contains('?') ? '&' : '?')}v={cacheBuster}";

            for (var attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await http.GetByteArrayAsync(url, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // 文件确实不存在，重试无意义，直接换源
                    lastError = ex;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    try { await Task.Delay(250 * (attempt + 1), ct); } catch (OperationCanceledException) { throw; }
                }
            }
        }

        Debug.WriteLine($"下载失败 [{repoRelativePath}]: {lastError?.Message}");
        return null;
    }

    /// <summary>
    /// 下载单个文件并校验，校验不过就重试。
    /// <para>
    /// 为什么要重试而不是直接失败：插件刚发布时 <c>raw.githubusercontent.com</c> 的边缘缓存
    /// 可能还在发旧文件，而索引是新拉的——「文件与清单对不上」这时并不是仓库被篡改，
    /// 等一会儿或换个 URL 就好了。所以第 2 次起在 URL 上挂随机参数绕缓存，
    /// 仍不通过才认定有问题，并在提示里说清可能是缓存延迟。
    /// </para>
    /// </summary>
    private async Task<string?> DownloadVerifiedAsync(
        HttpClient http,
        string repoPath,
        string relative,
        PluginFileInfo? expected,
        string destinationPath,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        string? lastProblem = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var bytes = await DownloadBytesAsync(
                http, repoPath, ct, attempt == 1 ? null : Guid.NewGuid().ToString("N"));

            if (bytes == null)
                return $"下载 {relative} 失败（已尝试全部下载源），请检查网络后重试";

            var problem = VerifyBytes(bytes, relative, expected);
            if (problem == null)
            {
                await File.WriteAllBytesAsync(destinationPath, bytes, ct);
                return null;
            }

            lastProblem = problem;
            Debug.WriteLine($"第 {attempt} 次校验未通过：{problem}");

            if (attempt < maxAttempts)
            {
                try { await Task.Delay(1500 * attempt, ct); } catch (OperationCanceledException) { throw; }
            }
        }

        return $"{lastProblem}。已重试 {maxAttempts} 次" +
               "——如果这个插件是刚刚才发布的，可能是下载源（CDN）缓存尚未刷新，过 1~2 分钟再试即可。";
    }

    /// <summary>校验下载到的字节是否符合清单；符合返回 null，否则返回可读原因。</summary>
    private static string? VerifyBytes(byte[] bytes, string relative, PluginFileInfo? expected)
    {
        if (expected == null) return null;   // 兼容模式：没有清单可校验

        if (expected.Size > 0 && bytes.LongLength != expected.Size)
            return $"文件 {relative} 大小不符（期望 {expected.Size} 字节，实际 {bytes.LongLength} 字节），已中止安装";

        if (!string.IsNullOrWhiteSpace(expected.Sha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(bytes));
            if (!actual.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                return $"文件 {relative} 校验值不匹配，已中止安装（文件可能被篡改）";
        }

        return null;
    }

    private async Task<string?> DownloadStringAsync(
        HttpClient http,
        string repoRelativePath,
        CancellationToken ct)
    {
        var bytes = await DownloadBytesAsync(http, repoRelativePath, ct);
        if (bytes == null) return null;

        var text = Encoding.UTF8.GetString(bytes);
        // 有些工具写 JSON 时带 BOM，Newtonsoft 会报错，这里先去干净
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>
    /// 从 GitHub 获取可用插件列表。
    /// <para>
    /// 首选仓库根目录的 <c>plugins-index.json</c>：一次请求就拿到全部插件（含每个文件的
    /// SHA-256 清单），比「列目录 + 逐个取 metadata.json」少 3~4 次往返，失败概率低得多。
    /// 索引不存在时回退到遍历仓库文件树，并依次探测 <c>plugins/{Folder}/</c> 与
    /// <c>{Folder}/</c> 两种布局（历史分发仓库把插件放在根目录，写死一种就会「市场里一个插件都没有」）。
    /// </para>
    /// </summary>
    public async Task<List<GitHubPluginInfo>> GetAvailablePluginsAsync(CancellationToken ct = default)
    {
        using var http = CreateHttpClient();

        var fromIndex = await TryLoadIndexAsync(http, ct);
        if (fromIndex != null) return fromIndex;

        return await DiscoverByTreeAsync(http, ct);
    }

    private async Task<List<GitHubPluginInfo>?> TryLoadIndexAsync(HttpClient http, CancellationToken ct)
    {
        var json = await DownloadStringAsync(http, "plugins-index.json", ct);
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var index = JsonConvert.DeserializeObject<PluginIndex>(json);
            if (index?.Plugins == null || index.Plugins.Count == 0) return null;

            var prefix = index.LayoutPrefix ?? string.Empty;
            if (!string.IsNullOrEmpty(prefix)) prefix = prefix.TrimEnd('/') + "/";

            var valid = index.Plugins
                .Where(p => !string.IsNullOrWhiteSpace(p.Id) && IsSafeFolderName(p.Folder))
                .ToList();

            foreach (var plugin in valid) plugin.LayoutPrefix = prefix;

            if (valid.Count == 0) return null;

            Debug.WriteLine($"插件索引加载成功：{valid.Count} 个插件");
            return valid.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"解析插件索引失败，回退到遍历仓库: {ex.Message}");
            return null;
        }
    }

    private async Task<List<GitHubPluginInfo>> DiscoverByTreeAsync(HttpClient http, CancellationToken ct)
    {
        var result = new List<GitHubPluginInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<GitHubTreeItem>? tree = null;

        foreach (var layout in GitHubFolderLayouts)
        {
            ct.ThrowIfCancellationRequested();
            var prefix = string.IsNullOrEmpty(layout) ? string.Empty : layout + "/";

            // 用 git tree 一次性拿到全部路径，比逐级 contents 请求省得多。
            // 注意它依赖 api.github.com，国内网络可能不可达——所以插件仓库应当提供
            // plugins-index.json 作为首选路径。
            tree ??= await GetTreeAsync(http, ct);
            if (tree == null) continue;

            var folders = tree
                .Where(t => t.type == "blob")
                .Select(t => t.path)
                .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(p => p[prefix.Length..])
                .Where(p => p.Contains('/'))
                .Select(p => p[..p.IndexOf('/')])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var folder in folders)
            {
                if (!seen.Add(folder)) continue;
                try
                {
                    var metaJson = await DownloadStringAsync(http, prefix + folder + "/metadata.json", ct);
                    if (string.IsNullOrWhiteSpace(metaJson)) continue;

                    var info = JsonConvert.DeserializeObject<GitHubPluginInfo>(metaJson);
                    if (info == null || string.IsNullOrWhiteSpace(info.Id)) continue;

                    info.Folder = folder;
                    info.LayoutPrefix = prefix;
                    result.Add(info);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // 跳过没有 metadata.json / 元数据损坏的目录
                }
            }

            if (result.Count > 0) break;   // 第一种布局命中就不再探测
        }

        return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private async Task<List<GitHubTreeItem>?> GetTreeAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/git/trees/{GitHubBranch}?recursive=1";
            var json = await http.GetStringAsync(url, ct);
            return JsonConvert.DeserializeObject<GitHubTreeResponse>(json)?.tree;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"获取仓库文件树失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 下载并安装插件。
    /// <para>
    /// 关键点（旧实现只下载主 DLL 与 metadata.json，导致带依赖的插件装完加载不了）：
    /// 先下载到临时目录再整体替换，并且当 metadata.json 提供了文件清单时逐个校验
    /// 大小与 SHA-256，任何一个文件不匹配就整体放弃，不留下半成品。
    /// </para>
    /// </summary>
    public async Task<InstallResult> InstallPluginAsync(
        GitHubPluginInfo pluginInfo,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pluginInfo.Folder) || !IsSafeFolderName(pluginInfo.Folder))
            return InstallResult.Fail($"插件目录名不合法：{pluginInfo.Folder}");

        var prefix = pluginInfo.LayoutPrefix;
        var targetDir = Path.Combine(_pluginsDirectory, pluginInfo.Folder);
        var stagingDir = Path.Combine(_pluginsDirectory, $".staging-{pluginInfo.Folder}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(stagingDir);
            using var http = CreateHttpClient();

            // 1) 确定要下载的文件集合
            List<(string RepoPath, string Relative, PluginFileInfo? Expected)> wanted = new();

            if (pluginInfo.HasManifest)
            {
                foreach (var f in pluginInfo.Files)
                {
                    if (!IsSafeRelativePath(f.Path))
                        return InstallResult.Fail($"插件清单包含不安全路径：{f.Path}");
                    // 清单里的路径既可能是 "Sub/x.dll"，也可能已带插件目录前缀，统一按相对插件目录处理
                    var rel = f.Path.StartsWith(pluginInfo.Folder + "/", StringComparison.OrdinalIgnoreCase)
                        ? f.Path[(pluginInfo.Folder.Length + 1)..]
                        : f.Path;
                    if (!IsSafeRelativePath(rel))
                        return InstallResult.Fail($"插件清单包含不安全路径：{f.Path}");
                    wanted.Add((prefix + pluginInfo.Folder + "/" + rel, rel, f));
                }
            }
            else
            {
                var tree = await GetTreeAsync(http, ct);
                var folderPrefix = prefix + pluginInfo.Folder + "/";
                var blobs = tree?.Where(t => t.type == "blob"
                        && t.path.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (blobs == null || blobs.Count == 0)
                    return InstallResult.Fail("无法获取插件文件列表（仓库不可访问或插件目录为空）");

                foreach (var b in blobs)
                    wanted.Add((b.path, b.path[folderPrefix.Length..], null));
            }

            if (wanted.Count == 0)
                return InstallResult.Fail("插件清单为空");

            // 2) 下载 + 校验
            foreach (var (repoPath, relative, expected) in wanted)
            {
                ct.ThrowIfCancellationRequested();

                var destPath = Path.Combine(stagingDir, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                var problem = await DownloadVerifiedAsync(http, repoPath, relative, expected, destPath, ct);
                if (problem != null) return InstallResult.Fail(problem);
            }

            // 2.5) 把插件元数据一并写进插件目录。
            //      它不能出现在自己的校验清单里（清单是后算的），所以由宿主用「已经过校验的
            //      索引数据」生成，而不是再去仓库下载一次——既避免了「清单漏管一个文件」，
            //      也让插件目录是自描述的：LoadPlugin 能直接按 metadata.json 的 DllName 定位主程序集。
            var metadataJson = JsonConvert.SerializeObject(pluginInfo, Formatting.Indented);
            await File.WriteAllTextAsync(
                Path.Combine(stagingDir, "metadata.json"), metadataJson, new UTF8Encoding(false), ct);

            // 3) 整体替换旧版本
            var staleDirectoryKept = false;
            if (Directory.Exists(targetDir))
            {
                UnloadPlugin(pluginInfo.Id);
                if (!TryDeleteDirectory(targetDir, out _))
                {
                    // DLL 仍被「已加载但未完全回收」的插件占用时目录删不掉。
                    // 这时不能放弃安装，退化成「把新文件覆盖上去」：旧版本可能残留少量
                    // 新版本已不存在的文件，但功能是好的。真正的清理交给卸载时处理。
                    staleDirectoryKept = true;
                }
            }

            Directory.CreateDirectory(targetDir);
            CopyDirectory(stagingDir, targetDir);

            // 4) 记账
            var installed = new InstalledPluginInfo
            {
                Id = pluginInfo.Id,
                Name = pluginInfo.Name,
                Version = pluginInfo.Version,
                InstalledAt = DateTime.Now,
                LocalPath = targetDir,
                Verified = pluginInfo.HasManifest
            };

            var existing = _installedPlugins.FirstOrDefault(p => p.Id == pluginInfo.Id);
            if (existing != null)
            {
                installed.InstalledAt = existing.InstalledAt;
                installed.LastUpdated = DateTime.Now;
                _installedPlugins.Remove(existing);
            }

            _installedPlugins.Add(installed);
            SaveInstalledPlugins();

            return InstallResult.Ok(DescribeInstallWarning(pluginInfo, staleDirectoryKept));
        }
        catch (OperationCanceledException)
        {
            return InstallResult.Fail("安装已取消");
        }
        catch (Exception ex)
        {
            return InstallResult.Fail(ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            }
            catch { /* 清理失败不影响结果 */ }
        }
    }

    private static string? DescribeInstallWarning(GitHubPluginInfo pluginInfo, bool staleDirectoryKept)
    {
        if (staleDirectoryKept)
            return "旧版本的部分文件仍被占用，新版本已覆盖安装；如果功能异常，请关闭程序后重新安装";
        if (!pluginInfo.HasManifest)
            return "该插件未提供文件校验清单，安装时无法校验文件完整性";
        return null;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 装载 / 卸载
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 加载已安装的插件；失败时返回 null，原因写入 <see cref="LoadErrors"/>。
    /// </summary>
    public IPlugin? LoadPlugin(string pluginId)
        => TryLoadPlugin(pluginId, out var plugin, out _) ? plugin : null;

    /// <summary>
    /// 加载已安装的插件并给出可读的失败原因。
    /// </summary>
    public bool TryLoadPlugin(string pluginId, out IPlugin? plugin, out string? error)
    {
        plugin = null;
        error = null;

        if (_loadedPlugins.TryGetValue(pluginId, out var loaded))
        {
            plugin = loaded;
            return true;
        }

        var installed = _installedPlugins.FirstOrDefault(p => p.Id == pluginId);
        if (installed == null)
        {
            error = "该插件未安装";
            Record(pluginId, error);
            return false;
        }

        var candidates = ResolveDllCandidates(installed);
        if (candidates.Count == 0)
        {
            error = $"在 {installed.LocalPath} 中找不到可加载的插件程序集";
            Record(pluginId, error);
            return false;
        }

        // 逐个候选 DLL 尝试：旧实现取「第一个不是 HelpDesk.Contracts 的 DLL」，
        // 在目录里存在 Newtonsoft.Json.dll 等依赖时可能随机挑到依赖而报「找不到插件类型」。
        var failures = new List<string>();
        foreach (var dllPath in candidates)
        {
            try
            {
                var loadContext = new PluginLoadContext(dllPath);
                var assembly = loadContext.LoadFromAssemblyPath(dllPath);

                var pluginType = assembly.GetTypes().FirstOrDefault(
                    t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

                if (pluginType == null)
                {
                    loadContext.Unload();
                    failures.Add($"{Path.GetFileName(dllPath)}：未找到 IPlugin 实现");
                    continue;
                }

                if (Activator.CreateInstance(pluginType) is not IPlugin instance)
                {
                    loadContext.Unload();
                    failures.Add($"{Path.GetFileName(dllPath)}：无法实例化 {pluginType.Name}");
                    continue;
                }

                if (instance.Metadata.MinHostVersion > HostVersion)
                {
                    instance.Dispose();
                    loadContext.Unload();
                    failures.Add(
                        $"{Path.GetFileName(dllPath)}：需要宿主 {instance.Metadata.MinHostVersion:F1} 或更高版本，当前 {HostVersion:F1}");
                    continue;
                }

                _loadContexts[pluginId] = loadContext;
                _loadedPlugins[pluginId] = instance;
                plugin = instance;
                _loadErrors.Remove(pluginId);
                return true;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(dllPath)}：{ex.Message}");
            }
        }

        error = string.Join("；", failures);
        Record(pluginId, error);
        return false;
    }

    private void Record(string pluginId, string error)
        => _loadErrors[pluginId] = new PluginLoadError { PluginId = pluginId, Reason = error };

    /// <summary>
    /// 解析出候选插件程序集，优先级：metadata.json 的 DllName → 形如
    /// HelpDesk.Plugins.*.dll 的文件 → 其余非契约、非已知依赖的 DLL。
    /// </summary>
    private List<string> ResolveDllCandidates(InstalledPluginInfo installed)
    {
        string[] all = [];
        try
        {
            if (Directory.Exists(installed.LocalPath))
                all = Directory.GetFiles(installed.LocalPath, "*.dll", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"枚举插件目录失败: {ex.Message}");
        }

        var result = new List<string>();

        var declared = ReadDeclaredDllName(installed.LocalPath);
        if (!string.IsNullOrWhiteSpace(declared))
        {
            var path = Path.Combine(installed.LocalPath, declared);
            if (File.Exists(path)) result.Add(path);
        }

        foreach (var f in all.Where(f => Path.GetFileName(f)
                     .StartsWith("HelpDesk.Plugins.", StringComparison.OrdinalIgnoreCase)))
            if (!result.Contains(f, StringComparer.OrdinalIgnoreCase))
                result.Add(f);

        foreach (var f in all)
            if (!result.Contains(f, StringComparer.OrdinalIgnoreCase))
                result.Add(f);

        return result;
    }

    private static string? ReadDeclaredDllName(string pluginDir)
    {
        try
        {
            var metaFile = Path.Combine(pluginDir, "metadata.json");
            if (!File.Exists(metaFile)) return null;
            var meta = JsonConvert.DeserializeObject<GitHubPluginInfo>(File.ReadAllText(metaFile));
            return string.IsNullOrWhiteSpace(meta?.DllName) ? null : meta!.DllName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 卸载插件（内存 + 磁盘）。
    /// <para>
    /// 先从已安装清单里摘掉并落盘，再尝试删目录：这样即使文件因为仍被占用而删不掉，
    /// 用户视角的「已卸载」也已经生效，剩下的空壳会登记进待删除清单，在下次启动
    /// （那时还没有任何插件被加载，DLL 不会被占用）时自动清理。
    /// 这一点很关键——插件可能注册了静态事件订阅而活得更久，指望 GC 一定回收掉
    /// 是不可靠的。
    /// </para>
    /// </summary>
    public UninstallResult UninstallPlugin(string pluginId)
    {
        var plugin = _installedPlugins.FirstOrDefault(p => p.Id == pluginId);
        if (plugin == null) return UninstallResult.Fail("该插件未安装");

        UnloadPlugin(pluginId);
        _loadErrors.Remove(pluginId);

        _installedPlugins.Remove(plugin);
        SaveInstalledPlugins();

        if (Directory.Exists(plugin.LocalPath) && !TryDeleteDirectory(plugin.LocalPath, out var deleteError))
        {
            SchedulePendingDeletion(plugin.LocalPath);
            return UninstallResult.Ok(
                $"插件已卸载，但它的文件暂时被占用（{deleteError}），" +
                "已安排在下一次启动程序时自动清理。");
        }

        return UninstallResult.Ok(null);
    }

    /// <summary>
    /// 卸载插件（从内存中）。
    /// <para>
    /// <see cref="AssemblyLoadContext.Unload"/> 只是「请求」卸载，真正的终结发生在 GC 之后。
    /// 所以这里主动触发一次回收并给文件系统一点时间，否则紧接着删目录会因 DLL 仍被
    /// 占用而失败（旧实现就是悄悄返回 false，界面上表现为「卸载没反应」）。
    /// </para>
    /// </summary>
    public void UnloadPlugin(string pluginId)
    {
        if (_loadedPlugins.TryGetValue(pluginId, out var plugin))
        {
            try { plugin.Dispose(); } catch { /* 插件自身清理异常不应阻断卸载 */ }
            _loadedPlugins.Remove(pluginId);
        }

        if (_loadContexts.TryGetValue(pluginId, out var context))
        {
            try { context.Unload(); } catch { }
            _loadContexts.Remove(pluginId);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>带重试的目录删除。</summary>
    private static bool TryDeleteDirectory(string path, out string? error)
    {
        error = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return true;
                Directory.Delete(path, true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Thread.Sleep(120 * (attempt + 1));
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        return !Directory.Exists(path);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), true);
    }

    /// <summary>
    /// 检查插件是否有更新
    /// </summary>
    public async Task<bool> CheckForUpdateAsync(string pluginId, CancellationToken ct = default)
    {
        var installed = _installedPlugins.FirstOrDefault(p => p.Id == pluginId);
        if (installed == null) return false;

        var available = await GetAvailablePluginsAsync(ct);
        var remote = available.FirstOrDefault(p => p.Id == pluginId);
        return remote != null && remote.Version != installed.Version;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 路径安全
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>插件目录名只允许单层、无分隔符、无盘符（单层名本身无法构成上跳路径）。</summary>
    private static bool IsSafeFolderName(string folder)
        => folder.Length is > 0 and <= 64
           && folder is not "." and not ".."
           && folder.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0;

    /// <summary>拒绝绝对路径、<c>..</c> 上跳、盘符与非法字符，防止仓库内容写到插件目录之外。</summary>
    private static bool IsSafeRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return false;
        if (Path.IsPathRooted(relative)) return false;
        if (relative.Length > 260) return false;

        var segments = relative.Replace('\\', '/').Split('/');
        if (segments.Length == 0) return false;

        foreach (var seg in segments)
        {
            if (seg.Length == 0) return false;
            if (seg == "." || seg == "..") return false;
            if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (seg.Contains(':')) return false;
        }
        return true;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 待删除清单（插件目录被占用时的兜底清理）
    // ────────────────────────────────────────────────────────────────────────

    private void LoadPendingDeletions()
    {
        try
        {
            if (!File.Exists(_pendingDeletionsFile)) return;
            _pendingDeletions = JsonConvert.DeserializeObject<List<string>>(
                File.ReadAllText(_pendingDeletionsFile)) ?? new();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取待删除清单失败: {ex.Message}");
            _pendingDeletions = new();
        }
    }

    private void SavePendingDeletions()
    {
        try
        {
            var json = JsonConvert.SerializeObject(_pendingDeletions, Formatting.Indented);
            File.WriteAllText(_pendingDeletionsFile, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"保存待删除清单失败: {ex.Message}");
        }
    }

    private void SchedulePendingDeletion(string path)
    {
        if (!_pendingDeletions.Contains(path, StringComparer.OrdinalIgnoreCase))
            _pendingDeletions.Add(path);
        SavePendingDeletions();
    }

    /// <summary>
    /// 清理上次没能删掉的插件目录。在构造函数里调用——那时还没有任何插件被加载，
    /// DLL 不会被占用，因此这一次通常都能删掉。
    /// </summary>
    private void ProcessPendingDeletions()
    {
        if (_pendingDeletions.Count == 0) return;

        var remaining = new List<string>();
        foreach (var path in _pendingDeletions)
        {
            // 只清理确实位于插件目录内部的路径：配置文件是文本，
            // 万一被改动过也不能让它指向插件目录之外的地方
            if (!IsInsidePluginsDirectory(path)) continue;
            if (Directory.Exists(path) && !TryDeleteDirectory(path, out _)) remaining.Add(path);
        }

        _pendingDeletions = remaining;
        SavePendingDeletions();
    }

    private bool IsInsidePluginsDirectory(string path)
    {
        try
        {
            var root = Path.GetFullPath(_pluginsDirectory);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(path);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 已安装清单
    // ────────────────────────────────────────────────────────────────────────

    private void LoadInstalledPlugins()
    {
        if (!File.Exists(_installedPluginsFile)) return;
        try
        {
            var json = File.ReadAllText(_installedPluginsFile);
            _installedPlugins = JsonConvert.DeserializeObject<List<InstalledPluginInfo>>(json) ?? new();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取已安装插件清单失败: {ex.Message}");
            _installedPlugins = new();
        }
    }

    private void SaveInstalledPlugins()
    {
        try
        {
            var json = JsonConvert.SerializeObject(_installedPlugins, Formatting.Indented);
            File.WriteAllText(_installedPluginsFile, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"保存已安装插件清单失败: {ex.Message}");
        }
    }

    /// <summary>安装结果（带用户可读的说明）。</summary>
    public readonly record struct InstallResult(bool Succeeded, string? Message)
    {
        public static InstallResult Ok(string? warning = null) => new(true, warning);
        public static InstallResult Fail(string reason) => new(false, reason);
    }

    /// <summary>卸载结果。<see cref="Message"/> 非空表示「已卸载但需要注意」。</summary>
    public readonly record struct UninstallResult(bool Succeeded, string? Message)
    {
        public static UninstallResult Ok(string? warning = null) => new(true, warning);
        public static UninstallResult Fail(string reason) => new(false, reason);
    }
}

internal class GitHubTreeResponse
{
    [JsonProperty("tree")]
    public List<GitHubTreeItem>? tree { get; set; }
}

internal class GitHubTreeItem
{
    public string path { get; set; } = string.Empty;
    public string type { get; set; } = string.Empty;
    public long size { get; set; }
}

/// <summary>
/// 插件仓库根目录的 <c>plugins-index.json</c>。
/// <para>
/// 存在的意义是把「发现插件」压缩成一次请求：列出目录 + 逐个取 metadata.json 需要
/// 1 + N 次往返，任何一次抖动都会让某个插件在市场里凭空消失（实测确实会）。
/// </para>
/// </summary>
public class PluginIndex
{
    /// <summary>索引格式版本，便于以后演进。</summary>
    public int Version { get; set; } = 1;

    /// <summary>插件目录所在的前缀（<c>"plugins/"</c> 或空串表示仓库根）。</summary>
    public string LayoutPrefix { get; set; } = string.Empty;

    /// <summary>说明文字，仅用于人工查看。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>各插件的元数据（与 metadata.json 内容一致，含文件校验清单）。</summary>
    public List<GitHubPluginInfo> Plugins { get; set; } = new();
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
        // 契约程序集必须用宿主自己的那一份，否则插件里的 IPlugin 与宿主的 IPlugin
        // 会是两个不同的类型，强制转换会失败。返回 null 即交给默认上下文解析。
        if (assemblyName.Name?.StartsWith("HelpDesk.Contracts", StringComparison.OrdinalIgnoreCase) == true)
            return null;

        // 同名依赖优先用插件自带的版本
        var assemblyPath = Path.Combine(_pluginDirectory, $"{assemblyName.Name}.dll");
        if (File.Exists(assemblyPath))
            return LoadFromAssemblyPath(assemblyPath);

        // 插件目录下可能还有 runtimes/ 之类的子目录，回退搜索一次
        foreach (var sub in Directory.GetDirectories(_pluginDirectory))
        {
            var nested = Path.Combine(sub, $"{assemblyName.Name}.dll");
            if (File.Exists(nested)) return LoadFromAssemblyPath(nested);
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = Path.Combine(_pluginDirectory, unmanagedDllName);
        if (File.Exists(libraryPath))
            return LoadUnmanagedDllFromPath(libraryPath);

        return IntPtr.Zero;
    }
}
