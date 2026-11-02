param([string]$Compiler = "$PSScriptRoot/../../../.tools/zig-x86_64-windows-0.15.2/zig.exe")
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$output = Join-Path $projectRoot 'tests/KansoDesktop.NativeExperiments/bin/Release/net10.0-windows'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $projectRoot '.tools/zig-cache'
& $Compiler cc -shared -O2 -Wall -Wextra -Werror -target x86_64-windows-gnu (Join-Path $PSScriptRoot 'projection.c') (Join-Path $PSScriptRoot 'fixture_bridge.c') (Join-Path $PSScriptRoot 'desktop_peer.c') -lcomctl32 -luser32 -lshell32 -lole32 -loleaut32 -loleacc -luuid -o (Join-Path $output 'KansoProjection.dll')
if ($LASTEXITCODE -ne 0) { throw 'Native projection build failed' }
