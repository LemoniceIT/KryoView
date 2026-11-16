$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$output\KryoView.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Get-ChildItem "$PSScriptRoot\src\*.cs").FullName
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
