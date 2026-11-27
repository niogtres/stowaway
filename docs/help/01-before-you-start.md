# Before you start

- **Docker:** Docker Desktop (Linux containers) or Docker Engine in WSL. Windows containers aren't supported yet.
- **What Stowaway backs up:** your apps. That means their Docker volumes, consistent dumps of their databases, and folders inside a compose app's project folder (such as `./data` or `./config`). Host folders mapped in from elsewhere, whole drives and network shares are **not** included: they live outside the app, so back them up with your normal file backup (File History, your NAS's backup…). Inventory marks them *not included*.
- **Docker Desktop only runs while someone is signed in to Windows.** Backups that are due while Docker is stopped wait, and run once Docker is back. On a server, turn on *Start Docker Desktop when you sign in* and set up automatic sign-in, or use Docker Engine in WSL.
- **Somewhere to keep backups:** a second disk, a network share, or cloud storage (see [section 4](04-destinations.md)). A backup on the same disk as the data doesn't protect you from that disk failing.
- **An administrator account** to install it.

**See also:** [Install](02-install.md), [Destinations](04-destinations.md)
