# Signing in

You're signed in with your Windows account automatically: there's no separate password on this computer.

- **Local administrators** have access.
- **Standard users** need to be in a local group called `Stowaway Operators`. An administrator creates it once, from an elevated PowerShell:

  ```powershell
  New-LocalGroup -Name "Stowaway Operators" -Description "Can manage Stowaway"
  Add-LocalGroupMember -Group "Stowaway Operators" -Member "<user name>"
  ```

  The user may need to sign out of Windows and back in.

If you see **No access**, your Windows account isn't an administrator or in that group.

**See also:** [Remote access](12-remote-access.md), [Troubleshooting](14-troubleshooting.md)
