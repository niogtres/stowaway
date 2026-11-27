<#
.SYNOPSIS
  Writes the three winget manifest files (version, installer, defaultLocale) for a built MSI.
.DESCRIPTION
  Submitting to winget-pkgs needs an installer URL anyone can download, so it waits until releases are public
  (and the product has its final name). Until then the release workflow attaches these files to each release.
.EXAMPLE
  ./tools/New-WingetManifest.ps1 -Version 0.6.0 -Msi artifacts/Stowaway-0.6.0-x64.msi `
      -InstallerUrl https://github.com/niogtres/stowaway/releases/download/v0.6.0/Stowaway-0.6.0-x64.msi
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Msi,
    [Parameter(Mandatory)] [string] $InstallerUrl,
    [string] $PackageIdentifier = 'Niogtres.Stowaway',
    [string] $OutDir = 'artifacts/winget'
)

$ErrorActionPreference = 'Stop'
$manifestVersion = '1.9.0'
$msiPath = (Resolve-Path $Msi).Path
$sha256 = (Get-FileHash $msiPath -Algorithm SHA256).Hash

# The MSI's ProductCode lets winget match the installed program in Apps & features.
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($msiPath, 0))
$view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @("SELECT Value FROM Property WHERE Property='ProductCode'"))
$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
$record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
$productCode = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($database)

$dir = Join-Path $OutDir "$PackageIdentifier/$Version"
New-Item -ItemType Directory -Force $dir | Out-Null

$header = "# yaml-language-server: `$schema=https://aka.ms/winget-manifest"

@"
$header.version.$manifestVersion.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $manifestVersion
"@ | Set-Content -Encoding utf8 (Join-Path $dir "$PackageIdentifier.yaml")

@"
$header.installer.$manifestVersion.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
MinimumOSVersion: 10.0.17763.0
InstallerType: msi
Scope: machine
InstallModes:
  - interactive
  - silent
  - silentWithProgress
UpgradeBehavior: install
ElevationRequirement: elevatesSelf
Installers:
  - Architecture: x64
    InstallerUrl: $InstallerUrl
    InstallerSha256: $sha256
    ProductCode: '$productCode'
ManifestType: installer
ManifestVersion: $manifestVersion
"@ | Set-Content -Encoding utf8 (Join-Path $dir "$PackageIdentifier.installer.yaml")

@"
$header.defaultLocale.$manifestVersion.schema.json
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
PackageLocale: en-US
Publisher: Thomas Kraft
PublisherUrl: https://github.com/niogtres
PackageUrl: https://github.com/niogtres/stowaway
Copyright: Copyright (c) 2026 Thomas Kraft
LicenseUrl: https://github.com/niogtres/stowaway/blob/main/LICENSE
PackageName: Stowaway
License: MIT
ShortDescription: Sensible Docker backup management. Scheduled, encrypted backup and restore for Docker containers, volumes and bind mounts on Windows.
Description: |-
  Runs as a Windows service, discovers every container, compose stack, volume and bind mount, and backs them up on a
  schedule with restic (local folders, SMB shares, S3-compatible storage, Backblaze B2, Azure, SFTP, Dropbox).
  Consistent database dumps, retention, restore and full app recreation, managed from a local web UI.
Tags:
  - backup
  - docker
  - restic
  - restore
ManifestType: defaultLocale
ManifestVersion: $manifestVersion
"@ | Set-Content -Encoding utf8 (Join-Path $dir "$PackageIdentifier.locale.en-US.yaml")

Write-Host "winget manifests in $dir (SHA256 $sha256, ProductCode $productCode)"
