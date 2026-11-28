# Writing a provider

Stowaway backs up databases and writes to destinations through **providers**: one class per database family or destination type, behind a small interface. This guide explains those interfaces, when Stowaway calls each method, and what a new provider needs before it can be merged.

You'll need C# and a working [development setup](../.github/CONTRIBUTING.md#development-setup). Read [DESIGN.md](DESIGN.md) §3 (architecture) first if you haven't.

## Why built in, not plugins

Providers are compiled into Stowaway and registered with dependency injection. Stowaway doesn't load plugin DLLs at run time, and that's deliberate (DESIGN.md §10, decision 9). The service runs as LocalSystem and holds every repository password and cloud token, so any code loaded into it could read all of them. A new provider therefore arrives as a pull request with tests, and ships in the next release.

If an extension ever has to live outside this repository, it will run as a separate program that Stowaway talks to. Nothing like that exists yet; open an issue if you need it.

| | Database providers | Destination providers |
|---|---|---|
| Interface | `IDatabaseProvider` | `IDestinationProvider` |
| Code | `src/Stowaway.Docker/Databases/` | `src/Stowaway.Docker/Destinations/` |
| Shared types (no Docker) | `src/Stowaway.Core/Databases/` | `src/Stowaway.Core/Destinations/` |
| Registration | `AddDatabaseProviders` in `src/Stowaway.Docker/ServiceCollectionExtensions.cs` | `AddDestinationProviders` (same file) and `DestinationProviders.BuiltIn` |
| Guard tests | `DatabaseProviderTests` | `DestinationProviderTests` |

---

## Database providers

A database provider backs up one family of databases **consistently while they run**, checks that its login works, and proves in a restore drill that its backups load.

### Where Stowaway uses it

1. **Inventory.** `DatabaseDetector` (`src/Stowaway.Core/Inventory/`) recognises a container's database from its image name and sets `ContainerInfo.Database`. The registered providers' `Kinds` and `Platform` decide `DumpSupported`, which the inventory, the backup strategy and the web client all read. Nothing else keeps a list of supported databases.
2. **Login check.** **Check login** in the Inventory calls `ProbeAsync` with either the login the owner saved or none (automatic).
3. **Backup.** After any containers have been stopped, `BackupService` calls `DumpAsync` on every provider for the engine's platform, in turn. A provider with nothing to do for the item returns `DumpOutcome.Nothing`.
4. **Drill.** A restore drill restores the snapshot's `_dumps` folder into a throwaway volume and calls `VerifyAsync`, which loads the dumps into throwaway containers and checks them.

### Path A: a shell definition (most databases)

If the database runs in a **Linux container** and ships a command-line tool that writes a dump to stdout, you don't write a provider. You write a `ShellDatabase` definition, and the built-in shell provider runs it through `sh` inside the container. Because the dump tool comes from the database's own image, it always matches the server version.

Redis is the shortest example (`src/Stowaway.Core/Databases/RedisDatabase.cs`):

```csharp
public static ShellDatabase Definition { get; } = new(
    [DatabaseKind.Redis],
    "rdb",                                         // file extension in the snapshot's _dumps folder
    Credentials:
        """
        f() { [ -n "$1" ] && cat "$1" 2>/dev/null; }
        DB_USER="$STOWAWAY_DB_USER"
        DB_PASS="${STOWAWAY_DB_PASSWORD:-${REDIS_PASSWORD:-$(f "$REDIS_PASSWORD_FILE")}}"
        """,
    Dump:  """exec redis-cli ${DB_USER:+--user "$DB_USER"} ${DB_PASS:+-a "$DB_PASS"} --no-auth-warning --rdb -""",
    Probe: """exec redis-cli ${DB_USER:+--user "$DB_USER"} ${DB_PASS:+-a "$DB_PASS"} --no-auth-warning ping""",
    Drill: file => new DrillCheck(
        Environment: [],
        CommandOverride: ["sh", "-c", $"mkdir -p /data && cp {file} /data/dump.rdb && exec redis-server --dir /data --dbfilename dump.rdb"],
        Ready: ["redis-cli", "ping"],
        Load: null,                                // loaded at start-up by the command above
        Probe: ["redis-cli", "dbsize"],
        ProbeLabel: "keys"));
```

The parts:

