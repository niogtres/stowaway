using Stowaway.Core.Backups;

namespace Stowaway.Core.Destinations;

/// <summary>Where a field appears in the add and edit forms.</summary>
public enum FieldSection
{
    /// <summary>Where the backups are: changing it later means the backups were moved.</summary>
    Location,

    /// <summary>How to sign in: can be replaced later without moving anything.</summary>
    Credentials,
}

/// <summary>How the web client renders a field.</summary>
public enum FieldType
{
    Text,
    Password,
    Multiline,
    Number,

    /// <summary>A subfolder of /Stowaway in a cloud drive (Dropbox, OneDrive).</summary>
    StowawayFolder,

    /// <summary>On or off: <c>true</c> or empty.</summary>
    Toggle,
}

/// <summary>Older destinations keep these values in their own columns instead of the settings and secrets JSON.</summary>
public enum DestinationColumn
{
    None,
    Path,
    SmbUsername,
    SmbDomain,
    SmbPassword,
}

/// <summary>One value a destination type needs.</summary>
/// <param name="Key">Stable key: stored in the destination's settings or secrets, and sent by the web client.</param>
/// <param name="Secret">Stored encrypted, never sent back to the browser.</param>
/// <param name="Hidden">Kept by Stowaway, not shown in the forms (e.g. a Dropbox token, recorded SSH host keys).</param>
/// <param name="Half">Takes half the form's width, next to another half field.</param>
/// <param name="Column">Kept in a column of its own (older destination types).</param>
public sealed record DestinationField(
    string Key,
    string Label,
    FieldType Type = FieldType.Text,
    FieldSection Section = FieldSection.Location,
    bool Required = false,
    bool Secret = false,
    bool Hidden = false,
    string? Placeholder = null,
    string? Hint = null,
    string? Default = null,
    bool Mono = false,
    bool Half = false,
    [property: System.Text.Json.Serialization.JsonIgnore] DestinationColumn Column = DestinationColumn.None);

/// <summary>
/// Describes a destination type to the web client, which renders its add and edit forms from this. The server owns
/// the list, so a new type needs no web changes beyond, at most, a new icon.
/// </summary>
/// <param name="Icon">An icon name the web client knows (e.g. "folder", "cloud").</param>
/// <param name="SignIn">A browser sign-in the type uses instead of typed credentials ("dropbox"), or null.</param>
/// <param name="Notes">Shown under the fields when adding one.</param>
public sealed record DestinationDescriptor(
    DestinationKind Kind,
    string Label,
    string Icon,
    string NamePlaceholder,
    IReadOnlyList<DestinationField> Fields,
    string? SignIn = null,
    string? Notes = null)
{
    public DestinationField? Field(string key) => Fields.FirstOrDefault(f => f.Key == key);
}
