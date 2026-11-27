# Stowaway — Design

Status: **v0.3: M0–M5 implemented; M6 in progress.** Decisions are in §10. Sections 3–8 describe the system as built; the per-milestone notes in §9 have the details. · Updated: 2026-11-27

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
│  Browser ──HTTPS──►  Stowaway Service  (Windows Service, .NET 10)  │
│  (React SPA)         ├─ Kestrel: REST API + SignalR + static SPA       │
│                      ├─ Discovery   (Docker API + events stream)       │
│                      ├─ Scheduler   (Cronos, plans in SQLite)          │
│                      ├─ Job engine  (plans → steps → runner)           │
│                      ├─ Store       (SQLite: config, history, catalog) │
│                      └─ Secrets     (DPAPI, service-account scope)     │
│                              │ named pipe (Docker Engine API)          │
│                              ▼                                         │
│                      Docker Engine (Desktop/WSL2 or dockerd)           │
│                              │                                         │
│              ┌───────────────┴────────────────┐                        │
│              ▼                                ▼                        │
│   ephemeral "runner" container      docker exec into DB container      │
│   (restic + rclone, local image)    (pg_dumpall / mysqldump / …)       │
│   mounts target volumes :ro         stdout → sink runner → dump volume │
│              │                                                         │
└──────────────┼─────────────────────────────────────────────────────────┘
               ▼
   restic repository: local disk · SMB/UNC · S3-compatible · B2 · Azure · SFTP · Dropbox (rclone)
