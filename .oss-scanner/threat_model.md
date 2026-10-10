# pgNimbus threat model

For Anthropic's OSS Scanner. The image is built by `.oss-scanner/Dockerfile`; this file says what to look at, how
to exercise it offline, and how we rate what you find. Project-wide conventions and the reasons behind most of the
security-relevant code are in `.claude/CLAUDE.md` ("Hard architectural rules" 2, 4, 6 and 7 especially) and
`SECURITY.md`.

## What this project does and where untrusted input enters

pgNimbus is a desktop PostgreSQL client (.NET 10, Avalonia). `src/PgNimbus.Core` is the engine (Npgsql, SSH.NET,
the OS credential stores); `src/PgNimbus.App` is the UI. The user is trusted: running SQL the user typed is the
product, not a vulnerability. What is not trusted:

- **The server and everything it returns.** A malicious or compromised server, or anyone who can create objects or
  write rows in a database the user opens: schema, table, column, role, function and type names, column values of
  every type (json, arrays, bytea, ranges, composites, extension types), error messages, NOTIFY payloads, EXPLAIN
  output, `pg_stat_activity` and `pg_stat_statements` text, and very large or deeply nested results.
- **The network path**: TLS to the server (`SslModes`, `ConnectionProfile.CreateDataSource`) and the SSH jump host
  (`SshTunnel`, `SshHostKeyVerifier`, `KnownHosts`, `SshAgentClient`).
- **Text and files the user brings in but did not write**: a pasted connection string or `postgres://` URI
  (`ConnectionStringParser`), a pasted query plan (`ExplainService.Import`, `ExplainPlanTextParser`), a CSV, TSV or
  JSON file imported into a table (`Import/TabularFileParser`), a `.sql` file opened from disk.
- **Other local users** on Linux and macOS, who must not be able to read what pgNimbus writes under the app data
  directory (`Settings/AppDataFile`, `Diagnostics/CrashLogger`), nor plant files there, nor read a password off a
  process's command line.
