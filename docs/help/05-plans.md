# Backup plans

A plan says **what** to back up, **how**, **where** and **when**, and how long to keep backups. Open **Plans** and choose **Create plan**.

1. **What:** *All apps* (including ones you add later) or *Selected apps*. An "app" is a compose project or a standalone container, together with its volumes and bind mounts.
2. **How:**
   - **Automatic** (recommended): copies files while the app keeps running. Databases it recognises (PostgreSQL, MySQL/MariaDB, MongoDB, Redis) are dumped with their own tools, so the backup is consistent with no downtime. Apps with a database it can't dump are stopped briefly instead.
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

**See also:** [Destinations](04-destinations.md), [Watching backups run](06-activity.md), [Restore drills](09-drills.md)
