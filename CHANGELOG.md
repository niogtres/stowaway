# Changelog

All notable changes to Stowaway are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

### Fixed

Since the internal 0.7.x builds:

- MySQL, MariaDB and MongoDB dumps failed with "syntax error: unexpected end of file" when built from a Windows checkout (CRLF line endings in the dump scripts).
- A backup sidecar running a database image (for example a `pg_dump` cron container on the `postgres` image) was treated as a database and its dump failed. Containers whose command is a script that never starts the server are no longer databases.
- The remote sign-in screen reset itself every 15 seconds, clearing what was typed.
- Dialogs with fields closed when clicking outside them, losing what was typed.

[Unreleased]: https://github.com/niogtres/stowaway/commits/main
