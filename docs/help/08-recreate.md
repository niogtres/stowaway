# Recover a whole app

If an app's containers, volumes or network are gone (a deleted stack, a rebuilt Docker installation, a new computer pointing at the same destination), open its latest snapshot and choose **Recreate app**. Stowaway:

1. restores the app's data,
2. recreates missing networks and pulls missing images,
3. recreates the containers with their original settings, and
4. starts them in dependency order (only those that were running when the backup was taken).

Images you **built yourself** can't be pulled from a registry. Stowaway tells you if one is missing; restore the snapshot's *App definition* (compose files, `.env`) to a folder and rebuild the image with `docker compose build` first.

**See also:** [Restore](07-restore.md), [Restore drills](09-drills.md)
