# Remote access

By default only this computer can open Stowaway. To manage it from another computer on your network:

1. **Add an account.** In **Settings → Remote access**, choose **Add account**, pick a user name (your email address works) and a password (at least 8 characters, with upper and lower case letters and a digit or symbol), and choose **Continue**.
2. **Connect an authenticator app.** Scan the QR code with an authenticator app (Microsoft Authenticator, Google Authenticator, 1Password, Bitwarden…). **Save the recovery codes** somewhere safe, away from the phone; each one signs you in once if the phone is lost. Then enter the 6-digit code the app shows and choose **Confirm**.
3. **Switch it on.** Tick **Allow access from other computers** and enter the names other computers use to reach this one (the computer name, its full DNS name, and/or its IP address; the suggestions below the box help). Only these names are accepted.
4. **Pick a certificate:**
   - **Issued by Stowaway:** works straight away, but browsers warn until you trust it. Choose **Download the certificate** and, on each computer you'll use, double-click the file → *Install Certificate* → *Local Machine* → *Trusted Root Certification Authorities*.
   - **A certificate installed on this computer:** if you have one for this computer's name from your own or a public certificate authority, import it (with its private key) into the computer's *Personal* store first, then pick it here.
5. **Choose networks** to allow through Windows Firewall: *Domain* and *Private* are on by default; leave *Public* off unless you're sure.
6. Choose **Save**, then **Restart service**. After a few seconds, other computers can open `https://<name>:8484`.

Signing in from another computer needs the user name, password and the current code from the app (or a recovery code).

- **Lockouts:** 5 wrong attempts from one address lock that address out of the account for 15 minutes, so strangers guessing can't lock *you* out. 20 wrong attempts from anywhere lock the whole account for 15 minutes. Setting a new password (on this computer) lifts both at once.
- **Sessions** end after 30 minutes without activity, and after 8 hours in any case.
- **Protected changes** ask for a fresh code from the app: restoring, recreating an app, deleting anything, and creating or changing plans, destinations or notifications. After that, you're not asked again for 10 minutes. Backing up, checking and test restores (drills) don't ask.
- **Alerts:** a sign-in from a new address, a lockout, or a recovery code being used is sent to your notification channels (those set to report failures) and to the Windows Event Log.
- **Audit log:** **Settings → Audit log** lists sign-ins and every change made through Stowaway, with who and from which address, for 180 days.

For safety, these only work **on this computer**: managing remote access and its accounts, restarting the service, and showing recovery keys.

To turn remote access off, untick it, **Save** and **Restart service**. The firewall rule is removed.

**See also:** [Access over the internet](13-internet-access.md), [Signing in](03-signing-in.md)
