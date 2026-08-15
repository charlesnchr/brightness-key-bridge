$ErrorActionPreference = 'Stop'

$installDirectory = Join-Path $env:LOCALAPPDATA 'BrightnessKeyBridge'
$installedExecutable = Join-Path $installDirectory 'BrightnessKeyBridge.exe'
$logPath = Join-Path $installDirectory 'bridge.log'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process -Name 'BrightnessKeyBridge' -ErrorAction SilentlyContinue | Stop-Process
Remove-ItemProperty -Path $runKey -Name 'BrightnessKeyBridge' -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $installedExecutable -Force -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $logPath) {
    Write-Output "The diagnostic log was kept at $logPath."
}

Write-Output "Brightness Key Bridge has been removed from startup and uninstalled."
