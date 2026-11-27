# Changelog

All notable changes to Stowaway are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/niogtres/stowaway/compare/v0.8.0...HEAD
[0.8.0]: https://github.com/niogtres/stowaway/releases/tag/v0.8.0
