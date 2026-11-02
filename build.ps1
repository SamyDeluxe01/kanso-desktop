param([Parameter(Mandatory=$true)][string]$Compiler)
$ErrorActionPreference='Stop'
$project=Join-Path $PSScriptRoot 'tests/KansoDesktop.NativeExperiments/KansoDesktop.NativeExperiments.csproj'
$out=Join-Path $PSScriptRoot 'dist/KansoDesktop'
& dotnet restore $project
if($LASTEXITCODE){throw 'Restore failed'}
& (Join-Path $PSScriptRoot 'tests/KansoDesktop.NativeExperiments/native/build.ps1') -Compiler $Compiler
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $out
if($LASTEXITCODE){throw 'Publish failed'}
Copy-Item (Join-Path $PSScriptRoot 'tests/KansoDesktop.NativeExperiments/bin/Release/net10.0-windows/KansoProjection.dll') $out
Set-Content (Join-Path $out 'personal-mode.flag') 'Personal desktop mode'
if(!(Test-Path -LiteralPath (Join-Path $out 'KansoDesktop.exe'))){throw 'Release apphost missing'}
Write-Host "Start: $out/KansoDesktop.exe"

