# Install

1. Run `Stowaway-<version>-x64.msi` and follow the steps.
2. The installer adds the **Stowaway** service (it starts automatically with Windows), a Start-menu shortcut, and a tray icon. The tray icon starts when you click **Finish**, and for every user when they sign in.
3. Open **Stowaway** from the Start menu. The first visit may take a few seconds while it looks at what Docker is running.

Everything Stowaway stores (settings, history, logs, certificates and the keys to your backups) lives in `C:\ProgramData\Stowaway`. Only administrators and the service can open that folder.

**See also:** [Signing in](03-signing-in.md), [Destinations](04-destinations.md), [Backup plans](05-plans.md)
