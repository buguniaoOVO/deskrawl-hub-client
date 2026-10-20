param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot 'publish'),
    [string]$DesktopDirectory = [Environment]::GetFolderPath('Desktop')
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PublishDirectory '桌破中控.exe'
$target = Join-Path $DesktopDirectory '桌破中控.exe'
$localData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DeskrawlHubClient'

if (-not (Test-Path -LiteralPath $source)) {
    throw "发布文件不存在：$source"
}
$running = Get-CimInstance Win32_Process -Filter "Name='桌破中控.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -eq $target }
if ($running) {
    throw '桌破中控.exe 当前正在运行。关闭原窗口后再运行安装脚本。'
}

New-Item -ItemType Directory -Force -Path $localData | Out-Null
if ((Test-Path -LiteralPath $target) -and -not (Test-Path -LiteralPath (Join-Path $localData 'original-controller.exe.bak'))) {
    Copy-Item -LiteralPath $target -Destination (Join-Path $localData 'original-controller.exe.bak')
}
Copy-Item -LiteralPath $source -Destination $target -Force

foreach ($name in @('桌破中控服务端.exe', '桌破中控客户端.exe', '桌破中控客户端.exe.WebView2')) {
    $legacyPath = Join-Path $DesktopDirectory $name
    if (Test-Path -LiteralPath $legacyPath) {
        try {
            $item = Get-Item -LiteralPath $legacyPath -Force
            $item.Attributes = $item.Attributes -bor [IO.FileAttributes]::Hidden
        } catch {
            Write-Warning "无法隐藏旧文件：$legacyPath"
        }
    }
}

Write-Host "已安装单文件客户端：$target"
Write-Host '旧桌面文件已设为隐藏属性。'
