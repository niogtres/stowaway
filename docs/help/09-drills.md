# Restore drills

A backup you've never restored is a hope, not a backup. With **Prove the backups restore, weekly** ticked in a plan (the default for new plans), Stowaway proves each app's latest backup can be restored, without touching the real app:

- files are restored into scratch space and checked against the backup;
- database dumps are loaded into a temporary database container and queried.

A failed drill shows up in **Activity** and sends a notification. Snapshots that passed show **Verified restorable**. You can run a drill any time with **Verify restore** on a snapshot.

**See also:** [Restore](07-restore.md), [Notifications](10-notifications.md)
