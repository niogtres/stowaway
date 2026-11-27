# Security policy

Stowaway runs as `LocalSystem`, controls Docker and holds the keys to your backups, so security reports are taken seriously.

## Reporting a vulnerability

Please **don't open a public issue**. Report it privately through GitHub instead:
**[Report a vulnerability](https://github.com/niogtres/stowaway/security/advisories/new)** (Security tab → *Report a vulnerability*).

Include what's affected, how to reproduce it, and the impact you expect. You'll get an answer within a week; fixes for confirmed issues are released as soon as possible, and you'll be credited in the advisory unless you'd rather not be.

## Supported versions

Only the latest release receives security fixes.

## Scope

In particular: sign-in and remote access (Windows sign-in, accounts, authenticator codes, sessions, lockouts), host and origin checks, the handling of repository passwords and cloud credentials, and anything that lets one user reach data or actions they shouldn't.

How Stowaway is secured is described in [docs/DESIGN.md](docs/DESIGN.md) (§6, and the M6 notes).
