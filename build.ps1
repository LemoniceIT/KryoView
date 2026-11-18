param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'))

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$output\KryoView.exe" "/win32icon:$PSScriptRoot\assets\KryoView.ico" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Get-ChildItem "$PSScriptRoot\src\*.cs").FullName
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$themeOutput = Join-Path $output 'themes'
New-Item -ItemType Directory -Force -Path $themeOutput | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'themes') -Filter '*.theme' -File |
    Copy-Item -Destination $themeOutput -Force