```

### 3.1 Why one service process
A single self-contained process hosts the API, UI, scheduler and engine, so there's one thing to install, update, log and secure. The UI is a static SPA embedded in the service binary. The **tray app** is the same executable started with `--tray` at each user's logon: it only shows status and opens the browser.

### 3.2 Backup engine: restic, run inside a runner container
- **restic** gives deduplication, encryption (AES-256), compression, many backends (native plus rclone), `forget/prune` retention, `check` integrity, and file-level restore. Its repository format is mature and well documented, so users are never locked in to us.
- Backups run in an **ephemeral runner container** (`stowaway/runner:<hash>`, built locally on first use from the pinned `restic/restic` image plus rclone and socat; see M5a). We mount the target volumes read-only at `/data/<container>/<mountpath>`. This approach:
  - works the same for named, anonymous and bind mounts, and on Docker Desktop and native dockerd
  - gives file-level snapshots, so the UI can browse and restore single files
  - needs no host access to `/var/lib/docker` (which doesn't exist on Windows with Desktop)
- Local Windows destinations (e.g. `D:\Backups`, `\\nas\docker`) are bind-mounted into the runner. Cloud backends are reached directly.
- **Fallback:** for a Windows-containers engine, run host-side `restic.exe` fed by the Docker archive API (phase 2).

### 3.3 Consistency strategies (per app, chosen automatically and overridable)
1. **App-aware dump** *(default for detected DBs)*: Postgres (`pg_dumpall`), MySQL/MariaDB (`mysqldump`/`mariadb-dump --single-transaction`), MongoDB (`mongodump --archive`), Redis (`redis-cli --rdb -`). We run `docker exec` in the DB container, so the dump tool matches the server version, and stream its stdout into a temporary volume that is backed up with the data. Credentials come from the container env (`POSTGRES_USER`, `MYSQL_ROOT_PASSWORD`, …). *Not built yet:* SQLite `.backup` and a per-app credential override form.
2. **Stop → copy → start**: respects compose `depends_on` order and restores the previous state. Shows the expected downtime.
3. **Live copy**: for config-style volumes; fastest, no downtime.
4. **Hooks** *(not built yet)*: pre/post commands (exec in container or on host) for anything custom.

### 3.4 What a "backup" contains
For each app (compose project or standalone container), one restic snapshot tagged `stowaway`, `app=<id>`, `run=<id>` (and `plan=<id>` for scheduled runs) holding:
- `/data/<container>/<mount path>`: volume and bind contents (per policy)
- `/data/_dumps/`: database dumps
- `/data/_stowaway/`: `app.json`, `run.json`, `docker inspect` JSON per container, the compose file(s) and `.env` (found from the `com.docker.compose.project.config_files` / `working_dir` labels)
- *Not built yet:* `docker save` of locally built images. Today the recreate tells you to rebuild them from the saved compose files.

This makes **"restore the whole stack on a fresh machine"** possible, not just "put files back".

### 3.5 Discovery
- Full scan on start, then live updates from `GET /events`, pushed to the UI over SignalR.
- Inventory model: **App** (compose project or standalone) → **Containers** → **Mounts** (volume / bind / tmpfs) → **Volumes**, plus **Orphan volumes**.
- Each item has a **protection status**: Protected · Pending (new plan, not run yet) · Stale (last success older than 2× the plan's longest gap) · Failing · Unprotected · Stateless (no data).
- Size estimates come from the engine's `/system/df` (volumes) and a budgeted walk of the Windows filesystem (bind mounts).
- Engines: auto-detect pipes (`docker_engine`, `dockerDesktopLinuxEngine`) or an explicit endpoint. Managing several hosts is future work (§10 #6).

### 3.6 Scheduling, retention, verification
- **Plan** = selection (all apps, including future ones, minus exclusions; or selected apps; optionally unowned volumes) + strategy + destination + schedule + retention + weekly restore drills. Notifications are global channels.
- Schedules: presets (hourly/daily/weekly) or cron, with a plain-language preview and "next 5 runs" shown in the UI. A missed run (e.g. the PC was off) is caught up once at startup, not once per missed occurrence.
- Retention: GFS (`keep-last`, `-hourly`, `-daily`, `-weekly`, `-monthly`, `-yearly`) applied with `restic forget` after each scheduled backup; space is reclaimed by the weekly `prune`.
- **Integrity:** weekly `restic check --read-data-subset=5%`.
- **Restore drills** *(differentiator)*: on a schedule, restore the latest snapshot into a scratch volume, start a throwaway DB container on dumps, run a probe query, then tear it down. The dashboard shows "last verified restorable: 2 days ago".
- Concurrency: one job at a time in a single queue (backups, restores, maintenance, drills). *Not built yet:* bandwidth/CPU limits.

### 3.7 Restore
- **Targets:** overwrite in place (a safety snapshot is taken first by default) · restore into a **new volume** · extract to a **host folder** · **recreate the app** (data in place, then missing containers rebuilt from their saved definitions; see M5b).
- **Granularity:** whole app → single volume → single file/folder (via restic `ls`/`restore --include`).
- **Flow:** pick a point in time (timeline), preview what will change (sources, containers that will be stopped or recreated, deleted extra files), type the app's name for in-place restores, then run with live progress. *Not built yet:* snapshot comparison and a post-restore health check.

### 3.8 Notifications
Email (SMTP), webhook (generic JSON or Slack-compatible, which also covers Discord and Mattermost) and ntfy. Rules: on failure / on recovery / on every success. *Planned for M6:* the Windows Event Log. *Not built yet:* a daily digest.

## 4. Technology stack

| Layer | Choice | Why |
|---|---|---|
| Service | **.NET 10 (LTS)** Worker + ASP.NET Core, `UseWindowsService()` | First-class Windows Service support, self-contained single-file publish, no runtime prerequisite, the SDK is already on the dev box |
| Docker API | **Docker.DotNet.Enhanced** (named pipe), thin wrapper of our own for gaps (`/system/df`) | Native npipe support, streams, events |
| Scheduler | **Cronos** (cron parsing) + own scheduler over the plans table | Cron, catch-up of missed runs, no second persistence model (see §10 #7) |
| Store | **SQLite** + EF Core | Zero-admin, in `%ProgramData%\Stowaway` |
| Live updates | **SignalR** | Progress, logs, inventory changes |
| Logging | Serilog → rolling file (Event Log planned for M6) | Supportability |
| Backup | **restic** (pinned) + **rclone** in a locally built runner image | See §3.2 |
| UI | **React 19 + TypeScript + Vite**, **Tailwind v4**, own small component set, TanStack Query/Router, Motion, SignalR client | A modern, accessible, fast UI with a component system we own |
| Installer | **WiX v5 MSI** (service install, wizard UI, Start-menu link; the service creates and trusts its own certificate) + winget manifest later | Silent install / GPO friendly, clean upgrades |
| CI/CD | GitHub Actions (windows-latest): build, test, MSI artifact; GitHub release with the MSI on `vX.Y.Z` tags | — |
| Tests | xUnit v3, including real-engine integration tests (skipped on engines without Linux containers); Playwright drives the UI during development | Backup software must be tested against real engines |

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

- Kestrel binds to **localhost only** over HTTPS. A per-install self-signed certificate is created by the service and added to LocalMachine\Root when it runs as a service. Host filtering blocks DNS rebinding; a request-origin guard blocks cross-site WebSocket and CSRF attempts. *M6b:* opt-in remote access (off by default), with a self-issued or an installed certificate; see the M6b notes.
- **Auth** *(M6a)*: Windows Negotiate (members of local `Administrators` or a `Stowaway Operators` group). *M6b:* remote-access accounts (password + TOTP) for other computers. Managing remote access, restarting the service and revealing recovery keys stay local-only.
- Repository passwords and cloud keys are encrypted with **DPAPI in the service account's (LocalSystem's) user scope** in SQLite. *LocalMachine* scope is deliberately not used: any local user could decrypt it. Each destination's recovery key can be revealed (and the reveal is logged): *lose it and backups are unrecoverable*.
- The service runs as **LocalSystem** (it needs access to the Docker pipe). *M6c:* every API change and every remote sign-in event is in the audit log.
- Runner containers get read-only source mounts, `no-new-privileges`, and no network for local and SMB destinations. Secrets arrive as files inside the container, never in its configuration.

## 7. Known constraints and risks

| Risk | Mitigation |
|---|---|
| **Docker Desktop only runs while a user is logged in** | Detect "engine unavailable"; queued runs wait ("waiting for Docker") until the engine returns, and missed schedules are caught up once. Docs recommend "Start Docker Desktop when you sign in" plus auto-logon, or native dockerd/Docker CE in WSL for servers. |
| Bind mounts to huge drives, shares, folders outside the app | **Decided (2026-11-27): not Stowaway's job.** Stowaway backs up an app: its volumes, database dumps, and folders inside its compose project folder. Drives, network shares (bind or CIFS/NFS volume) and any other host folder, including those of standalone containers, are never included, not measured, and can't be switched on; the UI says so and points to a file backup. |
| DB credential discovery fails | Discovery reads the official images' variables and their `*_FILE` (Docker secrets) forms. When that fails (password changed after setup, other images), a **Login** form per database container saves user, password and optionally one database (DPAPI), passed to the dump as exec environment (`STOWAWAY_DB_*`), with a **Test** button. A failed dump that looks like a login problem points there. |
| Windows-containers engine | Supported since M7a: host-side restic (shipped with Stowaway) with VSS while containers run. Data inside a container's own layer isn't backed up (warning in the UI), except SQL Server databases (M7b). |
| restic repo lock left behind by a crash | A run that hits a lock checks who holds it. Locks held only by our own runners (hostname `stowaway-*`) are stale by construction, since runs on one repository are serialised, so they're removed and the command retried once. Any other holder fails the run with a clear "locked" message. |
| **"Docker" is a trademark of Docker, Inc.** Their brand guidelines restrict its use in product names and logos | Resolved: the product is **Stowaway** (decision 8). "Docker" appears only descriptively, as in the tagline. Artwork must never use Docker's own logos. A whale in Stowaway's artwork is the owner's decision (2026-11-27). |

## 8. Repository layout

```
/src
  Stowaway.Service/        ASP.NET Core host, API, SignalR, notifications, Windows Service, tray app (--tray)
  Stowaway.Core/           domain: inventory, plans, backups, restore, restic output parsing
  Stowaway.Docker/         Docker access, discovery, runner (restic) orchestration, dumps, restore, drills
  Stowaway.Data/           EF Core / SQLite, migrations
