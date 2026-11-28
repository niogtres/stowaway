# Writing a provider

Stowaway backs up databases and writes to destinations through **providers**: one class per database family or destination type, behind a small interface. **App profiles** add what Stowaway knows about one product that runs in containers, such as Business Central. This guide explains those interfaces, when Stowaway calls each method, and what a new one needs before it can be merged.

You'll need C# and a working [development setup](../.github/CONTRIBUTING.md#development-setup). Read [DESIGN.md](DESIGN.md) §3 (architecture) first if you haven't.

## Why built in, not plugins

Providers are compiled into Stowaway and registered with dependency injection. Stowaway doesn't load plugin DLLs at run time, and that's deliberate (DESIGN.md §10, decision 9). The service runs as LocalSystem and holds every repository password and cloud token, so any code loaded into it could read all of them. A new provider therefore arrives as a pull request with tests, and ships in the next release.

If an extension ever has to live outside this repository, it will run as a separate program that Stowaway talks to. Nothing like that exists yet; open an issue if you need it.

| | Database providers | Destination providers | App profiles |
|---|---|---|---|
| Interface | `IDatabaseProvider` | `IDestinationProvider` | `IAppProfile` |
| Code | `src/Stowaway.Docker/Databases/` | `src/Stowaway.Docker/Destinations/` | `src/Stowaway.Core/Profiles/` (pure: no Docker, no I/O) |
| Shared types (no Docker) | `src/Stowaway.Core/Databases/` | `src/Stowaway.Core/Destinations/` | same folder |
| Registration | `AddDatabaseProviders` in `src/Stowaway.Docker/ServiceCollectionExtensions.cs` | `AddDestinationProviders` (same file) and `DestinationProviders.BuiltIn` | `AppProfiles.BuiltIn` |
| Guard tests | `DatabaseProviderTests` | `DestinationProviderTests` | `AppProfileTests` |

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
2. `TakeSignIn(session)` runs only if your descriptor names a browser sign-in (`SignIn`: "dropbox" or "onedrive", a backend of `RcloneSignInService`). It returns the credentials the sign-in produced, such as a token.
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
| `UpdatedValues` | Values that changed while opening, such as a refreshed OneDrive token. Stowaway saves them to the stored destination. |

`AppendOnly(values)` says the repository only accepts new data (an append-only REST server). Stowaway then skips retention and prune for it, and logs that the server's owner removes old snapshots.

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
| `Type` | `Text`, `Password`, `Multiline`, `Number`, `StowawayFolder` (a subfolder of /Stowaway in a cloud drive), or `Toggle` (on is `DestinationKeys.On`). |
| `Section` | `Location` (where the backups are; changing it later means the backups moved) or `Credentials` (can be replaced any time). The edit form treats them differently. |
| `Secret` | Stored encrypted and never sent back to the browser. Every `Password` field must be secret; a guard test checks this. |
| `Hidden` | Kept by Stowaway but not shown in forms (a sign-in token, a OneDrive drive id, recorded SSH host keys). |
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

The helpers: `S` (trimmed value), `Secret` (untrimmed value), `Has`, `Require`, `Prefix`/`Suffix` (an optional folder inside the bucket), `BucketName`, and `Connection` (network on, no files). Override `Normalize`, `PrepareAsync`, `TakeSignIn`, `OpenAsync` or `AppendOnly` when the type needs them; `SftpProvider`, `DropboxProvider`, `RestServerProvider` and `OneDriveProvider` show how.

Types that aren't network services, such as `LocalFolderProvider` and `SmbProvider`, implement `IDestinationProvider` directly and return mounts from `OpenAsync`. They also have to work with host-side restic on Windows-container engines (`runner.OnHost`), where there is no runner container to mount into.

### Steps

1. Add the type to `DestinationKind` (`src/Stowaway.Core/Backups/BackupModels.cs`) and to the `DestinationKind` type in `web/src/lib/api.ts`.
2. Write the provider in `src/Stowaway.Docker/Destinations/`.
3. Register it in `AddDestinationProviders` and in `DestinationProviders.BuiltIn`. A new browser sign-in also needs a `SignInBackend` in `RcloneSignInService.Backends`.
4. Add tests. Check `Validate` and `Connect` the way `CloudRepositoryTests` does (`tests/Stowaway.Tests/Backups/CloudAndDumpTests.cs`): accepted and rejected values, and the exact repository string and environment. `DestinationProviderTests` checks the descriptor, the registration, and that stored values round-trip unchanged. If you can, test against a real service or a local emulator (MinIO for S3, an SFTP container).
5. Add the type to the user guide (`docs/help/04-destinations.md`).

