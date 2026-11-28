# Signing in

There are two ways to sign in, depending on where you open Stowaway.

## On this computer: your Windows account

At `https://localhost:8484` you're signed in with your Windows account automatically: there's no separate password.

- **Local administrators** have access, including accounts that are administrators through a domain group.
- **Standard users** need to be in a local group called `Stowaway Operators`. An administrator creates it once, from an elevated PowerShell:

  ```powershell
  New-LocalGroup -Name "Stowaway Operators" -Description "Can manage Stowaway"
  Add-LocalGroupMember -Group "Stowaway Operators" -Member "<user name>"
  ```

  The user may need to sign out of Windows and back in.

If you see **No access**, your Windows account isn't an administrator or in that group.

## From another computer: a Stowaway account

When [remote access](12-remote-access.md) is on, other computers get a sign-in page instead. Windows accounts don't work there: you sign in with a **Stowaway account** that an administrator created on this computer (**Settings → Remote access → Add account**). Each account has:

- a **user name** and a **password** (at least 8 characters, with upper and lower case letters and a digit or symbol), and
- an **authenticator app** (Microsoft Authenticator, Google Authenticator, 1Password…), connected when the account was created.

To sign in, enter the user name, the password and the 6-digit code the app shows right now. Lost the phone? Choose **Use a recovery code** and enter one of the account's recovery codes; each works once.

While you're signed in remotely:

- Restores, deleting anything, and changing plans, destinations or notifications ask for a **fresh code** from the app; after that, not again for 10 minutes.
- The session ends after **30 minutes** without activity, and after **8 hours** in any case. **Sign out** is at the bottom of the sidebar.
- Some things only work on this computer: managing remote access and its accounts, restarting the service, and showing, checking or confirming recovery keys.

Locked out after too many wrong attempts, or a new phone? An administrator fixes it on this computer: **Settings → Remote access**, then **Password** (sets a new one and lifts the lockout) or **New authenticator** (new QR code and recovery codes). Details: [Remote access](12-remote-access.md).

**See also:** [Remote access](12-remote-access.md), [Access over the internet](13-internet-access.md), [Troubleshooting](14-troubleshooting.md)
