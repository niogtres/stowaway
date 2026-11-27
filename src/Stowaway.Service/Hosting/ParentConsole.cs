using System.Runtime.InteropServices;

namespace Stowaway.Service.Hosting;

/// <summary>
/// The executable is a Windows (GUI) app so the tray never opens a console window. When the service is started by
/// hand from a terminal (development, troubleshooting), this attaches to that terminal so its log output shows there.
/// </summary>
internal static class ParentConsole
{
    private const int AttachParentProcess = -1;

    public static void Attach() => _ = AttachConsole(AttachParentProcess);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
