# HelpDesk 电脑助手

面向**不太懂电脑**的 Windows 用户的插件式电脑助手。主程序只保留最必要的一套外壳，具体功能（网速检测、软件卸载、系统诊断……）做成插件，用户需要哪个装哪个，用不上的一律不占地方。

- 主程序：WPF / .NET 8，自包含发布（用户不需要另外安装 .NET 运行时）
- 插件：独立的 .NET 程序集，从 GitHub 仓库在线发现、下载、校验、加载
- 安装包：Inno Setup 6（装到用户目录，全程不弹 UAC）

---

## 当前内置功能

| 插件 | 说明 |
| --- | --- |
| **网络工具** (`network-toolkit`) | 网速检测（下行 / 上行 / 延迟 / 抖动，多节点自动选择）、网络适配器信息、DNS 解析、Ping 连通性 |
| **软件卸载** (`software-uninstaller`) | 按注册表登记的官方卸载程序卸载软件；卸载后按注册表记录的安装目录清理残留 |
| **系统诊断** (`system-diagnostics`) | CPU / 内存 / 磁盘实时状态与健康评分 |
| **系统清理** (`system-cleaner`) | 扫描并清理当前账户的临时文件 |
| **故障排除指南** (`troubleshoot-guide`) | 15 类常见电脑故障的排查步骤 |

---

## 项目结构

```
HelpDesk.slnx
src/
  HelpDesk.Contracts/         插件契约：IPlugin / IPluginContext / PluginManager
  HelpDesk.Host/              主程序（WPF 外壳、插件市场、设置）
  HelpDesk.Plugins/           各功能插件（每个插件一个项目）
tools/
  PluginPackager/             插件打包器：生成 metadata.json（含 SHA-256）与 plugins-index.json
  build-release.ps1           一键发布：自包含发布 + 插件打包 + 编译安装包
installer/
  HelpDesk.iss                Inno Setup 6 安装脚本
plugins-release/              插件分发仓库（独立 git 仓库，内容由 PluginPackager 生成）
```

---

## 构建与打包

### 需要什么

- .NET 8 SDK（或更高版本的 SDK，能编译 `net8.0-windows` 即可）
- Windows x64
- **Inno Setup 6**，本项目的默认路径：`F:\YA\Inno Setup 6\ISCC.exe`

### 一条命令出安装包

```powershell
.\tools\build-release.ps1
```

它会依次完成：