### Rules for destination providers

- **Secrets go only in `Environment` or `Files`.** Never put them in the repository string, `ExtraArgs`, log lines or the location returned by `Validate`, which the browser sees.
- **Validation is offline and explains itself.** Say what to enter ("Enter the S3 endpoint URL, e.g. …"), not what went wrong internally.
- **Location keys are forever.** Changing what a location field means would point existing destinations somewhere else. Add a new key instead.
- **Clean up.** Anything `OpenAsync` creates, its `Cleanup` removes, also when the run fails.
- **Don't let credentials expire silently.** If the service replaces or expires tokens, refresh them in `OpenAsync` and return the new ones in `UpdatedValues`. A destination that stops working months after setup is the worst kind of backup failure.

### Worked example: OneDrive

Three destination types were added at the same time. Two of them show how small a typical provider is. The third, OneDrive, is the worked example because it didn't fit the interface as it was.

**The simple ones.** `RestServerProvider` and `GcsProvider` (`ServerProviders.cs`) are about 60 lines each: a descriptor, `Validate` and `Connect`. Both follow the secrets rule without extra work.

- The REST server's user and password go in `RESTIC_REST_USERNAME`/`RESTIC_REST_PASSWORD`, never into the `rest:https://user:pw@…` form restic also accepts. `Validate` refuses an address with a user in it and says where it goes instead.
- The Google service-account key goes in as a file under `RepositoryConnection.SecretsDir`, and `GOOGLE_APPLICATION_CREDENTIALS` points at it.
- A self-signed REST server certificate travels the same way, as a file named by `RESTIC_CACERT`.

Both runners accept these: on Windows-container engines the host runner rewrites `/run/stowaway/…` paths in the environment to its own work folder.

The REST server needed one new member: **`AppendOnly`**. A server started with `--append-only` refuses deletes, which is its point: ransomware on this computer can't remove the backups. Before, retention and the weekly prune would have failed on every run. Now the provider says the destination is append-only, and `BackupService` skips both with a log line saying why. The member has a default (`false`), so no other provider changed.

**OneDrive: what was different.** On the surface, OneDrive is Dropbox again: rclone, a browser sign-in, backups under `/Stowaway`. Copying `DropboxProvider` would have compiled, and failed in two ways.

**Decision 1: one sign-in service, not a second copy.** The sign-in (`rclone authorize`, the local callback port, the paste-the-address step for remote browsers) was written for Dropbox only, down to the URL check that keeps the server from following a redirect anywhere else. It became `RcloneSignInService`, with one `SignInBackend` per service: rclone's backend name, a label, and the one host whose sign-in page may be followed (`login.microsoftonline.com` for OneDrive). The API routes and the web component (`SignInConnect`) take the service from the descriptor's `SignIn`. The folder field became `FieldType.StowawayFolder`, because "Dropbox folder" no longer described it. *Rule applied:* a second user of a mechanism is the time to generalize it, not the first.

**Decision 2: find the drive after the sign-in.** rclone's OneDrive remote needs a drive id and type, which the token alone doesn't give. `PrepareAsync` reads them from Microsoft Graph (`/me/drive`) with the new access token. Two edge cases shaped it:

- **Reconnecting** with a different account is refused when the drive id changes. The repository-id check would catch it too, but later and with a less clear message.
- **Reusing** another OneDrive destination's account copies only its credentials-section fields, and the copied token has usually expired. So the drive id and type are credentials-section fields too (hidden), and the Graph lookup only runs for a fresh sign-in.

**Decision 3: save the refreshed token.** This is the part that changed the interface. Dropbox's refresh token never expires, so rclone refreshing the access token in memory on every run is fine. Microsoft *replaces* the refresh token when it's used and lets an unused one expire after 90 days. With the Dropbox design, backups to OneDrive would have stopped about three months after the sign-in, long after anyone remembered setting it up.

- `OpenAsync` checks the token's expiry (`RcloneTokens.NeedsRefresh`).
- When the access token is about to expire, a short rclone run against a temporary config file refreshes it, and the file is read back. That's in a runner, or `rclone.exe` on the host.
- The new token is returned in **`RepositoryAccess.UpdatedValues`**.
- `ResticRepository` stores it: it re-reads the destination first, so a rename made meanwhile isn't overwritten, and it never fails the run over it.

Refreshes are serialized, because two at once would each receive a different new token. Checking the expiry first means at most one extra rclone run an hour, not one per restic command.

