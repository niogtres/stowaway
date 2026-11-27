# DockerBackup — Design Proposal

Status: **v0.2 — accepted, decisions recorded in §10** · Date: 2026-11-27

## 1. Goal

A Windows 10/11 (and Windows Server) application that:

1. **Discovers** everything Docker holds that is worth protecting: containers (running and stopped), compose stacks, named, anonymous and orphaned volumes, bind mounts, and the definitions needed to recreate them.
2. **Backs it up on a schedule**, consistently, to local, network or cloud storage, with retention.
3. **Restores** it with confidence: a whole stack, one volume, a single file, or into a new volume side by side with the old one.
4. Is driven by a **polished web UI** served by a Windows Service that runs headless, even when nobody is logged in.

## 2. What the reference host taught us

The dev machine (Docker Desktop 29.5, Linux engine, `npipe:////./pipe/dockerDesktopLinuxEngine`) already shows every hard case:

| Observed | Implication for design |
|---|---|
| Compose stacks (`shop`, `blog`, `wiki`, …) next to standalone containers (`Jellyfin`, `Grafana`, …) | Group by compose project (labels); standalone containers are their own "app". |
| Anonymous volumes with hash names (Jellyfin `/config`, Gitea) | Show them by **owner container + mount path**, never by hash. |
| Volumes from deleted containers | Detect **orphaned** volumes and flag them ("unowned data"). |
| Bind mounts to `X:\Dev\...` and to whole drives `M:/`, `U:/` (media) | Bind mounts are backed up **opt-in, per mount**; root drives and very large paths are excluded by default, with a size estimate shown. |
| `postgres:17`, `mysql:8.0` with live data dirs | Copying the files of a running DB gives a torn backup. Needs **app-aware dumps** or stop/start. |
| `/var/run/docker.sock` bind (wiki-web) | Never back up sockets or devices; skip `tmpfs`. |
| Locally built images (`photos:local`, `shop-app`) | These can't be pulled again from a registry, so offer an optional `docker save` of the image. |
| Exited containers (`wiki-server`, `Gitea`) | Stopped containers' data is still protected; don't start them for a backup. |

## 3. Architecture

```
┌──────────────────────────── Windows host ─────────────────────────────┐
│                                                                        │
│  Browser ──HTTPS──►  DockerBackup Service  (Windows Service, .NET 10)  │
│  (React SPA)         ├─ Kestrel: REST API + SignalR + static SPA       │
│                      ├─ Discovery   (Docker API + events stream)       │
│                      ├─ Scheduler   (Cronos, plans in SQLite)          │
│                      ├─ Job engine  (plans → steps → runner)           │
│                      ├─ Store       (SQLite: config, history, catalog) │
│                      └─ Secrets     (DPAPI, LocalMachine scope)        │
│                              │ named pipe (Docker Engine API)          │
│                              ▼                                         │
│                      Docker Engine (Desktop/WSL2 or dockerd)           │
│                              │                                         │
│              ┌───────────────┴────────────────┐                        │
│              ▼                                ▼                        │
│   ephemeral "runner" container      docker exec into DB container      │
│   (restic + tar + zstd)             (pg_dump / mysqldump / …) ──stdout─┤
│   mounts target volumes :ro         piped into runner's restic --stdin │
│              │                                                         │
└──────────────┼─────────────────────────────────────────────────────────┘
               ▼
   restic repository: local disk · SMB/UNC · S3/MinIO · B2 · Azure · SFTP
```

### 3.1 Why one service process
A single self-contained process hosts the API, UI, scheduler and engine, so there's one thing to install, update, log and secure. The UI is a static SPA embedded in the service binary. A small optional **tray app** (phase 2) only shows status and opens the browser.

