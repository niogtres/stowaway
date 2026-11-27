# Troubleshooting

| Problem | What to do |
|---|---|
| **"Docker isn't running"** | Start Docker Desktop (or your WSL engine). Backups that were due run once it's back. |
| **Browser says the connection isn't private** on this computer | The service adds its certificate to Windows automatically when it starts. Restart the **Stowaway** service (or the computer), then reopen the browser. |
| **No access** | Your Windows account needs to be an administrator or in `Stowaway Operators` ([section 3](03-signing-in.md)). The service log says why it refused (look for *Denied*). |
| **A backup failed** | Open it in **Activity**: each step shows its log. Common causes: the destination is unreachable or full, a share password changed, or a database dump couldn't log in (set the database's **Login** in Inventory; see [Backup plans](05-plans.md)). |
| **I can't see my backup at the destination** | Backups are encrypted, so you see restic's folders (`config`, `data`, `index`, `keys`, `locks`, `snapshots`), not your files. Dropbox backups are under `/Stowaway` (older Dropbox destinations may be in the root). Browse and restore from **Snapshots** ([Destinations](04-destinations.md)). |
| **A destination can't be removed** | A backup plan still uses it. Point the plan at another destination, or delete the plan, first. |
| **Dropbox sign-in ends on "localhost refused to connect"** | Expected when you use Stowaway from another computer: copy that page's address into the Add destination window and choose **Finish** ([Destinations](04-destinations.md)). |
| **"Repository is locked"** | Stowaway clears locks left behind by its own interrupted runs automatically. A lock held by something else (for example restic run by hand) has to be removed by whoever created it. |
| **A folder or drive shows *not included*** | It's mapped into a container from outside the app (elsewhere on the computer, a drive or a network share). Stowaway backs up apps, not general files: use your file backup for it, or move the data into a Docker volume or the app's compose project folder. |
| **No tray icon** | Look in the **^** overflow next to the clock. It starts after installing and at every sign-in. |
| **Remote access doesn't connect** | Check **Settings → Remote access** for a message (for example a missing certificate), that the name you use is in the list, and that the network type is allowed through the firewall. Behind a proxy: check that the proxy's address is the one entered, and the nginx error log. |
| **"Confirm with your authenticator" keeps coming back** | Each code works once. Wait for the app to show the next code. |
| **Lost the authenticator phone** | Sign in with a recovery code, or on this computer choose **New authenticator** for the account. |

**Logs** are in `C:\ProgramData\Stowaway\logs` (one file per day, kept for 14 days).

**See also:** [Signing in](03-signing-in.md), [Remote access](12-remote-access.md)
