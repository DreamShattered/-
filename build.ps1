# Build / publish entry point.
#
#   build.ps1           publish a self-contained single-file exe into publish\
#   build.ps1 -Debug    debug build only (framework-dependent, fast)
#
# NOTE: this file is intentionally pure ASCII. Windows PowerShell 5.1 reads
# BOM-less UTF-8 as ANSI, which turns non-ASCII source into a parse error.
# Keep it ASCII so the script survives any editor.

param([switch]$Debug)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\FocusFreeze.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet not found. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0'
}
if (-not (Test-Path $proj)) {
    throw "Project file not found: $proj"
}

if ($Debug) {
    Write-Host 'Building Debug (framework-dependent, needs .NET 8 Desktop Runtime)...' -ForegroundColor Cyan
    dotnet build $proj -c Debug -nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
    $dbgExe = Get-ChildItem (Join-Path $root 'src\bin\Debug\net8.0-windows') -Filter '*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    Write-Host ''
    if ($dbgExe) { Write-Host ('Output: ' + $dbgExe.FullName) -ForegroundColor Green }
    return
}

$out = Join-Path $root 'publish'
New-Item -ItemType Directory -Force -Path $out | Out-Null

# Bail out with a readable message if the published exe is locked (program running).
$existing = Get-ChildItem $out -Filter '*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($existing) {
    $locked = $false
    try { $fs = [System.IO.File]::Open($existing.FullName, 'Open', 'ReadWrite', 'None'); $fs.Close() }
    catch { $locked = $true }
    if ($locked) {
        Write-Host ('Publish aborted: ' + $existing.FullName) -ForegroundColor Yellow
        Write-Host 'That file is locked because the program is running. Close it and publish again.' -ForegroundColor Yellow
        Write-Host 'The exe was NOT updated; nothing else was touched.' -ForegroundColor Yellow
        exit 1
    }
}

# Remove only the old main program; keep the usage doc / config.json next to it.
Get-ChildItem $out -Filter '*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host 'Publishing Release (self-contained single file)...' -ForegroundColor Cyan
dotnet publish $proj -c Release -o $out -nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }

$exe = Get-ChildItem $out -Filter '*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $exe) { throw 'No exe produced - check the output above.' }

# Ship the latest plain-text usage doc next to the exe.
$doc = Get-ChildItem (Join-Path $root 'docs') -Filter '*.txt' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($doc) { Copy-Item $doc.FullName (Join-Path $out $doc.Name) -Force }

$mb = [math]::Round($exe.Length / 1MB, 1)
Write-Host ''
Write-Host ('Done: ' + $exe.FullName) -ForegroundColor Green
Write-Host "Size: $mb MB - self-contained single file. Copy this one file to any 64-bit Windows and double-click it; no .NET runtime needed." -ForegroundColor Green
