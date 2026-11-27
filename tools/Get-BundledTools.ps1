<#
.SYNOPSIS
  Downloads the Windows programs Stowaway ships for Windows-container engines (restic, rclone) into tools\bin,
  verifying each against a pinned SHA-256. Skips downloads that are already present and verified.
.NOTES
  On Linux-container engines these run inside the runner image instead; see BackupOptions.ResticImage.
  Bump versions deliberately: update the hash from the project's official SHA256SUMS, and the license text in
  tools\licenses if it changed.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null

$tools = @(
    @{ Name = 'restic.exe'; Version = '0.19.1'
       Url = 'https://github.com/restic/restic/releases/download/v0.19.1/restic_0.19.1_windows_amd64.zip'
       Sha256 = 'da948ad707ed690426473aaba2046cd61f8f90f6f0e7dab6be0d5796531de67d'
       Entry = 'restic_0.19.1_windows_amd64.exe' },
    @{ Name = 'rclone.exe'; Version = '1.76.0'
       Url = 'https://downloads.rclone.org/v1.76.0/rclone-v1.76.0-windows-amd64.zip'
       Sha256 = '8b4fd2737ade4e70c057ef4a49deb7cffe94b0fb9d7587c189d149823f21a1dc'
       Entry = 'rclone-v1.76.0-windows-amd64/rclone.exe' }
)

foreach ($tool in $tools) {
    $target = Join-Path $bin $tool.Name
    $stamp = "$target.version"
    if ((Test-Path $target) -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq "$($tool.Version) $($tool.Sha256)")) {
        continue
    }

    $zip = Join-Path ([IO.Path]::GetTempPath()) ("stowaway-" + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        Write-Host "    downloading $($tool.Name) $($tool.Version)"
        Invoke-WebRequest -Uri $tool.Url -OutFile $zip -UseBasicParsing
        $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $tool.Sha256) { throw "$($tool.Url): SHA-256 $hash doesn't match the pinned $($tool.Sha256)" }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $entry = $archive.Entries | Where-Object { $_.FullName -eq $tool.Entry } | Select-Object -First 1
            if (-not $entry) { throw "$($tool.Entry) not found in $($tool.Url)" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
        finally { $archive.Dispose() }
        Set-Content -Path $stamp -Value "$($tool.Version) $($tool.Sha256)" -Encoding ascii
    }
    finally { Remove-Item $zip -ErrorAction SilentlyContinue }
}
