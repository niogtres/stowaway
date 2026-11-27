namespace Stowaway.Service.Hosting;

public sealed class ServiceOptions
{
    public const string SectionName = "Stowaway";
    public const string ServiceName = "Stowaway";
    public const int DefaultPort = 8484;

    /// <summary>HTTPS port the UI/API listens on (loopback only).</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>Root for config, database, logs and certificates. Defaults to %ProgramData%\Stowaway.</summary>
    public string? DataDirectory { get; set; }
}
