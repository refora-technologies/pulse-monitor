# Puts the two third-party programs Pulse ships in place, from their official releases, and
# refuses any file whose SHA-256 is not the one pinned here.
#
#     powershell -ExecutionPolicy Bypass -File fetch-third-party.ps1
#
# They are not kept in the repository: they are other projects' signed releases, and keeping
# copies here would let a changed file slip in unnoticed. Pinning the hash means the exact
# bytes that were tested are the bytes that ship. To move to a newer version, change the
# version, address and hash together, and test it like any other change.
#
#   PawnIO, the optional sensor driver's installer   https://github.com/namazso/PawnIO.Setup
#   PresentMon, which measures frame rates           https://github.com/GameTechDev/PresentMon

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$files = @(
    @{
        Path   = 'PawnIO_setup.exe'
        Url    = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.1.0/PawnIO_setup.exe'
        Sha256 = 'a3a46226c5e2824f4cdd42be0eecbabfc672c86f7889710f5ab1e6ad385b47a0'
    },
    @{
        Path   = 'Resources\PresentMon\PresentMon-2.5.1-x64.exe'
        Url    = 'https://github.com/GameTechDev/PresentMon/releases/download/v2.5.1/PresentMon-2.5.1-x64.exe'
        Sha256 = '9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191'
    }
)

# Windows PowerShell 5.1 does not use TLS 1.2 by default, and GitHub requires it.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

foreach ($f in $files) {
    $target = Join-Path $here $f.Path

    if (Test-Path $target) {
        $have = (Get-FileHash $target -Algorithm SHA256).Hash.ToLower()
        if ($have -eq $f.Sha256) { Write-Output "ok        $($f.Path)"; continue }
        # A different file where this one belongs is never replaced silently: it could be a
        # newer version someone is testing, or it could be something that should not be there.
        throw "$($f.Path) is present but its SHA-256 is $have, not the pinned $($f.Sha256). Move it away and run this again."
    }

    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    $temp = "$target.download"
    Write-Output "fetching  $($f.Path)"
    Invoke-WebRequest -Uri $f.Url -OutFile $temp -UseBasicParsing

    $got = (Get-FileHash $temp -Algorithm SHA256).Hash.ToLower()
    if ($got -ne $f.Sha256) {
        Remove-Item $temp -Force
        throw "$($f.Path) downloaded with SHA-256 $got, not the pinned $($f.Sha256). Not used."
    }
    Move-Item $temp $target -Force
    Write-Output "verified  $($f.Path)"
}
