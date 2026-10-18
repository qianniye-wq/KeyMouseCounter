#requires -version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    注册 KeyMouseCounter 开机自启的计划任务（开发文档 §8.2）。
    注意：创建计划任务需要管理员权限，本脚本要求以管理员身份运行。

.DESCRIPTION
    用计划任务而不是注册表 Run 键：可设失败重启、无 UAC 弹窗、可指定运行级别。
    任务名固定 KeyMouseCounter；重复执行是幂等的（-Force 覆盖）。

    需要覆盖提权程序时用 -RunLevel Highest（普通使用保持 Limited 即可，最小权限原则）。

.PARAMETER ExePath
    KeyMouseCounter.exe 路径。默认依次找：
      dist\KeyMouseCounter.exe（相对本脚本上级目录）→ 脚本所在目录 → 当前目录。

.PARAMETER RunLevel
    Limited（默认，最小权限）或 Highest。

.PARAMETER Uninstall
    只注销任务，不注册。

.EXAMPLE
    .\install-autostart.ps1
    .\install-autostart.ps1 -ExePath 'D:\Apps\KeyMouseCounter\KeyMouseCounter.exe' -RunLevel Highest
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [ValidateSet('Limited', 'Highest')]
    [string]$RunLevel = 'Limited',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$TaskName = 'KeyMouseCounter'

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

# ---- 注销分支 ----
if ($Uninstall) {
    Write-Step "注销计划任务 $TaskName"
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host '    已注销。' -ForegroundColor Green
    } else {
        Write-Host '    任务不存在，无需注销。' -ForegroundColor Yellow
    }
    return
}

# ---- 定位 exe ----
function Resolve-Exe {
    param([string]$explicit)
    if ($explicit) {
        if (-not (Test-Path $explicit)) { throw "指定的 exe 不存在：$explicit" }
        return (Resolve-Path $explicit).Path
    }
    $candidates = @(
        (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\KeyMouseCounter.exe'),
        (Join-Path $PSScriptRoot 'KeyMouseCounter.exe'),
        (Join-Path (Get-Location).Path 'KeyMouseCounter.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return (Resolve-Path $c).Path }
    }
    throw '找不到 KeyMouseCounter.exe，请用 -ExePath 指定，或先运行 scripts\publish.ps1。'
}

$exe = Resolve-Exe -explicit $ExePath
Write-Step "注册开机自启：$exe"

# ---- 身份：优先 USERDOMAIN\USERNAME，其次 whoami ----
$userId = if ($env:USERDOMAIN -and $env:USERNAME) { "$env:USERDOMAIN\$env:USERNAME" } else { (whoami).Trim() }

$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path -Parent $exe)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable
$principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel $RunLevel

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Settings $settings -Principal $principal -Force | Out-Null

# ---- 回读校验 ----
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $task) { throw '计划任务注册后回读失败，请用 taskschd.msc 手工检查。' }

$run = (Get-ScheduledTaskInfo -TaskName $TaskName).LastTaskResult
Write-Host '    注册成功。' -ForegroundColor Green
Write-Host "    任务名   : $TaskName"
Write-Host "    触发     : 登录时（$userId）"
Write-Host "    运行级别 : $RunLevel"
Write-Host "    状态     : $($task.State)  上次结果: $run"
Write-Host ''
Write-Host '提示：可在「设置 → 常规 → 开机自启」里开关，效果与此脚本一致。' -ForegroundColor Cyan

