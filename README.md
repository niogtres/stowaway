# DockerBackup

Scheduled backup and restore for everything Docker holds on a Windows 10/11 host: containers, compose stacks, volumes and bind mounts. It runs as a Windows Service and is managed from a web UI.

> Status: **design phase**. See [docs/DESIGN.md](docs/DESIGN.md).

## Planned stack
- Windows Service: .NET 10 (ASP.NET Core, Quartz.NET, SQLite, Docker.DotNet)
- Backup engine: restic, run in an ephemeral runner container
- Web UI: React 19 + TypeScript + Vite + Tailwind + shadcn/ui
- Installer: WiX MSI
