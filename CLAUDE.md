# Stowaway: maintainer notes

Design and decisions: [docs/DESIGN.md](docs/DESIGN.md). Read §10 (decisions) before changing scope.

## Layout
- `src/Stowaway.Service`: ASP.NET Core host, Windows Service, API (`/api/*`), serves SPA from `wwwroot` (generated, git-ignored).
- `src/Stowaway.Core`: domain types, no infrastructure dependencies.
- `src/Stowaway.Docker`: Docker Engine access (Docker.DotNet.Enhanced, builder API: `new DockerClientBuilder().WithEndpoint(..).Build()`).
- Databases and destinations are built-in providers (`Stowaway.Docker/Databases`, `Stowaway.Docker/Destinations`), and product knowledge (e.g. Business Central) is a built-in app profile (`Stowaway.Core/Profiles`); DESIGN decision 9. How to add one: `docs/PROVIDERS.md`; keep it in step when the interfaces change.
- `web/`: React 19 + Vite 8 + TS 7 + Tailwind v4. `npm run build` writes into the service's `wwwroot`.
- `src/Stowaway.Data`: EF Core + SQLite (destinations, runs). Add migrations with `dotnet tool restore` then `dotnet ef migrations add <Name> --project src/Stowaway.Data --startup-project src/Stowaway.Data`.
- Backups run restic in ephemeral runner containers (`stowaway/runner:<hash>`, built from the pinned `restic/restic` image; `ResticRunner`), or host-side `restic.exe` for Windows-container engines (`HostResticRunner`, pinned in `tools/Get-BundledTools.ps1`). Bump pins deliberately and re-run the integration tests.
- `installer/`: WiX **v5** MSI (do not upgrade to v6+ without approval: it requires accepting the OSMF EULA).
- `tests/Stowaway.Tests`: xUnit v3 on Microsoft.Testing.Platform (`global.json` opts in).

## License and docs
- MIT, copyright (c) 2026 Thomas Kraft (project-level notices only; no per-file headers).
- After changing NuGet or npm dependencies, run `python -I tools/generate-notices.py` and commit `THIRD-PARTY-NOTICES.txt`; new dependencies must be MIT-compatible.
- Add user-visible changes to `CHANGELOG.md` under *Unreleased*; on a release tag, rename that section to the version and date.
- The user guide lives in `docs/help/NN-slug.md` (one topic per file); the web client bundles the same files as its Help section. Link between topics by file name (`04-destinations.md`).
- Help screenshots (`docs/help/images/*.png`, referenced as `images/name.png`) come from `node tools/screenshots/capture.mjs`: the built web client on made-up data (`tools/screenshots/fixtures.mjs`). Re-run it after visible UI changes; add fixture data for new screens, never real names, paths or keys.

## Commands
- Full build → MSI: `./build.ps1 [-Version x.y.z] [-SkipTests]` (output in `artifacts/`).
- Dev: `dotnet run --project src/Stowaway.Service` (https://localhost:8484, data in `%LOCALAPPDATA%\Stowaway-dev`) and `npm run dev` in `web/` (http://localhost:5173, proxies `/api`).
- Tests: `dotnet test`. Web checks: `npm run lint`, `npm run build` (includes `tsc -b`).

## Conventions
- Package versions live only in `Directory.Packages.props`; .NET packages stay on the **10.x** line (net10.0 LTS).
- Warnings are errors. Keep `oxlint --deny-warnings` clean.
- Loopback-only HTTPS and `AllowedHosts` (DNS-rebinding defence) are security features: don't widen them without an explicit decision. Remote access (M6b) is the one opt-in exception: off by default, configured only from this computer, remote users need password + TOTP. Keep `LocalOnly` on anything that manages access or reveals secrets. New API writes need a fresh code from remote sessions by default; mark one `.AllowWithoutFreshCode()` only if it can't destroy, redirect or expose data. Never decide local vs remote from a header.
- Secrets use DPAPI **CurrentUser** scope of the service account (LocalSystem), never LocalMachine scope.
- Never stop, modify or back up the owner's real containers in tests; use throwaway containers.
- Work on a branch and open a PR; CI (`.github/workflows/ci.yml`) must be green. Releases come from tags `vX.Y.Z`.
- GitHub: the `gh` CLI is not installed locally; use the REST API with the token from `git credential fill`.
