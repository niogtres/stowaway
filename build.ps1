<#
.SYNOPSIS
  Builds DockerBackup end to end: web UI -> tests -> self-contained service -> MSI.
.EXAMPLE
  ./build.ps1                      # version 0.1.0-dev, runs tests
  ./build.ps1 -Version 0.2.0       # release version
  ./build.ps1 -SkipTests
  ./build.ps1 -Version 0.2.0 -SignThumbprint 0123ABCD...   # sign with a certificate from your store (e.g. on a token)
.NOTES
  Code signing (optional): pass -SignThumbprint for a certificate in CurrentUser\My or LocalMachine\My, or set
  DOCKERBACKUP_SIGN_PFX (base64 of a .pfx) and DOCKERBACKUP_SIGN_PFX_PASSWORD (CI). Without either, the build is unsigned.
  The service executable is signed before it goes into the MSI, then the MSI itself.
#>
[CmdletBinding()]
param(
    [string] $Version = '0.1.0',
    [string] $Configuration = 'Release',
    [switch] $SkipTests,
    [string] $SignThumbprint,
    [string] $TimestampServer = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish'
$outDir = Join-Path $root 'artifacts'

function Invoke-Step([string] $Name, [scriptblock] $Action) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)" }
}

function Get-SigningCertificate {
    if ($SignThumbprint) {
        $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My |
            Where-Object { $_.Thumbprint -eq $SignThumbprint.Replace(' ', '').ToUpperInvariant() -and $_.HasPrivateKey } |
            Select-Object -First 1
        if (-not $cert) { throw "No certificate with a private key and thumbprint $SignThumbprint in CurrentUser\My or LocalMachine\My." }
        return $cert
    }
    if ($env:DOCKERBACKUP_SIGN_PFX) {
        $bytes = [Convert]::FromBase64String($env:DOCKERBACKUP_SIGN_PFX)
        return [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $bytes, $env:DOCKERBACKUP_SIGN_PFX_PASSWORD,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    }
    return $null
}

function Invoke-Sign([System.Security.Cryptography.X509Certificates.X509Certificate2] $Certificate, [string[]] $Files) {
    foreach ($file in $Files) {
        $arguments = @{ FilePath = $file; Certificate = $Certificate; HashAlgorithm = 'SHA256'; IncludeChain = 'NotRoot' }
        if ($TimestampServer) { $arguments.TimestampServer = $TimestampServer }
        $result = Set-AuthenticodeSignature @arguments
        if ($result.Status -in 'NotSigned', 'HashMismatch', 'Incompatible', 'NotSupportedFileFormat') {
            throw "Signing $file failed: $($result.Status) $($result.StatusMessage)"
        }
        Write-Host "    signed $(Split-Path $file -Leaf) ($($result.Status))"
    }
}

$signingCertificate = Get-SigningCertificate
if ($signingCertificate) {
    Write-Host "Code signing with $($signingCertificate.Subject) ($($signingCertificate.Thumbprint))" -ForegroundColor Cyan
}
else {
    Write-Host 'No signing certificate given: building unsigned.' -ForegroundColor Yellow
}

Push-Location (Join-Path $root 'web')
try {
    Invoke-Step 'web: npm ci' { npm ci --no-fund --no-audit }
    Invoke-Step 'web: lint' { npm run lint }
    Invoke-Step 'web: build' { npm run build }
}
finally { Pop-Location }

if (-not $SkipTests) {
    Invoke-Step 'dotnet test' { dotnet test --configuration $Configuration }
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Invoke-Step 'dotnet publish' {
    dotnet publish (Join-Path $root 'src\DockerBackup.Service') `
        --configuration $Configuration --runtime win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:Version=$Version --output $publishDir
}

if ($signingCertificate) {
    Write-Host '==> sign service executable' -ForegroundColor Cyan
    Invoke-Sign $signingCertificate @(Join-Path $publishDir 'DockerBackup.Service.exe')
}

Invoke-Step 'installer' {
    dotnet build (Join-Path $root 'installer\DockerBackup.Installer.wixproj') `
        --configuration $Configuration -p:ProductVersion=$Version -p:PublishDir="$publishDir\" `
        -p:OutputPath="$outDir\"
}

$msi = Join-Path $outDir "DockerBackup-$Version-x64.msi"
if ($signingCertificate) {
    Write-Host '==> sign installer' -ForegroundColor Cyan
    Invoke-Sign $signingCertificate @($msi)
}

Write-Host "Built $msi" -ForegroundColor Green
