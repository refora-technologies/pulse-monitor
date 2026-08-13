# Computes the SHA-256 of the built installer and writes a sidecar file next to it.
# Upload BOTH files to the GitHub release — Pulse's updater refuses to run an
# installer whose hash doesn't match this sidecar (no code-signing cert, so this
# hash is the only integrity check the auto-updater has).
#
# Usage: pwsh installer\generate-checksum.ps1 [-InstallerPath installer\PulseSetup.exe]

param(
    [string]$InstallerPath = "$PSScriptRoot\PulseSetup.exe"
)

if (-not (Test-Path $InstallerPath)) {
    Write-Error "Installer not found: $InstallerPath"
    exit 1
}

$hash    = (Get-FileHash -Path $InstallerPath -Algorithm SHA256).Hash.ToLower()
$outPath = "$InstallerPath.sha256"
Set-Content -Path $outPath -Value $hash -NoNewline

$installerFile = Split-Path -Leaf $InstallerPath
$checksumFile  = Split-Path -Leaf $outPath

Write-Host "SHA-256: $hash"
Write-Host "Written to: $outPath"
Write-Host ""
Write-Host "Upload BOTH '$installerFile' and '$checksumFile' as release assets."
Write-Host "If the checksum file is missing from a release, Pulse's auto-updater will refuse to auto-install it."
