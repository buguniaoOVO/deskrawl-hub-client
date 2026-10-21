$ErrorActionPreference = 'Stop'
$dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if (-not $dotnetCommand) {
    throw '未找到 dotnet.exe，请安装 .NET 8 SDK 并确认它位于 PATH 中。'
}
$dotnet = $dotnetCommand.Source
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Assets\engine.payload.gz'))) {
    throw '客户端运行模块资源缺失。'
}
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Assets\Deskrawl.ico'))) {
    throw '客户端图标缺失，请先运行 generate-icon.py。'
}

$compiler = Get-Command gcc.exe -ErrorAction SilentlyContinue
if (-not $compiler) {
    throw '未找到 MinGW x64 gcc.exe，无法构建游戏即时限帧组件。'
}
$pluginSource = Join-Path $PSScriptRoot 'FrameCapLivePlugin\FrameCapLivePlugin.c'
$pluginOutputDirectory = Join-Path $PSScriptRoot 'FrameCapLivePlugin\publish'
$pluginDll = Join-Path $pluginOutputDirectory 'DeskrawlConsole.FrameCap.Live.dll'
New-Item -ItemType Directory -Path $pluginOutputDirectory -Force | Out-Null
& $compiler.Source -shared -O2 -static-libgcc -finput-charset=UTF-8 -fexec-charset=UTF-8 '-Wl,--kill-at' `
    -o $pluginDll $pluginSource -luser32 -lkernel32
if ($LASTEXITCODE -ne 0) {
    throw "游戏即时限帧组件构建失败，gcc exit code: $LASTEXITCODE"
}
if (-not (Test-Path -LiteralPath $pluginDll)) {
    throw "游戏即时限帧组件未生成：$pluginDll"
}
Copy-Item -LiteralPath $pluginDll -Destination (Join-Path $PSScriptRoot 'Assets\DeskrawlConsole.FrameCap.Live.dll') -Force

$publishDirectory = Join-Path $PSScriptRoot 'publish'
& $dotnet publish (Join-Path $PSScriptRoot 'DeskrawlHubClient.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "客户端发布失败，dotnet exit code: $LASTEXITCODE"
}

$builtClient = Join-Path $publishDirectory 'DeskrawlHubClient.exe'
$finalClient = Join-Path $publishDirectory '桌破中控.exe'
$installPathFile = Join-Path $PSScriptRoot 'install-path.local.txt'
$installedClient = ''
if (Test-Path -LiteralPath $installPathFile -PathType Leaf) {
    $installPathValue = Get-Content -LiteralPath $installPathFile -Raw
    if ($null -ne $installPathValue) { $installedClient = $installPathValue.Trim() }
}
$runningClient = Get-CimInstance Win32_Process -Filter "Name='桌破中控.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -eq $finalClient -or ($installedClient -and $_.ExecutablePath -eq $installedClient) }
if ($runningClient | Where-Object { $_.ExecutablePath -eq $finalClient }) {
    $finalClient = Join-Path $publishDirectory '桌破中控-待更新.exe'
}
if (Test-Path -LiteralPath $finalClient) {
    Remove-Item -LiteralPath $finalClient -Force
}
Move-Item -LiteralPath $builtClient -Destination $finalClient
Get-ChildItem -LiteralPath $publishDirectory -Filter '*.xml' -File | Remove-Item -Force
Write-Host "独立客户端已生成：$finalClient"

if (-not [string]::IsNullOrWhiteSpace($installedClient)) {
    if (-not [System.IO.Path]::IsPathFullyQualified($installedClient)) {
        throw 'install-path.local.txt 必须填写绝对 exe 路径。'
    }
    if (-not (Test-Path -LiteralPath (Split-Path -Parent $installedClient) -PathType Container)) {
        throw "默认安装目录不存在：$(Split-Path -Parent $installedClient)"
    }
    $installedClientProcess = $runningClient | Where-Object { $_.ExecutablePath -eq $installedClient }
    if ($installedClientProcess) {
        throw "桌破中控正在运行（PID $($installedClientProcess.ProcessId)），请先关闭客户端，再重新运行发布脚本以覆盖：$installedClient"
    }
    Copy-Item -LiteralPath $finalClient -Destination $installedClient -Force
    Write-Host "已覆盖本地配置的客户端：$installedClient"
} else {
    Write-Host '未配置本地安装路径；仅生成发布文件。需要自动覆盖时，在 install-path.local.txt 中填写目标 exe 绝对路径。'
}