**What it cost.** Two new members with defaults (`AppendOnly`, `UpdatedValues`), one service generalized, and one field type renamed. Every existing provider compiled and passed its tests unchanged. The tests stub Microsoft Graph with an `HttpMessageHandler`. They also give `RcloneTokens` no runner, so a test fails loudly if the provider refreshes when it shouldn't.

**Tested for real, and still open.** The REST server has an integration test against a real `restic/rest-server` container in append-only mode, with a user and password (`RestServerIntegrationTests`). Backups go in, a wrong password is refused, and deleting a snapshot fails, which is exactly what `AppendOnly` is there to avoid. OneDrive and Google Cloud Storage need an account, so they haven't run against the real service yet. Try them once by hand before a release, including a OneDrive backup more than an hour after the sign-in, which is the first refresh.

---

## App profiles

A database provider knows how to back up one kind of database. An app profile knows one **product**: how to recognize its containers, which of its host folders belong to it, and what to store with a snapshot so it can be brought back. Write a profile when the generic rules back up the right data but miss product knowledge; write a database provider when Stowaway can't get at the data at all.

| Question | Answer it with |
|---|---|
| "Stowaway can't dump this database." | a database provider |
| "Stowaway backs up the data, but leaves out a folder this product always needs." | a profile (`Bind`) |
| "The snapshot has the data, but I can't get the app running again from it." | a profile (`DefinitionFiles`, `RecreateWarning`) |
| "The card should say what this is." | a profile (`Detect`) |

### The interface

```csharp
public interface IAppProfile
{
    string Id { get; }                                    // stored in snapshots: never change it
    AppProduct? Detect(IReadOnlyDictionary<string, string> labels);
    (string? Reason, string? Note) Bind(string defaultReason, string windowsSource, string destination);
    IReadOnlyDictionary<string, string> DefinitionFiles(ContainerDefinition container, DateTimeOffset backedUpAt);
    string? DefinitionDescription { get; }
    string? RecreateWarning(string containerName);
}
```

| Member | Called by | When |
|---|---|---|
| `Detect` | `InventoryBuilder` | every inventory scan, per container. The labels include the image's. The first profile that answers wins. The returned `AppProduct` (name, version, country, a note for the card) is kept in `app.json` with every snapshot. |
| `Bind` | `InventoryBuilder.DecideBind` (inventory, sizing and backups all use it) | only for bind mounts the generic rule leaves out **and** the owner could include (`HostPaths.CanInclude`). Return a null reason plus a note to include the folder, or a clearer reason to keep it out. Drives, system folders, shares and sockets never reach a profile. |
| `DefinitionFiles` | `BackupService` while it writes the app definition | every backup, per recognized container. `ContainerDefinition` is the container's saved `docker inspect`: environment, memory, DNS, ports, bind mounts. The files go to the snapshot's *App definition*, next to the container settings. |
| `DefinitionDescription` | `SnapshotCatalog` | shown where the snapshot lists its contents ("container settings and …"). |
| `RecreateWarning` | `RestoreService` preview | before **Recreate app** brings back a container of this product. |

Profiles are pure functions over data Stowaway already has. That makes them testable without Docker, and they can't stop, change or slow down anything. It's also why they live in `Stowaway.Core`, not next to the providers.

### Rules for app profiles

- **Add to the generic rules, never replace them.** The database provider still backs up the database. A profile can include only folders the owner could include anyway; `DecideBind` enforces this, and `AppProfileTests` checks it with a profile that tries to take everything.
- **Recognize by what the vendor sets, not by names.** Use labels from the vendor's image (they survive renames and private registries). Never use container names: those belong to the owner.
- **Explain every exclusion.** A folder you deliberately leave out gets a reason that says what it is and where to back it up instead.
- **The environment holds secrets.** `ContainerDefinition.Env` is the container's real environment. Pick the settings you need by name and never copy anything else into a file, note or log. Strip tokens from URLs. A test must check that known secret values don't appear in your output.
- **Generated scripts are run by people.** Say at the top what the script does and how to run it. Make it stop with a clear message when the input isn't what it expects, never guess.
- **Ids are forever.** `Id` is stored in snapshots. If a profile is later removed, its snapshots are shown without a product (`AppProfiles.Of` returns null).

### Worked example: Business Central

This is how the Business Central profile (`BusinessCentralProfile`, `BusinessCentralScript`) came about, with the decisions along the way. It's the reference for the next profile.

**The situation.** Business Central runs in Windows containers made with BcContainerHelper. The SQL Server provider already backs up the database inside the container: a `.bak` while it runs, or the database files while it's stopped. Inventory still got three things wrong:

