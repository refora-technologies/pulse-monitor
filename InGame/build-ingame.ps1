# Builds Pulse's in-game overlay (pulse_ingame.dll) into <output>\ingame, with the two weights of
# Pulse's font it draws with. Called by build.ps1; can be run on its own.
#
#     powershell -ExecutionPolicy Bypass -File InGame\build-ingame.ps1 [-Output publish] [-Testing]
#
# -Testing builds the test build instead: it can be told to force an error, and it logs its
# readings every two seconds, for the in-game tests. A release never has it.
#
# Needs the C++ build tools (Visual Studio 2022 or its Build Tools, with "Desktop development with
# C++"), found the standard way, through vswhere. Dear ImGui is fetched and checked by
# fetch-third-party.ps1, not kept in the repository.
param([string]$Output = 'publish', [switch]$Testing)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $here

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'vswhere was not found: install Visual Studio 2022 or its Build Tools with the C++ workload.' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'No Visual Studio with the C++ tools was found.' }
$devcmd = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'

$imgui = Join-Path $here 'third_party\imgui'
if (-not (Test-Path (Join-Path $imgui 'imgui.cpp'))) { throw 'Dear ImGui is missing: run fetch-third-party.ps1 first.' }

$out = Join-Path $repo $Output
$dest = Join-Path $out 'ingame'
$obj = Join-Path $here 'obj'
New-Item -ItemType Directory -Force $dest, (Join-Path $dest 'fonts'), $obj | Out-Null

$sources = @('hooks', 'frame_stats', 'ui', 'render_dx11', 'render_dx12', 'link', 'log') | ForEach-Object { "`"$here\src\$_.cpp`"" }
$sources += @('imgui', 'imgui_draw', 'imgui_tables', 'imgui_widgets') | ForEach-Object { "`"$imgui\$_.cpp`"" }
$sources += @('imgui_impl_dx11', 'imgui_impl_dx12') | ForEach-Object { "`"$imgui\backends\$_.cpp`"" }

# /EHa so a crash inside ImGui is caught by the overlay's own safety net; /MT so the DLL needs no
# runtime installed in the game; IM_ASSERT never stops the game (src\imconfig_pulse.h).
$defines = '/DNDEBUG /DIMGUI_USER_CONFIG="\"imconfig_pulse.h\""'
if ($Testing) { $defines += ' /DPULSE_INGAME_TESTING' }
$cl = "cl /nologo /std:c++17 /O2 /MT /EHa /W3 /LD $defines /I`"$here\src`" /I`"$imgui`" " + ($sources -join ' ') +
      " /Fo`"$obj\\`" /Fe`"$dest\pulse_ingame.dll`" /link d3d11.lib d3d12.lib dxgi.lib user32.lib d3dcompiler.lib"

$script = Join-Path $obj 'build.cmd'
Set-Content $script "@echo off`r`ncall `"$devcmd`" -arch=x64 -host_arch=x64 >nul 2>nul`r`n$cl`r`n" -Encoding ASCII
& cmd /c "`"$script`""
if ($LASTEXITCODE -ne 0) { throw 'the in-game overlay did not build' }

# The two weights the overlay draws with, and the font's licence (SIL Open Font License).
foreach ($f in 'PlusJakartaSans-Medium.ttf', 'PlusJakartaSans-Bold.ttf', 'OFL.txt') {
    Copy-Item (Join-Path $repo "Resources\Fonts\$f") (Join-Path $dest 'fonts') -Force
}
Write-Output "built $dest\pulse_ingame.dll$(if ($Testing) { ' (test build)' })"
