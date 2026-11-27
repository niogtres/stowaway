# Restore

Open **Snapshots**, pick the app and the point in time. Choose **Restore everything**, or **Restore** next to one volume or bind mount, then pick where to:

- **Where it was** (overwrite the current data): the containers using that data are stopped first and started again after. A **safety snapshot** of the current data is taken first, so you can undo the restore.
- **New volume:** restores into a new Docker volume and leaves the original alone, so you can compare or attach it to a test container.
- **Folder on this PC:** restores into a folder, for example to pick a few files out by hand.

To restore only some files or folders, choose **Browse** next to a volume or bind mount and **Restore** on the file or folder you want.

**Database dumps** are restored as files (`.sql` or the database's own format) into a new volume or a folder, never over the running database. Load them with the database's own tools.

**See also:** [Recover a whole app](08-recreate.md), [Restore drills](09-drills.md)
