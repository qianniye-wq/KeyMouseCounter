#requires -version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    注销 KeyMouseCounter 开机自启计划任务（开发文档 §8.2 / §8.4）。
    注意：注销计划任务需要管理员权限，本脚本要求以管理员身份运行。

.DESCRIPTION
    等价于 scripts\install-autostart.ps1 -Uninstall，单独保留是为了让卸载步骤直观。
    幂等：任务不存在时只提示，不报错。

.PARAMETER PurgeData
    同时删除用户数据 %LOCALAPPDATA%\KeyMouseCounter（配置、数据库、日志、导出）。
    默认不删，避免误删统计历史。也可用 KeyMouseCounter.exe --purge。

.EXAMPLE
    .\uninstall-autostart.ps1
    .\uninstall-autostart.ps1 -PurgeData
#>
[CmdletBinding()]
param(
    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'
$TaskName = 'KeyMouseCounter'

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

Write-Step "注销计划任务 $TaskName"
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host '    已注销。' -ForegroundColor Green
} else {
    Write-Host '    任务不存在，无需注销。' -ForegroundColor Yellow
}

if ($PurgeData) {
    $root = Join-Path $env:LOCALAPPDATA 'KeyMouseCounter'

    # 安全校验：只允许删这个固定名字的目录
    if ((Split-Path -Leaf $root) -ne 'KeyMouseCounter') {
        throw "拒绝删除非常规路径：$root"
    }

    if (Test-Path $root) {
        Write-Step "删除用户数据 $root"
        Get-ChildItem $root -Force | ForEach-Object { Write-Host "    删除 $($_.Name)" }
        Remove-Item $root -Recurse -Force
        Write-Host '    已删除。' -ForegroundColor Green
    } else {
        Write-Host "    数据目录不存在：$root" -ForegroundColor Yellow
    }
} else {
    Write-Host ''
    Write-Host '统计历史仍保留在 %LOCALAPPDATA%\KeyMouseCounter\。' -ForegroundColor Cyan
    Write-Host '如需一并删除，重跑本脚本加 -PurgeData，或执行 KeyMouseCounter.exe --purge。'
}

