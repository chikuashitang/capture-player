[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NgxIncludeDir,
    [Parameter(Mandatory)][string]$NgxLibDir,
    [string]$Out = (Join-Path $PSScriptRoot '..\out\resources\frei0r')
)
$ErrorActionPreference = 'Stop'
$compiler = Get-Command cl.exe -ErrorAction SilentlyContinue
if (!$compiler -or $env:VSCMD_ARG_TGT_ARCH -ne 'x64') {
    throw 'Run from the x64 Native Tools Command Prompt for Visual Studio.'
}
$include = (Resolve-Path -LiteralPath $NgxIncludeDir).Path
$library = (Resolve-Path -LiteralPath $NgxLibDir).Path
foreach ($file in @((Join-Path $include 'nvsdk_ngx.h'), (Join-Path $include 'nvsdk_ngx_params.h'),
                     (Join-Path $library 'nvsdk_ngx_s.lib'))) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing NGX SDK file: $file" }
}
$dest = (New-Item -ItemType Directory -Force -Path $Out).FullName
$source = Join-Path $PSScriptRoot 'capture_nr.cpp'
$options = @('/nologo','/std:c++20','/O2','/EHsc','/MT','/W4','/wd4191','/LD',"/I$include",
    $source, "/Fo$dest\capture_nr.obj", '/link', "/OUT:$dest\capture_nr.dll",
    "/IMPLIB:$dest\capture_nr.lib", "/LIBPATH:$library", 'd3d12.lib', 'dxgi.lib', 'nvsdk_ngx_s.lib',
    'advapi32.lib', 'shlwapi.lib', 'shell32.lib', 'version.lib', 'user32.lib')
& $compiler.Source @options
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-Item -LiteralPath (Join-Path $dest 'capture_nr.dll')
