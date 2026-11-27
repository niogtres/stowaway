# DockerBackup: maintainer notes

Design and decisions: [docs/DESIGN.md](docs/DESIGN.md). Read §10 (decisions) before changing scope.

## Layout
- `src/DockerBackup.Service`: ASP.NET Core host, Windows Service, API (`/api/*`), serves SPA from `wwwroot` (generated, git-ignored).
- `src/DockerBackup.Core`: domain types, no infrastructure dependencies.
- `src/DockerBackup.Docker`: Docker Engine access (Docker.DotNet.Enhanced, builder API: `new DockerClientBuilder().WithEndpoint(..).Build()`).
- `web/`: React 19 + Vite 8 + TS 7 + Tailwind v4. `npm run build` writes into the service's `wwwroot`.
- `installer/`: WiX **v5** MSI (do not upgrade to v6+ without approval: it requires accepting the OSMF EULA).
- `tests/DockerBackup.Tests`: xUnit v3 on Microsoft.Testing.Platform (`global.json` opts in).

## Commands
- Full build → MSI: `./build.ps1 [-Version x.y.z] [-SkipTests]` (output in `artifacts/`).
- Dev: `dotnet run --project src/DockerBackup.Service` (https://localhost:8484, data in `%LOCALAPPDATA%\DockerBackup-dev`) and `npm run dev` in `web/` (http://localhost:5173, proxies `/api`).
- Tests: `dotnet test`. Web checks: `npm run lint`, `npm run build` (includes `tsc -b`).

## Conventions
- Package versions live only in `Directory.Packages.props`; .NET packages stay on the **10.x** line (net10.0 LTS).
- Warnings are errors. Keep `oxlint --deny-warnings` clean.
- Loopback-only HTTPS and `AllowedHosts` (DNS-rebinding defence) are security features: don't widen them without an explicit decision.
- Secrets use DPAPI **CurrentUser** scope of the service account (LocalSystem), never LocalMachine scope.
- Never stop, modify or back up the owner's real containers in tests; use throwaway containers.
- Work on a branch and open a PR; CI (`.github/workflows/ci.yml`) must be green. Releases come from tags `vX.Y.Z`.
- GitHub: the `gh` CLI is not installed locally; use the REST API with the token from `git credential fill`.
