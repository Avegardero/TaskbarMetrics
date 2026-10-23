$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "C# compiler not found: $csc"
}

$outDir = Join-Path $root "bin"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Get-Process TaskbarMetrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$sources = Get-ChildItem -Path $root -Filter "*.cs" | ForEach-Object { $_.FullName }
if ($sources.Count -eq 0) { throw "No C# sources found in $root" }

$manifest = Join-Path $root "app.manifest"
$icon = Join-Path $root "app.ico"
$exe = Join-Path $outDir "TaskbarMetrics.exe"

& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    "/win32manifest:$manifest" "/win32icon:$icon" "/out:$exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    $sources

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

$info = Get-Item $exe
Write-Host ("Built {0} ({1:N0} bytes)" -f $info.FullName, $info.Length)
