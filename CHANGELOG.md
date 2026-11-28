# Changelog

All notable changes to Stowaway are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **Include** a host folder that's mapped into a container from outside the app (for example a configuration folder), so it's backed up with the app. Drives, system folders and network shares stay excluded.
- Databases inside containers are sized by their last backup; until then the app shows *size unknown* instead of 0 B.
- **Business Central** containers are recognized (version and localization on the card). Their own `my` folder (setup scripts, certificates) is backed up by default, and every snapshot includes a script that recreates the container with `New-BcContainer` and loads the backed-up database. *Recreate app* warns that it would start with a new database. New, and not yet tested on a second machine: please report how it goes.
- A guide for contributors who want to add a database or destination type, or an app profile like Business Central: `docs/PROVIDERS.md`.

### Fixed

- Installing a new version could take several minutes: the bundled restic and rclone (112 MB) were packed inside the service and unpacked to a temporary folder on each new version's first start, for the service and for every user's tray icon, and old copies were never removed. They're now installed as files in `tools` next to the service.
- Windows containers whose only data is a SQL Server database inside the container showed as *Stateless*, and a backup plan skipped them. They now count as data, also when stopped.
- Stopped containers showed as *Unhealthy*: Docker keeps the last health check after a container stops. Health is now shown only for running containers.
- The protection score stayed on *Measuring your data…* when no size could be measured (databases inside containers). It now counts items when sizes are unknown, and says so when there is nothing to back up.

### Documentation

- The install guide explains the SmartScreen warning for the unsigned installer and how to check the download's checksum.

## [0.12.0] - 2026-11-28

### Changed
- **Destination forms:** the Add and Edit destination windows are drawn from the server's description of each destination type, so new types need no form changes. The fields and checks are the same; the SFTP fields now sit in two columns.
- **Editing a destination** re-checks the backups only when its location or credentials actually change. Saving the same folder again no longer does.
- **Docs:** the design notes, README, contributing guide and help were brought up to date. `CONTRIBUTING.md` and `SECURITY.md` moved to `.github/`.

### Fixed
- **Unowned volumes** no longer lists `dockerbackup-restic-cache`, Stowaway's own restic cache from before the project was renamed. The service removes it at startup.

## [0.11.1] - 2026-11-27

