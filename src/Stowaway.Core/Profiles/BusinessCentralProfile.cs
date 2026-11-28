using System.Globalization;

namespace Stowaway.Core.Profiles;

/// <summary>
/// Microsoft Dynamics 365 Business Central (and Dynamics NAV) containers made by BcContainerHelper. Their image labels
/// name the product and version. BcContainerHelper maps a folder of its own per container into <c>C:\run\my</c>
/// (setup scripts and certificates), which belongs to the app; the artifact cache and its shared folder don't. The
/// databases live inside the container and are backed up by the SQL Server provider (<c>SqlServerDumps</c>); each
/// snapshot also gets a script that recreates the container (<see cref="BusinessCentralScript"/>), because recreating it
/// from its saved definition gives a new container with a new database.
/// </summary>
public sealed class BusinessCentralProfile : IAppProfile
{
    public const string ProfileId = "business-central";
    public const string Name = "Business Central";
    public const string NavName = "Dynamics NAV";

    /// <summary>The <c>maintainer</c> label on Microsoft's Business Central and NAV images.</summary>
    public const string Maintainer = "Dynamics SMB";

    /// <summary>Where BcContainerHelper maps the container's own folder (<c>…\Extensions\&lt;name&gt;\my</c>).</summary>
    public const string MyFolder = @"c:\run\my";

    public const string MyFolderNote = "Business Central: this container's own setup scripts and certificates";
    public const string ArtifactCache = "Business Central artifact cache: downloaded again when a container needs it. Back it up with your file backup if you like.";
    public const string SharedFolder = "BcContainerHelper's shared folder, used by every Business Central container on this computer. Back it up with your file backup.";
    public const string CardNote = "Business Central: the database and the container's own folder are backed up, with a script that recreates the container from the snapshot (see Help, Recover a whole app).";

    public string Id => ProfileId;

    public string? DefinitionDescription => "the Business Central restore script";

    public AppProduct? Detect(IReadOnlyDictionary<string, string> labels)
    {
        if (!labels.TryGetValue("maintainer", out var maintainer) || !string.Equals(maintainer, Maintainer, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var version = labels.GetValueOrDefault("version");
        if (string.IsNullOrWhiteSpace(version) && !labels.ContainsKey("nav"))
        {
            return null; // Another Dynamics SMB image (e.g. a generic one before setup): nothing to go on.
        }

        // Business Central starts at version 13; earlier versions are Dynamics NAV.
        var major = int.TryParse(version?.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : (int?)null;
        var country = labels.GetValueOrDefault("country");
        return new AppProduct(
            ProfileId,
            major is < 13 ? NavName : Name,
            string.IsNullOrWhiteSpace(version) ? null : version,
            string.IsNullOrWhiteSpace(country) ? null : country.ToUpperInvariant(),
            CardNote);
    }

    public (string? Reason, string? Note) Bind(string defaultReason, string windowsSource, string destination)
    {
        if (string.Equals(destination.TrimEnd('\\', '/'), MyFolder, StringComparison.OrdinalIgnoreCase))
        {
            return (null, MyFolderNote);
        }

        var leaf = Path.GetFileName(windowsSource.TrimEnd('\\'));
        if (leaf.Equals("bcartifacts.cache", StringComparison.OrdinalIgnoreCase))
        {
            return (ArtifactCache, null);
        }

        if (leaf.Equals("BcContainerHelper", StringComparison.OrdinalIgnoreCase))
        {
            return (SharedFolder, null);
        }

        return (defaultReason, null);
    }

    public IReadOnlyDictionary<string, string> DefinitionFiles(ContainerDefinition container, DateTimeOffset backedUpAt) =>
        BusinessCentralScript.Build(container, backedUpAt) is { } script
            ? new Dictionary<string, string> { [BusinessCentralScript.PathOf(container.Name)] = script }
            : new Dictionary<string, string>();

    public string? RecreateWarning(string containerName) =>
        $"{containerName} is a Business Central container: recreated from its settings, it starts with a new database, not the backed-up one. " +
        $"Use the restore script instead: restore the App definition to a folder and run {BusinessCentralScript.PathOf(containerName).Replace('/', '\\')} (see Help, Recover a whole app).";
}
