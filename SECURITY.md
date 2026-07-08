# Security Policy

pgNimbus is a database client: it handles connection credentials, SSH
tunnel keys, and query results. Security reports are taken seriously.

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Instead, use
GitHub's private vulnerability reporting:

**[Report a vulnerability](https://github.com/Shman4ik/pgNimbus/security/advisories/new)**

(Repository → Security → "Report a vulnerability".)

Include what you can: affected version or commit, platform, reproduction
steps, and impact as you understand it. You should get an initial
response within a few days. Please give a reasonable window for a fix
before public disclosure.

## Scope notes

Things especially worth reporting:

- Credential handling — passwords are supposed to live only in the OS
  credential store (DPAPI on Windows, a permission-restricted file
  elsewhere), never in profile/settings JSON or logs.
- SSH tunnel handling (host key verification, key material).
- Anything that lets a malicious *server* or a crafted query result
  execute code or corrupt the client.

Unsigned release binaries (SmartScreen/Gatekeeper warnings) are a known,
documented limitation — not a vulnerability report.

## Supported versions

Pre-1.0, only the latest release receives fixes.
