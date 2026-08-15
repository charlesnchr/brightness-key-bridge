$ErrorActionPreference = 'Stop'

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $PSScriptRoot 'BrightnessKeyBridge.cs'
$output = Join-Path $PSScriptRoot 'BrightnessKeyBridge.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "The Windows .NET Framework C# compiler was not found at $compiler."
}
if (-not (Test-Path -LiteralPath $source)) {
    throw "BrightnessKeyBridge.cs was not found beside this build script."
}

& $compiler /nologo /target:winexe /platform:anycpu /optimize+ "/out:$output" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $source
if ($LASTEXITCODE -ne 0) {
    throw "The C# compiler exited with code $LASTEXITCODE."
}

Get-Item -LiteralPath $output | Select-Object FullName, Length, LastWriteTime
