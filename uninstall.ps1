$ErrorActionPreference = "Stop"

Get-Process TaskbarMetrics -ErrorAction SilentlyContinue | Stop-Process -Force

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$existing = Get-ItemProperty -Path $runKey -Name "TaskbarMetrics" -ErrorAction SilentlyContinue
if ($existing) {
    Remove-ItemProperty -Path $runKey -Name "TaskbarMetrics"
    Write-Host "Auto-start removed."
}

$targetDir = Join-Path $env:LOCALAPPDATA "TaskbarMetrics"
$expected = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "TaskbarMetrics"))
if (Test-Path $targetDir) {
    $resolved = [System.IO.Path]::GetFullPath($targetDir)
    if ($resolved -ne $expected) {
        throw "Unexpected uninstall path: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
    Write-Host "Removed $resolved"
}

Write-Host "TaskbarMetrics uninstalled."
