# Contributing to Stowaway

Thanks for helping make Docker backups on Windows less painful. Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

[Open an issue](https://github.com/niogtres/stowaway/issues/new) with:

- what you did, what you expected, and what happened instead;
- the Stowaway version (sidebar, or *About*), Windows version, and Docker Desktop or engine version;
- the relevant lines from `C:\ProgramData\Stowaway\logs` (remove passwords, keys and host names you don't want public).

**Security problems** don't belong in public issues: see [SECURITY.md](SECURITY.md).

## Suggesting a feature

Open an issue describing the problem you want solved before writing code for anything large, so we can agree on the approach first. [docs/DESIGN.md](docs/DESIGN.md) explains how Stowaway is built and why (§10 lists the decisions made so far).

## Development setup

Requirements: Windows 10/11, [.NET SDK 10.0.1xx](https://dotnet.microsoft.com/download), [Node.js 24](https://nodejs.org/), and Docker Desktop with Linux containers (for the integration tests).

```powershell
git clone https://github.com/niogtres/stowaway.git
cd stowaway
dotnet tool restore
cd web; npm ci; npm run build; cd ..        # the service serves the built UI
dotnet run --project src/Stowaway.Service    # https://localhost:8484 (sign-in off in Development)
cd web; npm run dev                          # UI with hot reload on http://localhost:5173
```

Build the installer with `./build.ps1 -Version 0.1.0` (output in `artifacts/`).

## Before you open a pull request

- **Tests:** `dotnet test`. The integration tests use the local Docker engine and create only throwaway containers and volumes (named `dbtest-*`); they never touch your own containers.
- **Web checks:** `npm run lint` and `npm run build` in `web/` (the build includes type checking).
- **No warnings:** warnings are errors in .NET, and `oxlint --deny-warnings` must stay clean.
- **Dependencies:** .NET package versions live only in `Directory.Packages.props` and stay on the 10.x line. Add new dependencies only with a good reason, and check that their license is compatible with MIT.
- **Security-sensitive areas** (sign-in, remote access, host filtering, the request-origin guard, secret storage) need a test for the behaviour you change. Anything that manages access or reveals secrets stays local-only.
- **Docs:** update [docs/USER-GUIDE.md](docs/USER-GUIDE.md) / the help pages in `docs/help/` when behaviour visible to users changes.

Keep pull requests focused on one change, describe what and why, and make sure CI is green.

## License

Stowaway is released under the [MIT License](LICENSE). By contributing, you agree that your contributions are licensed under the same terms.