### 3.2 Backup engine: restic, run inside a runner container
- **restic** gives deduplication, encryption (AES-256), compression, many backends (native plus rclone), `forget/prune` retention, `check` integrity, and file-level restore. Its repository format is mature and well documented, so users are never locked in to us.
- Backups run in an **ephemeral runner container** (`ghcr.io/niogtres/dockerbackup-runner`, Alpine plus restic, a few MB). We mount the target volumes read-only at `/data/<container>/<mountpath>`. This approach:
  - works the same for named, anonymous and bind mounts, and on Docker Desktop and native dockerd
  - gives file-level snapshots, so the UI can browse and restore single files
  - needs no host access to `/var/lib/docker` (which doesn't exist on Windows with Desktop)
- Local Windows destinations (e.g. `D:\Backups`, `\\nas\docker`) are bind-mounted into the runner. Cloud backends are reached directly.
- **Fallback:** for a Windows-containers engine, run host-side `restic.exe` fed by the Docker archive API (phase 2).

### 3.3 Consistency strategies (per app, chosen automatically and overridable)
1. **App-aware dump** *(default for detected DBs)*: Postgres (`pg_dumpall`), MySQL/MariaDB (`mysqldump`/`mariadb-dump --single-transaction`), MongoDB (`mongodump --archive`), Redis (`BGSAVE` then copy), SQLite files (`.backup`). We run `docker exec` in the DB container, so the dump tool matches the server version, and stream its stdout into `restic backup --stdin`. Credentials come from the container env (`POSTGRES_USER`, `MYSQL_ROOT_PASSWORD`, …) or a user override.
2. **Stop → copy → start**: respects compose `depends_on` order and restores the previous state. Shows the expected downtime.
3. **Live copy**: for config-style volumes; fastest, no downtime.
4. **Hooks**: pre/post commands (exec in container or on host) for anything custom.

### 3.4 What a "backup" contains
For each app (compose project or standalone container), one restic snapshot tagged `app=<name>,plan=<id>,run=<id>` holding:
- `/data/...` volume and bind contents (per policy)
- `/dumps/...` DB dumps
- `/meta/` `docker inspect` JSON, network definitions, the compose file(s) and `.env` (found from `com.docker.compose.project.config_files` / `working_dir` labels), image digests
- `/images/*.tar` (optional) `docker save` of locally built images

This makes **"restore the whole stack on a fresh machine"** possible, not just "put files back".

### 3.5 Discovery
- Full scan on start, then live updates from `GET /events`, pushed to the UI over SignalR.
- Inventory model: **App** (compose project or standalone) → **Containers** → **Mounts** (volume / bind / tmpfs) → **Volumes**, plus **Orphan volumes**.
- Each item has a **protection status**: Protected · Stale (last success older than the plan interval × 2) · Failing · Unprotected · Excluded.
- Size estimates come from `docker system df -v` (volumes) and a background `du` in the runner (binds, throttled).
- Engines: auto-detect pipes (`docker_engine`, `dockerDesktopLinuxEngine`), with optional extra endpoints (TCP+TLS) so one install can manage several hosts later.

### 3.6 Scheduling, retention, verification
- **Plan** = selection (apps/volumes/tags, including *"all apps matching rule"* so new containers are picked up automatically) + strategy + destination + schedule + retention + notifications.
- Schedules: presets (hourly/daily/weekly) or cron, with a plain-language preview and "next 5 runs" shown in the UI. A missed run (e.g. the PC was off) is caught up once at startup, not once per missed occurrence.
- Retention: GFS (`keep-last`, `-hourly`, `-daily`, `-weekly`, `-monthly`, `-yearly`) applied with `restic forget --prune`, scheduled separately from backups.
- **Integrity:** weekly `restic check --read-data-subset=5%`.
- **Restore drills** *(differentiator)*: on a schedule, restore the latest snapshot into a scratch volume, start a throwaway DB container on dumps, run a probe query, then tear it down. The dashboard shows "last verified restorable: 2 days ago".
- Concurrency: one job per repository at a time, a global parallelism limit, and bandwidth/CPU limits.

### 3.7 Restore
- **Targets:** overwrite in place (a safety snapshot is taken first, automatically) · restore into a **new volume** · extract to a **host folder** · **recreate the stack** (compose file, volumes, images, `compose up`).
- **Granularity:** whole app → single volume → single file/folder (via restic `ls`/`restore --include`).
- **Flow:** pick a point in time (timeline slider), preview what will change (sizes, files, containers that will be stopped), dry-run, run with live progress, then post-restore health check (container healthcheck status).

### 3.8 Notifications
Email (SMTP), webhook (generic JSON), Teams/Slack/Discord, ntfy, and the Windows Event Log. Rules: on failure / on recovery / daily digest.

## 4. Technology stack

| Layer | Choice | Why |
|---|---|---|
| Service | **.NET 10 (LTS)** Worker + ASP.NET Core, `UseWindowsService()` | First-class Windows Service support, self-contained single-file publish, no runtime prerequisite, the SDK is already on the dev box |
| Docker API | **Docker.DotNet** (named pipe), thin wrapper of our own for gaps | Native npipe support, streams, events |
| Scheduler | **Cronos** (cron parsing) + own scheduler over the plans table | Cron, catch-up of missed runs, no second persistence model (see §10 #7) |
| Store | **SQLite** + EF Core | Zero-admin, in `%ProgramData%\DockerBackup` |
| Live updates | **SignalR** | Progress, logs, inventory changes |
| Logging | Serilog → rolling file + Event Log | Supportability |
| Backup | **restic** (pinned version) in runner image; `restic.exe` bundled for fallback | See §3.2 |
| UI | **React 19 + TypeScript + Vite**, **Tailwind v4 + shadcn/ui (Radix)**, TanStack Query/Router, Framer Motion, Recharts, cmdk | A modern, accessible, fast UI with a component system we own |
| Installer | **WiX v5 MSI** (service install, firewall, URL ACL, cert) + winget manifest later | Silent install / GPO friendly, clean upgrades |
| CI/CD | GitHub Actions (windows-latest): build, test, publish MSI + runner image to GHCR on tag | — |
| Tests | xUnit + Testcontainers (real Docker), Playwright for UI | Backup software must be tested against real engines |

## 5. UX concept — "calm confidence"

Dark-first with a light theme, glass surfaces over a subtle gradient, one accent colour, generous spacing, motion only for state changes. Every screen answers **"am I safe?"** at a glance.

- **Dashboard:** a large *Protection Score* ring (% of discovered data covered by a healthy plan); "Needs attention" cards (unprotected volume appeared, failing plan, unverified restore); a 14-day run heat-strip; storage growth sparkline per destination; next scheduled runs.
- **Inventory:** apps as cards (or a stack graph: app → containers → volumes) with status chips and sizes. Bulk select → "Protect with plan…". Orphans and excluded binds are clearly separated.
- **Plans:** a 4-step wizard (What → How → Where → When/Keep) with live summaries: "Backs up 6 apps · ~4.2 GB · ~3 min downtime for blog-db · daily 02:00 · keeps 7d/4w/12m".
- **Snapshots / Time Machine:** pick an app, scrub a timeline, browse files, compare two points (added/changed/removed), then restore from the context menu.
- **Restore wizard:** what → point in time → target → impact preview → run. Destructive steps need a typed confirmation.
- **Activity:** live job view with step timeline and streaming log; history with filters.
- **Destinations & Settings:** connection test buttons, repository stats, secrets never shown again after saving.
- Global **⌘K / Ctrl+K command palette** ("backup shop now", "restore blog-db…"), toasts, keyboard-first, WCAG AA.

## 6. Security

- Kestrel binds to **localhost only** by default over HTTPS (a per-install self-signed certificate is added to LocalMachine\Root by the installer). Remote access is opt-in: bind address + your own certificate.
- **Auth:** Windows Negotiate (members of local `Administrators` or a `DockerBackup Operators` group), plus optional local accounts with TOTP for remote use.
- Repository passwords and cloud keys are encrypted with **DPAPI in the service account's (LocalSystem's) user scope** in SQLite. *LocalMachine* scope is deliberately not used: any local user could decrypt it. An export of the repo key is offered at setup: *lose it and backups are unrecoverable*.
- The service runs as **LocalSystem** (it needs access to the Docker pipe). Every API call is audit-logged.
- The runner container gets read-only mounts for backup and no network except to the backend.

## 7. Known constraints and risks

| Risk | Mitigation |
|---|---|
| **Docker Desktop only runs while a user is logged in** | Detect "engine unavailable", mark runs *Missed* (not failed), retry when the engine returns. Docs recommend "Start Docker Desktop when you sign in" plus auto-logon, or native dockerd/Docker CE in WSL for servers. |
| Bind mounts to huge drives | Opt-in only, a size estimate first, per-mount exclude patterns. |
| DB credential discovery fails | Fall back to stop/copy/start and raise an "app-aware dump unavailable" warning with a credential override form. |
| Windows-containers engine | v1 targets Linux containers (the vast majority). The Windows engine path is phase 2 (§3.2 fallback). |
| restic repo lock left behind by a crash | Automatic `unlock` of stale locks older than N hours, owned by our host. |
| **"Docker" is a trademark of Docker, Inc.** Their brand guidelines restrict its use in product names and logos | Fine while private. Choose a distinct product name before any public release ("for Docker" phrasing is the usual safe form). Artwork must never use the whale or Docker logos. |

## 8. Repository layout

```
/src
  DockerBackup.Service/        ASP.NET Core host, API, SignalR, Windows Service
  DockerBackup.Core/           domain: inventory, plans, jobs, strategies
  DockerBackup.Docker/         Docker API wrapper, discovery, runner orchestration
  DockerBackup.Restic/         restic CLI wrapper, JSON output parsing
  DockerBackup.Data/           EF Core / SQLite
  DockerBackup.Tray/           (phase 2) tray app
/web                           React SPA (Vite) → embedded into Service at build
/runner                        Dockerfile for dockerbackup-runner image
/installer                     WiX project
/tests                         unit + integration (Testcontainers) + e2e (Playwright)
/docs                          design, ADRs, user guide
/.github/workflows             ci.yml, release.yml
```

## 9. Milestones

| # | Milestone | Outcome |
|---|---|---|
| M0 | Skeleton | Solution, CI, MSI installs a service that serves the UI shell over HTTPS |
| M1 | Discovery | Live inventory of apps, containers, volumes, binds, orphans; protection status |
| M2 | Backup core | Runner image, restic repos (local/SMB), live and stop/start strategies, manual "back up now" |
| M3 | Scheduling | Plans wizard, Quartz schedules, retention/prune, history, notifications |
| M4 | Restore | Volume / new volume / file-level / host folder; snapshot browser and timeline |
| M5 | App-aware and cloud | DB dumps, S3/B2/Azure/SFTP and **Dropbox**, stack recreate, restore drills |
| M6 | Polish and ship | Tray app, code signing, winget, remote access + TOTP, docs site |

### Implementation notes
- **M0 (done):** service host, HTTPS on loopback, engine monitor, UI shell, MSI, CI.
- **M1 (done):** discovery. Apps are grouped by compose project. Bind sources are mapped back to Windows paths (`/run/desktop/mnt/host/x/...` → `X:\...`). Sockets, tmpfs, system paths and whole drives are excluded by default. Volume sizes come from `/system/df` (raw HTTP over the pipe, because the client library lacks it); bind sizes come from a budgeted walk of the host filesystem. Engine events trigger debounced rescans, and changes are pushed to the UI over SignalR. "Locally built image" = no registry digest **or** Compose build labels: with the containerd image store, local builds have digests too. A request-origin guard protects hub and write endpoints against cross-site WebSocket and CSRF attacks.
- **M2 (done):** backup core.
  - **Runner:** `restic/restic:0.19.1` (pinned), in ephemeral containers labelled `dockerbackup.runner`. Leftovers are removed at startup. No network for local/SMB destinations, `no-new-privileges`, and a persistent `dockerbackup-restic-cache` volume.
  - **Repository password:** placed as a file inside the container before start (not in env, so never visible in `docker inspect`).
  - **SMB destinations:** a CIFS volume (`local` driver, `type=cifs`) created per run and removed afterwards, so share credentials only exist on the engine during a run. Commas in credentials are doubled for the CIFS option parser. *Only verified by unit tests so far: no SMB share was available on the dev host.*
  - **Snapshot layout:** `/data/<container>/<mount path>` plus `/data/_dockerbackup/` (app.json, run.json, container inspect JSON, compose files and `.env`). Tags: `dockerbackup`, `app=<id>`, `run=<id>`; host = machine name.
  - **Strategies:** *Live*, or *Stop & start*. Stop & start stops dependents first (from the `com.docker.compose.depends_on` label) and always restarts in dependency order, even on failure or cancel. Downtime is recorded per run.
  - **Queue:** one backup at a time. Interrupted runs are marked failed at startup.
  - **Persistence:** SQLite through EF Core with migrations (`dotnet ef migrations add <Name> --project src/DockerBackup.Data --startup-project src/DockerBackup.Data`).
- **M3 (done):** plans and protection.
  - **Plans:** *All apps* (including future apps, minus exclusions) or *Selected*, optionally with unowned volumes (each backed up as its own `volume:<name>` item). Strategy *Auto* (stop & start only for apps running a database), *Live* or *Stop & start*. Five-field cron in host local time.
  - **Retention:** a GFS policy applied after each scheduled backup with `restic forget`, filtered by `app=<id>,plan=<id>` tags, so manual snapshots and other plans are never touched. `--keep-last` is always at least 1.
  - **Scheduling:** the scheduler ticks every 30 s and catches up a missed occurrence once. While the engine is down, runs wait in the queue ("waiting for Docker") instead of failing. Scheduled items already pending are not queued twice.
  - **Maintenance:** weekly `prune` + `check --read-data-subset=5%` per destination, in the 01:00–06:00 window (any time once a day overdue), plus a manual button.
  - **Protection status:** Protected (last success within 2× the plan's longest gap between runs), Pending (new plan, not run yet), Stale, Failing (last scheduled run failed), Unprotected, Stateless (no data or empty). The score is the share of bytes in Protected items.
  - **Notifications:** email (MailKit), webhook (generic JSON or Slack-compatible) and ntfy, for failure / recovery / every success. Settings are DPAPI-protected, and secrets are never returned to the UI (blank on edit means keep).
- **M4 (done):** restore.
  - **Snapshot browser:** apps → timeline → snapshot. Sources are recomputed from the snapshot's own `app.json` with `BackupPlanner.Layout`, the same function backups use, so paths always line up. Directory browsing uses non-recursive `restic ls`. Contents and listings are cached (snapshots are immutable).
  - **Targets:** in place, a new volume, or a folder on this PC.
  - **Scope:** a whole snapshot, one source, or one file or folder (`restic restore <id>:<source> --include <path>`).
  - **Full in-place restores** use `--delete`, so the data matches the snapshot exactly.
  - **Safety:** in-place restores stop every running container that uses the data (from any app) and always restart them. They first take a **safety snapshot** (tag `safety`; retention never touches it), which is the undo point.
  - **Preview and confirmation:** the impact is shown before anything changes, and an in-place restore requires typing the app's name.
  - **Execution:** restores run in the same queue as backups, so they never race a backup on the same repository.
- **M5a (done):** database dumps and cloud destinations.
  - **Runner image:** `dockerbackup/runner:<hash>`, built locally on first use as `FROM restic/restic:<pin>` plus `rclone` and `socat` from Alpine. No registry publishing is needed. If the build fails, it falls back to the plain restic image (everything works except Dropbox).
  - **Secrets in runners:** all restic and cloud variables are written to `/run/dockerbackup/env` inside the container before it starts, then sourced by an `sh` entrypoint wrapper. Nothing secret is in the container config, so nothing shows in `docker inspect`.
  - **Dumps:**
    - **Engines:** Postgres (`pg_dumpall`), MySQL/MariaDB (`mysqldump`/`mariadb-dump`, all databases, single transaction), MongoDB (`mongodump --archive`) and Redis (`redis-cli --rdb -`). Credentials come from the official images' environment variables.
    - **How it runs:** inside the database's own container via `exec`. Its stdout streams through the service into a "sink" runner (`cat > /dumps/<name>`) on a temporary volume. That volume is backed up at `/data/_dumps` and removed afterwards.
    - **Failures:** a failed dump fails the run (so you're notified), but the files are still copied live.
    - **Automatic strategy:** "Automatic" now means live copy plus dumps, and only stops apps running a database that can't be dumped.
  - **Cloud destinations:**
    - **S3/compatible:** `s3:<endpoint>/<bucket>/<prefix>` with `AWS_*` variables.
    - **B2:** `b2:` with `B2_ACCOUNT_*`.
    - **Azure:** `azure:` with `AZURE_ACCOUNT_*`.
    - **SFTP:** key auth. Host keys are pinned at setup with `ssh-keyscan` and checked with `StrictHostKeyChecking=yes`, so a changed server identity is refused.
    - **Dropbox:** via rclone (`rclone:dropbox:` with an `RCLONE_CONFIG_DROPBOX_*` env-defined remote).
    - **Network:** cloud runners have network access; local and SMB runners stay offline.
  - **Dropbox sign-in:** `rclone authorize dropbox` runs in a runner. Its loopback listener is bridged by socat to the host's `127.0.0.1:53682`, which is the redirect URI registered for rclone's Dropbox app. The token goes straight from the session to destination creation and never reaches the UI.
- **M5b (done):** recreate and restore drills.
  - **Recreate app:**
    - **Data:** an in-place restore of the data runs first.
    - **Containers:** every container of the app that no longer exists is rebuilt from the `docker inspect` JSON saved in the snapshot. Config and HostConfig are carried over unchanged. Runtime fields (ids, IPs, the generated hostname, short-id aliases) are dropped.
    - **Networks and images:** missing user networks are recreated, with compose labels so `docker compose` adopts them. Images are pulled if absent.
    - **Order:** containers start in dependency order, and only if they were running at backup time.
    - **Local images:** a locally built image that's missing blocks the recreate, with instructions to rebuild it from the saved compose files. The snapshot's "App definition" source (compose files, `.env`, container settings) can be restored to a folder for that.
  - **Restore drills (`Drill` runs):** the real app is never touched.
    - **Data sources:** restored into scratch volumes with `restic restore --verify`. Sources over 5 GB are skipped; the weekly `check` still samples them.
    - **Dumps:** each dump is loaded into a throwaway, network-less container of the same image. Readiness waits for the real TCP listener (the official images run a socket-only init server first). A probe query (databases or keys count) must answer.
    - **Scheduling:** weekly per plan item (plan option `VerifyRestores`, default on for new plans), in the night window, and only when there's a newer snapshot to verify. Also available on demand ("Verify restore").
    - **Results:** a failed drill fails the run and alerts. The snapshot shows "Verified restorable …".
- **M5 (planned) Dropbox:** restic has no native Dropbox backend, but it supports rclone (`-r rclone:<remote>:<path>`). The plan:
  - Ship a runner image that bundles rclone next to restic.
  - Add a "Connect Dropbox" OAuth flow in the UI. Its token is stored DPAPI-protected and passed to rclone as an environment-based remote config (`RCLONE_CONFIG_DROPBOX_TYPE=dropbox`, `RCLONE_CONFIG_DROPBOX_TOKEN=…`), never written to disk.
  - Cloud destinations need the runner to have network access (local and SMB runners stay offline).

M0–M3 is a usable MVP ("set it and forget it backups"). M4 makes it trustworthy, and M5 makes it best in class.

## 10. Decisions (2026-11-27)

| # | Topic | Decision |
|---|---|---|
| 1 | Repo name | Renamed to `niogtres/docker_backup` (private). |
| 2 | License | Closed source for now. No license file. |
| 3 | Backup engine | **restic** (BSD-2-Clause, bundled). The service already owns scheduling, policy and UI, and restic is a minimal, scriptable engine with JSON output, `--stdin` support for DB dumps and a stable repo format. Kopia would duplicate our UI/server/policy layers. All engine calls go through an `IBackupEngine` abstraction so it can be swapped later. |
| 4 | Container platforms | v1: Linux containers only. Windows-containers engine in phase 2. |
| 5 | Remote access | Localhost only for the MVP. |
| 7 | Scheduler | **Cronos + own scheduler** instead of Quartz.NET (decided during M3). Plans already live in our SQLite database. Quartz's persistent store would add a second persistence model with about a dozen tables, and the behaviour we need is small: compute the next run, catch up once, wait for the engine. |
| 6 | Multi-host | Planned for later. v1 manages one host, but the data model carries an `EngineEndpoint` id from day one so adding hosts later needs no migration. |
