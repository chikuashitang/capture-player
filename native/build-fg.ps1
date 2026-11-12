[CmdletBinding()]
param([string]$Out = (Join-Path $PSScriptRoot '..\resources'))

$ErrorActionPreference = 'Stop'
$compiler = Get-Command cl.exe -ErrorAction SilentlyContinue
if (!$compiler -or $env:VSCMD_ARG_TGT_ARCH -ne 'x64') {
    throw 'Run from the x64 Native Tools Command Prompt for Visual Studio.'
}

$destination = (New-Item -ItemType Directory -Force -Path $Out).FullName
$source = Join-Path $PSScriptRoot 'fg_direct.cpp'
$options = @('/nologo','/std:c++20','/O2','/EHsc','/MT','/W4','/DFG_BENCH',
    $source, "/Fo$destination\fg_direct.obj", '/link', "/OUT:$destination\gpu-preview.exe",
    'd3d11.lib', 'd3dcompiler.lib', 'dxgi.lib', 'user32.lib')
& $compiler.Source @options
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-Item -LiteralPath (Join-Path $destination 'gpu-preview.exe')
