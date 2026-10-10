# Privacy

pgNimbus is a desktop PostgreSQL client. It has no account, no cloud service and
no telemetry. It sends nothing to the developer or to any third party.
Everything it keeps stays on your computer, and this page lists all of it.

Last updated: 10 October 2026.

## What goes over the network

pgNimbus opens network connections to two kinds of host, both of which you
configure:

- **Your PostgreSQL servers.** The queries you run, the rows they return, and
  the catalog reads that fill the schema tree and completion.
- **Your SSH jump hosts**, when a profile uses an SSH tunnel.

It makes no other connection. There is no update check, no usage analytics, no
automatic crash report, no remote configuration, and nothing is downloaded at
run time: fonts, icons and themes are built into the app.

Two buttons open a page in your web browser, and only when you click them:

- **pgNimbus on GitHub** in the About box (and the macOS app menu) opens the
  project page.
- **Report on GitHub** in the crash window opens a new GitHub issue form, filled
  in with the error type and message (passwords masked), the pgNimbus version,
  the operating system version, and the crash log's path with your home folder
  shortened to `~`. Your browser sends that text to GitHub as part of the page
  address. Nothing is published until you submit the form yourself, and you can
  edit it first.

The app also talks to programs on your own computer: the OS password store, the
SSH agent when a profile signs in with it, and PostgreSQL's `pg_dump` when you
make a [backup](guide/backup.md). `pg_dump` connects to the same server as the
window you start it from, and gets the password through its environment, never
its command line.

## What your PostgreSQL server sees

- Your IP address, or the SSH jump host's when you use a tunnel.
- The user name and database you connect with, and your password or a proof
  of it, depending on the server's authentication method.
- The application name `pgNimbus`, for connections made from the connection
  dialog. It carries no version number or other identifier. A backup connects
  as `pg_dump`.
- Every statement you run.
- The statements pgNimbus runs for itself (the schema tree, completion, the
  monitoring windows). Each starts with the comment `/* pgNimbus */`, so it can
  be told apart in `pg_stat_activity`, `pg_stat_statements` and server logs.

## What an SSH jump host sees

Your IP address, your SSH user name and sign-in (password, key or agent
signature), the database host and port you forward to, and the SSH library's
identification string, `SSH-2.0-Renci.SshNet.SshClient` with its version.

## What is stored on your computer

Everything lives in one folder:

| System | Folder |
| --- | --- |
| Windows | `%AppData%\pgNimbus` |
| macOS and Linux | `~/.config/pgNimbus`, or `$XDG_CONFIG_HOME/pgNimbus` when that is set |

The version from the Microsoft Store can instead keep the folder in
`%LocalAppData%\Packages\DmitriiShmanev.pgNimbus_5cjm84wd2pj14\LocalCache\Roaming\pgNimbus`.
Which one Windows uses depends on how it runs packaged apps, so check both.

On macOS and Linux the folder and its files can be read only by your user
account. On Windows they have the normal permissions of your profile folder.

