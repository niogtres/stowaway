# DockerBackup user guide

DockerBackup backs up the containers, compose stacks, volumes and bind mounts on a Windows 10/11 computer running Docker, on a schedule, and restores them when you need to. It runs as a Windows service and you manage it from a web page at **https://localhost:8484**.

This guide covers installing it, making your first backup, restoring, and keeping an eye on things.

## Contents

1. [Before you start](#1-before-you-start)
2. [Install](#2-install)
3. [Signing in](#3-signing-in)
4. [Add a destination](#4-add-a-destination)
5. [Create a backup plan](#5-create-a-backup-plan)
6. [Watch backups run](#6-watch-backups-run)
7. [Restore](#7-restore)
8. [Recover a whole app after a disaster](#8-recover-a-whole-app-after-a-disaster)
9. [Restore drills](#9-restore-drills)
10. [Notifications](#10-notifications)
11. [The tray icon](#11-the-tray-icon)
12. [Remote access](#12-remote-access)
13. [Troubleshooting](#13-troubleshooting)
14. [Uninstall](#14-uninstall)

## 1. Before you start

- **Docker:** Docker Desktop (Linux containers) or Docker Engine in WSL. Windows containers aren't supported yet.
- **Docker Desktop only runs while someone is signed in to Windows.** Backups that are due while Docker is stopped wait, and run once Docker is back. On a server, turn on *Start Docker Desktop when you sign in* and set up automatic sign-in, or use Docker Engine in WSL.
- **Somewhere to keep backups:** a second disk, a network share, or cloud storage (see [section 4](#4-add-a-destination)). A backup on the same disk as the data doesn't protect you from that disk failing.
- **An administrator account** to install it.

## 2. Install

1. Run `DockerBackup-<version>-x64.msi` and follow the steps.
2. The installer adds the **DockerBackup** service (it starts automatically with Windows), a Start-menu shortcut, and a tray icon for every user.
3. Open **DockerBackup** from the Start menu. The first visit may take a few seconds while it looks at what Docker is running.

Everything DockerBackup stores (settings, history, logs, certificates and the keys to your backups) lives in `C:\ProgramData\DockerBackup`. Only administrators and the service can open that folder.

## 3. Signing in

You're signed in with your Windows account automatically: there's no separate password on this computer.

- **Local administrators** have access.
- **Standard users** need to be in a local group called `DockerBackup Operators`. An administrator creates it once, from an elevated PowerShell:

  ```powershell
  New-LocalGroup -Name "DockerBackup Operators" -Description "Can manage DockerBackup"
  Add-LocalGroupMember -Group "DockerBackup Operators" -Member "<user name>"
  ```

  The user may need to sign out of Windows and back in.

If you see **No access**, your Windows account isn't an administrator or in that group.

## 4. Add a destination

A destination is where backups go. Open **Destinations**, choose **Add destination**, and pick one:

| Destination | Use it for | You'll need |
|---|---|---|
| **Local folder** | A second internal or USB disk | A folder path such as `D:\Backups` |
| **Network share** | A NAS or another PC (SMB) | `\\server\share\folder`, a user name and password |
| **S3 / compatible** | Amazon S3, Wasabi, MinIO, Cloudflare R2… | Endpoint, bucket, access key and secret key |
| **Backblaze B2** | Low-cost cloud storage | Bucket, key ID and application key |
| **Azure Blob** | Microsoft Azure | Storage account, key and container |
| **SFTP server** | Any server you can SSH into | Host, user and an SSH key; the server's identity is remembered at setup and checked every time |
| **Dropbox** | Your Dropbox account | Choose **Connect Dropbox** and approve access in the browser window that opens |

Backups are **encrypted** before they leave your computer. Each destination has its own **recovery key**:

> **Save the recovery key somewhere safe, away from this computer.** If this computer is lost, you need it (and the destination) to get your data back. Without it, nobody can read the backups, including you.

You can show it again later with the key button on the destination. It can only be shown on this computer, never through remote access.

## 5. Create a backup plan

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

## 6. Watch backups run

**Activity** shows backups as they run (with progress) and the history of every backup, restore and check. Open an entry to see each step and its log.

Once a week, in the night, DockerBackup also checks a sample of each destination for damage.

## 7. Restore

Open **Snapshots**, pick the app and the point in time. Choose **Restore everything**, or **Restore** next to one volume or bind mount, then pick where to:

- **Where it was** (overwrite the current data): the containers using that data are stopped first and started again after. A **safety snapshot** of the current data is taken first, so you can undo the restore.
- **New volume:** restores into a new Docker volume and leaves the original alone, so you can compare or attach it to a test container.
- **Folder on this PC:** restores into a folder, for example to pick a few files out by hand.

To restore only some files or folders, choose **Browse** next to a volume or bind mount and **Restore** on the file or folder you want.

**Database dumps** are restored as files (`.sql` or the database's own format) into a new volume or a folder, never over the running database. Load them with the database's own tools.

## 8. Recover a whole app after a disaster

If an app's containers, volumes or network are gone (a deleted stack, a rebuilt Docker installation, a new computer pointing at the same destination), open its latest snapshot and choose **Recreate app**. DockerBackup:

1. restores the app's data,
2. recreates missing networks and pulls missing images,
3. recreates the containers with their original settings, and
4. starts them in dependency order (only those that were running when the backup was taken).

Images you **built yourself** can't be pulled from a registry. DockerBackup tells you if one is missing; restore the snapshot's *App definition* (compose files, `.env`) to a folder and rebuild the image with `docker compose build` first.

## 9. Restore drills

A backup you've never restored is a hope, not a backup. With **Prove the backups restore, weekly** ticked in a plan (the default for new plans), DockerBackup proves each app's latest backup can be restored, without touching the real app:

- files are restored into scratch space and checked against the backup;
- database dumps are loaded into a temporary database container and queried.

A failed drill shows up in **Activity** and sends a notification. Snapshots that passed show **Verified restorable**. You can run a drill any time with **Verify restore** on a snapshot.

## 10. Notifications

In **Settings → Notifications**, add a channel to hear about failed backups (and when they work again):

- **Email** (any SMTP server),
- **Webhook** (generic JSON, or Slack-compatible for Slack, Discord and Mattermost),
- **ntfy** push notifications to your phone.

Use **Send test** to check a channel. Warnings and errors are also written to the Windows **Application** event log under the source `DockerBackup`.

## 11. The tray icon

The DockerBackup icon in the notification area shows the protection status at a glance (for example *100% protected* or *2 backups failing*) and pops up a notice when backups start failing. Right-click it to open DockerBackup, **Activity** or **Snapshots**. Double-click opens the dashboard.

## 12. Remote access

By default only this computer can open DockerBackup. To manage it from another computer on your network:

1. **Add an account.** In **Settings → Remote access**, choose **Add account**, pick a user name and a password (at least 12 characters), and choose **Continue**.
2. **Connect an authenticator app.** Scan the QR code with an authenticator app (Microsoft Authenticator, Google Authenticator, 1Password, Bitwarden…). **Save the recovery codes** somewhere safe, away from the phone; each one signs you in once if the phone is lost. Then enter the 6-digit code the app shows and choose **Confirm**.
3. **Switch it on.** Tick **Allow access from other computers** and enter the names other computers use to reach this one (the computer name, its full DNS name, and/or its IP address; the suggestions below the box help). Only these names are accepted.
4. **Pick a certificate:**
   - **Issued by DockerBackup:** works straight away, but browsers warn until you trust it. Choose **Download the certificate** and, on each computer you'll use, double-click the file → *Install Certificate* → *Local Machine* → *Trusted Root Certification Authorities*.
   - **A certificate installed on this computer:** if you have one for this computer's name from your own or a public certificate authority, import it (with its private key) into the computer's *Personal* store first, then pick it here.
5. **Choose networks** to allow through Windows Firewall: *Domain* and *Private* are on by default; leave *Public* off unless you're sure.
6. Choose **Save**, then **Restart service**. After a few seconds, other computers can open `https://<name>:8484`.

Signing in from another computer needs the user name, password and the current code from the app (or a recovery code). After 5 wrong attempts the account is locked for 15 minutes; an administrator can unlock it at once by setting a new password. Sessions last 12 hours.

For safety, these only work **on this computer**: managing remote access and its accounts, restarting the service, and showing recovery keys.

To turn remote access off, untick it, **Save** and **Restart service**. The firewall rule is removed.

## 13. Troubleshooting

| Problem | What to do |
|---|---|
| **"Docker isn't running"** | Start Docker Desktop (or your WSL engine). Backups that were due run once it's back. |
| **Browser says the connection isn't private** on this computer | The service adds its certificate to Windows automatically when it starts. Restart the **DockerBackup** service (or the computer), then reopen the browser. |
| **No access** | Your Windows account needs to be an administrator or in `DockerBackup Operators` ([section 3](#3-signing-in)). |
| **A backup failed** | Open it in **Activity**: each step shows its log. Common causes: the destination is unreachable or full, a share password changed, or a database dump couldn't sign in (use *Stop & start* for that app). |
| **"Repository is locked"** | DockerBackup clears locks left behind by its own interrupted runs automatically. A lock held by something else (for example restic run by hand) has to be removed by whoever created it. |
| **Remote access doesn't connect** | Check **Settings → Remote access** for a message (for example a missing certificate), that the name you use is in the list, and that the network type is allowed through the firewall. |
| **Lost the authenticator phone** | Sign in with a recovery code, or on this computer choose **New authenticator** for the account. |

**Logs** are in `C:\ProgramData\DockerBackup\logs` (one file per day, kept for 14 days).

## 14. Uninstall

Uninstall **DockerBackup** from *Settings → Apps* in Windows. This removes the service, the tray icon, the firewall rule and the certificate it trusted.

It **keeps** `C:\ProgramData\DockerBackup`, which holds the keys to your backups. Your backups at the destinations are never deleted. If you won't need those backups any more, and you've saved the recovery keys you want to keep, you can delete that folder yourself.
