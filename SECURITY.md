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

- Credential handling: passwords are supposed to live only in the OS
  credential store (DPAPI on Windows, the Keychain on macOS, Secret Service
  on Linux), never in profile/settings JSON or logs.
- SSH tunnel handling (host key verification, key material).
- Anything that lets a malicious *server* or a crafted query result
  execute code or corrupt the client.

Unsigned release binaries (SmartScreen/Gatekeeper warnings) are a known,
documented limitation — not a vulnerability report.

## Dependencies

Every restore checks the full NuGet dependency graph against known advisories
(`NuGetAuditMode=all` in `Directory.Build.props`) and a moderate, high or
critical finding fails the build instead of only warning (NU1902 through
NU1904 are promoted to errors). Dependabot keeps both NuGet packages and
GitHub Actions current. SSH.NET ships `ScpClient` and `SftpClient`, which have
carried advisories of their own; pgNimbus never constructs either type, so
those advisories do not reach the app regardless of the package version in
use.

## Supported versions

Pre-1.0, only the latest release receives fixes.
