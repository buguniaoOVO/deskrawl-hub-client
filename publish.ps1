$ErrorActionPreference = 'Stop'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "未找到 .NET SDK：$dotnet"
}
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Assets\engine.payload.gz'))) {
    throw '客户端运行模块资源缺失。'
}
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Assets\Deskrawl.ico'))) {
    throw '客户端图标缺失，请先运行 generate-icon.py。'
}

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
Move-Item -LiteralPath $builtClient -Destination $finalClient -Force
Get-ChildItem -LiteralPath $publishDirectory -Filter '*.xml' -File | Remove-Item -Force
Write-Host "独立客户端已生成：$finalClient"
