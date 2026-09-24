using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using HelpDesk.Contracts;
using Newtonsoft.Json;

// 插件打包器：把 src/HelpDesk.Plugins 下的插件编译产物整理成可分发的插件包，
// 并为每个文件生成 SHA-256 清单（宿主在安装时会逐个校验），最后生成仓库根目录的
// plugins-index.json。
//
// 为什么要用工具而不是手写 metadata.json：清单一旦手写就会和实际文件漂移，
// 而「校验清单和文件对不上」会直接让用户装不上插件。这里从编译产物里现算，
// 顺手还把插件程序集真的加载一遍——等于每次打包都做了一次冒烟测试。

var options = CommandLineOptions.Parse(args);
if (options == null)
{
    Console.Error.WriteLine("""
用法:
  PluginPackager pack --source <src/HelpDesk.Plugins 目录> --out <插件分发仓库目录> [--layout root|plugins] [--configuration Release]

说明:
  pack   逐个插件读取编译产物 + 反射读取插件元数据，复制到 --out 下，并生成
         metadata.json（含每个文件的 SHA-256）与仓库根目录的 plugins-index.json。
""");
    return 2;
}

var sourceRoot = Path.GetFullPath(options.Source);
if (!Directory.Exists(sourceRoot))
{
    Console.Error.WriteLine($"找不到插件源码目录：{sourceRoot}");
    return 1;
}

var outRoot = Path.GetFullPath(options.Out);
Directory.CreateDirectory(outRoot);

var layoutPrefix = options.Layout == "plugins" ? "plugins" : string.Empty;
var targetRoot = string.IsNullOrEmpty(layoutPrefix) ? outRoot : Path.Combine(outRoot, layoutPrefix);
Directory.CreateDirectory(targetRoot);

var targetFramework = options.TargetFramework;
var packaged = new List<GitHubPluginInfo>();
var failures = new List<string>();

foreach (var projectDir in Directory.GetDirectories(sourceRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
{
    var projectName = Path.GetFileName(projectDir);
    var csproj = Directory.GetFiles(projectDir, "*.csproj").FirstOrDefault();
    if (csproj == null)
    {
        Console.WriteLine($"跳过 {projectName}（没有 .csproj）");
        continue;
    }

    var binDir = Path.Combine(projectDir, "bin", options.Configuration, targetFramework);
    var mainDll = Directory.Exists(binDir)
        ? Directory.GetFiles(binDir, "HelpDesk.Plugins.*.dll").FirstOrDefault()
        : null;

    if (mainDll == null)
    {
        var message = $"{projectName}: 在 {binDir} 找不到 HelpDesk.Plugins.*.dll，请先构建该插件";
        Console.Error.WriteLine("错误 " + message);
        failures.Add(message);
        continue;
    }

    PluginMetadata metadata;
    try
    {
        metadata = PluginIntrospection.ReadMetadata(mainDll);
    }
    catch (Exception ex)
    {
        var message = $"{projectName}: 加载插件程序集失败 —— {ex.Message}";
        Console.Error.WriteLine("错误 " + message);
        failures.Add(message);
        continue;
    }

    if (string.IsNullOrWhiteSpace(metadata.Id))
    {
        var message = $"{projectName}: 插件 Metadata.Id 为空";
        Console.Error.WriteLine("错误 " + message);
        failures.Add(message);
        continue;
    }

    var folder = string.IsNullOrWhiteSpace(metadata.GitHubFolder) ? projectName : metadata.GitHubFolder;
    var destination = Path.Combine(targetRoot, folder);

    if (Directory.Exists(destination)) Directory.Delete(destination, true);
    Directory.CreateDirectory(destination);

    var packagedFiles = new List<PluginFileInfo>();
    foreach (var file in Directory.GetFiles(binDir, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(binDir, file).Replace('\\', '/');
        if (!ShouldPackage(relative)) continue;

        var target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, true);

        packagedFiles.Add(new PluginFileInfo
        {
            Path = relative,
            Size = new FileInfo(target).Length,
            Sha256 = Sha256Of(target)
        });
    }

    if (packagedFiles.Count == 0)
    {
        var message = $"{projectName}: 打包后没有任何文件";
        Console.Error.WriteLine("错误 " + message);
        failures.Add(message);
        continue;
    }

    var info = new GitHubPluginInfo
    {
        Id = metadata.Id,
        Name = metadata.Name,
        Description = metadata.Description,
        Version = metadata.Version,
        Author = metadata.Author,
        Folder = folder,
        Tags = metadata.Tags,
        DllName = Path.GetFileName(mainDll),
        Size = packagedFiles.Sum(f => f.Size),
        Files = packagedFiles
    };

    File.WriteAllText(
        Path.Combine(destination, "metadata.json"),
        PluginIntrospection.ToMetadataJson(info),
        new UTF8Encoding(false));

    packaged.Add(info);

    var hostWarning = metadata.MinHostVersion > PluginManager.HostVersion
        ? $"  ⚠ 要求宿主 >= {metadata.MinHostVersion:F1}（当前 {PluginManager.HostVersion:F1}）"
        : string.Empty;
    Console.WriteLine(
        $"已打包 {folder,-22} {info.Name,-14} v{info.Version,-8} {packagedFiles.Count} 个文件 "
        + $"{info.Size / 1024.0:F1} KB{hostWarning}");
}

// 生成仓库根目录的索引：宿主一次请求即可拿到全部插件，避免逐个取 metadata.json 时
// 某次网络抖动导致某个插件在市场里「凭空消失」。
var index = new PluginIndex
{
    Version = 1,
    LayoutPrefix = layoutPrefix,
    Note = $"由 tools/PluginPackager 自动生成于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}，请勿手工编辑",
    Plugins = packaged.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
};

var indexPath = Path.Combine(outRoot, "plugins-index.json");
File.WriteAllText(indexPath, PluginIntrospection.ToIndexJson(index), new UTF8Encoding(false));
Console.WriteLine();
Console.WriteLine($"已写入索引：{indexPath}（{packaged.Count} 个插件）");

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.Error.WriteLine($"有 {failures.Count} 个插件打包失败：");
    foreach (var failure in failures) Console.Error.WriteLine("  - " + failure);
    return 1;
}

return 0;

// ──────────────────────────────────────────────────────────────────── 辅助

static bool ShouldPackage(string relativePath)
{
    var fileName = Path.GetFileName(relativePath);

    // 调试符号、运行时描述文件对插件运行没有意义，只会让包变大
    if (fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) return false;
    if (fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)) return false;
    if (fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)) return false;

    // 契约程序集必须由宿主提供：插件目录里再带一份只会造成「同一个接口两个程序集」
    // 的版本混淆（宿主的 PluginLoadContext 也会刻意忽略插件目录里的这一份）。
    if (fileName.Equals("HelpDesk.Contracts.dll", StringComparison.OrdinalIgnoreCase)) return false;

    // 宿主已随包提供 Newtonsoft.Json，插件目录里再带一份会让内存里出现两份同版本程序集
    if (fileName.Equals("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase)) return false;

    return true;
}

static string Sha256Of(string path)
    => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

internal sealed class CommandLineOptions
{
    public string Source { get; init; } = string.Empty;
    public string Out { get; init; } = string.Empty;
    public string Layout { get; init; } = "root";
    public string Configuration { get; init; } = "Release";
    public string TargetFramework { get; init; } = "net8.0-windows";

    public static CommandLineOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "pack") return null;

        string? source = null;
        string? outDir = null;
        var layout = "root";
        var configuration = "Release";
        var targetFramework = "net8.0-windows";

        for (var i = 1; i < args.Length; i++)
        {
            // 每个开关后面都必须跟一个值，否则视为用法错误
            if (i + 1 >= args.Length) return null;

            switch (args[i])
            {
                case "--source": source = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                case "--layout": layout = args[++i]; break;
                case "--configuration": configuration = args[++i]; break;
                case "--tfm": targetFramework = args[++i]; break;
                default: return null;
            }
        }

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(outDir)) return null;
        if (layout is not ("root" or "plugins")) return null;

        return new CommandLineOptions
        {
            Source = source,
            Out = outDir,
            Layout = layout,
            Configuration = configuration,
            TargetFramework = targetFramework
        };
    }
}