1. The card said "SQL Server", nothing more.
2. All four host folders were left out, including the container's own `my` folder (setup scripts, certificates).
3. A restore brought back the database, but no running Business Central. **Recreate app** recreates the container from its saved settings, so it starts from the artifact again with a fresh database, and the backup is never loaded.

**Decision 1: a profile, not a database provider.** Stowaway could already get at all the data, so a new `IDatabaseProvider` would have duplicated the SQL Server one. What was missing was product knowledge, so this became a new kind of extension. *Rule applied:* add to the generic rules, don't replace them.

**Decision 2: recognize it from the image labels.** We asked for a real container's labels (read-only `docker inspect`, with secret-looking values blanked) before writing any code. Microsoft's images carry `maintainer: Dynamics SMB`, plus `version`, `country`, `cu` and `nav`. A container counts as Business Central when the maintainer matches and a version is present; versions below 13 are shown as Dynamics NAV. The image name was rejected as the signal, because it changes between registries and tags. *Rule applied:* recognize by what the vendor sets.

**Decision 3: which folders.** BcContainerHelper maps four folders into every container:

| Folder (container path) | Decision | Why |
|---|---|---|
| `…\Extensions\<name>\my` (`C:\run\my`) | **included** by default | It belongs to this one container: its setup scripts and certificates. It's small. |
| `C:\bcartifacts.cache` (`C:\dl`) | left out, with its own reason | It's a download cache that refills itself, and it can be many GB. The owner can still choose **Include**. |
| `C:\ProgramData\BcContainerHelper` | left out, with its own reason | It's shared by every Business Central container on the computer, so it isn't this app's. |
| `drivers\etc` | left out (generic rule) | It's a system folder. A profile can't include it, by design. |

The profile decides by the container path (`C:\run\my`), which BcContainerHelper always uses, rather than the host path, which the owner can change.

**Decision 4: a restore script instead of Recreate.** The snapshot already holds the container's full `docker inspect`, so a backup can write a script that runs `New-BcContainer` with the same artifact URL, authentication, locale, memory, DNS, ports and extra folders. The script then loads the backup:

- a `.bak` (made while the container ran) goes in through `-bakFile`;
- database files (copied while it was stopped) are attached in place of the new container's own database.

It finds the backup through the `sqlserver.json` manifest the SQL Server provider writes, so the two cooperate through a file format, not through code. **Recreate app** gets a warning that points to the script.

**Decision 5: what stays out of the script.** The container's environment held `securePassword`, `passwordKeyFile`, `licenseFile` and `username`. None of them is copied. The script asks for a sign-in when it runs, and takes the license as a parameter. Insider artifact URLs carry a SAS token in the query: it's cut off, and the script asks for it as `-SasToken`. A test lists the secret values and checks that none appears. *Rule applied:* the environment holds secrets.

**What we got wrong first.** The first version had `if (BusinessCentral.Is(...))` checks in the inventory builder, the backup service, the restore preview, the snapshot catalog and the web client. A second product would have meant edits in all of them, and nothing stopped a product rule from including a whole drive. Moving it behind `IAppProfile` turned those checks into one lookup (`AppProfiles.Of(product)`). The drive rule moved into `DecideBind`, where it holds for every profile. The web client now shows `product.note` instead of checking for Business Central by name.

**What's still open.** Unit tests cover detection, the folder rules and the script text. The script itself has not yet run against a real Business Central host, and the guide's own bar is a real round trip: back up, restore to a folder, run the script on a second machine, sign in. Until that has passed, the help topic calls the script new and asks for reports. Do the same for anything you can't test in CI.

---

## Tests and the pull request

- `dotnet test` runs unit tests and integration tests against your local Docker engine. Integration tests create only throwaway containers and volumes named `stowaway-test-*` and must never touch anything else on the engine.
- Each provider needs at least one test of its own; database providers need a real dump and drill round trip. App profiles need unit tests for detection, folder decisions and every generated file, including a check that secrets stay out.
- Test classes that start the service (`WebApplicationFactory`, e.g. `ServiceFactory` or `SignInFactory`) must be in `[Collection(ServiceHosts.Name)]`. The factories switch sign-in on and off through process-wide environment variables, so two hosts built at once see each other's settings and fail at random.
- The pull request checklist in [CONTRIBUTING.md](../.github/CONTRIBUTING.md#before-you-open-a-pull-request) applies: no warnings, lint clean, a CHANGELOG line, and the user guide updated.

Not sure whether a database or service fits? [Open an issue](https://github.com/niogtres/stowaway/issues/new/choose) first and describe how it would be backed up or reached; it's quicker to agree on the approach before the code.