| File | What it holds | Encrypted |
| --- | --- | --- |
| `connections.json` | Your connection profiles: name, host, port, database, user name, SSL mode, root certificate path, colour, read-only flag, and SSH host, port, user name, sign-in method and key file path. No password. | No |
| `credentials/*.dpapi` (Windows only) | Saved database passwords, SSH passwords and key passphrases. See [passwords](#passwords). | Yes, DPAPI |
| `history.json` | Up to 200 statements you ran (the oldest go first, pinned ones stay): the SQL, when it ran, how long it took, the result line (row count, or the server's error message; none for a statement that held a password), and the `host/database` it ran on. | No |
| `saved-queries.json` | The queries you saved, as you saved them. | No |
| `workspace.json` | The text of your open tabs, per `host/database`, so the next session can reopen them. | No |
| `settings.json` | Your preferences, the last connection used, the paths of up to 10 recently opened `.sql` files, per-database lists of excluded schemas and LISTEN channels, the folder of your last backup, and the folder `pg_dump` runs from if you chose one. | No |
| `completion-usage.json` | The table, column and other names you picked from completion, with counts, per `host/database`, so they rank higher next time. | No |
| `window.json`, `connection-window.json` | Window size and position. | No |
| `known_hosts` | SSH host keys you accepted, in OpenSSH's format. | No |
| `trusted-roots.pem` | The certificate authorities your computer trusts, copied from the OS for `pg_dump` when a connection checks the server's certificate without a root certificate of its own. Public certificates only. | No |
| `logs/pgnimbus.log` | Unexpected errors: the time, the error type and message (passwords masked), and the stack trace. Kept to 1 MiB, then moved to `pgnimbus.log.old`. | No |

A file that pgNimbus can't parse is renamed to `<name>.corrupt-<date>` and kept,
so you don't lose its contents.

### Passwords

The passwords you save in the connection dialog never go into
`connections.json` or any other file in the table above.

- **Windows:** each password is encrypted with DPAPI for your Windows account and
  written to its own file in `credentials\`. pgNimbus does not use Windows
  Credential Manager.
- **macOS:** the Keychain, as a password item with the service name `pgNimbus`.
- **Linux:** the Secret Service (for example GNOME Keyring) through
  `libsecret-1.so.0`, labelled "pgNimbus connection password".

If the password store is unavailable, the connection dialog warns you and keeps
the password in memory only, until the last window using that connection
closes. SSH agent sign-in stores nothing.

Versions before 0.13.0 kept passwords on macOS and Linux in `credentials/*.cred`
files, base64-encoded but not encrypted. pgNimbus moves them into the Keychain or
the Secret Service the first time the connection dialog opens, and deletes each
file only after it has read the password back from the store. A file it could
not move stays, and the dialog shows a warning with the count.

### Masking of passwords in SQL

Before a statement goes into `history.json` or `workspace.json`, pgNimbus
replaces passwords it recognizes with `'<redacted>'::redacted`: `PASSWORD '…'`
in `CREATE ROLE` and `ALTER ROLE` (also inside a `DO` block, an `EXECUTE` string
or a comment), `password=…` in a connection string, and `user:password@` in a
URI. The crash log masks error messages the same way. When a statement held a
password, its history entry keeps no result line, because the server's error
message can quote part of the statement where masking can't find it. Masking
misses a secret that is not labelled as one, such as a key passed to
`pgp_sym_encrypt`, and it does not apply to saved queries. Everything else in
your SQL, such as an email address in a `WHERE` clause, is stored as you
typed it. [Where your password goes](getting-started/connecting.md#where-your-password-goes)
has the details.

### Files you create yourself

Exports, backups, saved plans and `.sql` files go only where you choose in the
save dialog.

## Clipboard

Copying puts text on the system clipboard, where clipboard history and clipboard
managers can keep it. The connection dialog's copy button leaves the password
out. **Copy With Password** (right-click that button) marks the text so that
Windows clipboard history and cloud clipboard, and macOS and KDE clipboard
managers that honor the markers, don't keep it, and clears the clipboard after
30 seconds if it still holds that text.

## Turning off history

Turn off **Record query history** in Settings, on the General tab. Statements you run after that are
not written anywhere. To remove entries already recorded, right-click the
history list and choose **Clear History**. Pinned entries survive that, so unpin
them first.

## Deleting your data

Uninstalling pgNimbus does not remove your data. To remove it:

1. In the connection dialog, right-click each profile and choose **Delete**.
   This also deletes its saved passwords from the Keychain or the Secret
   Service.
2. Quit pgNimbus and delete the folder listed [above](#what-is-stored-on-your-computer).
   On Windows, the saved passwords are inside it.

## Where you get pgNimbus

The Microsoft Store and WinGet install through Microsoft, and the other
downloads come from GitHub Releases. Those services handle the download under
their own privacy terms. The Microsoft Store can share aggregate install and
crash numbers with the developer in Partner Center, based on your Windows
diagnostic data settings. They contain nothing from your databases.

## Building from source

When you build pgNimbus yourself, Avalonia's build tooling
(`Avalonia.BuildServices`) sends anonymous build statistics to Avalonia, and the
.NET SDK sends its own usage data to Microsoft unless `DOTNET_CLI_TELEMETRY_OPTOUT`
is set. Both run on the build machine only. Neither is part of the app.

## Checking this

pgNimbus is open source under the MIT license. The code behind every statement
on this page is at <https://github.com/Shman4ik/pgNimbus>.

## Contact

Questions and concerns: <https://github.com/Shman4ik/pgNimbus/issues>. For a
security problem, use
[private vulnerability reporting](https://github.com/Shman4ik/pgNimbus/security/advisories/new)
instead.
