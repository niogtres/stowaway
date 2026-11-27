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

**See also:** [Backup plans](05-plans.md), [Restore](07-restore.md)
