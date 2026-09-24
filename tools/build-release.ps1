<#
.SYNOPSIS
    构建 HelpDesk 的完整发布产物：自包含发布 + 插件包（带校验清单）+ Inno Setup 安装包。

.DESCRIPTION
    本仓库（以及后续新建的 Windows 桌面项目）统一用 **Inno Setup 6** 打安装包。
    编译器默认路径：F:\YA\Inno Setup 6\ISCC.exe
    换机器时可以用 -InnoSetupPath 指定，脚本也会自动去常见安装位置找一遍。

    产物：
      artifacts\publish\                    自包含发布（用户无需另装 .NET 运行时）
      dist\HelpDesk-Setup-<版本>.exe        安装包（注册到「应用和功能」，带卸载程序）
      plugins-release\                      插件分发内容（可直接提交到插件仓库）

.EXAMPLE
    .\tools\build-release.ps1
    .\tools\build-release.ps1 -Version 1.2.0 -SkipPluginPublish
#>
[CmdletBinding()]
param(
    # 版本号；不填则从 src\HelpDesk.Host\HelpDesk.Host.csproj 的 <Version> 读取
    [string]$Version,

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    # Inno Setup 6 的安装目录（本机已安装于此；这是本项目的默认打包方式）
    [string]$InnoSetupPath = 'F:\YA\Inno Setup 6',

    # 跳过插件打包（只想出主程序安装包时用）
    [switch]$SkipPluginPublish,

    # 只做发布与插件包，不编译安装包
    [switch]$SkipInstaller,

    # 关闭 ReadyToRun 预编译（输出体积更小，但冷启动稍慢）
    [switch]$NoReadyToRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host "── $Text " -ForegroundColor Cyan -NoNewline
    Write-Host ('─' * [Math]::Max(1, 60 - $Text.Length)) -ForegroundColor DarkCyan
}

function Fail([string]$Message) {
    Write-Host "错误：$Message" -ForegroundColor Red
    exit 1
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    # ── 版本号 ──────────────────────────────────────────────────────────
    if (-not $Version) {
        $hostProject = Join-Path $repoRoot 'src\HelpDesk.Host\HelpDesk.Host.csproj'
        $match = Select-String -Path $hostProject -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
        if (-not $match) { Fail "无法从 $hostProject 读取 <Version>，请用 -Version 显式指定" }
        $Version = $match.Matches[0].Groups[1].Value.Trim()
    }
    Write-Host "HelpDesk 发布构建 · 版本 $Version · $Configuration · $Runtime" -ForegroundColor White

    # ── 1. 构建解决方案（插件也要构建，后面打包要用它的产物）────────────
    Write-Step '1/4 构建解决方案'
    & dotnet build 'HelpDesk.slnx' -c $Configuration -v q --nologo
    if ($LASTEXITCODE -ne 0) { Fail '解决方案构建失败' }

    # ── 2. 自包含发布 ───────────────────────────────────────────────────
    Write-Step '2/4 自包含发布主程序'
    $publishDir = Join-Path $repoRoot 'artifacts\publish'
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    $publishArgs = @(
        'publish', 'src\HelpDesk.Host\HelpDesk.Host.csproj'
        '-c', $Configuration
        '-r', $Runtime
        '--self-contained', 'true'
        '-o', $publishDir
        '-v', 'q', '--nologo'
    )
    # ReadyToRun 预编译能明显改善冷启动；文件夹发布（非单文件）下没有退出崩溃问题
    if (-not $NoReadyToRun) { $publishArgs += '-p:PublishReadyToRun=true' }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { Fail '发布失败' }

    $exePath = Join-Path $publishDir 'HelpDesk.exe'
    if (-not (Test-Path $exePath)) { Fail "发布产物里没有 HelpDesk.exe：$publishDir" }
    $publishSize = (Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum

    # ── 3. 插件打包 ─────────────────────────────────────────────────────
    if (-not $SkipPluginPublish) {
        Write-Step '3/4 打包插件（生成 SHA-256 清单与仓库索引）'
        $packager = Join-Path $repoRoot "tools\PluginPackager\bin\$Configuration\net8.0-windows\PluginPackager.dll"
        if (-not (Test-Path $packager)) { Fail "找不到插件打包器：$packager" }

        $releaseRepo = Join-Path $repoRoot 'plugins-release'
        & dotnet $packager pack `
            --source (Join-Path $repoRoot 'src\HelpDesk.Plugins') `
            --out $releaseRepo `
            --layout root `
            --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { Fail '插件打包失败' }
    }
    else {
        Write-Step '3/4 跳过插件打包'
    }

    # ── 4. 安装包 ───────────────────────────────────────────────────────
    $installerPath = $null
    if (-not $SkipInstaller) {
        Write-Step '4/4 编译 Inno Setup 安装包'

        $iscc = Join-Path $InnoSetupPath 'ISCC.exe'
        if (-not (Test-Path $iscc)) {
            # 换机器时的兜底查找：常见安装位置 + 注册表
            $candidates = @(
                (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
                (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
            ) | Where-Object { $_ -and (Test-Path $_) }

            if (-not $candidates) {
                foreach ($root in @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                                    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1')) {
                    try {
                        $install = (Get-ItemProperty -Path $root -ErrorAction Stop).InstallLocation
                        if ($install) {
                            $candidate = Join-Path $install 'ISCC.exe'
                            if (Test-Path $candidate) { $candidates = @($candidate); break }
                        }
                    }
                    catch { }
                }
            }

            if (-not $candidates) {
                Fail "找不到 Inno Setup 6 的 ISCC.exe。请安装 Inno Setup 6，或用 -InnoSetupPath 指定目录（默认期望：$InnoSetupPath）"
            }
            $iscc = $candidates[0]
            Write-Host "（已在 $iscc 找到 ISCC.exe）" -ForegroundColor DarkGray
        }

        $distDir = Join-Path $repoRoot 'dist'
        New-Item -ItemType Directory -Force -Path $distDir | Out-Null

        & $iscc "/DAppVersion=$Version" (Join-Path $repoRoot 'installer\HelpDesk.iss')
        if ($LASTEXITCODE -ne 0) { Fail '安装包编译失败' }

        $installerPath = Join-Path $distDir "HelpDesk-Setup-$Version.exe"
        if (-not (Test-Path $installerPath)) { Fail "安装包没有生成：$installerPath" }
    }
    else {
        Write-Step '4/4 跳过安装包'
    }

    # ── 汇总 ────────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host '发布完成：' -ForegroundColor Green
    Write-Host ("  自包含发布  {0}  ({1:N1} MB)" -f $publishDir, ($publishSize / 1MB))
    if (-not $SkipPluginPublish) {
        Write-Host ("  插件分发    {0}" -f (Join-Path $repoRoot 'plugins-release'))
    }
    if ($installerPath) {
        Write-Host ("  安装包      {0}  ({1:N1} MB)" -f $installerPath, ((Get-Item $installerPath).Length / 1MB)) -ForegroundColor White
    }
}
finally {
    Pop-Location
}