- **`Credentials`** is shell that works out the login. Read Stowaway's saved login first (`STOWAWAY_DB_USER`, `STOWAWAY_DB_PASSWORD`, `STOWAWAY_DB_DATABASE`; see `DatabaseDumps`), then the official image's variables, **including their `*_FILE` forms** (Docker secrets). The `f` helper reads a file only when its variable is set. Stowaway passes the saved login through `docker exec`'s environment, never on a command line.
- **`Dump`** runs after `Credentials` and writes the dump to **stdout**. Stowaway streams it into a dump volume that becomes part of the snapshot. Use the tool's consistent mode (`--single-transaction`, `--archive`, `--rdb`…).
- **`Probe`** runs after `Credentials`, only logs in, and exits 0 when the login works.
- **`Drill`** receives the dump file's path inside the drill container and returns a `DrillCheck`:

  | Field | Meaning |
  |---|---|
  | `Environment` | Extra variables for the throwaway server (e.g. its root password, `DrillChecks.Password`). |
  | `CommandOverride` | Replaces the image's command, for databases that load a dump at start-up. Otherwise null. |
  | `Ready` | Succeeds once the server accepts real connections. Beware of images that run a temporary server during initialisation. |
  | `Load` | Loads the dump into the running server; null when `CommandOverride` already did it. |
  | `Probe` | Prints **one number** that describes the restored data (tables, collections, keys). |
  | `ProbeLabel` | What that number counts, for the drill report ("3 tables"). |

  The drill container is started from the **same image** as the app's database, with no network, and is removed afterwards.

Steps:

1. If the database isn't detected yet, add it to `DatabaseKind` (`src/Stowaway.Core/Inventory/InventoryModels.cs`), its image names to `DatabaseDetector`, and a label to `databaseLabels` in `web/src/lib/api.ts` (plus the `DatabaseKind` type above it).
2. Add `YourDatabase.cs` next to `RedisDatabase.cs`, and add it to `ShellDatabases.All`. The shell provider, the inventory and the web client pick it up from there.
3. Write an integration test against a real container of the image, modelled on `Dumps_a_real_postgres_database_and_drills_it` (`tests/Stowaway.Tests/Backups/CloudAndDumpTests.cs`): start a throwaway container, put known data in it, dump, drill, compare the number. `DatabaseProviderTests` checks that every definition is complete.

Shell scripts must work in plain `sh` (BusyBox or dash), not only bash. Line endings are normalised for you (`ShellDatabase.DumpScript`), so a Windows checkout is fine.

### Path B: a full `IDatabaseProvider`

Write your own provider when shell commands aren't enough: a different platform (SQL Server in Windows containers is the existing example: `SqlServerProvider` and `SqlServerDumpService`), files that must be copied rather than streamed, or a dump that needs several steps.

```csharp
public interface IDatabaseProvider
{
    IReadOnlyCollection<DatabaseKind> Kinds { get; }   // the kinds you back up
    string Platform { get; }                           // Platforms.Linux or Platforms.Windows

    Task<ProbeResult> ProbeAsync(DockerClient client, string containerId, DatabaseKind kind,
        IReadOnlyList<string> environment, CancellationToken ct);
    Task<DumpOutcome> DumpAsync(DumpContext context, CancellationToken ct);
    Task VerifyAsync(DrillContext context, CancellationToken ct);
}
```

**`ProbeAsync`** checks the login without dumping. `environment` holds the saved login as `STOWAWAY_DB_*` variables, or is empty for automatic. Return `new ProbeResult(true, null)` on success, or `false` with a short reason the owner can act on ("password authentication failed for user …").

**`DumpAsync(DumpContext)`** gets:

| `DumpContext` | |
|---|---|
| `Client` | Docker client for the active engine. |
| `Item` | The app being backed up (`AppInfo`): its containers, their `Database`, `State` and mounts, and `Platform`. |
| `RunId` | Use it to name anything temporary, so cleanup can find it. |
| `Strategy` | `BackupStrategy.Live` or `BackupStrategy.StopStart`. With stop and start, the stopped database's files are already consistent; the shell provider dumps only under `Live`. |
| `Log` / `Phase` | Add lines to the run log, and set the step shown in the UI ("dumping databases"). |

It returns a `DumpOutcome`:

| `DumpOutcome` | |
|---|---|
| `Errors` | One `"container: reason"` per database that failed. Any error marks the run failed, even though the snapshot is still taken. |
| `Mounts` | Extra sources the backup must include, such as your dump volume, mounted at `DatabaseDumps.SnapshotDir` (`/data/_dumps` in the runner, the snapshot's `_dumps` folder) or a folder below it. |
| `Cleanup` | Runs after the backup whatever happened: remove volumes and temporary files here. |
| `FailureTitle` / `FailureConsequence` | Start and end of the run's error message, e.g. "SQL Server backup failed" … "The snapshot doesn't include those databases." |
| `Bytes` | Size of what you dumped. The inventory shows it as the size of databases that live inside a container. |

Write each container's dumps under its own folder or file name in `_dumps`, so several databases in one app don't collide. A provider for a host-side engine (`ResticRunner.OnHost`) can stage files in `runner.HostStageDirectory/_dumps` instead of returning mounts; `SqlServerDumpService` does this.

**`VerifyAsync(DrillContext)`** gets the restored dumps as a Docker volume (`Volume`), a function listing the top-level names in it (`DumpFiles`), the app as it was recorded in the snapshot (`App`), and a `Failures` list. Load each dump into a throwaway container started from the recorded image, with no network. Check that it really loaded: an integrity check plus a count, like `DBCC CHECKDB` and a table count. Add one entry to `Failures` per problem. Remove everything you started, also when a check fails.

Register the provider in `AddDatabaseProviders`. `DatabaseProviders` builds `DatabaseSupport` from all registered providers, so the inventory marks your kind as dumpable on your platform automatically.

### Rules for database providers

- **Read-only towards the owner's containers.** Run dump and login commands with `docker exec`. Never change a container's configuration, files or state. Stopping and starting is decided by the backup strategy, not by providers.
- **Secrets stay out of sight.** Pass passwords through environment variables of the exec or of throwaway containers, never in command lines, labels or log lines. Logs and errors may name users and databases, never passwords.
- **Drills touch nothing real.** Throwaway containers and volumes only, no network, always removed.
- **Fail loudly.** A dump that produced nothing, or a truncated one, is an error, not a success. Check exit codes and sizes.

---

## Destination providers

A destination provider describes one type of backup destination: which values it needs, how to check them, and how restic reaches the repository.

### The lifecycle

**Adding a destination** (`DestinationService.CreateAsync`):

1. The web client draws the form from your `Descriptor` (served at `GET /api/destination-types`) and sends back `settings` and `secrets`, keyed by your field keys.
2. `TakeSignIn(session)` runs only if your descriptor names a browser sign-in (`SignIn`, e.g. "dropbox"). It returns the credentials the sign-in produced, such as a token.
3. `Normalize(values, creating: true)` tidies typed values in place: trims them, adds defaults, fixes formats.
4. `Validate(values)` checks the complete set and returns the **location** shown for the destination (`s3://bucket/folder (host)`). Throw `DestinationValidationException` with a message the owner can act on. Don't use the network here.
5. `PrepareAsync(values, changed, creating: true)` does any slow work that needs the network before saving. `SftpProvider` uses it to record the server's SSH host keys.
6. The values are stored: non-secret fields in the settings JSON, secret ones encrypted (DPAPI) in the secrets JSON. Then restic initialises the repository, or opens an existing one, through `OpenAsync`.

**Editing a destination** (`UpdateAsync`) works the same way. `Normalize` runs with `creating: false` on the typed values only, and `changed` lists the keys that really changed. If anything changed, Stowaway opens the repository again and accepts the edit only if it is **the same repository**, identified by restic's repository id. That's what lets an owner fix credentials or follow a moved folder without pointing a destination at someone else's backups.

**Every run** calls `OpenAsync(values)`. It returns a `RepositoryAccess`, which is disposed when the run ends:

| `RepositoryAccess` | |
|---|---|
| `Repository` | restic's repository string (`s3:…`, `sftp:…`, `rclone:…`), or `ResticRepository.RepositoryPath` for a mounted folder. |
| `Environment` | Variables for restic and rclone (keys, tokens). They're written to a file inside the runner container, never into its configuration. |
| `Files` | Files placed in the runner before it starts (private keys, `known_hosts`, an rclone config). |
| `ExtraArgs` | Extra restic arguments (`-o sftp.args=…`). |
| `Mounts` | Bind or volume mounts the runner needs (local folders, a temporary SMB volume). |
| `Network` | Whether the runner needs network access. Cloud types do; local folders don't. |
| `Cleanup` | Undoes anything `OpenAsync` created, such as the SMB provider's temporary volume or connection. |

### The descriptor

```csharp
public override DestinationDescriptor Descriptor { get; } = new(
    DestinationKind.S3, "S3 / compatible", Icon: "cloud", NamePlaceholder: "S3",
    [
        new(DestinationKeys.Endpoint, "Endpoint", Required: true, Mono: true, Default: "https://s3.amazonaws.com", Hint: "…"),
        new(DestinationKeys.Bucket, "Bucket", Required: true, Mono: true, Half: true),
        new(DestinationKeys.Prefix, "Folder in bucket (optional)", Mono: true, Half: true, Placeholder: "docker"),
        new(DestinationKeys.AccessKeyId, "Access key ID", Section: FieldSection.Credentials, Required: true, Secret: true, Half: true),
        new(DestinationKeys.SecretAccessKey, "Secret access key", FieldType.Password, FieldSection.Credentials, Required: true, Secret: true, Half: true),
    ]);
```

| `DestinationField` | |
|---|---|
| `Key` | Stable key, stored in the settings or secrets and sent by the web client. **Never rename a key once released**: stored destinations use it. Shared keys live in `DestinationKeys`. |
| `Type` | `Text`, `Password`, `Multiline`, `Number`, or `DropboxFolder`. |
| `Section` | `Location` (where the backups are; changing it later means the backups moved) or `Credentials` (can be replaced any time). The edit form treats them differently. |
| `Secret` | Stored encrypted and never sent back to the browser. Every `Password` field must be secret; a guard test checks this. |
| `Hidden` | Kept by Stowaway but not shown in forms (a Dropbox token, recorded SSH host keys). |
| `Required`, `Placeholder`, `Hint`, `Default`, `Mono`, `Half` | How the form renders it. |

`SignIn` names a browser sign-in flow, and `Notes` is text shown under the fields when adding one. `Icon` must be a name `web/src/lib/destinations.ts` knows; add one there if needed.

### Cloud types: derive from `CloudProvider`

Most new types are network services that restic (or rclone) reaches directly. `CloudProvider` implements `OpenAsync` for you, so you only write `Validate` and `Connect`:

```csharp
public sealed class S3Provider : CloudProvider
{
    public override DestinationKind Kind => DestinationKind.S3;
    public override DestinationDescriptor Descriptor { get; } = …;   // as above

    public override string Validate(IReadOnlyDictionary<string, string> values)
    {
        Require(BucketName(S(values, DestinationKeys.Bucket)), "Enter a valid bucket name.");
        Require(Has(values, DestinationKeys.AccessKeyId) && Has(values, DestinationKeys.SecretAccessKey), "Enter the access key and secret key.");
        return $"s3://{S(values, DestinationKeys.Bucket)}{Suffix(Prefix(values))}";
    }

    public override RepositoryConnection Connect(IReadOnlyDictionary<string, string> values) =>
        Connection($"s3:{S(values, DestinationKeys.Endpoint).TrimEnd('/')}/{S(values, DestinationKeys.Bucket)}{Suffix(Prefix(values))}", new()
        {
            ["AWS_ACCESS_KEY_ID"] = Secret(values, DestinationKeys.AccessKeyId),
            ["AWS_SECRET_ACCESS_KEY"] = Secret(values, DestinationKeys.SecretAccessKey),
        });
}
```

The helpers: `S` (trimmed value), `Secret` (untrimmed value), `Has`, `Require`, `Prefix`/`Suffix` (an optional folder inside the bucket), `BucketName`, and `Connection` (network on, no files). Override `Normalize`, `PrepareAsync` or `TakeSignIn` when the type needs them; `SftpProvider` and `DropboxProvider` show how.

Types that aren't network services, such as `LocalFolderProvider` and `SmbProvider`, implement `IDestinationProvider` directly and return mounts from `OpenAsync`. They also have to work with host-side restic on Windows-container engines (`runner.OnHost`), where there is no runner container to mount into.

### Steps

1. Add the type to `DestinationKind` (`src/Stowaway.Core/Backups/BackupModels.cs`) and to the `DestinationKind` type in `web/src/lib/api.ts`.
2. Write the provider in `src/Stowaway.Docker/Destinations/`.
3. Register it in `AddDestinationProviders` and in `DestinationProviders.BuiltIn`.
4. Add tests. Check `Validate` and `Connect` the way `CloudRepositoryTests` does (`tests/Stowaway.Tests/Backups/CloudAndDumpTests.cs`): accepted and rejected values, and the exact repository string and environment. `DestinationProviderTests` checks the descriptor, the registration, and that stored values round-trip unchanged. If you can, test against a real service or a local emulator (MinIO for S3, an SFTP container).
5. Add the type to the user guide (`docs/help/04-destinations.md`).

### Rules for destination providers

- **Secrets go only in `Environment` or `Files`.** Never put them in the repository string, `ExtraArgs`, log lines or the location returned by `Validate`, which the browser sees.
- **Validation is offline and explains itself.** Say what to enter ("Enter the S3 endpoint URL, e.g. …"), not what went wrong internally.
- **Location keys are forever.** Changing what a location field means would point existing destinations somewhere else. Add a new key instead.
- **Clean up.** Anything `OpenAsync` creates, its `Cleanup` removes, also when the run fails.

---

## Tests and the pull request

- `dotnet test` runs unit tests and integration tests against your local Docker engine. Integration tests create only throwaway containers and volumes named `stowaway-test-*` and must never touch anything else on the engine.
- Each provider needs at least one test of its own; database providers need a real dump and drill round trip.
- The pull request checklist in [CONTRIBUTING.md](../.github/CONTRIBUTING.md#before-you-open-a-pull-request) applies: no warnings, lint clean, a CHANGELOG line, and the user guide updated.

Not sure whether a database or service fits? [Open an issue](https://github.com/niogtres/stowaway/issues/new/choose) first and describe how it would be backed up or reached; it's quicker to agree on the approach before the code.
