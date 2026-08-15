$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'BrightnessKeyBridge.exe'
$installDirectory = Join-Path $env:LOCALAPPDATA 'BrightnessKeyBridge'
$installedExecutable = Join-Path $installDirectory 'BrightnessKeyBridge.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

if (-not (Test-Path -LiteralPath $source)) {
    throw "BrightnessKeyBridge.exe was not found beside this installer."
}

Get-Process -Name 'BrightnessKeyBridge' -ErrorAction SilentlyContinue | Stop-Process
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $installedExecutable -Force
New-ItemProperty -Path $runKey -Name 'BrightnessKeyBridge' -Value ('"{0}"' -f $installedExecutable) -PropertyType String -Force | Out-Null
Start-Process -FilePath $installedExecutable -WindowStyle Hidden

Write-Output "Brightness Key Bridge is installed and running."