1. 构建解决方案；
2. 自包含发布主程序到 `artifacts\publish\`；
3. 打包插件到 `plugins-release\`（重新计算每个文件的 SHA-256，并刷新仓库根目录的 `plugins-index.json`）；
4. 用 Inno Setup 编译出 `dist\HelpDesk-Setup-<版本>.exe`。

常用参数：

```powershell
.\tools\build-release.ps1 -Version 1.2.0      # 覆盖版本号（默认读 csproj 的 <Version>）
.\tools\build-release.ps1 -InnoSetupPath 'D:\Inno Setup 6'
.\tools\build-release.ps1 -SkipPluginPublish -SkipInstaller
```

> **打包方式约定**：本仓库（以及后续新建的 Windows 桌面项目）统一用 **Inno Setup 6** 出安装包，
> 编译器默认路径 `F:\YA\Inno Setup 6\ISCC.exe`。脚本在这个路径找不到时会去
> `%ProgramFiles(x86)%\Inno Setup 6`、`%ProgramFiles%\Inno Setup 6` 以及注册表里再找一遍，
> 仍然找不到才会报错。

### ⚠️ 写 PowerShell 脚本时的一个真实坑

本仓库的 `.ps1` 文件必须保存为 **UTF-8 with BOM**。

Windows PowerShell 5.1 在文件没有 BOM 时按系统 ANSI 代码页读取脚本，中文的 UTF-8 字节会被
拆错，而 GBK 的双字节「前导字节」会把紧跟其后的那个字符**一起吃掉**——结果是字符串的收尾
引号被吞掉，脚本报出「Try 语句缺少自己的 Catch 或 Finally 块」这类莫名其妙的语法错误。
同样的问题也适用于 `installer\HelpDesk.iss`（中文文案）。

补 BOM 的方法：

```powershell
$f = 'tools\build-release.ps1'
[IO.File]::WriteAllText($f, [IO.File]::ReadAllText($f, [Text.Encoding]::UTF8), (New-Object Text.UTF8Encoding($true)))
```

（主程序在提权执行 PowerShell 时也是同样的处理：脚本一律带 BOM 写入，见 `ProcessRunner.cs`。）

---

## 插件机制

### 契约

插件实现 `HelpDesk.Contracts.IPlugin`：

```csharp
public interface IPlugin : IDisposable
{
    PluginMetadata Metadata { get; }          // Id / Name / Version / Tags / MinHostVersion …
    void Init(IPluginContext context);
    UserControl GetView();                    // 插件的界面
    void OnActivated();
    void OnDeactivated();
}
```

`IPluginContext` 是插件能拿到的全部外部能力：数据目录、宿主版本、是否已提权、状态栏提示，
以及两个受控的执行入口（`RunPowerShellAsync` / `StartProcessAsync`）。

**安全设计**：所有需要外部执行的能力都收敛到宿主里唯一的实现（`HelpDesk.Host/ProcessRunner.cs`），
宿主负责提权与参数传递。插件无法自己拼接命令行，也就没有把命令行注入面扩散到每个插件里。
`PowerShellText.Literal()` 是脚本里嵌入第三方字符串（路径、注册表值）的唯一转义入口。

### 新增一个插件

1. 在 `src/HelpDesk.Plugins/` 下建一个类库项目，`TargetFramework` 用 `net8.0-windows`、开 `UseWPF`，
   引用 `HelpDesk.Contracts`；
2. 实现 `IPlugin`（元数据里的 `GitHubFolder` 决定它在分发仓库里的目录名，
   `MinHostVersion` 声明所需的最低宿主版本）；
3. 加进 `HelpDesk.slnx`；
4. 跑 `.\tools\build-release.ps1`——`PluginPackager` 会自动把它编译产物整理成插件包、
   算好 SHA-256 清单，并更新索引。

### 分发仓库

`plugins-release/` 是一个独立的 git 仓库（远端 `YYRMMAYO/HelpDesk-Plugins`），结构：

```
plugins-index.json            索引：宿主一次请求拿到全部插件（含校验清单）
<Folder>/metadata.json        单个插件的元数据 + 文件 SHA-256 清单
<Folder>/HelpDesk.Plugins.<X>.dll
```

宿主发现插件的顺序是「先读 `plugins-index.json`，失败再遍历仓库文件树」；下载文件时按
「`raw.githubusercontent.com` → `cdn.jsdelivr.net`」依次尝试（国内网络下 raw 域名经常不可达），
每个文件都用清单里的 SHA-256 校验，任何一个不匹配就整体放弃安装。

---

## 安全模型

### 提权

主程序以 **`asInvoker`** 运行（见 `src/HelpDesk.Host/app.manifest`），日常看网速、看系统信息
完全不需要管理员权限。只有确实需要写 HKLM 或删 Program Files 的操作（卸载软件、清理残留），
才由 `ProcessRunner` **针对那一个子进程**临时弹出 UAC 请求提权——而不是让整个程序常驻管理员权限。

提权执行的技术要点（都在 `ProcessRunner.cs` 的注释里）：

- 脚本写入宿主生成的临时 `.ps1`，用 `-File` 方式执行，**命令行上没有第三方数据**；
- 结果与退出码写进宿主指定的临时文件再读回（`ShellExecute` 启动的进程拿不到输出管道，
  也不能读 `ExitCode`）；
- 用户取消 UAC 会返回 `ElevationDenied`，不会被误判成「执行失败」。

### 软件卸载的安全边界

这是「不误删类同名文件」的具体做法（实现见 `src/HelpDesk.Plugins/SoftwareUninstaller/UninstallSafety.cs`）：

1. **只信注册表，绝不按名字猜。** 卸载走该软件自己登记的 `UninstallString` / `QuietUninstallString`；
   残留清理的候选**只有两个来源**：它自己记录的 `InstallLocation`，以及它自己的卸载注册表键。
   不做任何「目录名包含软件名」的模糊匹配——那正是误删同类文件的根源。
2. **卸载命令必须先过白名单。** 拒绝 `cmd` / `powershell` / `wscript` / `rundll32` / `regsvr32` /
   `certutil` / `curl` 这类解释器与下载器，拒绝含 `http(s)://`、UNC 与设备路径的命令，
   并且要求解析出的可执行文件**真实存在**。被拦下时把命令原文交给用户，由用户自己判断。