- **PostgreSQL's client programs** (`Backup/`). A backup runs `pg_dump`, a restore `pg_restore`, found by
  `PgToolLocator` in the places PostgreSQL, pgAdmin, Postgres.app and Homebrew install them, on PATH, or in a folder
  the user picked. What they print (progress lines naming the server's tables, errors quoting the server) is shown as
  text and parsed by `PgToolLog`, `PgDumpProgressTracker` and `PgRestoreProgressTracker`.
- **A backup file chosen for a restore** (`Backup/PgArchive`, `Backup/RestoreService`). Its first bytes decide what it
  is, and `pg_restore --list` output (object and role names from whoever made the file) is parsed and shown. Restoring
  it runs the SQL it holds as the connected role, which is what a restore is; what must not happen is anything beyond
  that SQL. Only pg_dump archives are restored, through `pg_restore` with a connection string; a plain SQL script is
  refused, never handed to psql, whose `\!` meta-command runs a shell (pgAdmin's CVE-2025-12762 and CVE-2025-13780).

## Promises the code makes, which a finding would break

1. **pgNimbus never sends SQL the user did not write or approve.** Everything it generates (browse queries, row
   filters, inline edits, staged commits, DDL templates, grant/role/policy scripts, schema actions) quotes
   identifiers through `Query/SqlIdentifier` and literals through `Query/SqlLiteral`, and anything placed in a `--`
   comment goes through `Query/SqlComment`. Every session forces `standard_conforming_strings=on`, and literals that
   hold a backslash are written as `E'...'` in case a pooler drops that option. A catalog name or a stored value that
   turns into executable SQL is the main thing we want found.
2. **User SQL runs exactly once.** No path executes a statement twice (`QueryEngine` describes first and executes
   once; a reconnect retries only what never left the client). Explain refuses a selection of more than one statement
   and always runs inside a transaction it rolls back. A read-only profile adds `default_transaction_read_only=on`.
3. **An edit lands on the row and table it was made on.** Inline edits, safe-mode staged batches (`PendingChangeSet`,
   `StagedRowCheck`, an optimistic-concurrency check under `FOR UPDATE`), row deletes (one transaction) and the
   browse-mode edit context (`EditableResultDetector`, checked by the result's table OID). A value saved must be the
   value shown or typed: the grid's display text round-trips through a server-side cast (`Converters/CellText`,
   `Schema/PgValueSyntax`, `QueryViewModel.ParseEditedText`).
4. **Secrets stay where they belong.** Connection passwords and SSH secrets live only in the OS store
   (`Connections/CredentialStore`: DPAPI files on Windows, Keychain on macOS, libsecret on Linux), never in
   `connections.json`, logs or the clipboard history (`Platform/SecretClipboard`). A `PASSWORD` typed into SQL is
   redacted before it reaches the query history, the workspace snapshot or a crash report (`Security/SecretRedactor`,
   `QueryHistoryStore`, `WorkspaceStore`, `CrashLogger`). A role password the app sets is sent to the server as a
   SCRAM verifier, never cleartext (`Security/ScramSha256Verifier`). `pg_dump` gets the connection's password as
   `PGPASSWORD` in its own environment, never as an argument, and the command preview the backup window shows is
   that command line, with no password in it (`Backup/PgToolProcess`, `Backup/PgToolConnection`). Every other
   inherited `PG*` variable is dropped, so the program connects where the window is connected and nowhere else.
5. **Nobody in the middle.** An SSH host key is checked against `~/.ssh/known_hosts` and pgNimbus's own file before
   the tunnel carries anything; a changed key refuses to connect. Verify-full checks the server certificate's name
   against the profile's host, also through the tunnel. `pg_dump` checks the same name (libpq's `host` beside
   `hostaddr=127.0.0.1` through a tunnel) against the profile's root certificate or, without one, the OS's trusted
   CAs exported to the app data directory (`Backup/TrustedRoots`, never the shared temp directory).
6. **Hostile input degrades, it does not take the app down.** Parsers have depth and size limits, results are capped
   in rows, bytes and columns (`ResultBudget`), and a value Npgsql cannot read becomes a placeholder cell.
7. **A failed backup or restore destroys nothing.** `pg_dump` writes to `<file>.partial`, which replaces the chosen
   file only when it exits cleanly; a failure or a stop deletes the partial file and leaves an older backup at that
   path as it was (`Backup/BackupService`). A restore is always one transaction that stops at its first error
   (`--single-transaction --exit-on-error`), replaces the current database only after a confirmation naming it, and
   drops a database it created for itself when it fails (`Backup/RestoreService`).

## Components that matter most / least

- Most: `src/PgNimbus.Core/Query` (the engine, SQL generation, Explain, staged edits, history), `Connections`
  (connection strings, TLS, SSH, credential stores), `Security` (script builders, redactor, SCRAM), `Schema`
  (catalog reads, `DdlTemplates`, `SchemaEditor`, `PgValueSyntax`), `Import`, `Export`, `Json`, `Settings`, and
  `Backup` (the libpq connection string and its quoting, the child's environment, the `pg_dump` arguments and the
  exact-name patterns a schema or table is selected by).
  In the App: `ViewModels/QueryViewModel*` and `Views/ResultsGridPanel*` (edit, staging, export paths),
  `Views/ConnectionDialog*` and `ViewModels/ConnectionDialogViewModel` (paste box, autosave, credential flow),
  `Views/HostKeyDialogPolicy`, `Platform/SecretClipboard`, `Converters/CellText`, and `Views/CrashWindow`, the one
  place that hands text derived from an exception message (which can quote the server) to the OS shell, as a
  pre-filled GitHub issue URL.
- In scope but lower: `.github/workflows` and `.github/actions` (token scope, script injection from a tag or input,
  pinned actions), `scripts/release` and `scripts/linux` (downloaded tools are sha256-pinned).
- Out of scope: `tools/` (screenshot harness, benchmarks), `tests/`, `docs/`, `website/`, `design/`,
  `tools/promo-video`, and `shared/nimbusUi` (styles, window chrome and hotkeys shared with kubeNimbus).

## How to exercise it

The image has PostgreSQL 17, an sshd and every NuGet package already restored. Nothing needs the network.

```sh
. .oss-scanner/services.sh        # starts Postgres (127.0.0.1:5432, postgres/postgres) and sshd (:22, tunnel/tunnel)
dotnet test --project tests/PgNimbus.Core.Tests -c Release --no-build
dotnet test --project tests/PgNimbus.App.Tests -c Release --no-build
# one class:
dotnet run --project tests/PgNimbus.Core.Tests -c Release --no-build -- --treenode-filter "/*/*/QueryEngineReconnectTests/*"
PGPASSWORD=postgres psql -h 127.0.0.1 -U postgres   # to create hostile objects and data
```

- `services.sh` exports `PGNIMBUS_TEST_CONN` and `PGNIMBUS_TEST_SSH`; tests gated on them skip without them. The
  backup and restore tests (`BackupServiceLiveTests`, `RestoreServiceLiveTests`) run the image's own `pg_dump` and
  `pg_restore` 17 from `/usr/lib/postgresql/17/bin`, which the automatic search finds.
- The server has TLS on, with Ubuntu's self-signed snakeoil certificate (CI's `postgres:17` has it off). For the
  no-TLS paths: `ALTER SYSTEM SET ssl = off`, then `pg_ctlcluster 17 main restart`.
