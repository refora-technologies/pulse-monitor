# Builds the release: the executable, the installer, and the installer's checksum.
#
# Written down because it was not. The publish that produces a shippable Pulse is not the
# obvious one: it is self-contained and single-file, which is what makes the executable about
# 180 MB rather than 200 KB, and nothing in the repository recorded that. Anyone rebuilding
# from a clean checkout, including us in six months, had to work it out from the size of the
# file already installed.
#
#     powershell -ExecutionPolicy Bypass -File build.ps1
#
# Works from a clean checkout: the two third-party programs Pulse ships (the PawnIO installer
# and PresentMon) are fetched from their official releases by fetch-third-party.ps1, which
# refuses anything whose SHA-256 is not the pinned one. GitHub builds releases with this same
# script, so a local build and a released one are made the same way.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

& (Join-Path $here 'fetch-third-party.ps1')
if (-not $?) { throw 'the third-party programs could not be put in place' }

# Inno Setup 6, wherever it is installed: INNO_ISCC when set, then the usual places. GitHub's
# Windows machines have it under Program Files (x86); this PC has it at C:\InnoSetup6.
$iscc = @($env:INNO_ISCC,
          'C:\InnoSetup6\ISCC.exe',
          "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it, or set INNO_ISCC to the path of ISCC.exe.' }

Write-Output 'Publishing...'
Remove-Item -Recurse -Force publish -ErrorAction SilentlyContinue

# Self-contained and single-file, so nothing needs installing first. The native libraries are
# left beside the executable (IncludeNativeLibrariesForSelfExtract is off in the project file)
# and setup.iss installs them with it, rather than having them unpacked to %TEMP% at launch.
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Write-Output 'Building the installer...'
& $iscc /Q setup.iss
if ($LASTEXITCODE -ne 0) { throw 'the installer did not build' }

# The updater refuses to run an installer whose checksum does not match, so this file is part
# of the release rather than a courtesy. It is uploaded alongside the installer as
# PulseSetup.exe.sha256.
$hash = (Get-FileHash 'installer\PulseSetup.exe' -Algorithm SHA256).Hash.ToLower()
Set-Content -Path 'installer\PulseSetup.exe.sha256' -Value $hash -NoNewline

$size = [math]::Round((Get-Item 'installer\PulseSetup.exe').Length / 1MB, 1)
Write-Output ''
Write-Output "installer\PulseSetup.exe  ($size MB)"
Write-Output "sha256  $hash"