### Changed
- **Dropbox from another computer:** the Add destination window now shows the three steps (allow in Dropbox, copy the address of the page that can't load, paste it and choose Finish) before you start, with the current step highlighted.

### Fixed
- **"No access" for some administrators:** membership of the local Administrators group is now read without resolving every member's name. One member that couldn't be looked up, such as *Domain Admins* while a domain PC is off the corporate network, or an Entra ID entry, made the old lookup refuse everyone. Membership through a domain or Entra ID group now counts too, and a refusal is logged with the reason.
- **Empty terminal window** after setup and at sign-in: the notification-area icon opened a console window, and closing it also closed the icon. Stowaway now runs without a console window.

## [0.11.0] - 2026-11-27

### Added
- **Edit destinations:** rename a destination, replace its password or keys, reconnect Dropbox, or follow backups you have moved to another folder. A new location is only saved if it holds the same backups.
- **Dropbox:** a new Dropbox destination can reuse the sign-in of one you already have, with a different folder.
- **Dropbox from another computer:** signing in through remote access now works. Paste the address the browser ends on and Stowaway finishes the sign-in.

### Changed
- **Dropbox:** backups always go under `/Stowaway`. The folder field is now an optional subfolder of it. Existing destinations keep their folder until you change their location.

### Fixed
- **Authenticator code boxes** (confirming a change remotely, connecting an authenticator) now put the cursor in the code field, and again after a wrong code. Other dialogs' first field gets the cursor too: opening a dialog used to move it to the close button.
- **Removing a destination** that a backup plan uses showed "An error occurred while processing your request". It now names the plans to change first.

## [0.10.1] - 2026-11-27

### Fixed
- **Dropbox:** a new Dropbox destination with no folder now goes in a folder named `Stowaway` instead of the root of your Dropbox (the field only showed "Stowaway" as a hint). Existing destinations are unchanged. The field now explains that Dropbox shows the encrypted backup data (`data`, `index`, `keys`, `snapshots`), not your files.

## [0.10.0] - 2026-11-27

### Added

- **Issue forms** on GitHub for bug reports and feature requests.
- **SQL Server in Windows containers** (Business Central, for example): while the container runs, each database is backed up with SQL Server's own copy-only `BACKUP DATABASE`; while it's stopped, its database files are copied out without starting it. Restore drills restore the databases into a temporary container from the same image and run `DBCC CHECKDB`. SQL logins can be set under **Login** when Windows authentication isn't enough.

## [0.9.0] - 2026-11-27

### Added

- **Windows containers:** Docker engines running Windows containers are supported. restic and rclone for Windows ship with Stowaway and run on the computer itself; while containers run, backups read through a Windows volume shadow copy (VSS), so files a container holds open are captured. Volumes are backed up; files a container writes inside itself aren't (the app card says so).

### Changed

- The sidebar no longer shows the signed-in user name (it can be an email address).
- Help: *Signing in* now also covers signing in from another computer with a Stowaway account.

### Fixed

- An app stayed **Failing** after a successful *Back up now* that followed a failed scheduled backup. Any later successful backup now clears it.

## [0.8.0] - 2026-11-27

The first public release.

### Added

- **Discovery:** every container, compose stack, volume and bind mount, with sizes, updated live as Docker changes. Compose projects are grouped into apps; anonymous volumes are shown by the container and path that use them; sockets, tmpfs and whole drives are left out unless included.
- **Backups** with [restic](https://restic.net) in throwaway runner containers: encrypted, deduplicated and compressed, with retention, weekly pruning and integrity checks.
- **Destinations:** a local folder, a network share (SMB), S3-compatible storage, Backblaze B2, Azure Blob, SFTP (with pinned host keys) and Dropbox. Each has its own recovery key.
- **Consistent databases without downtime:** PostgreSQL, MySQL/MariaDB, MongoDB and Redis are dumped with their own tools while they run. Strategies: Automatic, Live, and Stop & start (in compose dependency order).
- **Plans:** schedules with catch-up after missed runs, retention, all apps or selected apps, protection status for every app.
- **Restore:** whole sources or single files and folders, to where they were (with a safety snapshot to undo), to a new volume, or to a folder.
- **Recreate an app** after a disaster: data, networks, images and containers rebuilt from the snapshot.
- **Restore drills:** weekly proof that backups restore, into scratch space and throwaway database containers.
- **Notifications** by email, webhook (generic or Slack-compatible) and ntfy; warnings in the Windows Event Log.
- **Windows sign-in** for local administrators and members of the "Stowaway Operators" group.
- **Remote access** (off by default): accounts with password and authenticator app, recovery codes, a fresh code for destructive changes, lockouts, rate limits, an audit log and alerts. Works on the LAN or over the internet behind a reverse proxy such as nginx and Cloudflare.
- **Tray icon** with protection status and failure alerts.
- **Help** built into the web interface (the user guide as searchable topics) and an **About** page with license and third-party notices.
- **Installer** (MSI) with optional Authenticode signing, and winget manifests with each release.
- **Database logins:** when a dump can't log in with the container's own settings, set user and password (and optionally one database) per database in Inventory, with a test button. Passwords in Docker secret files (`*_FILE`) are found automatically.

### Changed

- **Stowaway backs up apps, not host files:** volumes, database dumps, and folders inside a compose app's project folder. Folders mapped in from elsewhere, whole drives and network shares are shown as *not included* (with the reason) and are no longer measured or backed up.
- Stowaway's own temporary containers and volumes (backup runners, dump and drill scratch space, the restic cache) no longer appear in the inventory.
- The installer starts the tray icon when you click Finish, and the tray closes and restarts cleanly during upgrades.

### Notes

- Runs on 64-bit Windows 10/11 and Windows Server 2022+, with a Linux-container Docker engine: tested with Docker Desktop on Windows 11; Docker Engine in WSL 2 and other Docker-compatible engines should work but are untested. Windows containers aren't supported.

### Fixed

Since the internal 0.7.x builds:

- MySQL, MariaDB and MongoDB dumps failed with "syntax error: unexpected end of file" when built from a Windows checkout (CRLF line endings in the dump scripts).
- A backup sidecar running a database image (for example a `pg_dump` cron container on the `postgres` image) was treated as a database and its dump failed. Containers whose command is a script that never starts the server are no longer databases.
- The remote sign-in screen reset itself every 15 seconds, clearing what was typed.
- Dialogs with fields closed when clicking outside them, losing what was typed.
- A database dump could hang the backup when Docker didn't pass on the end of the dump stream, and could fail with "No such exec instance" when Docker removed a finished command before its exit code was read.

[Unreleased]: https://github.com/niogtres/stowaway/compare/v0.12.0...HEAD
[0.12.0]: https://github.com/niogtres/stowaway/releases/tag/v0.12.0
[0.11.1]: https://github.com/niogtres/stowaway/releases/tag/v0.11.1
[0.11.0]: https://github.com/niogtres/stowaway/releases/tag/v0.11.0
[0.10.1]: https://github.com/niogtres/stowaway/releases/tag/v0.10.1
[0.10.0]: https://github.com/niogtres/stowaway/releases/tag/v0.10.0
[0.9.0]: https://github.com/niogtres/stowaway/releases/tag/v0.9.0
[0.8.0]: https://github.com/niogtres/stowaway/releases/tag/v0.8.0