/// <summary>用反射读取插件元数据，同时完成一次「这个 DLL 真的能加载」的冒烟测试。</summary>
internal static class PluginIntrospection
{
    /// <summary>
    /// 用 Newtonsoft 而不是 System.Text.Json：宿主读 metadata.json 用的就是 Newtonsoft，
    /// 两边必须遵守同一套 [JsonIgnore] 规则，否则会写出「写着 HasManifest/LayoutPrefix
    /// 这类宿主本不该看到的字段」的清单文件。Newtonsoft 还默认不转义中文，
    /// 生成的清单可以直接人工查看。
    /// </summary>
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore
    };

    /// <summary>只允许插件自己的程序集被加载；契约程序集一律用本工具这份。</summary>
    private sealed class ProbeLoadContext : AssemblyLoadContext
    {
        public ProbeLoadContext() : base("probe", isCollectible: true) { }
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }

    public static PluginMetadata ReadMetadata(string dllPath)
    {
        var context = new ProbeLoadContext();
        var assembly = context.LoadFromAssemblyPath(dllPath);

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // 个别类型加载失败时仍然继续：只要 IPlugin 实现是好的就算通过
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
        }

        var pluginType = types.FirstOrDefault(
            t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        if (pluginType == null)
            throw new InvalidOperationException($"在 {Path.GetFileName(dllPath)} 中找不到 IPlugin 实现");

        if (Activator.CreateInstance(pluginType) is not IPlugin plugin)
            throw new InvalidOperationException($"无法实例化 {pluginType.FullName}");

        var metadata = plugin.Metadata;
        plugin.Dispose();
        return metadata;
    }

    public static string ToMetadataJson(GitHubPluginInfo info)
        => JsonConvert.SerializeObject(info, JsonSettings);

    public static string ToIndexJson(PluginIndex index)
        => JsonConvert.SerializeObject(index, JsonSettings);
}
