![DockerBackup](assets/brand/social-preview.png)

# DockerBackup

Scheduled backup and restore for everything Docker holds on a Windows 10/11 host: containers, compose stacks, volumes and bind mounts. It runs as a Windows Service and is managed from a web UI at **https://localhost:8484**.

> Status: **M1 (discovery)**. Live inventory of apps, containers, volumes, bind mounts and orphans with sizes and protection status. Backups (M2) come next. See [docs/DESIGN.md](docs/DESIGN.md).

## Install
Run `DockerBackup-<version>-x64.msi` (from CI artifacts or Releases). It installs the `DockerBackup` service (LocalSystem, delayed auto-start) and a Start-menu link to the UI. Data lives in `%ProgramData%\DockerBackup` (access restricted to SYSTEM and Administrators).

## Build
Requirements: .NET SDK 10.0.1xx, Node.js 24.

```powershell
./build.ps1 -Version 0.1.0      # web UI -> tests -> self-contained publish -> artifacts/*.msi
```

## Develop
```powershell
dotnet run --project src/DockerBackup.Service   # API on https://localhost:8484
cd web; npm run dev                              # UI on http://localhost:5173 (proxies /api)
dotnet test
```

## Stack
.NET 10 (ASP.NET Core, Windows Service) · Docker.DotNet.Enhanced · React 19 + Vite + Tailwind v4 + TanStack · WiX v5 · restic (from M2)

## Brand assets
Masters live in `assets/brand/` and are supplied by the owner; see `docs/imagery-requests.txt`. Run `python -I tools/generate-brand-assets.py` to regenerate the derived files (ICO, favicon, installer BMPs, web illustrations).
