# Recover a whole app

If an app's containers, volumes or network are gone (a deleted stack, a rebuilt Docker installation, a new computer pointing at the same destination), open its latest snapshot and choose **Recreate app**. Stowaway:

1. restores the app's data,
2. recreates missing networks and pulls missing images,
3. recreates the containers with their original settings, and
4. starts them in dependency order (only those that were running when the backup was taken).

Images you **built yourself** can't be pulled from a registry. Stowaway tells you if one is missing; restore the snapshot's *App definition* (compose files, `.env`) to a folder and rebuild the image with `docker compose build` first.

## Business Central

**Recreate app** isn't enough for a Business Central container: recreated from its settings, it sets itself up again from the artifact with a **new** database, and your backed-up database isn't loaded. Stowaway warns you when you choose it.

Every snapshot of a Business Central container includes a restore script instead (`business-central\<container>.ps1` in the *App definition*). To bring the container back, on this or another computer:

1. Open the snapshot and restore **App definition** and **Database dumps** to a folder, for example `D:\Restore`.
2. Switch Docker to Windows containers, and install BcContainerHelper if it isn't there: `Install-Module BcContainerHelper`.
3. In an elevated PowerShell, run the script and point it at the dumps: `.\<container>.ps1 -Dumps 'D:\Restore'`.
4. Sign in with a user of the original container when asked: the restored database keeps its users. If the container had a license file, pass it with `-LicenseFile`.

The script creates the container from the same artifact and settings (authentication, language, memory, ports) and loads the database: a `.bak` if the container was running during the backup, or the database files if it was stopped. Passwords, the license and other secrets aren't stored in the script.

The script is new and hasn't been tested on many computers yet. If it stops with an error, the message says what it expected; please report it.

**See also:** [Restore](07-restore.md), [Restore drills](09-drills.md)
