# Review of the security-audit fix set, 2026-09-29

Internal working document. It reviews the 17 draft PRs that answer
[`security-audit-2026-09.md`](https://github.com/Shman4ik/pgNimbus/blob/claude/security-audit-opensource-083333/docs/design/security-audit-2026-09.md)
(branch `claude/security-audit-opensource-083333`). Nothing here was fixed on the
PR branches; every finding names the file, the line on the PR branch and the
change it needs.

## How it was checked

- Every PR diff read against the finding it answers and against `CLAUDE.md`.
- A trial merge of all 17 branches onto `bcb7f4e`, in PR-number order, in a
  scratch worktree. Both suites ran on the merged tree against a live
  PostgreSQL 17 (`postgres:17` via `wslc`) and the audit's sshd container
  (`PGNIMBUS_TEST_SSH`). A clean `--no-incremental` rebuild counted warnings.
- The audit's three live reproductions run as red/green checks (table below).
- Live probes against `pgn-audit` (127.0.0.1:5442) for Explain bypasses, the
  redactor, SSH prompt timing and capped DML, and an out-of-process depth probe
  that feeds 19 hostile shapes at 100,000-deep nesting to 14 groups of Core
  readers, one child process per pair on a 1 MB-stack thread.
- Per-branch full suites were not re-run here: each PR description reports its
  own run, and the merged-tree run executes every PR's tests together.
- `main` moved twice during the work: `bcb7f4e` (#297) when the trial merge was
  made, then `9aea487` (#302) before this document. The effect of #302 is under
  "Cross-PR".

### Red/green of the audit's live reproductions

| Test | main | PR alone | Verdict |
|---|---|---|---|
| `QueryEngineCompositeTests.AVolatileFunctionReturningACompositeRunsExactlyOnce` (finding 1) | red: expected 1, received 2 | #283 green, #284 green | goes red, fix holds |
| `QueryEngineReconnectTests.AStatementTerminatedWhileRunningIsReportedNotReRun` (finding 2) | red: `CommandResult`, the statement was re-run | #283 red (same), #284 green | goes red, fix holds |
| `ExplainServiceTests.Explaining_two_statements_runs_neither` (finding 3) | test does not compile on main (new API) | #286 green; red with the refusal removed ("Expected to throw ArgumentException") | goes red, but see #286 finding 1 |

The reconnect test was brought to main without its two `OutcomeUnknown`
assertions, which name a property #284 adds.

## Summary of verdicts

| PR | Finding | Verdict | Blockers |
|---|---|---|---|
| #283 | 1 | merge as is | none |
| #284 | 2 | merge after fixes | none |
| #286 | 3 | merge after fixes | `\r` ends a comment for the server, not for the lexer |
| #287 | 7 | merge after fixes | none |
| #288 | 5, 15 | merge as is | none |
| #289 | 16 parsers | merge after fixes (or merge with a follow-up) | none |
| #290 | 11, 12, 13 | merge as is, follow-up recommended | none |
| #291 | 10, 18 | merge after fixes | none |
| #292 | 4 | merge after fixes | none |
| #293 | 14, 17 | merge as is | none |
| #294 | 18 build/docs | merge after fixes | none |
| #295 | 16 UI | merge as is | none |
| #296 | 8 | rework the workspace half | a file-backed tab reopens redacted and saves over the file |
| #298 | 6 | merge as is | none |
| #299 | 9 | merge after fixes | none |
| #300 | 18 client | merge as is | none |
| #301 | 18 credentials | merge after fixes | none |

## Per PR

### #283 Describe first, execute once (`fix/engine-execute-once`)

**Verdict:** merge as is.

**Verification run:** red/green above. Merged tree: Core and App suites green
for its tests; zero IL warnings.

**Findings**

1. **should fix (a follow-up is fine)** `src/PgNimbus.App/ViewModels/QueryViewModel.cs:2592`:
   export of a hand-written query past the 100,000-row display cap now writes
   only the grid ("a query you wrote isn't run again for the rest"). That is what
   the audit asked, and the docs say so, but it removes ROADMAP D1 for every
   query a browse cannot express (joins, aggregates, filters the chips cannot
   hold). **Change:** offer an explicit "Run the query again to export every row"
   confirm that re-runs inside `BEGIN READ ONLY`, so a function that writes fails
   with 25006 instead of applying twice.
2. **nit** `CLAUDE.md:1895` (now in `.claude/rules/sql-completion.md` on main):
   the lexer paragraph still says "`IsSafeToReExecute` is untouched and must not
   get less conservative"; this PR deletes the method. **Change:** drop the
   sentence.

**Scope vs the audit:** closes finding 1. Every path the engine runs (single
statement, script, explicit transaction, safe mode's row check) describes with
`CommandBehavior.SchemaOnly` first and executes once. `IsSafeToReExecute` is
deleted rather than strengthened, and the "provably harmless" sentence is gone.
The cost is one extra round trip per statement (per script statement too), which
the PR states.

**Left undone, stated where:** the export narrowing, in the PR body,
`docs/guide/results.md` and CLAUDE.md.

### #284 Never re-send after the send (`fix/engine-no-retry-after-send`, stacked on #283)

**Verdict:** merge after fixes.

**Verification run:** red on main and on #283 alone, green on #284 (above).

**Findings**

1. **should fix** `src/PgNimbus.Core/Query/QueryEngine.cs:218` with
   `src/PgNimbus.App/ViewModels/AddRowViewModel.cs:140`: after a loss once the
   statement was sent, `ExecuteNonQueryAsync` rethrows the raw Npgsql exception,
   and Add-row shows "Insert failed: Exception while reading from stream". In
   the lost-acknowledgement case the INSERT may have committed, so a user who
   presses Add again inserts the row twice: the double apply this PR exists to
   stop, now done by hand. **Change:** throw a typed exception carrying
   `LossMessage(sent: true)` (the "may or may not have taken effect" text) and
   have Add-row say the row may already be there and to refresh first. The three
   `QueryViewModel` callers (`:1682`, `:1872`, `:2043`) are PK-keyed UPDATE and
   DELETE, idempotent, and only need the wording.
2. **nit** `docs/getting-started/connecting.md` (reconnect section): "half way"
   should be "halfway".
3. **nit** `tests/PgNimbus.Core.Tests/Query/QueryEngineReconnectTests.cs:366`: new
   CS8602 warning.

**Scope vs the audit:** closes finding 2 more strictly than asked: nothing is
re-sent after the send on any path, whatever the SqlState. A 57P01 during
open/describe (an idle pooled backend killed by a DBA) is still retried, because
nothing had been sent; the PR says so. The staged batch keeps its pre-COMMIT
retry with a correct argument (the transaction dies with the connection).
`ConnectionFailure.IsLoss` only changed its doc comment, so the NOTIFY listener's
reconnect is unaffected.

**Left undone, stated where:** the in-flight UPDATE that keeps running
server-side, in `connecting.md`.

### #286 Explain takes one statement (`fix/explain-one-statement`)

**Verdict:** merge after fixes.

**Verification run:** live test green; red with the refusal removed. A 14-shape
live probe of `ExplainService` against `pgn-audit`, each shape carrying
`COMMIT;` to escape the rollback transaction.

**Findings**

1. **blocker** `src/PgNimbus.Core/Text/SqlLexer.cs:156` (unchanged by the PR): a
   `--` comment ends only at `\n`; PostgreSQL's scanner ends it at `\n` or `\r`.
   `SELECT 1 --x\r; COMMIT; CREATE TABLE pwn_cr()` passes
   `ExplainService.SingleStatement`, and both Explain and Explain Analyze
   **created the table** live (the COMMIT ends the rollback transaction; the
   service then throws "This NpgsqlTransaction has completed"). The same lexer
   decides Run's caret targeting (`SqlScriptSplitter.StatementAt`), so a Run with
   the caret on the first line runs the hidden statements too. **Change:**
   `IndexOfAny(['\n', '\r'], …)` in the line-comment branch, a lexer tiling
   test, and the shape as an `ExplainServiceTests` case.
2. **should fix** (depends on #290): under `standard_conforming_strings = off`
   (a database default its owner can set),
   `SELECT 'a\''; COMMIT; CREATE TABLE pwn_scs(); --'` is one statement to the
   lexer and three to the server; the table was created live. #290's forced
   startup option closes this whenever the option reaches the server. **Change:**
   merge #290 first; as a belt, refuse a plain `'…'` literal that holds a
   backslash in `SingleStatement`.
3. **nit** `tests/PgNimbus.Core.Tests/Query/ExplainServiceTests.cs:95`: the live test's
   `SELECT 1; CREATE TABLE …` is also stopped by the new rollback transaction
   alone, so it does not prove the refusal protects anything. **Change:** add the
   `…; COMMIT; CREATE …` shape (and the `\r` shape once fixed).
4. **nit** `ExplainServiceTests.cs:51`, `:77`: new CS8602 warnings.

**Scope vs the audit:** both asks done: the refusal lives in Core
(`ExplainService.SingleStatement`, called by `ExplainAsync` and by the App's
`ExplainTarget`), and plain EXPLAIN now runs in the always-rolled-back
transaction. Shapes that held in the probe: nested comments, dollar tags,
`U&''`, `B''`, form feed, vertical tab, U+2028, a trailing comment, `a$b$`
identifiers. The named regression (a `BEGIN; …; COMMIT;` selection is refused)
reads fine: "EXPLAIN takes one statement, and 3 were given. Select a single
statement."

**Left undone, stated where:** nothing.

### #287 Client-side SCRAM verifier (`fix/scram-verifier-client-side`)

**Verdict:** merge after fixes.

**Verification run:** read the verifier against RFC 5802/7677 and libpq's
`scram_build_secret`; the live `ScramPasswordServerTests` passed in the merged
run.

**Findings**

1. **should fix** `src/PgNimbus.Core/Security/ScramSha256Verifier.cs:256` with
   `src/PgNimbus.App/PgNimbus.App.csproj:13`: the shipped app runs with
   `InvariantGlobalization`, where `Normalize(FormKC)` is the identity. A
   password NFKC would change (decomposed accents, full-width letters, ligatures,
   superscripts, U+2126) gets a verifier that libpq and pgJDBC clients, which do
   normalise, cannot match. Before this PR the server built the verifier from
   the cleartext with real NFKC. The PR calls this "not a regression" because
   Npgsql has the same limit, but the role's other clients (the application it
   was created for) do regress, silently. **Change:** when
   `NormalizationAvailable` is false and the password is not ASCII, warn in the
   role dialog or refuse the password; or carry the NFKC tables.
2. **should fix** `src/PgNimbus.Core/Security/RoleScriptBuilder.cs:347`: no
   server-version guard. Before PostgreSQL 10 a `SCRAM-SHA-256$…` literal is
   stored as the password itself, and no user-facing doc states a minimum server
   version. **Change:** refuse below `server_version_num` 100000 (the security
   window already has the version) and state the minimum in the docs.
3. **nit**: `password_encryption = md5` and PG 16's `scram_iterations` are
   ignored; psql's `\password` (`PQencryptPasswordConn`) honours both. State it,
   or read them.

**Scope vs the audit:** closes finding 7. The math is right: PBKDF2-HMAC-SHA-256,
a 16-byte CSPRNG salt, 4096 iterations, StoredKey and ServerKey, base64, and
libpq's two SASLprep shortcuts (ASCII as typed, a prohibited password hashed raw).

**Left undone, stated where:** the NFKC limit is in the type's doc comment and
the PR body; the pre-10 limit only in a doc comment.

### #288 Pinned actions and scoped tokens (`ci/pin-actions-and-scope-tokens`)

**Verdict:** merge as is.

**Verification run:** all 8 pins resolved through `gh api` (annotated tags
dereferenced): every SHA is the tag its comment names. The appimagetool 1.9.1
and type2-runtime 20251108 sha256 values match GitHub's published digests for
x86_64 and aarch64. Owner steps checked against live settings with read-only
GETs.

**Findings**

1. **nit** `.github/actions/require-on-main/action.yml:21`: the on-main gate also
   fails a `workflow_dispatch` from a PR branch, so a change to `release.yml`
   (this PR's included) cannot be rehearsed before merge. The action's
   description admits it. **Change:** for non-push events, warn instead of fail;
   the `release` job is already gated on a tag push.
2. **nit** (PR body, owner step 5): it changes nothing today, since live
   protection has `required_approving_review_count: 0` and
   `require_code_owner_reviews: false` (the PR says so). Drop it or label it
   inert. The audit's "What holds up" says `main` requires a CODEOWNERS review;
   the live setting says it does not.
3. **nit**: with immutable releases on (owner step 2), `gh release create` with
   assets creates a draft, uploads, then publishes, which is compatible. Worth
   watching on the first tag after enabling it.

**Scope vs the audit:** findings 5 and 15 in full, plus finding 18's `docs.yml`
split and the `dependency-review` required check: every `uses:` pinned,
softprops replaced by `gh release create --verify-tag`, benchmark-action pinned,
CycloneDX and wix in a tool manifest, SBOM in its own read-only job, top-level
`contents: read`, `persist-credentials: false`, the on-main gate, the publish
gate on push plus tag for both the release and `record_history`, and every
expression passed through `env:` with the version regex.

**Owner's manual steps:** 1 (tag ruleset: deletion, non_fast_forward, update) is
correct and does not block tag creation. 2 (`PUT …/immutable-releases`) and 3
(`sha_pinning_required`; local `./` actions and the local reusable workflow are
exempt) are correct. 4 (`vulnerability-alerts`, `automated-security-fixes`) is
correct. 5 is inert. 6 is correct: the check run is really named
`dependency-review`, app 15368 matches `build-test`, and `strict: false` is
today's value. Not proposed, deliberately: `enforce_admins`, which would stop
the owner's own admin merges.

### #289 Bounded plan and SQL parsers (`fix/parsers-bounded`)

**Verdict:** merge after fixes, or merge and open a follow-up for 1 to 4.

**Verification run:** depth probe against the merged Core, all 14 reader groups
(scope model, formatter, completion context, call site, keyword and command
grammars, value slot, statement inspector, browse parser, splitter, redactor,
completion edits, plan import, `JsonTree`) × 19 shapes at 100,000 deep. **No
stack overflow anywhere**, including the readers the PR did not list. Timings at
smaller sizes, re-run below the 50k-character threshold where completion stays on
the UI thread:

| Reader, shape | Size | Time |
|---|---|---|
| completion context, nested `WITH` | 2,000 deep (42k chars) | 3.4 s in `ExtractCteDefinitions` |
| completion context, nested `WITH` | 4,000 deep (44k chars) | ~46 s |
| browse parser, nested parens | 5,000 / 12,000 / 100,000 | 0.9 s / 5.3 s / >60 s |
| formatter, nested `WITH` | 4,000 / 100,000 | 2 s / throws |

**Findings**

1. **should fix** `src/PgNimbus.Core/Text/SqlCompletionContext.Ctes.cs:37`
   (`ExtractCteDefinitions`) grows about cubically and runs on popup open
   (`SqlCompletionProvider.cs:1743`, `:2283`, `:2846`), so a pasted 40 KB nest of
   CTEs freezes the editor per keystroke. **Change:** one pass with a paren
   stack, or go opaque past `SqlScopeModel.MaxDepth`; add nested `WITH` to
   `ParserRobustnessTests`' deep shapes.
2. **should fix** `src/PgNimbus.Core/Query/BrowseSqlParser.cs:43`: quadratic on
   nested parentheses in a WHERE, on the UI thread after a Run in a browse tab.
   **Change:** a depth cap (past it the query is simply not browse-shaped).
3. **should fix** `src/PgNimbus.App/Views/QueryEditorPanel.axaml.cs:1300`
   (`FormatCurrentStatement`): `SqlFormatter`'s indentation is O(depth²); a
   100,000-deep `WITH`, FROM subquery or UNION throws
   `ArgumentOutOfRangeException` (StringBuilder capacity) and the call site has
   no catch, so it reaches the crash window. **Change:** cap indentation depth in
   the formatter and catch at the call site with a status message.
4. **should fix** (older than the PR; the new comment is wrong)
   `src/PgNimbus.Core/Query/ExplainService.cs:117`: `JsonDocument.Parse` keeps the
   default `MaxDepth` of 64, which is about 31 plan levels (an object and an
   array per level). A plan deeper than that, such as a 30-plus table join, fails
   with a `FormatException`. `ExplainPlanTextParser.cs:34` says "real plans stay
   under 30; the JSON form is held to about the same", and its error says "more
   than a plan can be". **Change:** `JsonDocumentOptions { MaxDepth = 256 }`,
   text `MaxDepth` 128, reword the message.
5. **nit** (merge only): `ParserRobustnessTests` calls
   `SqlStatementInspector.IsSafeToReExecute`, which #283 deletes. Drop the two
   lines when rebasing.

**Scope vs the audit:** the four parser bullets of finding 16 are done: plan
import converts every failure to `FormatException` and reads figures by kind
(COSTS OFF works), counts saturate, the text regexes are linear
(`NonBacktracking` plus `\d+(?:\.\d+)?`, 2 s timeout, 4 MiB and depth caps), and
the scope reader and keyword grammar are iterative or depth-counted. The
`StripExplain` off-by-one it found is fixed. Generative tests now cover every
reader the audit listed.

**Left undone, stated where:** none claimed; items 1 to 3 are gaps.

### #290 Comment-safe names, PUBLIC as null, standard strings forced on (`fix/generated-sql-safety`)

**Verdict:** merge as is; follow-up recommended.

**Verification run:** a grep of the merged tree finds no interpolated `--` or
`/* */` comment carrying a catalog name outside `SqlComment.Safe` (the
slow-query header holds only numbers and times). `PublicRoleTests` and
`StandardConformingStringsTests` passed live in the merged run.

**Findings**

1. **should fix (follow-up)** `src/PgNimbus.Core/Query/SqlLiteral.cs:48` with
   `src/PgNimbus.Core/Schema/SchemaService.cs:297`: the guarantee rests on the
   startup option. Behind a pooler that drops `options` (finding 17's PgBouncer
   case) the database default applies again, and nothing checks; that reopens
   finding 13 and #286's backslash bypass. **Change:** read
   `current_setting('standard_conforming_strings')` in `GetWriteStateAsync` and
   warn as the read-only mark does; and have `SqlLiteral.Quote` emit `E'…'` with
   doubled backslashes when the text holds one, which is correct under either
   setting.

**Scope vs the audit:** closes 11, 12 and 13. The option is appended last, after
anything in `PGNIMBUS_CONN`, and it is not shown in the connection-string preview.

### #291 Owner-only, atomic app-data files (`fix/app-data-files`)

**Verdict:** merge after fixes.

**Verification run:** read `AppDataFile` in full; `AppDataIsolationTests` still
has teeth. The Unix mode tests skip on Windows; the PR reports a Linux container
run.

**Findings**

1. **should fix** (this is the merged tree's one failing test)
   `src/PgNimbus.Core/Connections/AppDataPaths.cs:26` with
   `tests/PgNimbus.Core.Tests/Settings/AppDataFileTests.cs:338`: the process-wide
   static `RootResolverForTests` is set to null under
   `[NotInParallel(nameof(AppDataPaths))]`, which does not keep other tests out.
   On the merged tree #292's
   `The_app_default_puts_its_own_file_under_the_app_data_root` failed with
   `ArgumentNullException` and passes alone. **Change:** make the seam
   `AsyncLocal<Func<string?>?>` so it reaches only the test's own flow.
2. **nit** `src/PgNimbus.Core/Settings/AppDataFile.cs:309`: every store write
   fsyncs, including the history rewrite after each Run on the UI thread. Fine
   on an SSD, felt on a network home directory. **Change:** fsync only
   `connections.json`, or write history off the UI thread.

**Scope vs the audit:** finding 10 plus finding 18's non-atomic writes, `/tmp`
fallback and workspace-restore items: 0600 files and 0700 directories created
that way (never set on Windows), tightening only inside the app-data root, a
unique temp file then rename, a corrupt file moved aside first, and a one-time
startup tightening pass off the UI thread.

### #292 SSH host key verification (`fix/ssh-host-key-verification`)

**Verdict:** merge after fixes.

**Verification run:** `SshTunnelHostKeyLiveTests` against `pgn-ssh-hostkey`:
6/6 green; with the `HostKeyReceived` handler unhooked 5/6 go red (the happy-path
query stays green, as it should). A live probe with a policy that answers late.

**Findings**

1. **should fix** `src/PgNimbus.Core/Connections/SshTunnel.cs:27`: the prompt counts
   against the 15 s connect timeout. Live, an accept after 20 s gives "Could not
   reach the SSH server 127.0.0.1:2299 (Session operation has timed out). Check
   the address, and that the VPN or network it sits behind is up." That is wrong
   advice for someone who was only reading the fingerprint carefully. **Change:**
   do not hold the handshake open on the user: fetch the key with a connection
   that stops after key exchange, ask, then connect; or retry once after an
   accepted prompt; at the least, word the error as a prompt timeout.
2. **should fix** `src/PgNimbus.Core/Connections/KnownHosts.cs:231`: entries of
   another key type are skipped, so a host known by `ssh-ed25519` that suddenly
   presents an `ssh-rsa` key gets a plain "unknown host" prompt with no warning,
   which is what an interceptor would do. **Change:** when the host is known,
   restrict `ConnectionInfo.HostKeyAlgorithms` to the known types (OpenSSH's
   behaviour), or treat it as a change, or warn in the prompt.
3. **should fix** (with #291) `src/PgNimbus.Core/Connections/SshHostKeyVerifier.cs:98`:
   `Path.Combine(AppDataPaths.GetRootDirectory(), "known_hosts")` throws once
   #291 makes the root nullable, so every SSH connect fails when there is no
   data directory. It also raises CS8604. **Change:** `AppDataPaths.Resolve`
   with keys kept in memory when it is null.
4. **should fix** (with #291) `src/PgNimbus.Core/Connections/KnownHosts.cs:319`: the
   app's own `known_hosts` is written with `File.AppendAllText`, so 0644 on Unix,
   and it lists every bastion the user reaches. **Change:**
   `AppDataFile.AppendAllText` once #291 is in.

**Scope vs the audit:** closes finding 4: `~/.ssh/known_hosts` (hashed entries,
`[host]:port`, patterns, `@revoked`) plus an app-owned file, revoked beats match
beats mismatch beats ask, the key is pinned for later re-keys, the prompt refuses
to run on the UI thread, and the connect docs mention host keys.

**Left undone, stated where:** host certificates (`@cert-authority`), in
CLAUDE.md and `connecting.md`.

### #293 Edit target identity, read-only behind a pooler (`fix/edit-target-identity`)

**Verdict:** merge as is.

**Findings**

1. **nit**: its `AGENTS.md` edit conflicts with #297, which deleted the file.
   Drop it; CLAUDE.md carries the same text.
2. **nit**: the pooler warning on the status line is cut with an ellipsis at the
   default width (the PR says so); the full text is in the tooltips.

**Scope vs the audit:** closes 14 and 17. Browse resumes only when every
column's `TableOid` is the browsed table's. The repeated-relation check covers
FROM subqueries, LATERAL and CTEs, and fails closed on a query the scope reader
gives up on. Views report their own OID, so they are no way around it. The
regression the owner asked about ("names compared without schema") has no
practical cost: two different same-named tables already have two OIDs and were
never editable.

**Left undone, stated where:** typed SQL still writes behind a pooler that drops
the option; stated in the tooltip, the status line, `connecting.md` and CLAUDE.md.

### #294 Build and docs items of finding 18 (`chore/audit-low-build-docs`)

**Verdict:** merge after fixes.

**Findings**

1. **should fix** `global.json:3`: SDK `10.0.401` with `latestFeature` is a
   floor, not a pin. CI still installs `10.0.x`, so a release is built with
   whatever SDK is newest that day; the SBOM's runtime-pack step is what really
   records it. The floor also breaks any 1xx-band SDK. The .NET SDK container
   carries 10.0.401 and is fine, but Ubuntu's source-built apt package, which
   the Linux sandbox recipe installs (now in `.claude/skills/verify/SKILL.md`),
   is normally 1xx; not verified here. **Change:** pass `global-json-file` to
   `setup-dotnet` for release builds, and either lower the floor to `10.0.100`
   or change the sandbox recipe.
2. **nit**: its SBOM step belongs in #288's `sbom` job (done in the trial merge;
   the merged step lost its `\` line break and is one long line).

**Scope vs the audit:** SDK pin, runtime pack in the SBOM, `nuget.config` with
`<clear/>` and source mapping, the verification docs (`--signer-workflow`,
`--source-ref`, `sha256sum -c`), hardened runtime with
`disable-library-validation`, and the docs items. Hardened runtime cannot be
tested on this machine; `release.yml`'s smoke launch from the mounted DMG would
catch a launch failure, so rehearse with a `workflow_dispatch` before the next tag.

### #295 Bounded results, blocking tree, NOTIFY (`fix/ui-bounded-results`)

**Verdict:** merge as is.

**Findings**

1. **nit**: the per-cell materialisation cap and the array/hstore preview
   formatting are left undone and stated only in the PR body. **Change:** one
   sentence in CLAUDE.md's result-bounds bullet.

**Scope vs the audit:** blocking tree as a spanning tree (each backend once,
under the first blocker a breadth-first walk from the roots reaches, pid order,
"also blocked by" label), a 256 MiB byte budget beside the row cap, a shared
budget for script sections, a 1,000-column cap, and NOTIFY coalescing. Checked
live: abandoning a stream now cancels it, and an `INSERT … RETURNING` of 300,000
rows capped at 1,001 still inserted every row (main and merged), because
PostgreSQL finishes the write before rows stream. The changed tree shape is
deterministic across refreshes and still names every blocker.

### #296 Workspace and history redaction, history switch (`fix/redact-workspace-and-history`)

**Verdict:** rework the workspace half; the redactor and history half is ready.

**Verification run:** a 24-shape redactor probe against the merged Core.

**Findings**

1. **blocker** `src/PgNimbus.Core/Settings/WorkspaceStore.cs:80` with
   `src/PgNimbus.App/ViewModels/MainViewModel.cs:759`: the snapshot redacts every
   tab, file-backed ones included, and the restore puts the snapshot text in the
   buffer before attaching the file. A migration file holding
   `CREATE ROLE app LOGIN PASSWORD 'x'`, opened and never edited, reopens after a
   restart dirty and showing `'<redacted>'`, and one Ctrl+S writes the
   placeholder over the real file. Over-redaction makes it worse for any tab:
   `' || quote_literal('s3cret')` becomes `'<redacted>')`, and every literal after
   a hanging PASSWORD is replaced. **Change:** do not persist the text of a clean
   file tab (read it from disk on restore), and refuse to persist a tab that
   `ContainsSecret` flags (restore it empty with a note), as the audit preferred.
   Keep redaction for history.
2. **should fix** `src/PgNimbus.Core/Security/SecretRedactor.cs:59`: the replacement
   `'<redacted>'` is a valid literal, so re-running a restored or
   history-opened `ALTER ROLE x PASSWORD '<redacted>'` sets that password (the
   same for a user mapping). **Change:** a marker that fails to parse, such as
   `PASSWORD /* redacted */` with no literal.
3. **nit**: the probe's leaks: a quoted option name
   `OPTIONS ("password" 's3cret')`, and `set_config('my.password', 's3cret', false)`.
   A PL/pgSQL variable holding the value is out of scope by design. Everything
   else held: `DO $x$`, `format(… %L …)`, `quote_literal`, conninfo in
   SUBSCRIPTION, dblink, `ALTER SYSTEM` and `COPY … PROGRAM 'PGPASSWORD=…'`,
   URI userinfo, `E''`, `U&''`, dollar quotes, comments between the keyword and
   the literal, `\r`.

**Scope vs the audit:** the redactor gaps, the history store as the choke point,
the one-time scrub, and the "Record query history" switch are done. The workspace
takes the redaction route the audit offered as its first option, and the PR body
names the re-run hazard itself.

**Left undone, stated where:** `pgp_sym_encrypt` keys, in the redactor's doc
comment and the PR body only. **Change:** one line in `connecting.md`'s history
paragraph.

### #298 Confirm destructive actions (`fix/confirm-destructive-actions`)

**Verdict:** merge as is.

**Findings**

1. **nit** `src/PgNimbus.App/Views/SchemaTreePanel.axaml.cs:310`: `ConfirmDialog` is
   always the danger style, so "Install" is a red button. Acceptable, or add a
   non-danger variant.

**Scope vs the audit:** closes 6. Drop column names `schema.table.column`,
install extension names the database, and a non-safe-mode multi-row delete goes
through `ApplyBatchAsync`: one transaction (a SAVEPOINT inside the user's own),
`ExpectedRowsAffected = 1`, and "Delete failed, nothing deleted" on any failure.

### #299 TLS defaults, root certificate, verify-full through the tunnel (`fix/tls-defaults-and-root-cert`)

**Verdict:** merge after fixes.

**Findings**

1. **should fix** `src/PgNimbus.Core/Connections/ConnectionStringParser.cs:706`:
   `sslrootcert` now comes through the paste box, which the audit praised for
   letting no transport option through. On Windows a pasted
   `sslrootcert=\\host\share\ca.pem` makes the app read the CA over SMB (an NTLM
   exchange with that host), and under Verify full the attacker's CA then
   "verifies" the attacker's certificate. **Change:** accept only a local absolute
   path from a paste (refuse UNC, `\\?\UNC\` and URLs) and show a pasted root
   certificate for confirmation.
2. **should fix** `src/PgNimbus.App/ViewModels/ConnectionDialogViewModel.cs:194`:
   `Require` by default fails against the default local Docker Postgres
   (`ssl = off`), which is the first thing many people try with the `localhost`
   placeholder. The new hint helps. **Change:** default to Prefer when the host
   is loopback (no network path to attack) and Require otherwise.
3. **nit** `ConnectionStringParser.cs:706`: libpq treats `sslmode=require` plus
   `sslrootcert` as verify-ca; here it stays Require and Npgsql ignores the root
   certificate. **Change:** map that pair to VerifyCa.

**Scope vs the audit:** closes 9. The `TargetHost` callback is set only for a
tunnel, the missing `SslMode` field now defaults to Require (tested through the
source-generated context), and the tunnel test goes red when the callback is a
no-op (PR body).

**Left undone, stated where:** `RequireAuth=scram-sha-256`, in the PR body only.

### #300 Finding 18 client items (`fix/audit-low-client`)

**Verdict:** merge as is.

**Findings**

1. **nit** `src/PgNimbus.App/Views/CrashWindow.axaml.cs:125`: "… (truncated — see the
   attached log)" puts an em dash in the issue body.

**Scope vs the audit:** checked in the code: cast types are read with
`format_type` under a `search_path` narrowed to `pg_catalog` in a rolled-back
transaction, so built-ins such as `character varying(20)` stay as they are and
the rest is qualified and quoted (`SchemaServiceCastTypeTests` passed live); the
secret clipboard sets the Windows history and cloud exclusions, macOS
`ConcealedType` and the KDE hint, and clears after 30 s only if the text is
unchanged; "Safe for Spreadsheets" lives in the Export menu, off by default;
GRANT uses identity arguments and `ON ROUTINE`. A type found through
`pg_catalog` (`jsonb`, `uuid`) stays unqualified, so an explicit
`search_path = x, pg_catalog` could still shadow it; contrived, not a finding.

### #301 Credential store: `.cred` migration and session cache (`fix/audit-low-credentials`)

**Verdict:** merge after fixes.

**Findings**

1. **should fix** `src/PgNimbus.Core/Connections/RecoverableCredentialStore.cs:172`:
   the one-pass migration keeps trying every file after the OS store has failed,
   while holding the store's lock. With a locked or hung Secret Service (up to
   15 s per call, about three calls per file) the first Connect, which waits for
   the credential chain, waits N × ~45 s. **Change:** stop at the first storage
   failure and report the remaining files as left.

**Scope vs the audit:** only failed-write passwords stay in memory, a failed
delete is remembered by id, a window close forgets its profile's session-only
passwords unless another window still uses them, and a verified round trip is
still required before a `.cred` file is removed.

## Cross-PR

### Trial merge onto `bcb7f4e`, PR-number order

| Merged | Conflicts | Suggested resolution |
|---|---|---|
| #283, #284, #286, #287, #288, #289 | none | |
| #290 | `RoleScriptBuilder.cs` (`RenderPassword`) | keep #287's `ScramSha256Verifier.Build`; drop the private `CommentSafe` that #290 moved to `SqlComment.Safe` |
| #291, #292 | none | |
| #293 | `AGENTS.md` (deleted by #297), `CLAUDE.md`, `tools/Screenshot/Fixtures.cs`, `Scenarios.cs` | keep `AGENTS.md` deleted; `MainWindowViewModel(WorkspaceEntry? workspace = null, bool readOnlyProfile = false)` passing both; append both scenarios |
| #294 | `.github/workflows/release.yml`, `CLAUDE.md` | move its runtime-pack step into #288's `sbom` job (keep the `\` continuation) |
| #295 | `QueryViewModel.cs`, `QueryEngine.cs` | drop `allowTextFallback` (#283 removed it); keep `maxBytes`/`ResultBudget`; keep #284's `sent` beside #295's `cancelled` and `cappedBy` |
| #296 | `QueryHistoryStore.cs`, `CLAUDE.md`, `connecting.md` | #296's redaction on top of #291's `AppDataFile.ReadJson`/`WriteJson` |
| #298 | none | |
| #299 | `ConnectionDialogViewModel.cs` (two connect paths), `Scenarios.cs`, `CLAUDE.md` | `SshTunnel.Connect(…, HostKeys)` from #292 feeding #299's endpoint tuple into `CreateDataSource` |
| #300 | `CrashWindow.axaml.cs`, `CrashLogger.cs`, `GrantScriptBuilderTests.cs`, `CLAUDE.md` | keep the null check on the log path and `CrashLog.HomeRelative`; both usings; both test sets |
| #301 | none | |

Semantic conflicts found after the textual merge:

- `ParserRobustnessTests` (#289) calls `IsSafeToReExecute`, deleted by #283. Drop
  the two calls.
- `ReadOnlyConnectionTests` (#293) calls `Fixtures.MainWindowViewModel(readOnlyProfile)`
  positionally; after the merge the first parameter is #291's `workspace`. Use a
  named argument.
- `SshHostKeyVerifier.DefaultOwnKnownHostsPath` (#292) against #291's nullable
  root, plus #291's global test seam: the merged tree's one failure (#291
  finding 1, #292 finding 3).
- #292 writes `known_hosts` without #291's helper (#292 finding 4).
- #286 is only fully safe with #290's forced `standard_conforming_strings`, and
  #290 is only safe while the startup option reaches the server (#290 finding 1).

No conflict markers are left. The merged tree builds; a clean rebuild has
**zero IL warnings** and five new nullable warnings (#284 `QueryEngineReconnectTests.cs:366`,
#286 `ExplainServiceTests.cs:51` and `:77`, #292 `SshHostKeyVerifier.cs:98` and
`SshHostKeyVerifierTests.cs:213`). `PgNimbus.Core` still references only Npgsql,
ProtectedData and SSH.NET (#291 adds an `InternalsVisibleTo`), no new
reflection-based `JsonSerializer` call appears, and no PR adds an em or en dash
to a user-facing doc.

**Merged-tree run** (fresh `postgres:17` plus the sshd): Core 1,889 total, 1,878
passed, 1 failed, 10 skipped (Unix file modes and the native store on Windows,
pg_stat_statements); App 693 total, 692 passed, 1 skipped (pg_stat_statements).
The failure is #292's `The_app_default_puts_its_own_file_under_the_app_data_root`,
which fails only in the merged run and passes alone (see above).

### `main` moved: #302

#302 (`9aea487`) moved five CLAUDE.md sections verbatim into `.claude/rules/*.md`
and the Linux sandbox recipe into the verify skill. Against the new `main`, six
branches now conflict in `CLAUDE.md` and one in `AGENTS.md`:

| Branch | Conflict in | Re-apply the hunk to |
|---|---|---|
| #283, #284, #289 | the completion paragraph under "Coding conventions" | `.claude/rules/sql-completion.md` |
| #288, #294 | "Benchmarks, release pipeline, Store, website" | `.claude/rules/release-ci.md` |
| #291 | same section | `.claude/rules/release-ci.md`, and check `headless-tests.md` for the app-data paragraph |
| #293 | `AGENTS.md` | nothing (keep it deleted) |

The other ten merge cleanly onto `9aea487`.

### Proposed merge order

Each step says what the next PR's author has to resolve once the one before is
on `main`.

1. **#288** first: every later PR then runs CI on pinned actions. Re-apply its
   CLAUDE.md hunk to `release-ci.md`.
2. **#294**: move its SBOM step into #288's `sbom` job; CLAUDE.md to `release-ci.md`.
3. **#283 → #284**, as the existing GitHub stack (merging #284 merges both).
   CLAUDE.md to `sql-completion.md`.
4. **#290**, before #286, so the lexer and the server agree on backslashes.
5. **#286**, after its lexer fix.
6. **#289**: drop the `IsSafeToReExecute` calls; CLAUDE.md to `sql-completion.md`.
7. **#287**: resolve `RenderPassword` against #290 as in the table.
8. **#291**, with the `AsyncLocal` seam.
9. **#301**.
10. **#292**: on top of #291, adopt `AppDataPaths.Resolve` and `AppDataFile`.
11. **#296**: its `QueryHistoryStore` on #291's helper; after the workspace rework.
12. **#293**: drop `AGENTS.md`; named `Fixtures` argument.
13. **#295**: drop `allowTextFallback`.
14. **#298**.
15. **#299**: `SshTunnel.Connect(…, HostKeys)` into `CreateDataSource`.
16. **#300**: crash window, crash log and grant-test conflicts as in the table.

PR-number order also works mechanically (the trial merge used it); this order
puts each dependency first and keeps `main` building after every step.

### Left undone by design

| Item | PR | Stated where |
|---|---|---|
| Per-cell materialisation cap, array/hstore preview formatting | #295 | PR body only |
| `RequireAuth=scram-sha-256` | #299 | PR body only |
| SSH host certificates | #292 | CLAUDE.md, `connecting.md` |
| Typed SQL not blocked on a pooled read-only profile | #293 | tooltip, status line, `connecting.md`, CLAUDE.md |
| `pgp_sym_encrypt` keys not redacted | #296 | redactor doc comment, PR body |

### Commit trailers

Recorded as found, no action taken: `Claude Fable 5.1` on #283, #284, #286, #293,
#298 and one of #288's two commits; `Claude Opus 5.5 (1M context)` on #287,
#288, #289, #290, #291, #292, #295, #296, #299, #300, #301; `Claude Sonnet 5` on
#294.

## After the review (2026-09-29)

The fixes were made on each PR's own branch, and the 17 branches were stacked
as one GitHub stack, bottom to top: #283, #284, #288, #294, #290, #286, #289,
#287, #291, #301, #292, #296, #293, #295, #298, #299, #300, then this document.
The order is the proposed merge order with the existing #283 → #284 stack kept
at the bottom (a stack can grow at the top but not be reordered). Each branch
merges the one below it, so every conflict above (and #302's move of CLAUDE.md
sections into `.claude/rules/`) was resolved once, in the branch where it first
appears, and every push was a fast-forward.

| PR | What was done |
|---|---|
| #283 | The stale `IsSafeToReExecute` sentence removed (now in `sql-completion.md`). |
| #284 | A loss after the send of a non-query throws `StatementOutcomeUnknownException`; Add-row says the row may already be there. "halfway". |
| #288 | Its CLAUDE.md edits moved to `release-ci.md`. No code change. |
| #294 | Its SBOM step moved into #288's `sbom` job; `global.json` floor lowered to 10.0.100. |
| #290 | `SqlLiteral` writes `E'…'` for text with a backslash, so a pooler dropping the startup option no longer reopens finding 13; `BrowseSqlParser` reads that form back as a typed chip. |
| #286 | **Blocker fixed**: `SqlLexer` ends a `--` comment at `\r` too; the live Explain test covers the `\r` and `COMMIT` shapes. |
| #289 | CTE extraction capped (3.4 s to 10 ms at 2,000 nested CTEs), browse parser and formatter decline past 64 levels, JSON plans read to depth 256. |
| #287 | The role editor refuses a password before PG10 and warns on a non-ASCII password without NFKC. |
| #291 | The app-data root seam is an `AsyncLocal`, which fixed the merged tree's one failing test. |
| #301 | The `.cred` migration stops at the first store refusal. |
| #292 | Known key types restrict the host key algorithms; a slow accept is retried once; no app-data folder keeps keys in memory; `known_hosts` written through `AppDataFile`. |
| #296 | **Blocker fixed**: a file-backed tab holding a secret keeps no text and reopens from its file; the marker is `'<redacted>'::redacted`, a syntax error if run. |
| #293 | `AGENTS.md` dropped; `Fixtures` takes both parameters. |
| #295 | CLAUDE.md names the two bounds still missing. |
| #298 | Merge only. |
| #299 | A pasted remote `sslrootcert` is refused; `require` + root certificate reads as VerifyCa; a new form defaults to Prefer for this machine. |
| #300 | The crash report's truncation note lost its em dash. |

Both suites passed at the top of the stack against a live PostgreSQL 17 and the
sshd (Core 1,914 passed, 10 skipped; App 699 passed, 1 skipped). Left for
follow-up issues: #283's export of a hand-written query past the display cap
(offer a re-run inside `BEGIN READ ONLY`), #290's runtime warning when
`standard_conforming_strings` is off, #289's `ExpandSelectStar` on a pasted
deep nest (runs on the explicit star expansion only), and the nits (#288's gate
on dispatch rehearsals, #291's fsync per history write, #298's red Install
button, #293's truncated status line).
