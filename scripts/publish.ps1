#requires -version 5.1
<#
.SYNOPSIS
    KeyMouseCounter 发布脚本（开发文档 §8.3）。

.DESCRIPTION
    1) 跑单元测试（Release）；
    2) 以 self-contained + PublishSingleFile 方式发布到 dist\；
    3) 把自启动脚本与 README 一并复制到 dist\，方便整目录拷走即用。

    注意：ScottPlot 带原生依赖，必须 IncludeNativeLibrariesForSelfExtract=true，
    否则运行时报 DllNotFound；WinForms 不支持 Trim / NativeAOT，勿开。

.PARAMETER Clean
    发布前删除 obj/bin/dist，避免增量缓存干扰。

.PARAMETER SkipTests
    跳过 dotnet test（仅本地快速出包时用，正式发布不要跳）。

.PARAMETER Configuration
    构建配置，默认 Release。

.PARAMETER Output
    输出目录，默认 dist。

.EXAMPLE
    .\scripts\publish.ps1
    .\scripts\publish.ps1 -Clean -Output D:\out\kmc
#>
[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$SkipTests,
    [string]$Configuration = 'Release',
    [string]$Output = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "    $text" -ForegroundColor Green }

# ---- 定位带 SDK 的 dotnet（本机 C:\Program Files\dotnet 可能只有运行时）----
function Resolve-Dotnet {
    $candidates = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    $candidates += 'D:\dotnet8\dotnet.exe'

    foreach ($c in $candidates) {
        if (-not (Test-Path $c)) { continue }
        try {
            $sdks = & $c --list-sdks 2>$null
            if ($LASTEXITCODE -eq 0 -and $sdks) { return $c }
        } catch { }
    }
    throw '找不到带 .NET SDK 的 dotnet。请安装 .NET 8 SDK，或设置 DOTNET_ROOT 指向 SDK 目录。'
}

$dotnet = Resolve-Dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$sdk = (& $dotnet --version).Trim()
Write-Step "使用 dotnet SDK $sdk ($dotnet)"

$outDir = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }

if ($Clean) {
    Write-Step '清理 obj / bin / 输出目录'
    foreach ($p in @($outDir, 'src\KeyMouseCounter.App\obj', 'src\KeyMouseCounter.App\bin', 'src\KeyMouseCounter.Tests\obj', 'src\KeyMouseCounter.Tests\bin')) {
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    }
}

$sln = Join-Path $root 'KeyMouseCounter.sln'
Write-Step "还原 + 构建 $Configuration"
& $dotnet build $sln -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败（exit $LASTEXITCODE）" }

if (-not $SkipTests) {
    Write-Step '运行单元测试'
    & $dotnet test (Join-Path $root 'src\KeyMouseCounter.Tests\KeyMouseCounter.Tests.csproj') -c $Configuration --nologo --no-build
    if ($LASTEXITCODE -ne 0) { throw "dotnet test 失败（exit $LASTEXITCODE）——发布中止" }
} else {
    Write-Host '    （已按 -SkipTests 跳过测试）' -ForegroundColor Yellow
}

Write-Step '发布单文件 self-contained'
& $dotnet publish (Join-Path $root 'src\KeyMouseCounter.App\KeyMouseCounter.App.csproj') `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $outDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }

# ---- 把交付辅助文件一起放进 dist ----
Write-Step '复制自启动脚本与 README'
foreach ($name in @('install-autostart.ps1', 'uninstall-autostart.ps1')) {
    $src = Join-Path $PSScriptRoot $name
    if (Test-Path $src) { Copy-Item $src $outDir -Force }
}
foreach ($name in @('README.md')) {
    $src = Join-Path $root $name
    if (Test-Path $src) { Copy-Item $src $outDir -Force }
}

$exe = Join-Path $outDir 'KeyMouseCounter.exe'
if (-not (Test-Path $exe)) { throw "发布失败：找不到 $exe" }

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Ok "产物: $exe"
Write-Ok "大小: $sizeMb MB"
Write-Ok "SHA256: $hash"
Write-Ok "输出目录: $outDir"
Write-Host ''
Write-Host '下一步：' -ForegroundColor Cyan
Write-Host "  1) 运行  $exe"
Write-Host "  2) 注册开机自启  .\scripts\install-autostart.ps1   （或 $exe --register-autostart；需管理员权限）"
Write-Host "  3) 卸载          .\scripts\uninstall-autostart.ps1"