3. **删除前把路径验到底。** 必须是绝对路径、盘符必须真实存在、不能是 UNC、不能是磁盘根目录、
   不能是系统/用户关键目录（`C:\Windows`、`C:\Program Files`、`C:\Users`、`AppData`、桌面文档……）、
   不能位于 `C:\Windows` 内部、不能包含 HelpDesk 自己、**不能包含其他已安装软件的安装目录**、
   不能是符号链接或交叉点。
4. **路径不拼进脚本。** 待删清单通过临时文件传给 PowerShell，脚本正文在编译期就固定，
   路径里出现引号、反引号、`$()` 都不可能改变脚本行为。
5. 删除后仍可清理失败的项会计入结果并告诉用户哪一项没删掉、为什么（通常是文件正被占用）。

### 插件完整性

- 插件仓库提供 `plugins-index.json` 与每个插件的 `metadata.json`，其中列出**每个文件的 SHA-256**；
  宿主安装时逐个校验，不匹配就整体放弃，不会留下半装状态。
- 未提供清单的插件仍可安装，但界面上会明确标注「未提供校验清单，无法校验完整性」，
  「已安装」列表里也会把「已校验 / 未提供校验清单」显示出来。
- **已知局限**：校验清单与文件来自同一个源，因此只能防「仓库文件被替换 / 下载被截断」，
  不能防「镜像站本身被攻陷」。要覆盖后者需要独立的签名清单（带内置公钥验签），目前尚未实现。

---

## 数据与日志位置

| 内容 | 路径 |
| --- | --- |
| 设置（插件源） | `%LOCALAPPDATA%\HelpDesk\settings.json` |
| 已安装插件清单 | `%LOCALAPPDATA%\HelpDesk\installed_plugins.json` |
| 待清理的插件目录 | `%LOCALAPPDATA%\HelpDesk\pending_deletions.json` |
| 插件本体 | `%LOCALAPPDATA%\HelpDesk\Plugins\` |
| 日志 | `%LOCALAPPDATA%\HelpDesk\logs\helpdesk-<日期>.log` |

设置页里有「打开日志文件夹」「打开插件文件夹」两个按钮，出问题时把日志发出来即可。

---

## 已知限制

- 插件下载依赖 `api.github.com` / `raw.githubusercontent.com` / `cdn.jsdelivr.net`，
  网络受限时可能拿不到插件列表（失败原因会显示在界面上，可以在「设置」里换插件源）。
- **插件刚发布后的 1~2 分钟内，下载源的 CDN 缓存可能还是旧内容**，此时安装会因为
  「文件与校验清单对不上」而中止（这是校验在正常工作）。宿主会自动重试 3 次并给出提示，
  过一会儿再试即可。
- 测速节点是公开的免费端点（Cloudflare、各高校/云厂商镜像），结果仅供参考；
  界面提供「自定义测速地址」以便在网络受限时使用自建节点。
- 卸载残留只处理注册表里记录的安装目录。注册表没记录安装位置的软件（绿色版）不会被推测清理。
- 目录树中含有符号链接或交叉点的残留会被拒绝清理（Windows PowerShell 的
  `Remove-Item -Recurse` 在遇到链接时可能删到链接指向的真实位置，代价太大，宁可不清）。
- 校验清单与文件来自同一处，能防「仓库文件被替换 / 下载被截断」，
  但不能防「镜像站本身被攻陷」；覆盖后者需要独立的签名清单（带内置公钥验签），尚未实现。
- 卸载程序不会删除用户数据（`%LOCALAPPDATA%\HelpDesk`），重装后设置与已装插件仍在。