- The best reproducer is a new TUnit test in `tests/PgNimbus.Core.Tests` (engine, parsers, SQL generation) or
  `tests/PgNimbus.App.Tests` (view models and real windows on Avalonia's headless platform with real key input; see
  `.claude/rules/headless-tests.md`). Both build offline. Existing live tests such as `QueryEngineCompositeTests`,
  `StandardConformingStringsTests`, `BrowseEditTargetTests` and `SshTunnelHostKeyLiveTests` show the shape.
- There is no display, so the app itself cannot be launched; drive UI flows through `PgNimbus.App.Tests`.
- To exercise the slow-queries window's reads: `ALTER SYSTEM SET shared_preload_libraries = 'pg_stat_statements'`,
  `pg_ctlcluster 17 main restart`, `CREATE EXTENSION pg_stat_statements`.
- Npgsql runs every statement of a parameter-less command that holds several. Do not assume the server will refuse
  `SELECT 1; DROP TABLE t` in one command; it will not.

## How we rate severity

- **Critical**: code execution on the user's machine caused by a server, a database object, a query result, a pasted
  string or an imported file; a stored password or private key sent to a host other than the one it belongs to.
- **High**: SQL the user did not write or approve reaches the server (injection through any generated SQL, a
  statement executed twice, a statement past a promised boundary: Explain, a rolled-back ANALYZE, a read-only
  profile); an edit or delete applied to a different row or table; TLS or SSH host-key verification bypassed; a
  cleartext password written to disk, a log, the clipboard history or the server's statement text where point 4
  says it is not.
- **Medium**: a destructive action without the confirmation the UI promises; a value silently changed on a
  display/edit/export round trip; app data readable or writable by another local user; a hostile server or database
  object that hangs or crashes the app with modest input.
- **Low**: a freeze that needs very large input the user chose to load; spoofing of what the UI shows (bidi or
  control characters in names), unless it misleads a destructive confirmation, which is Medium.

Rate by what an attacker who controls the server, a database object or the network path can make happen. A bug
that only the user can trigger against themselves with their own SQL is not a vulnerability.

## Anything to leave alone

- Findings fixed after the 2026-09 audit are listed in `docs/dev/design/security-audit-2026-09.md`. Do not re-report
  them as they were; a regression or an incomplete fix of one is a new finding, and please say which one it extends.
- Accepted by design (same document, "Left undone by design"): a single huge cell is read whole before the result
  budget can refuse it; no `RequireAuth=scram-sha-256`; SSH host certificates (`@cert-authority`) are not supported;
  typed SQL on a read-only profile behind a pooler that drops startup options is warned about, not blocked;
  `pgp_sym_encrypt` keys are not redacted.
- `Prefer` as the default SSL mode for a loopback host (localhost, 127/8, ::1, a socket directory) is deliberate.
- The backup runs whichever `pg_dump` it finds in the well-known install folders and on the user's PATH. A program
  planted there already runs as the user, which is outside this model.
- Unsigned or ad-hoc-signed release binaries, SmartScreen and Gatekeeper warnings.
- Anything that needs an attacker already running code as the user, except reading another user's files (above).
- Vulnerabilities inside Npgsql, Avalonia, SSH.NET or the .NET runtime, unless pgNimbus uses them unsafely. Report
  those upstream.

## Reports and patches

One root cause per report. Put the reproducer in as a test that fails before the patch and passes after it, name the
trust boundary it crosses, and point at file and line. Patches should follow `.claude/CLAUDE.md`: records for DTOs,
async all the way, no new packages in `PgNimbus.Core`, and the fix in the shared helper (`SqlIdentifier`,
`SqlLiteral`, `SqlComment`, `SecretRedactor`) rather than at one call site when the helper is where it belongs.