/web                           React SPA (Vite) → built into the Service's wwwroot
/installer                     WiX v5 project
/tests                         xUnit v3: unit + real-engine integration
/tools                         brand asset generator
/assets/brand                  artwork masters (supplied by the owner)
/docs                          design, imagery requests
/.github/workflows             ci.yml, release.yml
```

## 9. Milestones

| # | Milestone | Outcome |
|---|---|---|
| M0 | Skeleton | Solution, CI, MSI installs a service that serves the UI shell over HTTPS |
| M1 | Discovery | Live inventory of apps, containers, volumes, binds, orphans; protection status |
| M2 | Backup core | Runner image, restic repos (local/SMB), live and stop/start strategies, manual "back up now" |
| M3 | Scheduling | Plans wizard, schedules (Cronos), retention/prune, history, notifications |
| M4 | Restore | Volume / new volume / file-level / host folder; snapshot browser and timeline |
| M5 | App-aware and cloud | DB dumps, S3/B2/Azure/SFTP and **Dropbox**, stack recreate, restore drills |
| M6 | Polish and ship | M6a: Windows sign-in, stale-lock recovery, Event Log, tray app, uninstall cleanup. M6b: remote access + TOTP, code signing, winget manifests, user guide. M6c: internet access through a reverse proxy (trusted proxies, per-address lockouts, fresh codes for destructive changes, alerts, audit log) |

### Implementation notes
- **M0 (done):** service host, HTTPS on loopback, engine monitor, UI shell, MSI, CI.
- **M1 (done):** discovery. Apps are grouped by compose project. Bind sources are mapped back to Windows paths (`/run/desktop/mnt/host/x/...` → `X:\...`). Sockets, tmpfs, system paths and whole drives are excluded by default. Volume sizes come from `/system/df` (raw HTTP over the pipe, because the client library lacks it); bind sizes come from a budgeted walk of the host filesystem. Engine events trigger debounced rescans, and changes are pushed to the UI over SignalR. "Locally built image" = no registry digest **or** Compose build labels: with the containerd image store, local builds have digests too. A request-origin guard protects hub and write endpoints against cross-site WebSocket and CSRF attacks.
- **M2 (done):** backup core.
  - **Runner:** `restic/restic:0.19.1` (pinned), in ephemeral containers labelled `stowaway.runner`. Leftovers are removed at startup. No network for local/SMB destinations, `no-new-privileges`, and a persistent `stowaway-restic-cache` volume.
  - **Repository password:** placed as a file inside the container before start (not in env, so never visible in `docker inspect`).
  - **SMB destinations:** a CIFS volume (`local` driver, `type=cifs`) created per run and removed afterwards, so share credentials only exist on the engine during a run. Commas in credentials are doubled for the CIFS option parser. *Only verified by unit tests so far: no SMB share was available on the dev host.*
  - **Snapshot layout:** `/data/<container>/<mount path>` plus `/data/_stowaway/` (app.json, run.json, container inspect JSON, compose files and `.env`). Tags: `stowaway`, `app=<id>`, `run=<id>`; host = machine name.
  - **Strategies:** *Live*, or *Stop & start*. Stop & start stops dependents first (from the `com.docker.compose.depends_on` label) and always restarts in dependency order, even on failure or cancel. Downtime is recorded per run.
  - **Queue:** one backup at a time. Interrupted runs are marked failed at startup.
  - **Persistence:** SQLite through EF Core with migrations (`dotnet ef migrations add <Name> --project src/Stowaway.Data --startup-project src/Stowaway.Data`).
- **M3 (done):** plans and protection.
  - **Plans:** *All apps* (including future apps, minus exclusions) or *Selected*, optionally with unowned volumes (each backed up as its own `volume:<name>` item). Strategy *Auto* (stop & start only for apps running a database; since M5a, only for databases that can't be dumped), *Live* or *Stop & start*. Five-field cron in host local time.
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
  - **Runner image:** `stowaway/runner:<hash>`, built locally on first use as `FROM restic/restic:<pin>` plus `rclone` and `socat` from Alpine. No registry publishing is needed. If the build fails, it falls back to the plain restic image (everything works except Dropbox).
  - **Secrets in runners:** all restic and cloud variables are written to `/run/stowaway/env` inside the container before it starts, then sourced by an `sh` entrypoint wrapper. Nothing secret is in the container config, so nothing shows in `docker inspect`.
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
- **M6a (done):** sign-in and hardening.
  - **Windows sign-in:** Negotiate (Kerberos/NTLM) on every API and hub request; Edge and Chrome sign in silently because localhost is in the Local intranet zone. Allowed: local `Administrators` and members of an optional local group `Stowaway Operators`.
    - Loopback NTLM tokens can be filtered so the Administrators SID is missing entirely, so membership falls back to a machine-account lookup (`System.DirectoryServices.AccountManagement`, recursive, cached 5 minutes).
    - Anonymous: `/api/health` and the static UI shell (it holds no data). The UI shows "Sign-in required" or "No access" screens.
    - `Stowaway:Auth:Mode` = `Windows` (default) or `None` (development and tests only).
  - **Stale locks:** see §7.
  - **Event Log:** as a service, warnings and errors also go to the Application log under source `Stowaway`.
  - **Tray app:** `Stowaway.Service.exe --tray`, started for every user at logon (HKLM `Run`). Polls `/api/inventory` with the user's Windows credentials; shows the protection status; balloon when the failing count rises.
  - **Uninstall cleanup:** a deferred custom action runs `--uninstall-cleanup` on full uninstall (not upgrades): removes our certificate from LocalMachine\Root and the event source. `%ProgramData%\Stowaway` is kept on purpose: it holds the repository keys.
- **M6b (done):** remote access, code signing, winget, user guide.
  - **Remote access** (`remote-access.json` in the data directory; applied at service start because it changes the listeners):
    - **Listening:** loopback only by default. When on, Kestrel listens on all interfaces on the same port. SNI-independent certificate selection: loopback connections keep the locally trusted certificate; others get the remote one (self-issued for the configured names, 825 days, downloadable as `.cer`; or a LocalMachine\My certificate by thumbprint). A missing or invalid certificate keeps remote access off and says why.
    - **Host names** are added to `AllowedHosts` (DNS-rebinding defence) and count as allowed `https` origins for the origin guard. Requests from non-loopback addresses are refused outright while remote access is off (defence in depth).
    - **Firewall:** as a service, an inbound rule "Stowaway remote access" (program + port, chosen profiles; Domain and Private by default) is added at start and removed when off or on uninstall.
    - **Auth:** a policy scheme forwards loopback requests to Negotiate and remote ones to a cookie (`__Host-Stowaway`, Secure, HttpOnly, SameSite=Strict, 12 h absolute). Cookie keys: ASP.NET Data Protection in `keys/`, DPAPI (service account). Each request revalidates the account and its security stamp, so deleting an account or resetting its password or authenticator ends its sessions.
    - **Accounts** (SQLite `RemoteAccounts`): PBKDF2-SHA256 (600k iterations) password: 8+ characters with upper and lower case and a digit or symbol (moderate on purpose: TOTP is mandatory, plus rate limits and lockouts); user names may be email addresses; TOTP secret (RFC 6238, SHA-1, 30 s, 6 digits, ±1 step) protected with DPAPI; a code is accepted once (last step stored); 10 one-time recovery codes, stored as SHA-256. Unconfirmed until a first code is entered.
    - **Sign-in:** one request with user name, password and code; every failure looks the same (unknown users cost the same PBKDF2 time). 5 failures lock the account for 15 minutes; 10 attempts per minute per address (rate limiter). Failures and lockouts go to the Event Log.
    - **Local-only** (policy `LocalOnly`): `/api/remote/**`, `/api/system/restart`, and the recovery-key reveal. Enabling requires at least one confirmed account; the last confirmed account can't be deleted while it's on.
    - **Restart from the UI:** a detached `cmd /c "net stop … & net start …"` (a service can't start itself once stopped).
  - **Code signing:** `build.ps1 -SignThumbprint` (certificate in a store, including hardware tokens) or `STOWAWAY_SIGN_PFX`/`_PASSWORD` (release workflow secrets). `Set-AuthenticodeSignature` (no Windows SDK needed) signs the service executable before it goes into the MSI, then the MSI; RFC 3161 timestamp from DigiCert by default. Cloud HSM signing (Azure Trusted Signing, KeyLocker) would need its own step.
  - **winget:** `tools/New-WingetManifest.ps1` writes the version/installer/locale manifests (schema 1.9.0, MSI ProductCode, SHA-256); releases attach them with a `.sha256` file. Submission to winget-pkgs waits for public releases and the final product name.
  - **User guide:** `docs/USER-GUIDE.md`.
- **M6c (done):** remote access over the internet, through a reverse proxy (the owner's setup: Cloudflare → nginx on another LAN host → this host).
  - **Trusted proxies** (`TrustedProxies` in `remote-access.json`): when set, only those addresses may connect from other computers (firewall `remoteip` and the origin guard). The client address for rate limits, lockouts, alerts and the audit log is the last `X-Forwarded-For` entry, but only from a trusted proxy. **Local vs remote is always the TCP peer**, never a header: a forged `X-Forwarded-For: 127.0.0.1` can't turn a remote request into a local (Windows sign-in, `LocalOnly`) one.
  - **Lockouts** per (account, client address): 5 failures → 15 minutes, so strangers can't lock the owner out; 20 failures from anywhere lock the account (distributed guessing). Kept in memory (per address) and in the database (account).
  - **Fresh code (step-up):** remote sessions get `428` with `type: fresh-code-required` for every API change not marked `AllowWithoutFreshCode` (allowlist: back up now, cancel, checks, rescan, plan run-now/preview, maintenance, restore preview, drills, notification test, Dropbox sign-in start, sign-in/out). `POST /api/auth/elevate` with a TOTP code (single-use, counts towards lockouts; recovery codes not accepted) re-issues the cookie with a 10-minute elevation claim; the original issue time is kept. The SPA prompts and retries transparently.
  - **Sessions:** 30 minutes sliding, 8 hours absolute (issue-time claim checked on every request).
  - **Security events** (`ISecurityEvents`): sign-ins, failures, lockouts, recovery-code use and step-ups are logged (warnings reach the Event Log) and written to the audit log; new-address sign-ins, lockouts and recovery-code use are also sent to notification channels that report failures (`NotificationEvent.Security`).
  - **Audit log** (SQLite `AuditEntries`, 180 days): every API change (actor, client address, local/remote, method + path, status; never bodies) and the security events. `GET /api/audit`; Settings → Audit log.
  - **HSTS** on remote responses only (on localhost it would stick to every other local site).
  - **nginx** guide: Authenticated Origin Pulls, `X-Forwarded-For $http_cf_connecting_ip`, WebSocket upgrade, optional `proxy_ssl_verify` against Stowaway's certificate (`/api/remote/certificate?format=pem`; off in the default example, as the hop is on the LAN). Cloudflare Access recommended as an independent first gate.
- **After M6c (2026-11-27):** the app boundary and database logins.
  - **What gets backed up:** volumes, dumps, and bind mounts inside the compose project folder (`com.docker.compose.project.working_dir`). Other bind mounts (including all of standalone containers), drives, UNC paths and network-share volumes (driver other than `local`, or `type` cifs/smb/nfs) are excluded with a reason, not sized, and not selectable.
  - **Stowaway's own resources are invisible:** containers and volumes labelled `stowaway.runner`, the runner image (`stowaway.runner.image`) and the restic cache volume are filtered out of the inventory (`InventoryBuilder.WithoutOwnResources`).
  - **Database logins:** `DatabaseCredentials` table keyed by container name; `/api/databases/credentials` (list, put, delete, test). Dump scripts take `STOWAWAY_DB_USER/PASSWORD/NAME` first, then the image variables and `*_FILE` secrets.
  - **Dump robustness:** commands report their own exit code on stderr (Docker may delete a finished exec before it can be inspected), and the sink is considered done once its file holds every byte (the attach connection doesn't always pass on end-of-input).
  - **Tray:** started by the installer's Finish button; closes on Restart Manager requests during upgrades and registers for restart.
- **M7a (done): Windows-container engines.** Facts from a real host are in issue #4.
  - **Detection:** `EngineStatus.OsType == windows` → `AppInfo.Platform = windows` (stored in every snapshot's `app.json`).
  - **Where restic runs:** on the host (`tools\restic.exe`, shipped and pinned with SHA-256 by `tools/Get-BundledTools.ps1`; rclone too). `ResticRunner.OnHost` routes every call to `HostResticRunner`, which translates the runner-container paths (`/repo`, `/restore`, `/run/stowaway/…`, `/data/_stowaway/…`) to host paths (`HostPathMap`). Secrets go to a per-run folder in the protected data directory; `RESTIC_CACHE_DIR` is in the data directory too.
  - **Snapshot layout:** sources are recorded under their host paths (`/C/ProgramData/docker/volumes/<v>/_data`); metadata and dumps under the staging folder `<data>\stowaway-stage` (`/C/…/stowaway-stage/_stowaway`, `…/_dumps`). Restores and the catalog find them via the snapshot's recorded paths (`BackupPlanner.DataRootOf`). Linux snapshots are unchanged.
  - **VSS:** `--use-fs-snapshot` whenever a container of the app is running (and isn't being stopped for the backup): the only way to read files held open exclusively, and without it restic can keep an old copy of a locked file and still exit 0.
  - **SMB destinations:** a credentialed connection to the share (`WNetAddConnection2`) instead of a CIFS volume. **Dropbox sign-in:** `rclone.exe authorize` on the host. **SFTP:** Windows' OpenSSH client (`ssh`, `ssh-keyscan`).
  - **Stale locks:** host-side locks carry the computer's name; `restic unlock` (without `--remove-all`) removes only locks restic can prove stale.
  - **Windows system folders** (`C:\Windows`, `C:\Program Files`) are plumbing, like `/proc` on Linux.
- **M7b (done): SQL Server in Windows containers** (Business Central images keep their databases in the container's writable layer).
  - **Detection:** `mssql`, `businesscentral` and `dynamicsnav` images are `SqlServer`. `DatabaseDumps.Supported(kind, platform)`: on Windows engines only SQL Server is dumpable, so these apps stay on Live (no stop & start).
  - **Exec on Windows:** no `sh` anywhere and `cmd /c` re-parses its command line, so `sqlcmd`, `tar.exe` and `cmd /c mkdir|rmdir` are called directly with plain arguments; exit codes come from Docker (`DockerExec.RunAsync(windows: true)`). Saved SQL logins: `-U` plus `SQLCMDPASSWORD` in the exec environment; otherwise `-E` (ContainerAdministrator is sysadmin). Instances tried: `.\SQLEXPRESS`, then `.`.
  - **Running container** (`SqlServerDumpService`): list user databases and their files (`sys.master_files`), `BACKUP DATABASE … TO DISK = 'C:\stowaway-dump\<db>.bak' WITH COPY_ONLY, INIT, CHECKSUM`, then `tar.exe -cf -` streamed (`DockerExec.StreamAsync`, a pipe into `TarReader`) into `<stage>\_dumps\<container>\`, plus `sqlserver.json` (`SqlServerManifest`). The database files' paths are remembered in `<data>\sqlserver-files.json`.
  - **Stopped container:** the archive API (`docker cp`; refused for running Hyper-V containers) copies the remembered files and any `.mdf/.ndf/.ldf` under `C:\databases` to `_dumps\<container>\files\C\…`, without starting the container. It does bump `sandbox.vhdx`'s modified time.
  - **Restore:** like Linux dumps, to a folder or volume only; the help explains `RESTORE … WITH MOVE` / `FOR ATTACH`.
  - **Drill** (`SqlServerDrill`): a container from the same image with `powershell -EncodedCommand` as entrypoint, which starts only the `MSSQL$*`/`MSSQLSERVER` service; the restored `_dumps` volume at `C:\stowaway-drill`; `RESTORE FILELISTONLY` → `RESTORE … WITH MOVE`, or `DBCC CHECKPRIMARYFILE(…, 3)` → `CREATE DATABASE … FOR ATTACH`; then `DBCC CHECKDB` and a table count.
  - **Not covered by CI:** no SQL Server Windows image runs on the hosted runners; the end-to-end check runs on a real Business Central host (issue #4).

M0–M3 is a usable MVP ("set it and forget it backups"). M4 makes it trustworthy, and M5 makes it best in class.

## 10. Decisions (2026-11-27)

| # | Topic | Decision |
|---|---|---|
| 1 | Repo name | `niogtres/stowaway` (private). Created as `docker_backuup`, then `docker_backup`, renamed with the product (decision 8). |
| 2 | License | Closed source at first. *2026-11-27:* **MIT**, copyright (c) 2026 Thomas Kraft, published as open source. Third-party licenses in `THIRD-PARTY-NOTICES.txt` (generated, shipped with the program, shown on the About page). |
| 3 | Backup engine | **restic** (BSD-2-Clause, bundled). The service already owns scheduling, policy and UI, and restic is a minimal, scriptable engine with JSON output and a stable repo format. Kopia would duplicate our UI/server/policy layers. *Correction:* restic calls are concentrated in `ResticRepository`, but a formal `IBackupEngine` interface was not introduced; add one if a second engine is ever needed. |
| 4 | Container platforms | v1: Linux containers only. *M7 (2026-11-27):* Windows-container engines too, with host-side restic, based on experiments on a real Windows-container host (issue #4). |
| 5 | Remote access | Localhost only for the MVP. *M6b:* opt-in remote access, approved with the M6b scope. Off by default, switched on only from this computer, and only with Windows sign-in on. Remote users need a Stowaway account with password and authenticator. |
| 6 | Multi-host | Planned for later; v1 manages one host. *Correction:* the data model does **not** carry an engine id yet, so multi-host will need a migration (adding an engine id to destinations, plans and runs). |
| 8 | Product name | **Stowaway**, tagline "Sensible Docker backup management" (2026-11-27). Renamed everywhere before the first real install: product, service (`Stowaway`), data folder (`%ProgramData%\Stowaway`), namespaces and projects, runner image, restic tags. Repository renamed to `niogtres/stowaway`. |
| 7 | Scheduler | **Cronos + own scheduler** instead of Quartz.NET (decided during M3). Plans already live in our SQLite database. Quartz's persistent store would add a second persistence model with about a dozen tables, and the behaviour we need is small: compute the next run, catch up once, wait for the engine. |
