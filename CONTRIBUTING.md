# Contributing to Stowaway

Thanks for helping make Docker backups on Windows less painful. Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

[Open an issue](https://github.com/niogtres/stowaway/issues/new/choose) (the *Bug report* form asks for these):

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

## Adding support for a database

Database backups go through **database providers** (`src/Stowaway.Docker/Databases/IDatabaseProvider.cs`): each one backs up a family of databases consistently while they run, checks the login, and proves in restore drills that its dumps load. Providers are built in, not loaded as plugins (see decision 9 in [docs/DESIGN.md](docs/DESIGN.md)).

For a database that can be dumped with its own command-line tools inside its Linux container:

1. Add the kind to `DatabaseKind` and its image names to `DatabaseDetector` (`src/Stowaway.Core/Inventory/`), if it isn't detected yet.
2. Add a definition next to `PostgreSqlDatabase.cs` in `src/Stowaway.Core/Databases/`: the login script (Stowaway's saved login first, then the image's own variables), the dump and login-check commands, the file extension, and the drill check (start a throwaway server, load the dump, print one number).
3. Add it to `ShellDatabases.All`. The Linux provider, the inventory and the web client pick it up from there.
4. Test it against a real container of the image, like `Dumps_a_real_postgres_database_and_drills_it`. `DatabaseProviderTests` checks that every definition is complete.

A database that needs more than shell commands (like SQL Server in Windows containers) gets its own `IDatabaseProvider`, registered in `AddDatabaseProviders`.

## Adding a destination type

Each destination type is a **destination provider** (`src/Stowaway.Docker/Destinations/`, `IDestinationProvider`). It describes its fields, checks them, and tells restic how to reach the repository. The web client draws the add and edit forms from the provider's descriptor (`GET /api/destination-types`), so a new type needs no form code.

1. Add the type to `DestinationKind` (`src/Stowaway.Core/Backups/BackupModels.cs`).
2. Write the provider. Cloud types derive from `CloudProvider` (see `S3Provider`). Implement:
   - its descriptor: fields, which are secret, and which belong to the location or the credentials;
   - `Validate`: checks the values and returns the location shown to the user;
   - `Connect`: restic's repository string, environment and files.
   Override `Normalize`, `PrepareAsync` or `TakeSignIn` if the type needs them (see `DropboxProvider` and `SftpProvider`).
3. Register it in `AddDestinationProviders` and in `DestinationProviders.BuiltIn`. If the descriptor names a new icon, add it to `web/src/lib/destinations.ts`.
4. Test `Validate` and `Connect` like `CloudRepositoryTests` does. `DestinationProviderTests` checks the descriptor and that the service registers the type.

## Before you open a pull request

- **Tests:** `dotnet test`. The integration tests use the local Docker engine and create only throwaway containers and volumes (named `dbtest-*`); they never touch your own containers.
- **Web checks:** `npm run lint` and `npm run build` in `web/` (the build includes type checking).
- **No warnings:** warnings are errors in .NET, and `oxlint --deny-warnings` must stay clean.
- **Dependencies:** .NET package versions live only in `Directory.Packages.props` and stay on the 10.x line. Add new dependencies only with a good reason, and check that their license is compatible with MIT.
- **Security-sensitive areas** (sign-in, remote access, host filtering, the request-origin guard, secret storage) need a test for the behaviour you change. Anything that manages access or reveals secrets stays local-only.
- **Docs:** update [docs/USER-GUIDE.md](docs/USER-GUIDE.md) / the help pages in `docs/help/` when behaviour visible to users changes.

- **Changelog:** add a line under *Unreleased* in [CHANGELOG.md](CHANGELOG.md) for anything users would notice.

Keep pull requests focused on one change, describe what and why, and make sure CI is green.

## License

Stowaway is released under the [MIT License](LICENSE). By contributing, you agree that your contributions are licensed under the same terms.
