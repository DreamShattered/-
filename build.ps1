# 多动症矫正器 — 构建与发布脚本
#
#   .\build.ps1            发布 Release：自包含单文件 exe -> publish\
#   .\build.ps1 -Debug     只构建 Debug（框架依赖，仅供本机开发调试）

param([switch]$Debug)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\FocusFreeze.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '未找到 dotnet 命令。请先安装 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0'
}
if (-not (Test-Path $proj)) {
    throw "找不到工程文件：$proj"
}

if ($Debug) {
    Write-Host '构建 Debug（框架依赖，需要本机已装 .NET 8 Desktop Runtime）...' -ForegroundColor Cyan
    dotnet build $proj -c Debug -nologo
    if ($LASTEXITCODE -ne 0) { throw "构建失败，退出码 $LASTEXITCODE" }
    Write-Host ''
    Write-Host ('输出：' + (Join-Path $root 'src\bin\Debug\net8.0-windows\多动症矫正器.exe')) -ForegroundColor Green
    return
}

$out = Join-Path $root 'publish'
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 只清掉旧的主程序，保留同目录的使用说明、config.json 等文件
Get-ChildItem $out -Filter '*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host '发布 Release（自包含单文件）...' -ForegroundColor Cyan
dotnet publish $proj -c Release -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码 $LASTEXITCODE" }

$exe = Get-ChildItem $out -Filter '*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $exe) { throw '发布后没有找到 exe，请检查上面的输出。' }

$mb = [math]::Round($exe.Length / 1MB, 1)
Write-Host ''
Write-Host ('完成：' + $exe.FullName) -ForegroundColor Green
Write-Host "大小：$mb MB —— 自包含单文件，单独拷这一个文件到任何 64 位 Windows 上双击即可运行，目标机器无需安装 .NET 运行时。" -ForegroundColor Green
