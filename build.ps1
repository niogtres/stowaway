<#
.SYNOPSIS
  Builds DockerBackup end to end: web UI -> tests -> self-contained service -> MSI.
.EXAMPLE
  ./build.ps1                      # version 0.1.0-dev, runs tests
  ./build.ps1 -Version 0.2.0       # release version
  ./build.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [string] $Version = '0.1.0',
    [string] $Configuration = 'Release',
    [switch] $SkipTests
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

Invoke-Step 'installer' {
    dotnet build (Join-Path $root 'installer\DockerBackup.Installer.wixproj') `
        --configuration $Configuration -p:ProductVersion=$Version -p:PublishDir="$publishDir\" `
        -p:OutputPath="$outDir\"
}

$msi = Join-Path $outDir "DockerBackup-$Version-x64.msi"
Write-Host "Built $msi" -ForegroundColor Green
