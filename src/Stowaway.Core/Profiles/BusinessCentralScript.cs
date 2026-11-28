using System.Globalization;
using System.Text;
using Stowaway.Core.Backups;

namespace Stowaway.Core.Profiles;

/// <summary>
/// Writes the PowerShell script, stored with every snapshot of a Business Central container, that recreates the
/// container on any Windows Docker host: <c>New-BcContainer</c> from the same artifact and settings, then the backed-up
/// database (a .bak through <c>-bakFile</c>, or database files copied from the stopped container, attached in place of
/// the new container's own database). Secrets are never written: the person running it signs in, and names a license.
/// Recreating the container from its saved definition (Recreate app) gives a fresh container without the database.
/// </summary>
public static class BusinessCentralScript
{
    /// <summary>The script's folder in the snapshot's app definition.</summary>
    public const string Folder = "business-central";

    private static readonly string[] AuthTypes = ["Windows", "UserPassword", "NavUserPassword", "AAD"];

    /// <summary>The script's path relative to the app definition folder.</summary>
    public static string PathOf(string containerName) => $"{Folder}/{SqlServerDumps.FolderName(containerName)}.ps1";

    /// <summary>The script, or null when the definition has no artifact URL (a container not made by BcContainerHelper).</summary>
    public static string? Build(ContainerDefinition c, DateTimeOffset backedUpAt)
    {
        var artifactUrl = c.Env.GetValueOrDefault("artifactUrl");
        if (string.IsNullOrWhiteSpace(artifactUrl) || !Uri.TryCreate(artifactUrl, UriKind.Absolute, out var artifact))
        {
            return null;
        }

        // Insider artifacts carry a SAS token in the query: a secret, so it's left out.
        var needsToken = artifact.Query.Length > 1;
        var url = needsToken ? artifact.GetLeftPart(UriPartial.Path) : artifactUrl;
        var auth = AuthTypes.FirstOrDefault(a => a.Equals(c.Env.GetValueOrDefault("auth"), StringComparison.OrdinalIgnoreCase)) ?? "NavUserPassword";

        var settings = new StringBuilder();
        void Set(string name, string value) => settings.Append(CultureInfo.InvariantCulture, $"$parameters.{name} = {value}\r\n");
        if (c.Env.GetValueOrDefault("locale") is { Length: > 0 } locale)
        {
            Set("locale", Quote(locale));
        }

        if (IsYes(c.Env.GetValueOrDefault("useSSL")))
        {
            Set("useSSL", "$true");
        }

        if (IsYes(c.Env.GetValueOrDefault("enableSymbolLoading")))
        {
            Set("enableSymbolLoading", "$true");
        }

        if (c.MemoryBytes > 0)
        {
            Set("memoryLimit", Quote($"{(long)Math.Ceiling(c.MemoryBytes / (1024d * 1024 * 1024))}G"));
        }

        if (c.Dns is [var dns, ..])
        {
            Set("dns", Quote(dns));
        }

        if (c.PublishedPorts is { Count: > 0 } ports)
        {
            Set("publishPorts", $"@({string.Join(", ", ports.Order())})");
        }

        var additional = new List<string>();
        if (IsYes(c.Env.GetValueOrDefault("enableApiServices")))
        {
            additional.Add("--env enableApiServices=Y");
        }

        additional.AddRange((c.Binds ?? []).Where(b => !IsStandardBind(b)).Select(b => $"--volume \"{b.Host}:{b.Container}\""));
        if (additional.Count > 0)
        {
            Set("additionalParameters", $"@({string.Join(", ", additional.Select(Quote))})");
        }

        if (needsToken)
        {
            Set("sasToken", "$SasToken");
        }

        var name = Quote(c.Name);
        var hasLicense = !string.IsNullOrWhiteSpace(c.Env.GetValueOrDefault("licenseFile"));
        return Template
            .Replace("@NAME@", c.Name.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("@QNAME@", name, StringComparison.Ordinal)
            .Replace("@DATE@", backedUpAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@LICENSE@", hasLicense
                ? "    # The original container had a license file: give its path or URL here (a Cronus demo license is used otherwise)."
                : "    # A license file (path or URL), if the container needs one.", StringComparison.Ordinal)
            .Replace("@TOKEN@", needsToken ? "\r\n    # The artifact is an insider build: its SAS token (left out of this script).\r\n    [Parameter(Mandatory = $true)] [string] $SasToken," : "", StringComparison.Ordinal)
            .Replace("@ARTIFACT@", Quote(url), StringComparison.Ordinal)
            .Replace("@AUTH@", Quote(auth), StringComparison.Ordinal)
            .Replace("@SETTINGS@", settings.ToString(), StringComparison.Ordinal)
            .ReplaceLineEndings("\r\n");
    }

    /// <summary>Folders BcContainerHelper maps into every container itself (it maps them again).</summary>
    private static bool IsStandardBind((string Host, string Container) bind)
    {
        var container = bind.Container.TrimEnd('\\', '/');
        var host = bind.Host.TrimEnd('\\', '/');
        return container.Equals(BusinessCentralProfile.MyFolder, StringComparison.OrdinalIgnoreCase)
            || container.Equals(@"c:\dl", StringComparison.OrdinalIgnoreCase)
            || container.Equals(@"c:\driversetc", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(@"\BcContainerHelper", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(@"\bcartifacts.cache", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsYes(string? value) => value is not null && (value.Equals("Y", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private const string Template = """
        <#
        .SYNOPSIS
          Recreates the Business Central container '@NAME@' from a Stowaway snapshot.

        .DESCRIPTION
          Stowaway wrote this script when it backed up '@NAME@' (@DATE@). Run it on the computer that should host the container:

          1. In Stowaway, open the snapshot and restore "Database dumps" to a folder on this computer.
          2. Switch Docker to Windows containers, and install BcContainerHelper:  Install-Module BcContainerHelper
          3. In an elevated PowerShell:  .\@NAME@.ps1 -Dumps 'D:\Restore'

          It creates the container from the same artifact and settings with New-BcContainer, then loads the backed-up
          database: a .bak file (backed up while the container ran) through -bakFile, or the database files (copied while it
          was stopped), attached in place of the new container's own database. The restored database keeps its users: sign
          in with a user of the original container when asked.

          Files you kept in the container's own folder (BcContainerHelper\Extensions\@NAME@\my) are in the snapshot too;
          restore them to a folder if you need them.
        #>
        param(
            # The folder you restored the snapshot's database dumps to.
            [Parameter(Mandatory = $true)] [string] $Dumps,
            [string] $ContainerName = @QNAME@,
        @LICENSE@
            [string] $LicenseFile,@TOKEN@
            # A user of the original container (asked for when left out).
            [pscredential] $Credential
        )

        $ErrorActionPreference = 'Stop'
        Import-Module BcContainerHelper

        $manifest = Get-ChildItem -LiteralPath $Dumps -Recurse -Filter 'sqlserver.json' |
            Where-Object { (Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json).container -eq @QNAME@ } |
            Select-Object -First 1
        if (-not $manifest) { throw "No database backup of @NAME@ under $Dumps. Restore the snapshot's database dumps there first." }
        $backup = Get-Content -Raw -LiteralPath $manifest.FullName | ConvertFrom-Json
        $folder = $manifest.DirectoryName

        if (-not $Credential) { $Credential = Get-Credential -Message 'A Business Central user of the original container' }

        $parameters = @{
            accept_eula   = $true
            containerName = $ContainerName
            artifactUrl   = @ARTIFACT@
            auth          = @AUTH@
            Credential    = $Credential
            updateHosts   = $true
        }
        @SETTINGS@if ($LicenseFile) { $parameters.licenseFile = $LicenseFile }

        if ($backup.mode -eq 'backup') {
            $bak = @(Get-ChildItem -LiteralPath $folder -Filter '*.bak')
            if ($bak.Count -ne 1) { throw "Found $($bak.Count) .bak files in $folder; this script restores one. Load the others with RESTORE DATABASE." }
            New-BcContainer @parameters -bakFile $bak[0].FullName
            Write-Host "$ContainerName is ready with the restored database."
            return
        }

        # Database files copied from the stopped container (files\<drive>\<path> in the dump folder).
        $databases = @($backup.databases | Where-Object { $_.physicalFiles })
        if ($databases.Count -eq 0) {
            $mdf = @($backup.files | Where-Object { $_ -like '*.mdf' })
            if ($mdf.Count -ne 1) { throw "Can't tell which database files belong together in $folder. Attach them by hand with CREATE DATABASE ... FOR ATTACH." }
            $databases = @([pscustomobject]@{ name = [IO.Path]::GetFileNameWithoutExtension($mdf[0]); physicalFiles = @($backup.files) })
        }

        $files = @($databases | ForEach-Object { $_.physicalFiles } | Select-Object -Unique)
        foreach ($file in $files) {
            $copy = Join-Path $folder ('files\' + ($file -replace '^([A-Za-z]):', '$1'))
            if (-not (Test-Path -LiteralPath $copy)) { throw "Missing from the restored dumps: $copy" }
        }
        if (@($files | ForEach-Object { Split-Path -Leaf $_ } | Select-Object -Unique).Count -ne $files.Count) { throw 'Two database files share a name; attach them by hand.' }

        New-BcContainer @parameters

        # The container sees its own folder (BcContainerHelper\Extensions\<name>\my) as C:\run\my: stage the files there.
        $stage = Join-Path $bcContainerHelperConfig.hostHelperFolder "Extensions\$ContainerName\my\stowaway-restore"
        New-Item -ItemType Directory -Force -Path $stage | Out-Null
        foreach ($file in $files) {
            Write-Host "Copying $(Split-Path -Leaf $file)"
            Copy-Item -LiteralPath (Join-Path $folder ('files\' + ($file -replace '^([A-Za-z]):', '$1'))) -Destination $stage -Force
        }

        $plan = ConvertTo-Json -Depth 5 -Compress @{ databases = @($databases | ForEach-Object { @{ name = $_.name; files = @($_.physicalFiles) } }) }
        Invoke-ScriptInBcContainer -containerName $ContainerName -argumentList $plan -scriptblock {
            param($plan)
            $ErrorActionPreference = 'Stop'
            $plan = $plan | ConvertFrom-Json
            $instance = (Get-NavServerInstance | Select-Object -First 1).ServerInstance -replace '^.*\$', ''
            $current = Get-NAVServerConfiguration -ServerInstance $instance -KeyName DatabaseName
            $server = Get-NAVServerConfiguration -ServerInstance $instance -KeyName DatabaseServer
            $sqlInstance = Get-NAVServerConfiguration -ServerInstance $instance -KeyName DatabaseInstance
            if ($sqlInstance) { $server = "$server\$sqlInstance" }

            Set-NavServerInstance -ServerInstance $instance -Stop
            # The new container's own database goes; the restored one takes its place.
            Invoke-Sqlcmd -ServerInstance $server -QueryTimeout 600 -Query "ALTER DATABASE [$($current -replace '\]', ']]')] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$($current -replace '\]', ']]')]"
            foreach ($db in $plan.databases) {
                foreach ($file in $db.files) {
                    New-Item -ItemType Directory -Force -Path (Split-Path $file) | Out-Null
                    Move-Item -LiteralPath (Join-Path 'C:\run\my\stowaway-restore' (Split-Path -Leaf $file)) -Destination $file -Force
                }
                $on = ($db.files | ForEach-Object { "(FILENAME = N'$($_ -replace "'", "''")')" }) -join ', '
                Invoke-Sqlcmd -ServerInstance $server -QueryTimeout 3600 -Query "CREATE DATABASE [$($db.name -replace '\]', ']]')] ON $on FOR ATTACH"
            }

            Set-NAVServerConfiguration -ServerInstance $instance -KeyName DatabaseName -KeyValue $plan.databases[0].name
            Set-NavServerInstance -ServerInstance $instance -Start
            if ($plan.databases.Count -gt 1) { Write-Warning "Attached $($plan.databases.Count) databases; the service uses $($plan.databases[0].name). Mount other tenants with Mount-NavTenant." }
        }

        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "$ContainerName is ready with the restored database."
        """;
}
