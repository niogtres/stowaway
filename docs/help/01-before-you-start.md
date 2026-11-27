# Before you start

- **Windows:** 64-bit Windows 10 or 11, or Windows Server 2022 or later. Stowaway is a Windows program, so the computer it runs on must be Windows.
- **Docker with Linux containers:** Docker Desktop (what Stowaway is tested with), or Docker Engine inside WSL 2 (for example on Windows Server, where Docker Desktop isn't supported). For an engine Stowaway doesn't find by itself, set the system environment variable `Stowaway__Docker__Endpoint` (or `DOCKER_HOST`) to its address, such as `tcp://localhost:2375`, and restart the Stowaway service. Windows containers aren't supported.
- **What Stowaway backs up:** your apps. That means their Docker volumes, consistent dumps of their databases, and folders inside a compose app's project folder (such as `./data` or `./config`). Host folders mapped in from elsewhere, whole drives and network shares are **not** included: they live outside the app, so back them up with your normal file backup (File History, your NAS's backup…). Inventory marks them *not included*.
- **Docker Desktop only runs while someone is signed in to Windows.** Backups that are due while Docker is stopped wait, and run once Docker is back. On a server, turn on *Start Docker Desktop when you sign in* and set up automatic sign-in, or use Docker Engine in WSL.
- **Somewhere to keep backups:** a second disk, a network share, or cloud storage (see [section 4](04-destinations.md)). A backup on the same disk as the data doesn't protect you from that disk failing.
- **An administrator account** to install it.

**See also:** [Install](02-install.md), [Destinations](04-destinations.md)
