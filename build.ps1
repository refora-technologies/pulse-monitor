# Builds the release: the executable, the installer, and the installer's checksum.
#
# Written down because it was not. The publish that produces a shippable Pulse is not the
# obvious one: it is self-contained and single-file, which is what makes the executable about
# 79 MB rather than 200 KB, and nothing in the repository recorded that. Anyone rebuilding
# from a clean checkout, including us in six months, had to work it out from the size of the
# file already installed.
#
#     powershell -ExecutionPolicy Bypass -File build.ps1
#
# What is still not reproducible from a clean checkout, and is worth knowing before trying:
# Resources\PresentMon\PresentMon-2.5.1-x64.exe and PawnIO_setup.exe are not tracked in the
# repository, and there is no script that fetches them. They have to be put in place by hand
# from their own projects before this will produce a working build.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

$iscc = 'C:\InnoSetup6\ISCC.exe'

foreach ($needed in @(
    'Resources\PresentMon\PresentMon-2.5.1-x64.exe',
    'PawnIO_setup.exe')) {
    if (-not (Test-Path $needed)) {
        throw "$needed is missing. It is not tracked in the repository; see the note at the top of this script."
    }
}

if (-not (Test-Path $iscc)) { throw "Inno Setup was not found at $iscc" }

Write-Output 'Publishing...'
Remove-Item -Recurse -Force publish -ErrorAction SilentlyContinue

# Self-contained and single-file, which is what "no prerequisites required" in the README
# means. IncludeNativeLibrariesForSelfExtract is set in the project file, so the native
# libraries travel inside the executable and unpack beside it at runtime.
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
