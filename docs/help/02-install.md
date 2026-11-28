# Install

1. Run `Stowaway-<version>-x64.msi` and follow the steps. If Windows says **Windows protected your PC**, see [Unsigned installer](#unsigned-installer) below.
2. The installer adds the **Stowaway** service (it starts automatically with Windows), a Start-menu shortcut, and a tray icon. The tray icon starts when you click **Finish**, and for every user when they sign in.
3. Open **Stowaway** from the Start menu. The first visit may take a few seconds while it looks at what Docker is running.

Everything Stowaway stores (settings, history, logs, certificates and the keys to your backups) lives in `C:\ProgramData\Stowaway`. Only administrators and the service can open that folder.

## Unsigned installer

Stowaway's installer isn't code-signed yet, so Windows SmartScreen may stop it with **Windows protected your PC**. Choose **More info**, check that the app is `Stowaway-<version>-x64.msi`, then choose **Run anyway**.

To make sure the file is the one that was released, compare its checksum with the `.sha256` file next to it on the [Releases](https://github.com/niogtres/stowaway/releases) page. In PowerShell:

```powershell
(Get-FileHash .\Stowaway-<version>-x64.msi -Algorithm SHA256).Hash
Get-Content .\Stowaway-<version>-x64.msi.sha256
```

The first value must match the start of the `.sha256` line (letter case doesn't matter). If they don't, delete the file and download it again.

**See also:** [Signing in](03-signing-in.md), [Destinations](04-destinations.md), [Backup plans](05-plans.md)
