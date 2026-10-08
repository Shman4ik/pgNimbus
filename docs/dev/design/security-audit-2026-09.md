# Security audit, 2026-09

A full security audit of pgNimbus, written 2026-09-28, closed by the 17 pull
requests named in the table below (all merged 2026-09-29 as one GitHub stack)
and shipped in [1.0.0](https://github.com/Shman4ik/pgNimbus/releases/tag/v1.0.0).
It found 18 findings: five High, seven Medium, one Low/Info bundle of about
twenty small items, and five whose severity label is no longer recorded (see
[About this document](#about-this-document)).

Its companion is
[`security-audit-2026-09-review.md`](security-audit-2026-09-review.md), the
adversarial review of the fix set, which reports what each PR got wrong before
it merged. This document is what was found; that one is how well it was fixed.

## What was audited, and how

Everything the app does that crosses a trust boundary: the statements the query
engine sends and when it sends them twice, the SQL pgNimbus generates on the
user's behalf, what it stores on disk and in the OS credential store, the
transport (TLS and the SSH tunnel), what it does with hostile or merely very
large input, and the release pipeline that signs and attests the binaries.

The audit did not take the code's word for anything it could run. Its three
High findings in the query engine were reproduced live against a PostgreSQL 17
container (`pgn-release`, 127.0.0.1:5441), which is how the Explain finding
survived: the auditor assumed Npgsql's extended protocol would refuse a command
holding several statements with a 42601 and nearly downgraded the finding. It
does not. Npgsql sends a parameter-less `NpgsqlCommand` with several statements
as one message and PostgreSQL runs every one of them. That single fact is what
makes findings 1, 3 and 16 exploitable rather than theoretical.

The review then re-ran those three as red/green checks, added an sshd container
for the host-key work, probed 14 groups of Core readers with 19 hostile shapes
at 100,000-deep nesting, and read the repository's live settings through
`gh api`. Both suites passed at the top of the merge stack against a live
PostgreSQL 17 and the sshd: Core 1,914 passed and 10 skipped, App 699 passed
and 1 skipped.

## The findings

| # | Severity | What | Fixed in |
|---|---|---|---|
| 1 | High | The text-format fallback re-executed user SQL | [#283](https://github.com/Shman4ik/pgNimbus/pull/283) |
| 2 | High | Auto-reconnect re-ran a statement that had already been sent | [#284](https://github.com/Shman4ik/pgNimbus/pull/284) |
| 3 | High | Explain ran the second and later statements of a selection | [#286](https://github.com/Shman4ik/pgNimbus/pull/286) |
| 4 | High | The SSH tunnel accepted any host key | [#292](https://github.com/Shman4ik/pgNimbus/pull/292) |
| 5 | High | Unpinned third parties inside the attested release build | [#288](https://github.com/Shman4ik/pgNimbus/pull/288) |
| 6 | Medium | Destructive actions ran with no confirmation, and a partial delete committed | [#298](https://github.com/Shman4ik/pgNimbus/pull/298) |
| 7 | Medium | Role passwords reached the server as cleartext in statement text | [#287](https://github.com/Shman4ik/pgNimbus/pull/287) |
| 8 | Medium | The workspace snapshot kept tab SQL unredacted; redactor gaps; no history opt-out | [#296](https://github.com/Shman4ik/pgNimbus/pull/296) |
| 9 | Medium | TLS defaults and knobs | [#299](https://github.com/Shman4ik/pgNimbus/pull/299) |
| 10 | not recorded | App data files readable by other local users on Linux and macOS | [#291](https://github.com/Shman4ik/pgNimbus/pull/291) |
| 11 | not recorded | A catalog name in a generated script's comment could carry a live statement | [#290](https://github.com/Shman4ik/pgNimbus/pull/290) |
| 12 | not recorded | A role named `"PUBLIC"` was granted and revoked as PUBLIC | [#290](https://github.com/Shman4ik/pgNimbus/pull/290) |
| 13 | not recorded | Generated literals were safe only while `standard_conforming_strings` was on | [#290](https://github.com/Shman4ik/pgNimbus/pull/290) |
| 14 | Medium | An edit could land on another table's rows | [#293](https://github.com/Shman4ik/pgNimbus/pull/293) |
| 15 | Medium | Release workflow token scope, tags and releases | [#288](https://github.com/Shman4ik/pgNimbus/pull/288) |
| 16 | not recorded | Hostile or large input could hang or kill the app | [#289](https://github.com/Shman4ik/pgNimbus/pull/289), [#295](https://github.com/Shman4ik/pgNimbus/pull/295) |
| 17 | Medium | A read-only profile was silently writable behind a pooler | [#293](https://github.com/Shman4ik/pgNimbus/pull/293) |
| 18 | Low/Info | Miscellany: build, docs, client and credential items | [#291](https://github.com/Shman4ik/pgNimbus/pull/291), [#294](https://github.com/Shman4ik/pgNimbus/pull/294), [#300](https://github.com/Shman4ik/pgNimbus/pull/300), [#301](https://github.com/Shman4ik/pgNimbus/pull/301) |

## Findings in detail

### 1. The text-format fallback re-executed user SQL (High)

When a result column could not be materialized by Npgsql (an unmapped
composite, an extension type with no plugin loaded, `bit`, `hstore`), the engine
disposed the reader and executed the same command a second time with
`UnknownResultTypeList` set, so the awkward columns came back as text. The guard
was `SqlStatementInspector.IsSafeToReExecute`: a leading-keyword check plus a
denylist of side-effecting built-ins.

A VOLATILE user function is on no such list. `SELECT create_order()` read as a
plain SELECT, passed the guard, and ran twice. Reproduced live: one Run, two
inserted orders, the first already committed under autocommit. No lexical check
can tell what a function does, which is why the guard was deleted rather than
strengthened.

**Fixed by describing first and executing once.** Every statement is now sent
with `CommandBehavior.SchemaOnly` first (Parse and Describe, which runs
nothing). The returned row description gives the text-format mask, and the
statement then executes exactly once. The same describe-then-execute runs in the
script path, the explicit-transaction path and safe mode's row check. The mask
is applied only to single-statement commands, because Npgsql applies it to every
statement in a command and its length must match each one. The cost is one extra
round trip per statement.

`IsSafeToReExecute`'s other caller was the export re-run past the display cap,
so an export now re-runs only the app's own browse query; a hand-written query
exports the rows shown and the status line says so.

Test: `QueryEngineCompositeTests.AVolatileFunctionReturningACompositeRunsExactlyOnce`,
which on `main` failed with "expected 1, received 2".

### 2. Auto-reconnect re-ran a statement that had already been sent (High)

On any failure `ConnectionFailure.IsLoss` classified it as a connection loss,
the engine flushed the pool and sent the statement again. `pg_terminate_backend`
produces 57P01 whether it kills an idle pooled connection or a statement
mid-run, and the classifier cannot tell the two apart.

Reproduced live: an `INSERT … FROM pg_sleep(3)` that a DBA terminated one second
in came back reporting `rows=1`, with the row present and no error shown. The
lost-acknowledgement case is the other half of it: a long `UPDATE` whose socket
dies keeps running server-side, commits, and the retry applies it a second time.
`ExecuteNonQueryAsync`, which runs grid edits and the Add-row INSERT, was
exposed the same way.

**Fixed by keying the retry on when the failure happened, not on its shape.**
Every path already describes the statement before sending it, so a loss during
open or describe (the dead pooled socket a laptop sleep, a dropped tunnel or a
backend killed while idle leaves behind) is still retried once on a fresh
connection, invisibly. A loss after the send is never retried on any path: the
statement is reported with `ConnectionLost` and the new `OutcomeUnknown` set,
and a message saying it was not run again and may or may not have taken effect.
`ExecuteNonQueryAsync` throws `StatementOutcomeUnknownException` instead of
re-sending, and Add-row says the row may already be there rather than "Insert
failed", which had invited a second, duplicate INSERT by hand. A script retries
its first statement only when it never went out.

The one place a sent statement is still re-sent is the pre-commit staged batch,
which is safe for a reason the single-statement paths lack: it ran inside its own
transaction, and a connection that dies before `COMMIT` takes the whole
transaction with it server-side, so nothing from the first attempt can have
landed. Once `COMMIT` has been attempted it never retries either.

Test: `QueryEngineReconnectTests.AStatementTerminatedWhileRunningIsReportedNotReRun`,
with the idle-kill tests still passing as the transparent half.

### 3. Explain ran the second and later statements of a selection (High)

`ExplainService.ExplainAsync` built `EXPLAIN (…) {sql}` from the selected text
unsplit. `EXPLAIN` plans only the first statement of the text it is given, and
Npgsql runs every statement in the command, so explaining a selection of
`SELECT 1; CREATE TABLE …` returned a plan for the SELECT and created the table,
with no error anywhere. Worse on the ANALYZE path: a selection ending in
`…; COMMIT;` committed the write that the warnings strip had just promised was
rolled back.

**Fixed by refusing anything but one statement.** `ExplainService.SingleStatement`
splits the text with `SqlScriptSplitter` and throws, before a connection is
opened. `QueryViewModel.ExplainTarget` calls it first so the refusal lands on the
status line, and `ExplainAsync` calls it again so the promise holds for every
caller. Plain EXPLAIN now also runs inside the same always-rolled-back
transaction as ANALYZE: it costs nothing and leaves no path on which the planner
could persist anything.

The review found two ways past the first version of this fix, both fixed before
merge:

- **The lexer and the server disagreed about `\r`.** A `--` comment ended only
  at `\n` in `SqlLexer`, while PostgreSQL's scanner ends it at `\n` or `\r`. So
  `SELECT 1 --x` + CR + `; COMMIT; CREATE TABLE pwn_cr()` was one statement to
  the check and three to the server, and both Explain and Explain Analyze
  created the table live. The same lexer decides Run's caret targeting, so a Run
  with the caret on the first line ran the hidden statements too.
- **Backslashes, under `standard_conforming_strings = off`.** With the setting
  off, which a database owner can set, `SELECT 'a\''; COMMIT; CREATE TABLE
  pwn_scs(); --'` is one statement to the lexer and three to the server; the
  table was created live. Finding 13's forced startup option closes it, which is
  why #290 was merged before #286.

Test: `ExplainServiceTests.Explaining_two_statements_runs_neither`, plus the
`\r` and `COMMIT` shapes, live: the statements are refused and the table is not
created.

### 4. The SSH tunnel accepted any host key (High)

`SshTunnel.Connect` never subscribed to SSH.NET's `HostKeyReceived`, and SSH.NET
trusts every host key unless a handler says otherwise. Nothing in the app ever
looked at `known_hosts`. Anyone on the path to the bastion (hostile Wi-Fi, a
spoofed DNS answer) could terminate the SSH session and relay it, owning the
forwarded PostgreSQL socket and, with SSH password authentication, the SSH
password itself.

**Fixed with a real `known_hosts` implementation.** `Connections/KnownHosts`
(Core-pure) reads the OpenSSH format: comma-separated patterns with `*`, `?` and
`!`, `[host]:port`, hashed `|1|salt|hash` hosts (HMAC-SHA1) and `@revoked`;
`@cert-authority` is recognised and skipped, since host certificates are not
supported. `SshHostKeyVerifier` consults the user's `~/.ssh/known_hosts`
(read-only, never written) and then pgNimbus's own file, applying OpenSSH's
precedence: revoked anywhere refuses, a match anywhere trusts, a mismatch
refuses with a message naming host:port, both `SHA256:` fingerprints and the
file and line to remove, and only a key neither file knows reaches the prompt.

Three details are load-bearing. The key type compared is the one the key blob
names, not SSH.NET's `HostKeyName`, which is the negotiated signature algorithm
(`rsa-sha2-512` for an `ssh-rsa` line), so otherwise every RSA host would read
as unknown. The verdict is stashed and thrown from `Connect`, not from inside the
event, where SSH.NET would bury it under "Key exchange negotiation failed". And
the prompt is synchronous on SSH.NET's connect thread, so the dialog is posted to
the UI thread while that pool thread blocks; Accept is deliberately not the
default button, because the dialog opens a second after the Enter that started
the connect.

The review added four more, before merge: a host the files already know is
offered only the key types they know it by, so a server presenting a key of
another type is refused rather than falling through to a first-use prompt (which
is exactly what an interceptor would produce); a connect that timed out while
the user was reading the fingerprint is retried once; with no app-data root the
accepted keys are kept in memory for the session; and the app's own
`known_hosts` is written through the finding-10 helper, so 0600 rather than
0644.

Tests: `KnownHostsTests` (real `ssh-keygen` keys and `-H` hashes),
`SshHostKeyVerifierTests`, `HostKeyDialogTests` (the pool-thread round trip,
headless), and `SshTunnelHostKeyLiveTests` against a real sshd, where unhooking
the handler turns 5 of 6 tests red.

### 5. Unpinned third parties inside the attested release build (High)

The release attestation proves "built by this workflow". It never proved "built
only from reviewed inputs, from a reviewed commit". Every `uses:` was a mutable
tag, and a branch in two cases. `softprops/action-gh-release` ran with
`contents: write` next to `id-token: write`, so a compromised upstream could
have uploaded different binaries and minted valid provenance for them.
appimagetool came from a moving `continuous` release with no checksum, and it
then fetched the newest AppImage runtime by itself: that runtime is the first
code that runs when a user starts the AppImage.

**Fixed by pinning everything that runs in a release build.** Every action is
pinned to a full commit SHA with a version comment, in all six workflows, each
SHA resolved through `gh api` with annotated tags dereferenced. appimagetool
1.9.1 and type2-runtime 20251108 are pinned by a per-architecture sha256, and
the runtime is passed with `--runtime-file` so appimagetool downloads nothing.
`wix` and `CycloneDX` moved into a tool manifest. The SBOM runs in its own
read-only job, so the third-party tool never shares a runner with the binaries
that ship. Dependabot's `github-actions` ecosystem bumps SHA and comment
together.

### 6. Destructive actions ran with no confirmation (Medium)

Three of them, in an app where drop schema, drop extension, delete rows and
terminate backend all confirm:

- **Drop column.** `AlterTableViewModel.DropColumnCommand` went straight to
  `SchemaEditor.DropColumnAsync`. One click after selecting a row destroyed the
  column's data.
- **Install extension.** `CREATE EXTENSION` against the connected database
  confirmed nothing, while "Drop…" right beside it already did.
- **A multi-row delete with safe mode off** ran one autocommit DELETE per row,
  so a failure partway through (a blocking trigger, a lost connection) left
  whatever had already committed deleted and the rest untouched. The status line
  even reported it: "Delete failed after N row(s)".

**Fixed** with the shared `ConfirmDialog` for the first two, naming
`schema.table.column` and naming the extension and the database; and for the
third, one `ParameterizedStatement` per row with `ExpectedRowsAffected: 1`
handed to `QueryEngine.ApplyBatchAsync`, which runs them inside one transaction.
A delete is now all-or-nothing: "Deleted N rows", or "Delete failed, nothing
deleted".

Tests: `AlterTableConfirmTests`, `ExtensionInstallConfirmTests`, and
`QueryViewModelDeleteRowsTests`, where a trigger blocking the second of three
rows leaves all three in place.

### 7. Role passwords reached the server as cleartext in statement text (Medium)

The client side had been careful and the server side was wrong. The role editor
kept the password off screen and out of the query history, but the executed
`CREATE ROLE … PASSWORD '…'` carried the cleartext in the statement text,
because PostgreSQL has no parameter form for `PASSWORD`. So the password could
land in the server log on any failure (`log_min_error_statement` writes
`STATEMENT: …`, and "permission denied to create role" is the ordinary failure
on managed PostgreSQL), in every `log_statement = ddl` or pgaudit line, in
`pg_stat_activity` while the statement runs, and in `pg_stat_statements` before
PG 16.

**Fixed by doing what psql's `\password` does.** `Security/ScramSha256Verifier`
(Core-pure, `System.Security.Cryptography` only, no new package) computes the
SCRAM-SHA-256 secret on this machine: SASLprep as libpq's `pg_saslprep` applies
it (an all-ASCII password as typed, a prohibited one hashed raw rather than
refused, mapping and NFKC otherwise), a random 16-byte salt, PBKDF2-HMAC-SHA-256
with 4096 iterations, then StoredKey and ServerKey. The server stores a SCRAM
secret in a `PASSWORD` literal as-is whatever `password_encryption` says, and an
`md5` pg_hba line authenticates one by negotiating SCRAM, so the cleartext never
leaves the machine and nothing about the server changes.

Two limits, both raised in review and answered before merge. The app runs with
`InvariantGlobalization`, under which `string.Normalize` is the identity, so a
non-ASCII password holding compatibility characters is hashed as typed; that is
what Npgsql's own SCRAM client already sends at login from this app, but libpq
and pgJDBC clients normalise and would be refused, so the role editor now warns
for any non-ASCII password when normalisation is unavailable. And before PG 10
there is no SCRAM, so the server would store the verifier as the password
itself; the editor refuses to set a password there and points at psql's
`\password`.

Tests: `ScramSha256VerifierTests` against vectors computed with Python's
hashlib, and `ScramPasswordServerTests`, which creates a role through the real
path, logs in as it with the cleartext, and is refused the wrong one with 28P01.

### 8. The workspace snapshot kept tab SQL unredacted (Medium)

Redaction guarded only `SavedQueriesViewModel.RecordExecution`. The workspace
snapshot, written on every window close and connection switch, kept a typed
`ALTER ROLE x PASSWORD 'p'` exactly as typed in `workspace.json`. The redactor
itself also missed most of the ways a password actually appears in SQL, and
there was no way to turn history off.

**Fixed by moving redaction into the stores and widening the redactor.**
`QueryHistoryStore` redacts every entry it writes and scrubs the file once on
load, so an entry written before the redactor learned a shape is rewritten in
place; `WorkspaceStore.Save` redacts every tab's text, the other connections'
snapshots included. The redactor reads the statement with the shared `SqlLexer`
and then reads inside every string, dollar body and comment, because that is
where the leaks were: `DO $$ … PASSWORD 's' … $$`, `EXECUTE 'ALTER ROLE …
PASSWORD ''s'''`, `format(… %L …)`, `quote_literal`, conninfo `password=s` in
`CREATE SUBSCRIPTION` and `dblink_connect`, `user:s@` in a URI, and
commented-out statements. Inside those it also scans loosely, so an apostrophe
in `-- don't …` cannot hide a password, and a string ending in a hanging
PASSWORD redacts every later literal in the statement. The bias is deliberate:
redact too much. `AppSettings.RecordQueryHistory` turns history off, and the
sidebar says history is off rather than silently not growing.

The review found a blocker and a sharp edge, both fixed before merge:

- **A file-backed tab reopened redacted and one Ctrl+S overwrote the real
  file.** The snapshot redacted every tab, file-backed ones included, and the
  restore put the snapshot text in the buffer before attaching the file. A
  migration file holding `CREATE ROLE app LOGIN PASSWORD 'x'`, opened and never
  edited, reopened dirty showing `'<redacted>'`, and one Ctrl+S wrote the
  placeholder over the user's file. A clean file-backed tab now keeps no text at
  all and is read from its file on restore.
- **The replacement was a valid literal.** Re-running a restored or
  history-opened `ALTER ROLE x PASSWORD '<redacted>'` would have set the
  password to the text `<redacted>`. The marker is now `'<redacted>'::redacted`,
  a syntax error if run, and it must stay idempotent or the load-time scrub
  would rewrite the file on every launch.

Not covered, as the redactor's doc comment says: `pgp_sym_encrypt` keys, and
secrets held in a PL/pgSQL variable. Nothing next to them says what they are.

### 9. TLS defaults and knobs (Medium)

Five defects, one fix each:

- **New profiles defaulted to `Prefer`**, which Npgsql, like libpq, drops to
  plaintext whenever the server or anyone on the path declines TLS. New profiles
  start at Require. In review this became "Require, except Prefer for a loopback
  host", because a local Docker PostgreSQL has TLS off and Require failed the
  first connect anyone tried.
- **A missing field read as `Disable`.** `SslMode` is persisted as a number and
  its zero value is `Disable`, so a hand-edited `connections.json` without the
  field loaded as a plaintext-only profile. The record's parameter now defaults
  to Require, and the enum is never renumbered.
- **The picker showed six bare enum names**, and "Require" read as the safe one.
  The Core-pure `SslModes` table gives each a label and one line saying what it
  checks ("Require: encrypted, but the server's certificate is not checked") and
  marks Verify full recommended.
- **Provider CAs could not be used.** RDS, Cloud SQL and Supabase sign their
  servers with CAs no OS trusts, so Verify full could not pass and users fell
  back to Require. `ConnectionProfile.RootCertificatePath` and a Root Certificate
  field answer it, parsed from `sslrootcert=`, `PGSSLROOTCERT` and Npgsql's
  `Root Certificate=`. The review closed a hole in that new input: a pasted
  remote path is refused (UNC, `\\?\UNC\` and URLs), because reading one on
  Windows opens an SMB session to that host and its CA would then vouch for its
  owner's certificate under Verify full. A pasted `sslmode=require` with a root
  certificate reads as VerifyCa, as libpq has it.
- **Verify full could never pass through the SSH tunnel**, because the socket is
  `127.0.0.1:<port>` and Npgsql checked the certificate's name against that.
  Every connect now builds its pool through `ConnectionProfile.CreateDataSource`,
  which through a tunnel sets `TargetHost` to the profile's host.

`TlsSettingsTests` proves the last one end to end without a TLS PostgreSQL: a
local listener answers the SSLRequest with a certificate for `db.example.test`
from a throwaway CA, and the client sends its startup message only with the
callback in place. Two landmines surfaced writing it: SChannel validates the
server certificate after the handshake, so the server side "completing" proves
nothing, and Npgsql retries a failed open, so the listener has to keep accepting
or the retry waits out the connect timeout in the backlog.

### 10. App data files readable by other local users (severity not recorded)

Every store used `File.WriteAllText`, which creates `0644` under the usual
umask. Under a `0755` home directory (Debian, older Ubuntu, macOS) any other
local user could read the query history, the workspace's SQL, connection hosts,
usernames, SSH key paths and the crash log.

Two items of finding 18 belong to the same fix. Writes were not atomic, so a
crash mid-write left a torn `connections.json` that read as "no profiles", after
which the connection dialog's autosave wrote the empty list over every saved
profile. And `CrashLogger` fell back to `<temp>/pgNimbus/logs` when no app data
root resolved, which on Linux is the shared `/tmp`, where another user can
pre-create the directory and then read or plant the file.

**Fixed with one helper every store goes through.** `Settings/AppDataFile`
creates files `0600` and directories `0700` (never set on Windows) and tightens
an existing directory only when it is the app data root or inside it. Writes go
to a unique temp file in the same directory, are flushed, then moved over the
target; a file that still cannot be parsed is moved aside as
`<name>.corrupt-<UTC stamp>` before the store starts over. `App.TightenAppDataOnce`
restricts the root, its files, `logs/` and `credentials/` once per launch on the
thread pool, for what an earlier version left readable. There is no `/tmp`
fallback at all now: with no resolvable root, paths are null, the session runs
from memory, and the crash window says no log was written.

The review caught the one failing test in the merged tree: the app data root's
test seam was a process-wide static, which handed a null root to the SSH
host-key test running beside it. It is an `AsyncLocal`.

### 11. A catalog name in a generated comment could carry a live statement (severity not recorded)

Every generated script opens with a `--` comment naming what it is about, and a
schema or relation name may contain a line break. Role names are refused by
current servers; table names are not. A table named
`"x⏎ALTER ROLE eve SUPERUSER;--"` with an inert policy put a live `ALTER ROLE`
on the second line of the RLS re-create script, which autocommit ran before the
`CREATE POLICY` failed. Quoting protects nothing inside a comment.
`RoleScriptBuilder.Drop` had a private guard against this; the
`GrantScriptBuilder` hint, `DdlService`'s not-found lines and the RLS and
default-privileges comments did not.

**Fixed** with `SqlComment.Safe`, which strips the only two characters that can
end a comment, applied at every site. The RLS re-create and the
`ALTER DEFAULT PRIVILEGES` statement moved out of their view models into the
Core-pure `PolicyScriptBuilder` and `DefaultPrivilegeScriptBuilder` so the rule
is tested where the other script builders' rules are. The tests judge the result
with `SqlScriptSplitter`, not by eye: a name that escaped its comment adds a
statement.

### 12. A role named `"PUBLIC"` was granted and revoked as PUBLIC (severity not recorded)

Only the lowercase `public` is reserved, so `CREATE ROLE "PUBLIC"` is legal, and
`GrantScriptBuilder` matched the grantee's name case-insensitively. Granting to
that role granted to everyone; revoking from it left its access in place. The
RLS query had the same confusion from the other direction, returning the string
`'public'` for `polroles` oid 0.

**Fixed by making PUBLIC `null` end to end**, and nothing else. `aclexplode`
grantee 0 and `polroles` oid 0 come back as `null`, `GranteeSql` writes the
keyword only for `null` and quotes every name (so the role is written
`"PUBLIC"`), and `GranteeLabel` shows that role quoted on screen so the two can
be told apart. `PublicRoleTests` creates the role for real, grants to it and to
real PUBLIC, and revokes from it through the generated script.

### 13. Generated literals were safe only while `standard_conforming_strings` was on (severity not recorded)

`SqlLiteral.Quote` doubles only the quote, and its output is executed: browse
filters including filter-by-cell, the FK hop's seed, a role's `VALID UNTIL` and
`COMMENT`. With the setting off, which a database owner can set with
`ALTER DATABASE`, a backslash escapes too, and a stored `x\'' OR 1=1 --`
filtered by cell ran as SQL.

Parameters are not available on these paths: the browse WHERE round-trips
through the editor as text that `BrowseSqlParser` reads back into chips, where a
bind parameter cannot live, and `COMMENT ON` and `ALTER ROLE … VALID UNTIL` are
utility statements, which take no bind parameters.

**Fixed by forcing the setting on for every connection.**
`Options=-c standard_conforming_strings=on` is appended last, after anything in
`PGNIMBUS_CONN`, because the server applies `-c` switches in order, so a string
carrying `=off` cannot win; a startup option also beats the database's and the
role's defaults and survives the pool's `DISCARD ALL`. The review added the belt
and braces, since a pooler can drop startup options (finding 17): `SqlLiteral.Quote`
writes text holding a backslash as `E'…'` with the backslash doubled too, which
reads the same under either setting, and `BrowseSqlParser` reads that form back
as a typed chip so a LIKE chip's escaped wildcards survive the round trip.

`StandardConformingStringsTests` turns the test database's default off and shows
that a profile's session still reports `on`, that the hostile filter matches
only its own row, and that the old plain form returned every row.

### 14. An edit could land on another table's rows (Medium)

Two shapes, both of which produced an UPDATE against rows the user was not
looking at:

- **A browse tab resumed on the wrong table.** A browse tab's page query edited
  by hand to `SELECT * FROM orders LIMIT 100` still has the browse shape, so a
  Run resumed browse mode on the browsed table, `sales.orders`. With
  `search_path` resolving the bare name to `public.orders` first, the resumed
  edit context turned an inline edit (safe mode off) into
  `UPDATE "sales"."orders" … WHERE id = <the other table's row id>`. The shape of
  a query is not proof of what it reads, so browse mode now resumes, and an edit
  context is handed out, only when every result column's `TableOid` is the
  browsed table's.
- **A self-join was editable.** `SELECT c.id, p.name FROM items c JOIN items p
  ON p.id = c.parent_id` passed `CheckSingleTable` (one OID, distinct attnums),
  and an edit of `name`, the parent's, updated the child. The wire metadata
  cannot see it, so `EditableResultDetector.CheckRepeatedTable` reads the text
  through `SqlScopeModel` and refuses a relation read more than once where its
  columns can reach the result: the statement's branches, FROM subqueries,
  LATERAL items, CTE bodies and each CTE reference. Expression subqueries are
  skipped, because their columns never carry a table OID.

Tests: `EditableResultDetectorTests` for the join shapes, and
`BrowseEditTargetTests` live, with two schemas that each hold an `orders`.

### 15. Release workflow token scope, tags and releases (Medium)

Every build job inherited a workflow-wide `contents: write` and a persisted
checkout token. A tag on any commit was releasable, a `workflow_dispatch`
started from a tag published for real, and the version reached bash through
expression interpolation.

**Fixed** with a top-level `contents: read` and write only on the `release` job
and the benchmark call; `persist-credentials: false` on every checkout;
`gh release create --verify-tag`, which is create-only, so a re-run after the
release exists fails instead of replacing assets; a `require-on-main` composite
action running `git merge-base --is-ancestor` as the first step of every build
job; the publish gated on a push of a `v*` tag; every expression passed through
`env:` with a version regex; and `docs.yml` split into a read-only build job and
a publish job that runs only for `main`.

The repository half is settings rather than code, so the PR supplied the exact
`gh api` commands and ran none of them: a `Release tags` ruleset (deletion,
non_fast_forward, update), immutable releases, `sha_pinning_required`, Dependabot
alerts and security updates, and `dependency-review` as a required check beside
`build-test`. The review verified each against the repository's live settings and
found one proposed step inert (review requirements, with
`required_approving_review_count` at 0). `enforce_admins` was deliberately not
proposed, since it would stop the owner's own merges.

### 16. Hostile or large input could hang or kill the app (severity not recorded; client-side denial of service)

The audit's largest finding by surface: text and data that a user pastes, a
server returns, or another tool produces could freeze the window or take the
process down. It was fixed in two halves, the parsers (#289) and the UI (#295).

**Parsers.** Plan import and a Run of `EXPLAIN` caught `FormatException` only,
but `EXPLAIN (FORMAT JSON, COSTS OFF)` output threw `KeyNotFoundException`,
`[{"Plan": 5}]` threw `InvalidOperationException`, and a text `rows=` past
9.2e18 threw `OverflowException`; each reached the crash window. The EXPLAIN text
regex `(cost=[\d.]+\.\.[\d.]+` backtracked O(n²) on `(cost=` followed by a run
of dots. The scope reader recursed per `(` without incrementing depth, so
`SELECT ` followed by 20,000 open parentheses overflowed the stack, which is
uncatchable and kills the process, on every keystroke and after every Run;
`SqlKeywordGrammar.Governing` had the same recursion plus a token-list copy per
level. `SqlStatementInspector.StripExplain` read one past the end of its option
list on an unterminated quote.

Now every figure is read by kind with a default (a COSTS OFF plan is a tree of
zeros), counts saturate, every other failure is translated to a single
`FormatException`, the regexes run `NonBacktracking` with a match timeout, input
and nesting are capped, and the recursive readers are iterative or
depth-counted. `HostileText` and `ParserRobustnessTests` run every UI-thread
reader over every printable character at every position of several shapes,
seeded random text and random SQL words, and seven kinds of 100,000-deep
nesting, on a thread with a 256 KB stack so that "fits here" means it has
headroom in production rather than depending on the runner's default.

The review's own depth probe then found four more, all fixed before merge:
`ExtractCteDefinitions` grew about cubically and ran on every completion popup
(3.4 s at 2,000 nested CTEs, now 10 ms); `BrowseSqlParser` was quadratic on
nested parentheses in a WHERE, on the UI thread after a Run in a browse tab;
`SqlFormatter`'s indentation is O(depth²) and threw an uncaught StringBuilder
`ArgumentOutOfRangeException` that reached the crash window; and
`JsonDocument`'s default `MaxDepth` of 64 held a plan to about 31 levels, so
importing the plan of a 30-plus table join failed.

**UI.** `BlockingTree.Build` repeated a waiter under every blocker, subtree and
all, and `pg_blocking_pids` reports soft blocks too, so N sessions queued on one
hot row formed a complete DAG with about 2^(N-3) paths: 30 waiters built ~134M
nodes on the UI thread every 2 s, during exactly the incident that tab exists
for. It is a spanning tree now, each backend once, with the other blockers named
in the row. A result was bounded in rows only, and a row can be anything:
`SELECT *` over the telemetry demo's 37 KB jsonb cells is several GB, and one
`repeat('x', 500000000)` is a gigabyte. `MaxDisplayBytes` (256 MiB) is now
charged per row, a batch is handed over at 8 MB so a few huge rows reach the
budget one at a time, a script's sections share one budget, and abandoning a
stream cancels the query rather than draining every remaining row on disposal (a
50M-row stream abandoned after 1,000 rows took 21 s before). The grid's columns
are capped, and `NotifyMonitorViewModel`, which had posted one dispatcher item
per notification with its 500-row cap applied only as each item ran, now
coalesces under a lock with at most one drain posted.

Two gaps were left open and stated: a single cell is still read whole by Npgsql
before the budget can refuse its row, and `CellText.Preview` formats a whole
array or hstore literal before cutting it to 256 characters. Both want a
per-cell cap with the full value fetched on demand in the inspector. The second
was closed in #365 (2026-10): the literal writers stop at the preview's length.

### 17. A read-only profile was silently writable behind a pooler (Medium)

A read-only profile works by a startup option, which travels through whatever
sits in front of the server. PgBouncer with
`ignore_startup_parameters = options`, a common workaround for clients that send
options, drops it. `DetectWriteStateAsync` then replaced the profile-seeded hint
with the server's "writable" answer, so the lock left the title bar and the grid
became editable on a profile the user had explicitly marked read-only.

**Fixed by never letting the server's "writable" downgrade the profile.** A
read-only profile the server reports writable keeps a reworded hint, so every tab
still refuses an edit context; the title-bar mark turns amber and reads
"read-only not applied", with a tooltip saying that the server or a pooler did
not apply the option and that typed SQL can still write; and the status line says
it once. Typed SQL is deliberately not blocked: only the server could do that,
and here it never received the option. That limit is stated in the tooltip, the
status line and the user docs. A pooler that rejects the option instead fails the
connect, which is the right outcome.

### 18. Miscellany (Low/Info)

About twenty small items, split across four PRs. The file-permission and
atomic-write items are under finding 10 above.

**Build and docs (#294).** `global.json` had no SDK pin, so nothing in the repo
said which SDK a release was built with; it is a floor with `latestFeature`,
lowered in review to 10.0.100 because 10.0.401 refused 1xx-band SDKs such as
Ubuntu's apt package. The SBOM's NuGet-graph walk never saw the runtime pack and
ILCompiler pack that a NativeAOT publish statically links in (GC, TLS, crypto),
since neither is a `<PackageReference>`; a stdlib-only script adds them. There
was no repository `nuget.config`, leaving the dependency-confusion opening that
a developer machine's extra feeds create; it is now `<clear/>` plus nuget.org
with package source mapping. The verification docs showed
`gh attestation verify --repo`, which accepts an attestation from any workflow
or ref in the repository, and now show `--signer-workflow` and `--source-ref`
plus a no-`gh` `sha256sum -c` path. The macOS bundle was signed without hardened
runtime, so any same-user process could launch pgNimbus with
`DYLD_INSERT_LIBRARIES` and run code as it. And the `PGNIMBUS_CONN` docs did not
mention that the example puts the password in the environment, readable by other
processes of the same user, and usually into shell history.

**Client (#300).** CSV formula injection: an opt-in "Safe for Spreadsheets"
prefixes a text cell or header starting with `=`, `+`, `-`, `@`, tab or CR, off
by default because the quote changes the data for every reader that is not a
spreadsheet. The copy-connection-string button copied the real password, which
Windows clipboard history, cloud clipboard and every clipboard manager keep; it
now leaves the password out, and "Copy With Password" on its right-click menu
goes through `SecretClipboard` with the platform's do-not-keep markers and a 30 s
clear. The crash log and the pre-filled GitHub issue carried unredacted
exception messages, which can quote a statement, and the home directory's
account name; both are redacted and the path is written as `~/…`. The connection
URI parser split userinfo at the first `@`, so
`postgres://admin:1234/abcd@db/app` parsed as host `admin` and autosave wrote
the rest of the password into `connections.json` as the database name; it splits
at the last `@` before the query now, and no parser error quotes a parsed value.
Import read and parsed a whole file on the UI thread with no caps, so a
20,000-object JSON file whose objects each had their own keys became a 400M-cell
matrix; it is capped (512 MiB, 1,000,000 rows, 1,000 columns, 50M cells) and
parsed off the UI thread. A generated GRANT used
`pg_get_function_arguments`, which includes `DEFAULT …`, making the statement a
syntax error, and `ON FUNCTION` fails for a procedure; it uses identity
arguments and `ON ROUTINE`. A CAST target came from `format_type` under the
connection's `search_path`, so a schema created later could change what a tab's
cached cast resolved to; it is read under a `search_path` narrowed to
`pg_catalog` in a rolled-back transaction. The slow-query setup script's
`ALTER SYSTEM SET shared_preload_libraries` line replaces the whole list, so a
tab run whole unloaded every other preloaded library at the next restart; it
ships commented out. And `build-msix.ps1` left its ephemeral signing key behind.

**Credentials (#301).** A legacy `.cred` file (non-Windows, a base64 password)
moved to the OS store only when its profile was opened, so a profile nobody
reopened after upgrading kept its file forever; migration is now one verified
pass at startup, which the review made stop at the first refusal from the store,
since a hung Secret Service costs up to 15 s per call and Connect waits on the
pass. The session cache kept every loaded password for the process lifetime and a
delete merely set the entry to null; now only a password whose native write
failed is kept (memory is its only copy), a successful write or read keeps
nothing, a delete removes the entry, a delete the store refused is remembered by
id alone, and `ICredentialStore.Forget` drops a session-only password when the
last window connected with that profile closes.

## Left undone by design

From the review's own table, with where each is recorded:

| Item | Stated in |
|---|---|
| Per-cell materialisation cap (array/hstore preview formatting: closed in #365) | PR #295, and CLAUDE.md's result-bounds bullet |
| `RequireAuth=scram-sha-256` | PR #299 |
| SSH host certificates (`@cert-authority`) | CLAUDE.md, `docs/getting-started/connecting.md` |
| Typed SQL not blocked on a read-only profile behind a pooler | tooltip, status line, `connecting.md`, CLAUDE.md |
| `pgp_sym_encrypt` keys not redacted | the redactor's doc comment, PR #296 |

Follow-ups left open after the fix set merged: the export of a hand-written
query past the display cap (offer a re-run inside `BEGIN READ ONLY`), a runtime
warning when `standard_conforming_strings` is off, and `ExpandSelectStar` on a
pasted deep nest.

## About this document

This is a reconstruction, written 2026-10-07. The audit's own working document
lived at `docs/design/security-audit-2026-09.md` on the branch
`claude/security-audit-opensource-083333`, was never merged, and is gone: the
branch no longer exists on the remote, the file is in no commit reachable from
any ref, and the GitHub events that would have carried the branch's head SHA
have aged out. The review document's link to it had been returning 404 for
anyone browsing the repository, which is the only public document that claimed
to link the audit at all.

Everything above is drawn from sources that survive: the descriptions, severity
labels and live reproductions in the 17 fix PRs, the adversarial review in
[`security-audit-2026-09-review.md`](security-audit-2026-09-review.md), and the
evidence paragraphs the fixes left in `.claude/CLAUDE.md` and in the code.

What is faithful: the finding numbers (1 to 18, continuous, with no gaps, which
is itself evidence that no numbered finding is missing), what each finding was,
how the three High engine findings were reproduced, and what shipped.

What is not: the audit's own wording and structure, which are lost. Severity
labels are reproduced only where a fix PR recorded one, which leaves findings
10, 11, 12, 13 and 16 marked "not recorded" rather than guessed; by the
severities of their neighbours, 10 to 13 read as Medium and 16 as Medium or Low,
but the audit's own judgement is not recoverable. Any finding the audit raised
and then withdrew before writing a fix PR would also leave no trace here.
