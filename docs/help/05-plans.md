# Backup plans

A plan says **what** to back up, **how**, **where** and **when**, and how long to keep backups. Open **Plans** and choose **Create plan**.

1. **What:** *All apps* (including ones you add later) or *Selected apps*. An "app" is a compose project or a standalone container, together with its volumes, its databases and the folders inside its project folder. Folders mapped in from elsewhere on the computer, drives and network shares aren't part of the app and aren't backed up (see [Before you start](01-before-you-start.md)).
2. **How:**
   - **Automatic** (recommended): copies files while the app keeps running. Databases it recognises (PostgreSQL, MySQL/MariaDB, MongoDB, Redis; SQL Server in Windows containers) are dumped with their own tools, so the backup is consistent with no downtime. Apps with a database it can't dump are stopped briefly instead.
   - **Live:** copies files while everything runs. Fine for apps without databases.
   - **Stop & start:** stops the app's containers, copies, and starts them again. The most consistent; the app is briefly down.
3. **Where:** a destination.
4. **When & keep:** how often (hourly, every 6 hours, daily, weekly, or a custom schedule) and how many backups to keep per hour, day, week, month and year. Older backups are removed automatically, and the space is reclaimed once a week.

If the computer was off when a backup was due, the plan catches up once when it's back.

The **Dashboard** shows how much of your data is protected. **Inventory** lists every app, container, volume and bind mount with its protection status:

| Status | Meaning |
|---|---|
| Protected | Recent successful backup |
| Pending | In a plan, first backup not done yet |
| Stale | Covered, but the last successful backup is overdue |
| Failing | The last backup failed |
| Unprotected | Not in any plan |
| Stateless | Nothing to back up (no volumes or mounts) |
| Excluded | Left out on purpose (for example a whole drive mounted into a container) |

You can also back up a single app at any time with **Back up now** in **Inventory**.

## Database logins

Stowaway logs in to databases with the credentials the container was set up with (the official images' variables, including Docker secrets). If a dump fails because it can't log in, for example because the password was changed after setup, click **Login** next to the database in **Inventory**, enter a user and password (and optionally one database), use **Test these**, and **Save**. The password is stored encrypted and used only for dumps. **Use automatic** goes back to the container's own settings.

## SQL Server in Windows containers

Windows containers such as Business Central keep their SQL Server databases inside the container, where a volume backup can't see them. Stowaway backs them up separately:

- **Running container:** each user database is backed up with SQL Server's own `BACKUP DATABASE … WITH COPY_ONLY, CHECKSUM` (copy-only, so it doesn't disturb any backups you make yourself) and the `.bak` files go into the snapshot.
- **Stopped container:** the database files (`.mdf`, `.ldf`) are copied out without starting the container. Stowaway looks in `C:\databases` (where Business Central containers keep them) and wherever it saw the databases the last time it backed up the container while it ran. Copying from a stopped container updates the modified time of the container's disk file; nothing else changes.

Stowaway logs in with Windows authentication inside the container, which needs no setup in Business Central images. If your image needs a SQL login, set one under **Login** (see *Database logins* above).

**See also:** [Destinations](04-destinations.md), [Watching backups run](06-activity.md), [Restore drills](09-drills.md)
