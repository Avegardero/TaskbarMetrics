param(
    [switch]$AutoStart,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceExe = Join-Path $root "bin\TaskbarMetrics.exe"
if (-not (Test-Path $sourceExe)) {
    throw "TaskbarMetrics.exe not found. Run .\build.ps1 first."
}

$targetDir = Join-Path $env:LOCALAPPDATA "TaskbarMetrics"
$targetExe = Join-Path $targetDir "TaskbarMetrics.exe"

Get-Process TaskbarMetrics -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$existing = Get-ItemProperty -Path $runKey -Name "TaskbarMetrics" -ErrorAction SilentlyContinue
if ($AutoStart -or $existing) {
    if (-not (Test-Path $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    Set-ItemProperty -Path $runKey -Name "TaskbarMetrics" -Value ('"' + $targetExe + '"')
    Write-Host "Auto-start enabled."
}

if (-not $NoLaunch) {
    Start-Process -FilePath $targetExe
    Write-Host "Started TaskbarMetrics."
}

Write-Host "Installed to $targetExe"
