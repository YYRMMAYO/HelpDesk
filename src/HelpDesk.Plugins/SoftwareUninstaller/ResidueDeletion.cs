using HelpDesk.Contracts;

namespace HelpDesk.Plugins.SoftwareUninstaller;

/// <summary>
/// 残留删除的「执行侧」：生成 PowerShell 脚本、组装待删清单、解析执行结果。
/// <para>
/// 从界面代码里拆出来的原因有两个：一是这块逻辑是安全相关的，值得单独测试；
/// 二是它不依赖任何 UI 类型，可以脱离 WPF 直接跑。
/// </para>
/// <para>
/// <b>为什么不把路径拼进脚本</b>：路径来自注册表，属于外部数据。这里统一走
/// 「写入临时清单文件 → 脚本用 Get-Content 逐行读」，脚本正文因此在编译期就固定下来了，
/// 无论路径里出现引号、反引号、分号还是其他 PowerShell 元字符，都不可能改变脚本行为。
/// 脚本正文里唯一被插值的只有清单文件本身的路径，而那是我们生成的临时路径。
/// </para>
/// </summary>
public static class ResidueDeletion
{
    private const string ResultPrefix = "HD_RESULT";
    private const string ErrorPrefix = "HD_ERROR";
    private const string RegistryMarker = "REG|";

    /// <summary>把待删目录/文件与注册表键组装成清单文件的内容（一行一项）。</summary>
    public static List<string> BuildListLines(
        IEnumerable<string> filePaths,
        IEnumerable<string> registryKeyPaths)
    {
        var lines = new List<string>();

        foreach (var path in filePaths)
            if (!string.IsNullOrWhiteSpace(path))
                lines.Add(path.Trim());

        foreach (var key in registryKeyPaths)
        {
            var powerShellPath = UninstallSafety.ToPowerShellRegistryPath(key);
            if (powerShellPath != null)
                lines.Add(RegistryMarker + powerShellPath);
        }

        return lines;
    }

    /// <summary>
    /// 生成删除脚本。逐项独立 try/catch：一项失败不影响其余项，并把失败原因回传，
    /// 这样界面上能告诉用户「哪一项没删掉、为什么」。
    /// </summary>
    public static string BuildScript(string listFile)
    {
        var quotedListFile = PowerShellText.Literal(listFile);

        return string.Join("\r\n",
        [
            "$ErrorActionPreference = 'Continue'",
            "$__listFile = " + quotedListFile,
            "$__ok = 0",
            "$__fail = 0",
            "$__total = 0",
            "$__messages = New-Object System.Collections.ArrayList",
            "$__lines = @(Get-Content -LiteralPath $__listFile -Encoding UTF8)",
            "foreach ($__raw in $__lines) {",
            "    $__item = ([string]$__raw).Trim()",
            "    if ($__item.Length -eq 0) { continue }",
            "    $__total++",
            "    try {",
            "        if ($__item.StartsWith('" + RegistryMarker + "')) {",
            "            $__key = $__item.Substring(" + RegistryMarker.Length + ")",
            "            if (Test-Path -LiteralPath $__key) { Remove-Item -LiteralPath $__key -Recurse -Force }",
            "            $__ok++",
            "        }",
            "        elseif (Test-Path -LiteralPath $__item) {",
            "            Remove-Item -LiteralPath $__item -Recurse -Force",
            "            $__ok++",
            "        }",
            "        else {",
            "            $__ok++",
            "        }",
            "    }",
            "    catch {",
            "        $__fail++",
            "        [void]$__messages.Add($__item + ' :: ' + $_.Exception.Message)",
            "    }",
            "}",
            "Write-Output ('" + ResultPrefix + " ok=' + $__ok + ' fail=' + $__fail + ' total=' + $__total)",
            "foreach ($__m in $__messages) { Write-Output ('" + ErrorPrefix + " ' + $__m) }"
        ]);
    }

    /// <summary>解析脚本输出，取出成功数、失败数与失败明细。</summary>
    public static (int Ok, int Fail, List<string> Errors) ParseOutput(string? output)
    {
        var ok = 0;
        var fail = 0;
        var errors = new List<string>();
        if (string.IsNullOrEmpty(output)) return (ok, fail, errors);

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.StartsWith(ResultPrefix, StringComparison.Ordinal))
            {
                foreach (var part in line[ResultPrefix.Length..]
                             .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = part.Split('=', 2);
                    if (pair.Length != 2) continue;
                    if (pair[0] == "ok" && int.TryParse(pair[1], out var okValue)) ok = okValue;
                    else if (pair[0] == "fail" && int.TryParse(pair[1], out var failValue)) fail = failValue;
                }
            }
            else if (line.StartsWith(ErrorPrefix, StringComparison.Ordinal))
            {
                errors.Add(line[ErrorPrefix.Length..].Trim());
            }
        }

        return (ok, fail, errors);
    }
}
