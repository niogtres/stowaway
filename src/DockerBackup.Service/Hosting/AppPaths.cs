using System.Security.AccessControl;
using System.Security.Principal;

namespace DockerBackup.Service.Hosting;

/// <summary>Well-known locations under the data directory.</summary>
public sealed class AppPaths
{
    private AppPaths(string root)
    {
        Root = root;
        Logs = Path.Combine(root, "logs");
        Certificates = Path.Combine(root, "certs");
    }

    public string Root { get; }
    public string Logs { get; }
    public string Certificates { get; }

    public static AppPaths Resolve(ServiceOptions options, string contentRoot)
    {
        var root = string.IsNullOrWhiteSpace(options.DataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DockerBackup")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.DataDirectory), contentRoot);
        return new AppPaths(root);
    }

    /// <summary>
    /// Creates the directories. When <paramref name="restrictAccess"/> is set (running as the service),
    /// the root is locked down to SYSTEM and Administrators because it will hold secrets and keys.
    /// </summary>
    public void EnsureCreated(bool restrictAccess)
    {
        var root = Directory.CreateDirectory(Root);
        if (restrictAccess)
        {
            RestrictToSystemAndAdministrators(root);
        }

        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Certificates);
    }

    private static void RestrictToSystemAndAdministrators(DirectoryInfo dir)
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        dir.SetAccessControl(security);
    }
}
