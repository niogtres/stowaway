# Destinations

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

## What you see at the destination

Because the data is encrypted, you won't see your own files at the destination. In a folder or in Dropbox you'll see a `config` file and the folders `data`, `index`, `keys`, `locks` and `snapshots`. That is the backup. Browse and restore it from Stowaway. The destination list shows where each one is.

## Dropbox

- **Folder:** Dropbox backups always go under `/Stowaway`. You can add a subfolder, for example `/Stowaway/server1`, to keep backups from several computers apart.
- **More than one Dropbox destination:** when you add another one, choose **Same account as …** to reuse the sign-in you already have, and give it a different subfolder.

### Connecting Dropbox from another computer

When you use Stowaway through remote access, Dropbox can't send your browser straight back to the Stowaway computer, so there's one extra step:

1. Choose **Connect Dropbox**. Dropbox opens in a new tab. Sign in and choose **Allow**.
2. The tab then shows an error such as *"localhost refused to connect"*. That's expected. Copy the whole address from that tab's address bar. It starts with `http://localhost:53682/`.
3. Go back to the Stowaway tab, paste the address and choose **Finish**. Stowaway shows **Dropbox connected**. The address works only once and for a few minutes; if it fails, choose **Start again**.

## Editing a destination

Use the pencil button to rename a destination, change its password or keys, or reconnect Dropbox. **Change location** follows backups you have moved, for example a folder you moved in Dropbox or on the NAS. Stowaway only saves the new location if it holds the same backups, so an edit can't point a destination at different ones.

Older Dropbox destinations may sit in the root of your Dropbox. To tidy one up: move its `config` file and its folders into `/Stowaway` (or a subfolder) in Dropbox, then edit the destination and change its location to match.

## Removing a destination

Removing a destination only makes Stowaway forget it. The backups stay where they are, and you can connect them again with the recovery key. A destination that a backup plan still uses can't be removed: point the plan at another destination, or delete the plan, first.

**See also:** [Backup plans](05-plans.md), [Restore](07-restore.md)
