# pgNimbus — project memory

## What this is

A fast, open-source PostgreSQL GUI client (.NET 10 + Avalonia 12), MIT
licensed. Windows is the primary target; the core engine stays
cross-platform-capable. The thesis: **truly fast + open source + modern UI** —
a gap none of pgAdmin/DBeaver (heavy), TablePlus (fast but paid/closed), or
HeidiSQL (fast but dated, MySQL-first) fill. pgNimbus aims for HeidiSQL's
speed with TablePlus's polish, PostgreSQL-first.

## Keep this file current

Whenever a change touches something this file documents — tech stack
versions, architectural rules, coding conventions, the sandbox bootstrap
steps — update the corresponding section in the same commit/PR. That
includes the path-scoped files in `.claude/rules/` (completion, window
chrome, logo assets, headless tests, release/CI) and the `verify` skill,
which hold what used to be sections here. Treat a
stale `CLAUDE.md` (e.g. it still saying "Avalonia 11" after an upgrade to
12) as a bug, not a nitpick: it's the first thing a fresh session reads,
and wrong project memory is worse than none.

## Repository layout

The root holds only what tooling needs there (`PgNimbus.slnx`, `global.json`,
`nuget.config`, `Directory.Build.*`, `mkdocs.yml`) and the files GitHub shows
(README, LICENSE, CONTRIBUTING, SECURITY, CODE_OF_CONDUCT, ROADMAP). This file
lives in `.claude/`, which Claude Code reads the same as the root.

- `src/` — `PgNimbus.Core` (the engine) and `PgNimbus.App` (the Avalonia UI).
- `tests/` — `PgNimbus.Core.Tests`, `PgNimbus.App.Tests`, `PgNimbus.Benchmarks`.
- `tools/` — dev-only programs: the screenshot harness, `CompletionBench`,
  `UiBench` (UI-thread timings over big data; see "UI-thread work that grows
  with the data" under coding conventions), and `promo-video` (a Node project:
  the promo video rendered from a storyboard, with clips cut from the Store
  trailer and stills from the harness; its README says how to rebuild it).
- `shared/nimbusUi/` — the git subtree shared with kubeNimbus. Never move it:
  `git subtree push --prefix shared/nimbusUi` depends on the prefix.
- `packaging/` — installer and store templates, one folder per target
  (`msix`, `macos`, `linux`, `winget`; the Windows zip needs no template).
- `scripts/` — build, release, screenshot and design scripts.
- `.oss-scanner/` — what Anthropic's [OSS Scanner](https://github.com/anthropics/oss-scanner)
  builds and reads: a `Dockerfile` (the SDK image with every package restored,
  PostgreSQL 17 and an sshd, both suites run once), `services.sh` (starts the
  two servers and exports `PGNIMBUS_TEST_CONN`/`PGNIMBUS_TEST_SSH`) and
  `threat_model.md` (what is untrusted, how findings are rated, what is accepted
  by design). The scan runs with no network, so anything a test or reproducer
  needs is fetched in the Dockerfile. No CI job builds it (a failed build is
  emailed by the scanner); the commands to check it by hand are at its top. A
  change that moves a trust boundary, or accepts a finding by design, updates
  `threat_model.md` in the same PR.
- `docs/` — the published docs site only; `docs/dev/` holds contributor notes
  (design records, release checklists), kept off the site by `mkdocs.yml`. The
  "security audit 2026-09, finding N" citations scattered through this file are
  explained in
  [`docs/dev/design/security-audit-2026-09.md`](../docs/dev/design/security-audit-2026-09.md),
  with the review of the fix set beside it.
- `design/` — brand sources, masters and Store listing media. `website/` — the
  landing page.

## Pull requests: dependent work goes in a GitHub stack

`main` is protected and the owner reviews every PR, often several in a row, so
the order PRs merge in is not something to rely on. When one change builds on
another that isn't in `main` yet, make them a **native stacked pull request**
([GitHub docs](https://docs.github.com/en/pull-requests/how-tos/stacked-pull-requests)),
never a PR whose base is picked by hand as another PR's branch.

Why this is a rule (2026-09-27): #262 and #265 were opened with #260's branch
as their base. #260 was squash-merged first, and the repo kept merged branches
(`delete_branch_on_merge` was off; it is on since 2026-09-30, which makes GitHub
retarget such a PR onto `main`, but a stack is still the rule), so nothing
retargeted the two. They were
merged into a branch that led nowhere and showed *Merged* while neither
change was in `main`, until #263 carried them in. In a GitHub stack that can't
happen: when the bottom PR merges, GitHub rebases the rest and retargets the
next one onto `main` (squash included), and merging the top PR merges the whole
stack, bottom up. Branch protection and required checks apply to every PR in
the stack.

**Repository settings are kept the same in pgNimbus and kubeNimbus** (2026-09-30;
nimbusUi carries the security half). On `main`: the required checks
(`build-test` and `dependency-review` here, `Build & test` and
`dependency-review` there), resolved conversations, no force push or deletion,
and **no required approval** (CODEOWNERS only names who is asked). `v*` tags sit
under a `Release tags` ruleset (create, never move or delete), releases are
immutable, `sha_pinning_required` is on, Dependabot alerts and security updates,
secret scanning with push protection and private vulnerability reporting are on,
and merged branches are deleted. A change to one repo's settings is made to the
other in the same session.

How, with the `gh stack` extension (`gh extension install github/gh-stack`,
gh ≥ 2.90; installed on the dev machine):

- New work: `gh stack init <bottom-branch>`, commit, `gh stack add <next-branch>`,
  … then `gh stack submit` (pushes and opens or updates every PR with the right
  bases; `--auto` skips the editor and makes drafts unless `--open`).
- Branches that already exist: `gh stack init <bottom> <next> …` adopts them
  bottom to top.
- PRs that already exist: `gh stack link <pr-or-branch> <pr-or-branch> …`
  (bottom to top) creates or grows the stack on GitHub without local tracking.
- After something below merges: `gh stack sync` fetches, rebases the rest,
  pushes. `gh stack view` shows where each PR stands.

Independent changes don't need a stack; base them on `main` and accept a small
doc conflict later rather than chaining them. Limits: stacks are same-repo only
(no forks), and the feature is in public preview.

## The sibling project, and what is shared with it

kubeNimbus (`X:\source\kubeNimbus`, normally checked out beside this repo) is the
same product for Kubernetes, and the two must look and behave like one family.
The shared half lives in **[`shared/nimbusUi`](../shared/nimbusUi/)** — a git subtree
of [nimbusUi](https://github.com/Shman4ik/nimbusUi), referenced as an ordinary
`ProjectReference`:

- `Theme/Tokens.axaml` — the palette, radii, scrollbars, Fluent resource overrides.
- `Theme/Icons.axaml` — the MDI glyphs both apps draw.
- `Theme/Theme.axaml` — the shared style classes (`card`, `layer`, `chip`,
  `toolbar`, `searchpill`, `statusBar`, …).
- `Theme/Controls.axaml` — the Fluent **control** retheming: `TextBox`/`ComboBox`/
  `NumericUpDown` radius and brand text selection, one-line `TextBox` text and
  placeholder centred vertically (Fluent pins them under the top padding, so a
  smaller font sat high: the sidebar filter's placeholder above its magnifier), `SelectableTextBlock`,
  `ListBox`/`ListBoxItem`/`TreeView`/`TreeViewItem` rounded rows, `DataGrid` soft
  rules, the `.soft` and `.soft.danger` button families, `ToggleButton.soft`, the
  chip checked-hover washes, `TabControl`. **These moved out of
  `Styles/Theme.axaml`**, where they had been defined for pgNimbus alone: kubeNimbus
  had none of them and was drawing stock Fluent inputs, lists and grids next to
  these, which is what made the two apps stop looking like one family. Change them
  there, not here. What is left in this app's own `Styles/Theme.axaml` is
  `TabControl.segmented` and the AvaloniaEdit completion/search themes — all
  genuinely pgNimbus's (see DESIGN.md's not-shared table). The full-width capsule
  strip went up as `TabControl.capsule` (2026-10) when kubeNimbus's settings page
  needed it too; it was `TabControl.sidebar` here.
  **Load order is the only precedence, and the move broke it once** (2026-09).
  Avalonia styles have no specificity: for one property, the later style wins,
  whatever the selectors. `Controls.axaml` is included at the top of `Theme.axaml`,
  so the `Button.chip.active` wash that moved there with the rest now loaded
  *before* the base `Button.chip` rule, which reset every active chip to
  transparent at 0.6 opacity. The plan header's Color and Text/Tree switches and
  the cell inspector's View/Edit showed no selected segment until then. The wash
  now sits after the base rule in `Theme.axaml`, and
  `ChipActiveStateTests` renders an active chip and reads its brush. The rule
  this taught is nimbusUi's `CLAUDE.md` hard rule 7. A checked `ToggleButton.chip`
  also sets its own `Foreground` (`ToggleButtonForeground`) on the template part in
  all three states: Fluent's checked foreground is the accent-fill white, drawn on
  the chip's light wash, and the Wrap toggle and the filter pin were unreadable
  whenever they were on (`ChipActiveStateTests` reads that brush too).
  Since DESIGN.md rule 20 (2026-09-28) the shared layer also carries the body text
  size (13), row density, the two faces of a selected row, the focus ring
  (`Controls/FocusRing`), the switch (`Theme/ToggleSwitch.axaml`), the menu look,
  the flat buttons' hover and the opt-in `DataGrid.zebra`. See UI design rule 10
  for where pgNimbus opts in.
- `Chrome/` — the one-bar window chrome and its drawn caption buttons.
- `Hotkeys.cs` — Ctrl/Cmd resolution; `PgNimbus.App.Hotkeys` forwards to it.
- **[`DESIGN.md`](../shared/nimbusUi/DESIGN.md) — the UI rules, single source.**

Three rules about it:

1. **A change to a shared surface is a change to both apps.** Edit the files in
   place, build pgNimbus, then `git subtree push --prefix shared/nimbusUi`, pull
   it into kubeNimbus and build that too. Both working copies are normally open
   side by side, so this is one session's work, not a follow-up ticket. The PR
   template asks for the paired PR.
2. **The membership test is "can it be described without naming Postgres?"** If
   yes it probably belongs up there; if no it stays here. When in doubt leave it
   here — a wrong thing pulled up has to be un-shared against two consumers.
3. **`DESIGN.md` owns the rule text; this file owns the evidence.** Don't restate
   a shared rule here in full — that is exactly how the two files started
   disagreeing (the same red carried two names and two values for months).

## Hard architectural rules

1. **`PgNimbus.Core` has zero Avalonia/UI dependencies.** Its only packages are
   `Npgsql`, `System.Security.Cryptography.ProtectedData` (the DPAPI credential
   store) and `SSH.NET` (the tunnel) — all headless. Anything UI-related belongs
   in `PgNimbus.App`. This keeps the engine reusable for a future CLI or test
   harness — don't leak `Avalonia.*` or `CommunityToolkit.Mvvm` types into
   `Core`.
2. **Streaming + cancellation are non-negotiable.** `QueryEngine.ExecuteAsync`
   returns result rows via `IAsyncEnumerable<RowBatch>` in ~200-row batches so
   the UI can render before the full result set arrives. Every execution
   takes a `CancellationToken` and must actually stop mid-flight, not just at
   the start. The one deliberate exception: inside an explicit transaction
   (`BeginTransactionAsync`), statements run on the single held session
   connection and return a fully-materialized `MaterializedResultSet` instead —
   a lazily-streaming reader would pin that connection open and block the next
   statement in the transaction. A failed statement inside a transaction
   auto-rolls-back the block (so the connection never lingers in Postgres's
   aborted-transaction state), and `TransactionStateChanged` is how the App's
   "in transaction" indicator stays in sync no matter which path changed it.
   Auto-reconnect (2026-07, reshaped 2026-09): `QueryEngine` classifies a
   failure as connection loss (Postgres class-08 `SqlState`s / an admin or
   crash shutdown, or an `NpgsqlException` wrapping a socket/IO exception —
   deliberately not `TimeoutException`, which Npgsql also uses for command
   timeouts and pool exhaustion) versus an ordinary statement error, and on
   loss flushes the whole pool so the next rent opens a fresh socket. **What
   is retried is only what ran nothing.** Every path that may retry describes
   the statement before sending it (`DescribeAsync`; see "Describe first,
   execute once" under coding conventions), and a loss during open or describe — the dead pooled
   socket a laptop sleep, a dropped tunnel or a backend terminated *while
   idle* leaves behind — is retried once on a fresh connection, invisibly.
   A loss after the send is never retried, on any path: the statement is
   reported with `QueryError.ConnectionLost` and `OutcomeUnknown` set and a
   message saying it was not run again and may or may not have taken effect.
   The 2026-09 security audit (finding 2) reproduced why: an `INSERT` a DBA
   killed with `pg_terminate_backend` mid-run came back as 57P01, which
   `ConnectionFailure.IsLoss` rightly calls a loss, and the old retry
   re-sent it — the row was there and no error was shown. The same 57P01
   arrives for a backend killed while idle, so the classifier cannot tell the
   two apart; the *timing* of the failure can, which is what the `sent` flag
   keys on. `ExecuteNonQueryAsync` (grid edits and the Add-row INSERT) throws
   the loss instead of re-sending, as `StatementOutcomeUnknownException` once
   the statement was sent, so Add-row says the row may or may not have been
   inserted rather than "Insert failed" (which invited a second, duplicate
   INSERT); a script retries its first statement only
   when it never went out. The one place a statement that went out is sent
   again is the pre-commit staged batch, which is safe for a reason the
   single-statement paths lack: it ran inside its own transaction, a
   connection that dies before `COMMIT` takes the whole transaction with it
   server-side, so nothing from the first attempt can have landed; once
   `COMMIT` was attempted it never retries either. A failure mid-stream (rows
   already delivered) never retries. An explicit transaction is never
   silently re-established: a lost connection there clears
   `_transactionConnection` without sending `ROLLBACK` (no live socket to
   send it down) and returns a `QueryError` with `ConnectionLost`/`RolledBack`
   set, stating plainly that the transaction is gone and nothing from it
   committed. `QueryEngineReconnectTests` holds both halves: the idle kill is
   transparent, the mid-run kill is reported with the table still empty.
   The classification itself lives in `Query/ConnectionFailure.IsLoss`, not in
   `QueryEngine` (2026-08): the LISTEN/NOTIFY listener holds a connection open
   for hours and has to answer the same question when its wait loop throws, and
   two hand-kept copies of "what a dropped socket looks like" is exactly the
   kind of thing that drifts. `QueryEngine.IsConnectionLoss` forwards to it.
3. **PostgreSQL-first, not lowest-common-denominator.** `SchemaService` reads
   `pg_catalog` directly (not `information_schema`) so it can see materialized
   views, partitioned tables, and real Postgres semantics (e.g. primary-key
   flags via `pg_constraint`). Relation sizes ride the same path:
   `GetTablesAsync` carries `pg_total_relation_size` per relation for the
   schema tree's dim size hint (null for views and partitioned parents — no
   own-storage size worth showing), shown only when the "Show relation sizes"
   preference is on — off by default, on the Preferences page's Appearance
   section, persisted as `AppSettings.ShowSchemaSizes`. The **Database Overview** panel is backed
   by `Monitoring/DatabaseStatsService` (a read-only sibling of
   `ActivityService`), which reads the `pg_stat_*`/`pg_statio_*` views and the
   `pg_*_size` functions for db size, cache-hit ratios, largest relations
   (heap/index split), per-table seq-vs-index scan usage, and unused
   non-constraint indexes. Human-readable byte counts go through
   `PgNimbus.Core.ByteSize` (base-1024, unit-tested, shared by both) rather
   than being formatted ad hoc in the App. All four monitoring windows follow
   the same shape: one-live-instance, opened from the command palette (and the
   macOS Query native menu), no new toolbar button. The **Server Activity**
   window (backed by `ActivityService`) is two tabs: the flat
   `pg_stat_activity` grid, and a **Blocking** who-blocks-whom lock tree.
   `ActivityService.GetBlockingAsync` reads `pg_blocking_pids(pid)` (the
   authoritative "who holds the lock I want" — it understands lock groups /
   parallel workers, unlike a hand-rolled `pg_locks` self-join) plus one
   ungranted `pg_locks` row per waiter for the lock label; the pure,
   unit-tested `Monitoring/BlockingTree.Build` (a read-only sibling of
   `Json/JsonTree`) shapes those flat rows into a blocker→blocked forest,
   robust to chains, multi-blocker waiters, invisible (out-of-snapshot)
   blockers, and transient deadlock cycles (guarded against infinite
   recursion). **It is a spanning tree** (2026-09, security audit finding 16):
   each backend appears once, under the first of its blockers a breadth-first
   walk from the roots reaches (pid order, so every refresh has the same shape),
   and `BlockingTreeNode.AlsoBlockedBy` names the rest, which the row shows as
   "also blocked by …" (`BlockingNode.OtherBlockersLabel`; a root says "blocked
   by …" for a blocker outside the snapshot or in a cycle). It used to repeat a
   waiter under every blocker, subtree and all, and `pg_blocking_pids` reports
   soft blocks too: N sessions queued on one hot row form a complete DAG with
   about 2^(N-3) paths, so 30 waiters built ~134M nodes on the UI thread every
   2 s, during exactly the incident the tab is for. `BlockingTreeTests` builds
   that DAG under a timeout. The tree's nodes auto-expand so the whole wait chain shows at a
   glance and survives the 2s auto-refresh rebuild; cancel/terminate on the
   Blocking tab target the *selected* node's pid (aim at the root holder to
   release everyone beneath it).
   The third is the **LISTEN/NOTIFY monitor** (`Notifications/NotificationListener`
   + `NotifyMonitorWindow`), which stopped being a permanent sidebar tab in
   2026-08 — see UI design rule 1 for why that was the wrong home. Four things
   about it are load-bearing. (a) **It must never claim to be listening when it
   is not.** Npgsql only delivers notifications while something waits on the
   connection, so the listener parks in a `WaitAsync` loop; a dropped connection
   used to fault that loop with nobody observing the exception, leaving the dot
   green forever. The loop now classifies the failure through
   `ConnectionFailure.IsLoss`, retries **once** on a fresh connection with every
   channel re-`LISTEN`ed (pool flushed first, same reasoning as the engine's
   retry), and otherwise raises `Stopped` so the view model can drop
   `IsListening`. `Reconnected` is surfaced too, because NOTIFY keeps no backlog:
   anything published while the socket was down is simply gone, and the user has
   to know that. Both events are raised from the background loop, so the view
   model marshals them. (b) **Channels are persisted per connection**
   (`AppSettings.NotifyChannels`, read/rewritten only through the Core-pure
   `Settings/NotifyChannels`, keyed `host/database` like the workspace snapshot
   and the autocomplete exclusions) — retyping them after every restart was most
   of why the panel went unused. Restored channels do **not** auto-start the
   listener; opening a connection nobody asked for is the same surprise
   `AutoConnectLastProfile` defaults away from. (c) **The payload is a document,
   not a line.** Most NOTIFY payloads are JSON, and the window's detail pane is
   driven by the results grid's own `CellInspectorViewModel` in read-only mode,
   so pretty-printing and the collapsible `JsonTree` come for free rather than
   being reimplemented. The feed itself is capped at
   `NotifyMonitorViewModel.MaxNotifications` (500) — a chatty channel would
   otherwise grow it all afternoon. **A flood is coalesced before it reaches the
   dispatcher** (2026-09, security audit finding 16): the listener's event used
   to post one UI-thread item per notification, an unbounded queue with the cap
   applied only as each item ran. `Receive` now queues under a lock (keeping at
   most `MaxNotifications`, the rest could never be shown) and posts one drain
   unless one is already waiting; the drain applies the cap before inserting.
   The post is a constructor seam (`postToUi`), so `NotifyMonitorTests` raises
   10,000 notifications and counts the posts. (d) **It can publish**, through
   `pg_notify(@channel, @payload)` on a pooled connection (the listening one is
   parked in a wait, and `NOTIFY` takes literals rather than parameters). pgAdmin
   needs a second session to produce a test event; this is one button.
   The fourth is the **slow-query shortlist** (`Monitoring/StatementStatsService` +
   `SlowQueriesWindow`, ROADMAP Q2, 2026-09): pg_stat_statements for the current
   database. Four things are load-bearing. (a) **The column set is read from the
   catalog, not guessed from the server version**: it belongs to the *extension*
   version, which pg_upgrade leaves behind until `ALTER EXTENSION … UPDATE`
   (`total_exec_time` replaced `total_time` in 1.8; `toplevel` and
   `pg_stat_statements_info` came in 1.9, per-entry `stats_since` in 1.11), so
   `ReadAsync` asks `pg_attribute` which columns exist and finds the extension's
   schema through `pg_extension`. (b) **Three "unavailable" states are told
   apart**: not created in this database (`NotInstalled`), created but not in
   `shared_preload_libraries` (the view raises 55000, `NotLoaded`), and rows of
   other roles without `pg_read_all_stats` (text shown as `<insufficient
   privilege>` and a NULL queryid; counted as hidden, never listed). The window
   explains each, and the setup steps open as a script in a new tab; nothing
   ever creates the extension, changes a setting, or calls
   `pg_stat_statements_reset()` (that would reset everybody's numbers). The
   script's `ALTER SYSTEM SET shared_preload_libraries` line ships commented
   out (security audit 2026-09, finding 18): it replaces the whole list, so a
   tab run whole used to unload every other preloaded library at the next
   restart. `SlowQueriesTests` checks the line stays a comment. (c) **An
   interval is a subtraction until an entry starts over**, and the Core-pure,
   unit-tested `StatementStatsInterval.Between` (a sibling of `BlockingTree`)
   catches all three ways: the whole view reset (`stats_reset` moved), one entry
   reset or evicted and back (`stats_since` moved, or its calls went *down* where
   the server doesn't report it), or a new entry. In each the later counters are
   the interval's work, and the row is marked ↺. The baseline is the window's
   first read, moved only by "Restart interval", so it measures "what did my
   workload just do" without touching the server. A query preview is cut to
   `StatementStatsService.PreviewLength` server-side (up to 5,000 entries per
   refresh), and the whole text is fetched per statement when it is opened.
   (d) **Nothing runs from it**: a statement opens in a new tab under a comment
   saying where it came from, with its `$1` placeholders intact. (e) **The app's
   own reads are left out** (2026-09, 0.14.0 release pass: the top rows were
   pgNimbus's completion-catalog reads). Every statement the app sends on its
   own behalf, from `SchemaService`, `DdlService`, the permission and monitoring
   services, starts with `InternalSql.Marker` (`/* pgNimbus */`, via
   `InternalSql.Tag`); pg_stat_statements keeps that leading comment, and
   `ReadAsync` counts those rows as `OwnStatements` instead of listing them. What
   the user wrote or asked for (their queries, browse pages, EXPLAIN, imports,
   schema and security actions, cancel/terminate) stays unmarked. Typed-value
   prompting is #138's job. The live tests split by server kind: CI's plain
   `postgres:17` covers `NotInstalled`/`NotLoaded`, and a server started with
   `-c shared_preload_libraries=pg_stat_statements` covers the reads
   (`StatementStatsServiceTests`, `SlowQueriesTests`); each skips on the other.
4. **No passwords on `ConnectionProfile`.** Passwords come from
   `ICredentialStore` (DPAPI-encrypted files on Windows, macOS Keychain via
   SecItem APIs, Linux Secret Service via libsecret's non-variadic APIs), never
   persisted on the profile record itself. `CredentialStore.Create` shares a
   process-lifetime `RecoverableCredentialStore`: failed writes retain credentials
   only in session memory and expose a visible warning. **Memory holds nothing
   else** (security audit 2026-09, finding 18): a password the OS store accepted
   or returned is not cached (every load reads the store), a delete removes the
   entry, a delete the store refused is remembered by id only (so the password
   can't come back that session), and `ICredentialStore.Forget` drops a
   session-only password when the last main window connected with that profile
   closes (`App.ForgetSessionPasswordsOnClose`, keyed by
   `ConnectionDialogViewModel.ConnectedProfileId`). Legacy non-Windows `.cred`
   files move in **one pass at startup** (`MigrateLegacyFiles`, queued first on the
   connection dialog's credential chain, once per process): each is removed only
   after native write/read verification, or when the store already holds the same
   value. The ones that can't move (store unavailable, unreadable file, a
   different native value, which wins until a password edit resolves it) stay on
   disk and are reported once, as one line in the dialog's credential warning
   with their count. The first refusal from the store ends the pass (the rest
   are reported, not tried): a hung Secret Service call waits out its 15 s, and
   Connect waits on this pass. Before this a file moved only when its profile was opened,
   so profiles nobody reopened kept a base64 password forever. No new base64 files are written.
   Connection-dialog store operations run off the UI thread; Connect awaits initial
   credential loading. Linux calls are cancellable after 15 seconds and require
   libsecret plus a running Secret Service. macOS disallows interactive Keychain
   authorization prompts and reports denied/locked access as unavailable storage.
   Tests/previews use `MemoryCredentialStore`, never the user's real keychain.
   **A missing `credentials` directory is not a store failure** (2026-09-29, found
   on a real Mac): `DeleteLegacy` called `File.Delete`, which throws
   `DirectoryNotFoundException` (an `IOException`, so `IsStorageFailure`) when the
   directory does not exist, which is every new install. The first saved password
   stored fine in the Keychain and then showed "Password storage is unavailable",
   kept a copy in session memory, and a delete showed "an old unencrypted credential
   file could not be moved". It is guarded with `File.Exists`; the tests' fixture
   always created the directory, so the new ones use one that does not exist.
   **On macOS a Keychain item belongs to the code signature that made it, and an
   ad-hoc signature changes with every build** (same date). The store suppresses
   interactive prompts, so a build with another signature (a new release, or the
   `dotnet` host that ran a Debug build against the same profiles) gets
   `CredentialStoreException` on load, and a write does not fix it: measured with two
   binaries, create by A, load by B fails, update by B reports success, then neither
   can read it; only A's delete clears it. Users see an empty password field and the
   storage warning after every update. It ends with Developer ID signing (ROADMAP T5),
   and the first such build is one more identity change, so its release notes must say
   to delete the old `pgNimbus` Keychain items. Until then it is documented in
   `docs/getting-started/installation.md`. Nothing tests the real Keychain in CI: the
   store tests use a fake, which is why this and the missing directory went unseen.
   **SSH agent auth stores nothing at all** (2026-09). SSH.NET has no agent
   support, so `Connections/SshAgentClient` speaks the agent protocol itself
   (list identities, sign; Windows' `\\.\pipe\openssh-ssh-agent` unless
   `SSH_AUTH_SOCK` names another pipe, else the `SSH_AUTH_SOCK` socket), and
   `SshAgentKeySource` hands each key to SSH.NET's ordinary public-key auth as
   a `HostAlgorithm` whose `Sign` is an agent request — no new package, so
   Core's three stay three. RSA keys are offered as `rsa-sha2-512`/`-256` only
   (OpenSSH 8.8+ refuses SHA-1 `ssh-rsa`). This is what makes a
   passphrase-protected key usable without typing the passphrase, and switching
   a profile to agent auth deletes its stored SSH secret the way turning the
   tunnel off does. `SshAuthMethod` is persisted as a number: append, never
   reorder. `SshTunnel.Connect` throws `SshTunnelException` with a message
   written for the form (which step failed, what to check), connects with a
   15 s timeout rather than SSH.NET's 30 s (a jump host behind a VPN that is
   off never answers), and sends keep-alives every 30 s.
   **The jump host's key is verified** (2026-09, security audit finding 4).
   SSH.NET trusts every host key unless `HostKeyReceived` says otherwise, and
   nothing subscribed, so anyone on the path to the bastion could terminate the
   SSH session and relay it, owning the forwarded Postgres socket (and the SSH
   password with password auth). `SshTunnel.Connect` now takes an
   `SshHostKeyVerifier`, which reads the user's `~/.ssh/known_hosts` (never
   written) and then pgNimbus's own `<appdata>/pgNimbus/known_hosts` (appended
   in the line `ssh` writes, so `ssh-keygen -F/-R` work on it) through the
   Core-pure `Connections/KnownHosts` (OpenSSH format: comma lists, `*`/`?`/`!`
   patterns, `[host]:port`, `|1|salt|hash` HMAC-SHA1 hashed hosts, `@revoked`;
   `@cert-authority` is recognised and skipped, host certificates are not
   supported). The pure, unit-tested `SshHostKeyVerifier.Decide` applies
   OpenSSH's precedence across both files: revoked anywhere refuses, a match
   anywhere trusts, a mismatch refuses with a "host key changed" message
   naming host:port, both `SHA256:` fingerprints and the file and line to
   remove, and only a key neither file knows goes to the `ISshHostKeyPolicy`.
   Three details are load-bearing. (a) The type compared is the one the key
   blob names (`KnownHosts.KeyTypeOf`), not SSH.NET's `HostKeyName`, which is
   the negotiated signature algorithm (`rsa-sha2-512` for an `ssh-rsa` line), so
   every RSA host would otherwise read as unknown. (b) The verdict is stashed and
   thrown from `Connect`, not from inside the event, where SSH.NET would bury it
   under "Key exchange negotiation failed"; and a re-key later in the session
   accepts only the key trusted at connect, never prompting again. (c) The prompt
   is synchronous on SSH.NET's connect thread: the App's `HostKeyDialogPolicy`
   posts `HostKeyDialog` to the UI thread and blocks that pool thread (the
   connection dialog runs `Connect` under `Task.Run` and awaits it, so the UI
   thread keeps pumping), and it throws rather than hang if ever called on the
   UI thread. The view model's default policy (`RejectUnknownHostKeys`) refuses,
   since it has no window to ask from; the view swaps in the dialog. Accept is
   deliberately not `IsDefault`: the dialog opens a second after the Enter that
   started the connect. Three more from the review of these fixes: (d) a host
   the files already know is offered only the key types they know it by
   (`SshHostKeyVerifier.KnownKeyTypes` → `SshTunnel.RestrictHostKeyAlgorithms`,
   RSA as `rsa-sha2-512`/`-256`), as OpenSSH does, so a server presenting a key
   of another type is refused ("did not offer a host key of the type
   known_hosts knows it by") instead of falling through to a first-use prompt;
   (e) the prompt's reading time counts against the 15 s `ConnectTimeout`, so a
   connect that timed out after an accepted prompt is retried once, and trusts
   the now-remembered key without asking (`Prompts` tells the two apart);
   (f) accepted keys are also kept in memory, and with no app data root
   (`DefaultOwnKnownHostsPath` is `AppDataPaths.Resolve`, null) that is the only
   copy, for the session; the file is appended through `AppDataFile` (0600).
   Tests: `KnownHostsTests` (real `ssh-keygen` keys and
   `-H` hashes), `SshHostKeyVerifierTests`, `HostKeyDialogTests` (the pool-thread
   round trip, headless), and `SshTunnelHostKeyLiveTests` against a real sshd,
   gated on `PGNIMBUS_TEST_SSH` (`Host=…;Port=…;Username=…;Password=…`, optional
   `Target=host:port` for a query through the tunnel; locally a
   `linuxserver/openssh-server` container with `AllowTcpForwarding yes`).
   **TLS: new profiles start at Require, and Verify full is usable everywhere**
   (2026-09, security audit finding 9). Five defects, one fix each. (a) New
   profiles defaulted to `Prefer`, which Npgsql (like libpq) drops to plaintext
   whenever the server or anyone on the path declines TLS; the dialog's default
   is now `ConnectionDialogViewModel.DefaultSslMode = Require`, saved profiles
   keep their mode. A new form's mode follows its host until someone picks one
   (`SslModes.DefaultFor`): Prefer for this machine (`IsLoopback`: localhost,
   `*.localhost`, 127/8, `::1`, a socket directory), Require otherwise, because
   a local Docker Postgres has TLS off and Require failed the first connect
   anyone tried (review of these fixes). A loaded profile, a picker change or a
   pasted `sslmode` counts as picked. (b) `SslMode` is persisted as a number and its zero value is
   `Disable`, so a hand-edited `connections.json` without the field loaded as a
   plaintext-only profile: the record's `SslMode` parameter now defaults to
   `Require` (the source-generated reader honours a positional default; a test
   loads such a file). Never renumber the enum. (c) The picker showed six bare
   enum names and "Require" read as the safe one; the Core-pure `SslModes`
   table gives each a label and one line saying what it checks ("Require:
   encrypted, but the server's certificate is not checked"), marks Verify full
   recommended, and the combo's closed box shows the label alone
   (`SelectionBoxItemTemplate`) so it stays one line beside Username. Because Require
   now fails against a server with no TLS (a local Docker Postgres), a connect
   failure that says so gets `SslModes.ServerWithoutTlsHint` appended, naming
   Prefer/Disable. (d) Provider CAs (RDS, Cloud SQL, Supabase) are in no OS
   store, so Verify full could not pass against them and users fell back to
   Require: `ConnectionProfile.RootCertificatePath` (a path, safe in JSON) is
   the dialog's Root Certificate field, shown only for VerifyCa/VerifyFull,
   parsed from `sslrootcert=`/`PGSSLROOTCERT`/`Root Certificate=` (libpq
   `sslrootcert=system` clears it; a path on another machine, `\\host\…`,
   `//host/…` or a URL, is refused from a paste, since reading it on Windows opens
   an SMB session and its CA would vouch for its owner; `sslmode=require` with a
   root certificate reads as VerifyCa, as libpq has it), and written as Npgsql's `RootCertificate`
   only for those two modes (`UsesRootCertificate`; Npgsql ignores it under
   Require, and a string naming a CA would read as checked). (e) Through the SSH
   tunnel the socket is `127.0.0.1:<port>`, so Npgsql checked the certificate's
   name against 127.0.0.1 and Verify full always failed. Every connect now
   builds its pool with `ConnectionProfile.CreateDataSource`, which through a
   tunnel adds `UseSslClientAuthenticationOptionsCallback` setting
   `TargetHost = profile.Host` (the SNI and the name checked), everything else
   identical to `BuildConnectionString`; the Test button goes through
   `ConnectionTester.TestAsync(profile, …)` for the same reason.
   `TlsSettingsTests` proves it end to end without a TLS Postgres: a local
   listener answers the SSLRequest with a certificate for `db.example.test`
   issued by a throwaway CA, and the client sends its startup message (i.e.
   accepted the certificate) only with the callback. Two landmines found
   writing it: SChannel validates the server certificate *after* the handshake,
   so the server side "completing" proves nothing, and Npgsql retries a failed
   open, so the listener has to keep accepting or the retry waits out the
   connect timeout in the backlog. Not exercised: Verify full against a real
   server certificate (CI's `postgres:17` has TLS off).
5. **Crashes are logged and shown, never silent.** Critical/unhandled errors
   append to a plain-text log at `<appdata>/pgNimbus/logs/pgnimbus.log`
   (`PgNimbus.Core.Diagnostics.CrashLog` does the file I/O — directory-injectable
   and unit-tested — with the process-wide `CrashLogger` static as the facade;
   1 MiB rolling to `pgnimbus.log.old`, every write swallows its own failure so
   logging a crash can never itself throw). The log is appended through
   `Settings/AppDataFile` like every store, so on Linux and macOS it is created
   `0600` in a `0700` directory: it carries exception messages, which can quote
   a statement. With no resolvable app data root there is **no log**:
   `CrashLogger.LogFilePath` is null, `CrashWindow` says no log was written and
   leaves the path out of the GitHub issue. It used to fall back to
   `<temp>/pgNimbus/logs`, which on Linux is the shared `/tmp`, where another
   user can pre-create the directory and read or plant the file (2026-09
   security audit, finding 10). The App wires three global hooks in
   `src/PgNimbus.App/Diagnostics/CrashReporter.cs`: `AppDomain.UnhandledException`
   and `TaskScheduler.UnobservedTaskException` (log only — off the UI thread,
   the process is usually already terminating), plus
   `Dispatcher.UIThread.UnhandledException` (`AttachToDispatcher`, called from
   `App.OnFrameworkInitializationCompleted`) which is the real UI-thread net —
   it sets `e.Handled` and shows the crash window, then shuts the app down when
   it's dismissed. A startup/setup crash that escapes the message loop entirely
   is caught in `Program.Main`'s try/catch → `HandleFatal`, which shows the same
   `CrashWindow` by pumping a nested `DispatcherFrame` (the primary loop is gone
   by then). Landmines learned the hard way: a second `AppBuilder.Configure`
   throws "Setup was already called", so the crash window must reuse the
   already-initialized platform, never stand up a fresh Avalonia app; and
   `DispatcherTimer.Tick` exceptions are swallowed by Avalonia and never reach
   `Dispatcher.UnhandledException` (async-void handlers / posted continuations
   do), so don't rely on a timer to smoke-test the reporter. `CrashWindow`
   (`Views/CrashWindow.axaml`) is deliberately self-contained (no view model,
   touches no app services — it must render with the rest of the app broken):
   it shows the error, the on-disk log path, and a "Report on GitHub" button
   that opens a pre-filled new-issue URL (title/body/labels query params,
   including version + OS). **What leaves the machine is scrubbed** (security
   audit 2026-09, finding 18): `CrashLog.FormatEntry` passes the context and
   every exception message through `SecretRedactor.Redact` (a message can quote a
   failed `ALTER ROLE … PASSWORD '…'`), the issue title and body are redacted the
   same way, and the body names the log path through `CrashLog.HomeRelative`
   (`~/…`), since the home directory carries the OS account name.
6. **Query plans are parsed, analyzed, and heat-mapped — not dumped raw.**
   `ExplainService` runs `EXPLAIN (FORMAT JSON …)` and parses it into an
   `ExplainNode` tree; the ANALYZE path always asks for `BUFFERS` and
   `SETTINGS` (buffers are the most-requested EXPLAIN option and what the
   spill/lossy analysis reads — zero-valued buffer lines are dropped so the
   text view stays clean, and `ExplainTextFormatter` folds the per-pool block
   counters onto one `Buffers:` line — plus an `I/O Timings:` line — to match
   `EXPLAIN (FORMAT TEXT)`, while the individual counters stay in
   `ExplainNode.Details` for the re-color "Buffers" metric). `Monitoring`-style
   separation applies:
   `Query/PlanAnalyzer` is a **Core-pure, unit-tested** walker (a read-only
   sibling of `Json/JsonTree` and `Monitoring/BlockingTree`) that emits named
   `PlanWarning`s — bad row estimates, disk-spilled sorts/hashes, wasteful
   sequential scans, lossy bitmap heap blocks — each with an actionable
   one-liner and a conservative threshold constant. A row *over*-estimate is not
   reported for a node a `Limit` stopped early (2026-09): every browse page is
   `LIMIT 100`, so its ANALYZE showed a red "off by 10000×" on the scan every
   time. The exemption flows down through streaming nodes and stops at ones that
   read their whole input first (Sort, Hash, hashed/plain aggregates, Bitmap Heap
   Scan), at a Limit that ran its input dry, and never covers an under-estimate.
   The App wraps them in
   `PlanWarningViewModel` (glyph + severity brush) for the warnings strip, after
   `PlanAnalyzer.Condense` (2026-09, UI-thread audit): three of each kind and
   severity (`PlanWarning.Kind` groups "Row estimate off by N×"), then one
   "N more: …" line, in a strip capped at 220 px. A plan over a thousand
   partitions carried a warning per partition scan, all of them above the plan.
   The plan's text view is a `ReadOnlyTextView` and its tree is
   `TreeView.virtualizing`, for the same plans;
   `ExplainNodeViewModel` computes each node's exclusive **self time** so the
   tree's bar becomes a time-heat profile (falling back to cost when there's
   no ANALYZE timing) and tints the single slowest node as the bottleneck.
   `EXPLAIN ANALYZE` always runs inside a transaction `ExplainService` rolls
   back, so analyzing an INSERT/UPDATE/DELETE/MERGE (or a data-modifying CTE)
   never persists — `SqlStatementInspector.IsDataModifying` (Core-pure,
   unit-tested) drives the "rolled back" info note in the warnings strip.
   **Paste-a-plan** rides the same views with no DB round-trip:
   `ExplainService.Import(raw)` auto-detects JSON vs text and returns an
   `ImportedPlan` (parsed tree + display text). JSON parsing is tolerant of the
   shapes external tools emit (the `[{ "Plan": … }]` array, a lone
   `{ "Plan": … }` object, or a bare `{ "Node Type": … }` node); `FORMAT TEXT`
   is parsed best-effort by `Query/ExplainPlanTextParser` (another Core-pure,
   unit-tested sibling of `PlanAnalyzer`, which also strips psql framing).
   **Every way either parser can fail is one `FormatException`** (2026-09,
   security audit finding 16): the dialog and `TryParsePlanOutput` catch that
   type alone, and `EXPLAIN (FORMAT JSON, COSTS OFF)` output (no cost fields at
   all → `KeyNotFoundException`), `[{"Plan": 5}]` (`InvalidOperationException`)
   and a text `rows=` past 9.2e18 (`OverflowException`) each reached the crash
   window instead. Now every figure is read by kind with a default (a COSTS OFF
   plan is a tree of zeros), counts saturate (`ExplainService.ToLong`), and
   `Parse`/`Import` translate whatever else escapes. The text parser is bounded
   too: its numbers are `\d+(?:\.\d+)?` under `RegexOptions.NonBacktracking`
   with a match timeout (the old `[\d.]+\.\.[\d.]+` backtracked O(n²) on
   `(cost=` + a run of dots), nesting stops at `ExplainPlanTextParser.MaxDepth`
   (128; JSON reads to `ExplainService.MaxJsonDepth`, 256, about the same number
   of plan levels, since each level is an object and a `"Plans"` array:
   `JsonDocument`'s default of 64 had stopped a plan at ~31 levels, a join of
   that many tables) and input
   at `MaxInputLength` (4 MiB), because the formatter, the analyzer and the view
   models all walk the tree recursively. `ParserRobustnessTests` feeds both
   parsers the hostile inputs. The
   command palette's "Import query plan…" opens `ImportPlanDialog` and, on a
   successful parse, shows the plan in a **new tab**
   (`MainViewModel.OpenImportedPlan` → `QueryViewModel.ShowImportedPlan`) — same
   warnings strip and time-heat as a live plan. **Sharing back out**: the plan
   header's "Export ▾" flyout copies/saves the plan as JSON or rendered text —
   `ExplainService.ExplainAsync` returns an `ExplainRun` that keeps the raw
   server JSON, carried on `QueryViewModel.PlanJson` (null, and the JSON actions
   hidden, for a text import). **Text or tree, and the tree open** (2026-09, 0.14.0 release pass): the plan
   opens as text by default (#108's deliberate classic reading), but the last
   Text/Tree choice is remembered (`AppSettings.PlanTreeView`, carried by
   `MainViewModel.PlanTreeView` into every new tab), and the tree opens fully
   expanded (a `TreeViewItem` style in `ResultsGridPanel`, as the Blocking tree
   does): it used to open as one collapsed root, which hid the heat bars.
   **Re-color by metric**: the plan header (tree view)
   has a "Color:" segmented toggle — Time / Rows / Cost / Buffers — that rescales
   the heat bars. `ExplainNodeViewModel` is observable and holds each node's
   exclusive self-time, self-cost, output rows, and self-buffers (buffer counts
   read from `ExplainNode.Details`, cumulative like time, so exclusive = node
   minus children); `ApplyMetric` rescales bars and re-marks the hottest node in
   place. **Every route to a plan lands in the same views** (2026-07), through one
   `QueryViewModel.ShowPlan`: the Explain commands, an import, *and* a hand-written
   `EXPLAIN` the user simply Runs. That last one used to dump `QUERY PLAN` text into
   the grid; now `TryParsePlanOutput` feeds the output back through
   `ExplainService.Import` (so both `FORMAT TEXT` and `FORMAT JSON` work — JSON also
   keeps the plan's JSON export and the Buffers metric, which the text form can't
   carry), and shows the plan with the rows still behind it, one ✕ away. It works
   per script section too — `ScriptResultViewModel` carries the parsed
   `ImportedPlan`, so selecting the EXPLAIN section of a `SET …; EXPLAIN …` script
   shows its plan while the other sections show their grid. Unlike the Explain
   command, a Run *executes* the statement, so an `ANALYZE` of a write is not
   rolled back — that gets its own warning-strip note, mirroring the Explain path's
   "rolled back" one. **What the Explain commands explain**: the selection, else the
   statement the caret sits in (`ExplainTarget`, mirroring Run's targeting via
   `SqlScriptSplitter.StatementAt` + the view-pushed `QueryViewModel.CaretOffset`),
   with any existing `EXPLAIN` prefix removed by `SqlStatementInspector.StripExplain`.
   Both matter because `EXPLAIN` takes exactly one un-nested statement: handing it a
   whole script failed at the second one ("syntax error at or near SET").
   **A selection of several statements is refused, not planned** (2026-09
   security audit, finding 3). `EXPLAIN` plans only the first statement of the
   text it is given, and Npgsql runs every statement in a command, so an explain
   of a selection `SELECT 1; CREATE TABLE …` planned the SELECT and created the
   table with no error (reproduced live), and a selection ending in `…; COMMIT;`
   committed the write the ANALYZE path had promised to roll back.
   `ExplainService.SingleStatement` splits with `SqlScriptSplitter` and throws
   for anything but one statement; `ExplainTarget` calls it first so the refusal
   lands on the status line, and `ExplainAsync` calls it again so the promise
   holds for every caller. Plain EXPLAIN now also runs inside the same
   always-rolled-back transaction as ANALYZE; it costs nothing and leaves no
   path on which the planner could persist anything. `ExplainServiceTests`
   holds the audit's live check (two statements are refused and the table is
   not created). The design
   doc + competitive research is in
   [`docs/dev/design/explain-improvements.md`](../docs/dev/design/explain-improvements.md).
7. **Permissions are answered, not dumped — and never applied behind the user's
   back.** `src/PgNimbus.Core/Security/` reads roles and ACLs, but the headline is
   that it answers *"can this role do this, and why?"* rather than rendering
   what the catalog stores. pgAdmin, DBeaver, DataGrip and TablePlus all render
   the stored ACL, which omits everything a role reaches through group
   membership, ownership or PUBLIC — so a permission that works looks missing.
   `PrivilegeService.GetServerAnswersAsync` asks the server itself through
   `has_*_privilege()` (which expands inheritance, ownership and superuser
   server-side, so it is ground truth), and the Core-pure, unit-tested
   `EffectivePrivilegeResolver` — a sibling of `PlanAnalyzer`/`BlockingTree` —
   attributes each answer to a `PrivilegeSource` (direct / inherited-via-X /
   PUBLIC / owner / superuser) and reconciles against the server, which always
   wins: a yes the catalog cannot explain is reported `Unknown`, never dressed
   up as a direct grant. `RoleGraph` is the other pure half; its load-bearing
   rule is that a `NOINHERIT` membership shows in the tree but never counts as
   inherited, because claiming a privilege the server will refuse is worse than
   showing none.
   **Three landmines, all found the hard way.** `ObjectAcl.IsDefaultAcl` models
   a NULL catalog ACL as its own state — in Postgres that means "untouched: the
   owner has everything and the built-in defaults apply", not "no privileges",
   and rendering it as an empty grid is how a permissions UI teaches the wrong
   thing. `aclexplode` is *strict*, so a NULL ACL must be passed to it as-is;
   "defending" that with `COALESCE(acl, ARRAY[]::aclitem[])` fails every
   untouched object with `ACL arrays must be one-dimensional`, because an empty
   array literal is zero-dimensional. And `Privileges.For` must be given the
   real server version — asking a pre-PG17 server about `MAINTAIN` raises
   `unrecognized privilege type` and takes the whole matrix down.
   **Nothing writes from the window.** Every privilege change leaves as a script
   in a new editor tab (`MainViewModel.OpenGeneratedSql`), following the
   `DdlTemplates` precedent. The single exception is a statement carrying a
   `PASSWORD` literal: Postgres has no parameter form for one, so it would land
   on screen and in the on-disk query history — those run through
   `SecurityEditor` instead, are never shown, and `Security/SecretRedactor`
   covers the case where a user types one by hand. **It runs in the two stores
   that write SQL nobody asked to save** (2026-09, security audit finding 8):
   `QueryHistoryStore` redacts every entry it writes and scrubs the file once on
   load (an entry from before the redactor, or in a shape it learned later, is
   rewritten in place). The writes after a run, a pin and a Clear go through
   `AppendInBackground`/`SaveInBackground` (2026-09, UI-thread audit): one
   ordered queue on the thread pool, a lock shared by every store over the file
   (two windows each hold one), and `SavedQueriesViewModel.PendingHistoryWrite`
   for a test to await. Done on the UI thread, every Run re-read the file and
   redacted all 200 entries twice, and entries keep whole scripts. **An entry
   is redacted once, not on every read** (2026-09, benchmark pass):
   `QueryHistoryEntry.Redacted` is a stamp, `SecretRedactor.Version` plus a
   SHA-256 of the text and result line, and `Redact` returns a stamped entry as
   it is. The hash is what makes it safe: a `with` that changes the text, or a
   hand-edited file, no longer matches and is redacted again, and raising
   `SecretRedactor.Version` (do it whenever the redactor learns a shape it
   missed) re-scrubs every history on its next load. Before this, opening a
   window with a history holding five 1 MB scripts redacted all of it on the UI
   thread, 130–190 ms (`history_load_ms`); now it reads it, ~45 ms. An older
   pgNimbus ignores the field. That covers the result line too (`QueryHistoryStore.Redact`):
   it goes through the redactor, and a statement whose text held a secret keeps
   `WithheldSummary` instead, since a server error quotes the token it failed on
   (`syntax error at or near "…"`) with no keyword beside it for the redactor to
   find; the check is the marker in the redacted text, so entries redacted before
   this rule lose their result line on the next load. And `WorkspaceStore.Save` redacts every tab's text on its
   way into `workspace.json`, the other connections' snapshots included, except a
   file-backed tab, which keeps no text at all (`WorkspaceTab.TextFromFile`) and
   is read from its file on restore: redacted, it reopened modified and one
   Ctrl+S wrote the placeholder over the real file (review of these fixes). It used
   to guard only `SavedQueriesViewModel.RecordExecution`, and the workspace
   snapshot, written on every close and connection switch, kept a typed
   `ALTER ROLE x PASSWORD 'p'` as typed. A saved query and a `.sql` file are the
   user's explicit saves and are written as is. The redactor reads the statement
   with the shared `SqlLexer` and then reads *inside* every string, dollar body
   and comment, because that is where the audit found the leaks: `DO $$ …
   PASSWORD 's' … $$`, `EXECUTE 'ALTER ROLE … PASSWORD ''s'''` (a string's
   escapes are decoded to read it and the replacement encoded back), conninfo
   `password=s` in `CREATE SUBSCRIPTION`/`dblink_connect` and `user:s@` in a URI,
   and a commented-out statement. Inside those it also scans loosely (PASSWORD
   then any literal, whatever came before, so an apostrophe in `-- don't …`
   can't hide it), and a string ending in a hanging PASSWORD (`'… PASSWORD '`, a
   `format()` `%L`) redacts every later literal in the statement. Bias: redact
   too much. The marker is `'<redacted>'::redacted`, not a bare literal: a
   password slot takes only a string constant, so a restored or history-opened
   statement run again is a syntax error instead of setting the password to the
   text `<redacted>` (a live test asks the server). It must stay idempotent (a
   literal already followed by the cast is left alone), or the
   history's load-time scrub would rewrite the file on every launch.
   **History can be turned off**: `AppSettings.RecordQueryHistory` (default on,
   Settings' History section) gates `RecordExecution`, and the sidebar's history
   list says history is off (`HistoryOffHint`) rather than silently not growing.
   Tests: `SecretRedactorTests`, `QueryHistoryStoreTests`, `WorkspaceStoreTests`,
   `QueryHistoryPreferenceTests`.
   **The literal is a verifier, not the password** (2026-09, security audit
   finding 7). The client side had been right and the server side wrong: the
   cleartext went down the wire inside statement text, which lands in the
   server log on any failure (`log_min_error_statement` writes `STATEMENT: …`,
   and "permission denied to create role" is the ordinary failure on managed
   Postgres), in every `log_statement = ddl` or pgaudit line, in
   `pg_stat_activity` while it runs and in `pg_stat_statements` before PG 16.
   `RoleScriptBuilder` now renders the executed `PASSWORD` as the SCRAM-SHA-256
   secret `Security/ScramSha256Verifier` (Core-pure, `System.Security.Cryptography`
   only, pinned to vectors computed with Python's hashlib) builds on this
   machine, the way psql's `\password` does through `PQencryptPasswordConn`:
   SASLprep as libpq applies it (an all-ASCII password as typed, a prohibited
   one hashed raw rather than refused, mapping and NFKC otherwise), a random
   16-byte salt, PBKDF2-HMAC-SHA-256 × 4096, then StoredKey and ServerKey. The
   server stores a SCRAM secret in a `PASSWORD` literal as-is whatever
   `password_encryption` says, and an `md5` pg_hba line authenticates one by
   negotiating SCRAM, so the cleartext never leaves the machine and nothing
   about the server changes. Two things to know. The App runs with
   `InvariantGlobalization`, under which `string.Normalize` is the identity, so
   NFKC happens only in the tests (`NormalizationAvailable` says which); a
   non-ASCII password holding compatibility characters is hashed as typed
   there, which is what Npgsql's own SCRAM client, normalising through the
   same call, already sends at login from this app, but libpq and pgJDBC
   clients normalise and would be refused: the role editor says so for any
   non-ASCII password when normalisation is unavailable
   (`RoleEditorViewModel.NonAsciiPasswordWarning`). A server before PG10 has no
   SCRAM and would store the verifier as the password itself, so there the
   editor refuses to set a password (`PgFeatures.SupportsScramVerifier`) and
   points at psql's `\password`. And `SqlLiteral.Quote` on
   the verifier is safe whatever `standard_conforming_strings` says (finding
   13): base64, digits, `$` and `:` hold neither a quote nor a backslash.
   `ScramPasswordServerTests` creates a role through the real path and logs in
   as it with the cleartext, refusing the wrong one with 28P01.
   `GrantScriptBuilder.BuildBulk` is deliberately more correct than pgAdmin's
   Grant Wizard: `GRANT USAGE ON SCHEMA` comes first (theirs skips it and the
   user still gets `permission denied`), revoke is a preset rather than an
   impossibility, and the matching `ALTER DEFAULT PRIVILEGES` is offered with
   the creating role named, since one set for the wrong creator silently does
   nothing. `RoleScriptBuilder.Drop` emits the whole `REASSIGN OWNED` →
   `DROP OWNED` → `DROP ROLE` recipe with its "current database only" caveat —
   the answer to 2BP01, which Postgres reports without naming either the
   blocking objects or the fix. A function securable carries
   `pg_get_function_identity_arguments` and is granted `ON ROUTINE` (PG11+;
   security audit 2026-09, finding 18): `pg_get_function_arguments` includes
   `DEFAULT …`, which made the generated GRANT a syntax error, and a procedure
   fails under `ON FUNCTION`. `RoutineGrantTests` runs the script against a
   function with a default and a procedure. The research and the plan are in
   [`docs/dev/design/accounts-permissions.md`](../docs/dev/design/accounts-permissions.md).
   **Two rules every script builder keeps** (2026-09, security audit findings
   11 and 12; the RLS re-create and the default-privileges statement moved out
   of their view models into the Core-pure `PolicyScriptBuilder` and
   `DefaultPrivilegeScriptBuilder` so the rules are tested where the others
   are). (a) **A value placed in a `--` comment goes through
   `SqlComment.Safe`**, which strips `\r`/`\n` — the only characters that end a
   comment. A schema or relation name may contain a newline (role names are
   refused by current servers, table names are not), and every generated
   script opens with a comment naming what it is about: a table named
   `"x⏎ALTER ROLE eve SUPERUSER;--"` with an inert policy put a live `ALTER
   ROLE` on the second line of the re-create script, which autocommit ran
   before the `CREATE POLICY` failed. Quoting protects nothing inside a
   comment. `RoleScriptBuilder.Drop` had a private copy of this guard; the
   `GrantScriptBuilder` hint, `DdlService`'s not-found lines, the RLS and
   default-privileges comments did not. The tests judge a script with
   `SqlScriptSplitter` — an escaped comment adds a statement — not by eye.
   (b) **PUBLIC is `null`, end to end, and nothing else is.** Only the
   lowercase `public` is reserved, so `CREATE ROLE "PUBLIC"` is legal, and
   `GrantScriptBuilder` used to match the grantee's *name* case-insensitively:
   granting to that role granted to everyone, revoking from it left its access
   in place. Now `aclexplode` grantee 0 and `polroles` oid 0 come back as
   `null` (`AclEntry.Grantee`, `RlsPolicyInfo.Roles`), `GrantScriptBuilder.
   GranteeSql` writes the keyword for `null` and `SqlIdentifier.QuoteIfNeeded`
   for every name — so the role is `"PUBLIC"` — and `GranteeLabel` shows that
   role quoted so the two are told apart on screen. `PublicRoleTests` creates
   the role for real and revokes from it through the generated script.

## UI design rules

> Several of these are shared with kubeNimbus, and their canonical statement is in
> [`shared/nimbusUi/DESIGN.md`](../shared/nimbusUi/DESIGN.md): minimalism (1),
> double-click as the default action (2), never overwriting the active tab (3),
> drag-reorderable tabs (4), the Ctrl/Cmd resolver (5), the merged title bar
> (DESIGN.md rule 9, adopted on Windows in the same change that created that
> file), Title Case menus that open with the default action (DESIGN.md rule 18)
> and the macOS Edit/Window menus with focus routing (DESIGN.md rule 19, see
> "macOS native menu bar" in `.claude/rules/window-chrome.md`). What is kept below is the pgNimbus-specific evidence behind each — the
> concrete failure is why the rule is believed. Change a shared rule in DESIGN.md,
> not here.

1. **Minimalist design is a priority.** Every new always-visible control —
   especially a toolbar button — must be explicitly discussed and justified
   before it's added; the default answer is no. Secondary/rare actions belong
   in the command palette (Ctrl+K) or a context menu, not on the toolbar
   (that's why the auto-alias "AS" toggle moved from the toolbar to the
   palette, 2026-07, and why Delete left the connection dialog's button row for
   the saved-connections right-click menu — Connect / Duplicate / Delete —
   alongside the accent-colour row collapsing into one swatch button + flyout
   next to the Name field). **A context menu is not a dumping ground either**
   (2026-08): pgAdmin answers a right-click on a schema with 15 items plus an
   18-item Create submenu; pgNimbus's schema menu is six — New Table…, Copy
   Name, Refresh, Exclude from Autocomplete, Drop Schema…, Drop Schema
   (Cascade)… — and each earns its place the same way a toolbar button would.
   A relation's menu is four (2026-09): **Browse Rows** first, because it is what
   a double-click does and the menu used to offer only Source (DDL) and Alter
   Table…, so the commonest reason to right-click a table had no row; then Copy
   Name, Source (DDL), and Alter Table… for tables and partitioned parents only
   (`TableNode.CanAlter` — the dialog's ADD/DROP COLUMN only fails on a view).
   Every menu label is Title Case on every platform (DESIGN.md rule 18): the
   native menu bar already was, and the context menus under it said "Copy name"
   and "Drop schema...". `MenuTests` pins the schema and relation menus.
   "New Table…" is deliberately a `CREATE TABLE` template opened in a new tab
   (`Schema/DdlTemplates`, Core-pure and unit-tested), not a dialog: a form that
   can only express the column types a combo box lists is a worse tool than the
   statement itself, sitting in the editor where it can be edited and run. The
   two Drops go through `SchemaEditor.DropSchemaAsync` behind the shared
   `ConfirmDialog`; the plain one is Postgres's own RESTRICT (it fails on a
   non-empty schema, and that refusal lands in the sidebar's error strip), and
   CASCADE is a separate item with a confirm that says what it takes with it.
   **Security audit 2026-09, finding 6, closed two gaps this same pattern had
   missed** (2026-09-29): the Alter Table dialog's "Drop selected column" ran
   straight from `DropColumnCommand` to `SchemaEditor.DropColumnAsync` with
   nothing in between — one click after selecting a row destroyed the column's
   data — and the Extensions group's "Install" confirmed nothing while "Drop…"
   right beside it already did. Both now confirm the same way: `AlterTableViewModel.ConfirmDropColumnRequested`
   is a `Func<ColumnDetail, Task<bool>>` that `AlterTableDialog` wires in its
   `Opened` handler to a `ConfirmDialog` naming `schema.table.column`
   (`AlterTableConfirmTests`), and `SchemaTreePanel.OnInstallExtensionClick`
   confirms in code-behind exactly like `OnDropExtensionClick`, naming the
   extension and the database (`SchemaTreeViewModel.DatabaseName`, wired from
   `MainViewModel.ConnectionDatabase`; `ExtensionInstallConfirmTests`). The same
   finding's third gap was a non-safe-mode multi-row delete
   (`QueryViewModel.DeleteRowsAsync`) running one autocommit DELETE per row: a
   mid-batch failure (a blocking trigger, a lost connection) left whatever had
   already committed deleted and the rest untouched, and the status line even
   said so ("Delete failed after N row(s)") instead of preventing it. It now
   builds one `ParameterizedStatement` per row (`ExpectedRowsAffected: 1`, the
   same shape safe mode's staged batch already used) and hands the list to
   `QueryEngine.ApplyBatchAsync`, which runs them inside one transaction, so a
   delete is all-or-nothing: "Deleted N rows" or "Delete failed, nothing
   deleted: …" (`QueryViewModelDeleteRowsTests`, gated on `PGNIMBUS_TEST_CONN`,
   a trigger blocking the second of three rows).
   **Exclude from autocomplete** is the answer to "this database has 40 schemas
   and 30 belong to other teams": the schema stays in the tree (dimmed, eye-off
   marked, so the exclusion is visible where it was made and one right-click
   from undone) but contributes nothing to completion. The set is per
   connection, keyed `host/database` like the workspace snapshot, persisted in
   `AppSettings.AutocompleteExcludedSchemas` and read/rewritten only through the
   Core-pure `Settings/AutocompleteExclusions`. It's applied in
   `SqlCompletionProvider.RefreshAsync` rather than per keystroke, so an
   excluded schema's tables/columns/functions are never *fetched* — on a big
   catalog, excluding schemas makes the refresh itself faster. Nothing else is
   filtered by it: the tree and the command palette still show everything, which
   is why the toggle rebuilds only the completion cache instead of running the
   full `RefreshSchemaAsync` (which would collapse the tree).
   **The sidebar has two tabs, and the third one leaving is the rule's own
   evidence** (2026-08). Notify was a permanent third of the sidebar's
   navigation — equal billing with the schema tree — for the feature nobody
   opened, while Server Activity and Database Overview, used far more often,
   have no permanent UI at all and are reached from the palette. The rarest
   surface had the most prominent home, which is what rule 1 exists to catch;
   it is now `NotifyMonitorWindow`, opened from the palette like its two
   siblings. The icon-only collapse threshold on the remaining tab labels came
   down with it (300 → 210), since two labels fit where three did not.
   **The sidebar's filter box searches the database, not the screen** (2026-08).
   It used to walk only the nodes the tree had already loaded, and the tree
   loads a schema's tables lazily on first expand — so a table in a schema
   nobody had opened was reported as absent, while Ctrl+K found it instantly
   (the palette searches `GetAllRelationsAsync`). `SchemaTreeViewModel` now
   matches that same whole-catalog list: a filter pass runs synchronously over
   the loaded tree first (typing stays responsive), then again once the
   snapshot is in hand, and a schema whose *unloaded* tables match is revealed
   and expanded, which triggers its lazy load. The snapshot is host-supplied
   (`AllRelationsRequested`, wired to `MainViewModel.GetRelationsAsync`) so the
   sidebar and the palette share one cache and one invalidation on
   `RefreshSchemaAsync`. Two things make it hold together: the children that
   arrive from that triggered load default to visible, so the VM watches each
   schema's `Children` collection and re-vets them against the live filter
   (otherwise the schema flashes open with every table it owns); and a schema
   that *has tables on the tree* is judged by them rather than by the snapshot,
   which is the fresher of the two. That test is deliberately "has `TableNode`
   children", not `IsLoaded`: expanding sets `IsLoaded` at once, so keying on it
   dropped the snapshot a moment before the rows it was standing in for arrived,
   and the schema vanished mid-load.

   **What the filter does is undone when the filter goes away** (2026-09). The
   reveal above expands a schema, and nothing used to close it again: a
   one-character query matches a table in nearly every schema, so the box left
   the whole tree open, and clearing it put the rows back but never the
   expansion — which read as the sidebar having "remembered" a state nobody
   asked for. `_autoExpanded` records the schemas the *filter* opened, and they
   are closed when they stop matching or when the box is cleared; a schema the
   *user* opened is never touched. The two are told apart by `SetExpanded`,
   which assigns `IsExpanded` with tracking suppressed — everything else that
   sets it is the user, and is recorded by path in `_userExpanded`. That set is
   also what survives a refresh: the tree is rebuilt from scratch there, so the
   nodes coming back reopen themselves from it (`OnSchemasChanged`), tables
   included as their schema's load returns.

   **Root groups are filtered by their own name, and no matches is a state of
   its own** (2026-09). `Roles`/`Extensions` aren't schemas and their children
   aren't tables, so they used to be exempt from the filter entirely — which
   left `Roles` alone on screen for a query that found nothing, reading as a
   hit. They now match on the group name. And an empty panel on its own reads as
   a broken sidebar, so `ShowNoMatches` drives an explicit cue under the box. It
   is held back while the catalog snapshot is still in flight (`_awaitingCatalog`):
   until it lands, the schemas nobody expanded haven't been consulted, and
   "nothing matched" isn't a fact yet — without that the cue flashed on the
   first keystroke against a remote server. A *failed* fetch does release it: the
   loaded tree's verdict is then final.

   **Every level of the tree virtualizes, so the filter works on lists, not on
   visibility** (2026-09, UI-thread audit). Avalonia's `TreeView` realizes every
   row it holds: expanding a schema of 5,000 tables built 5,011 rows and froze
   the window for 8 s. The tree is `TreeView.virtualizing` (`Styles/Theme.axaml`:
   a `VirtualizingStackPanel` at the root and in every item, which sizes itself
   from the effective viewport, so a nested level virtualizes inside its parent
   row). Two consequences are load-bearing. (a) A virtualizing panel realizes a
   hidden row to learn it takes no space, so the old `IsVisible` binding on
   `IsFilteredIn` made a filter matching one table realize all 5,000. The tree
   binds to `SchemaTreeViewModel.ShownSchemas` and each node's `ShownChildren`,
   the filtered copies of `Schemas`/`Children`; `SyncShownChildren` rebuilds
   one with a single Reset, only when it differs, and a filter pass calls it
   after deciding a node. `Children` stays the source of truth (the tests, the
   harness and the filter read it). (b) `TreeView` moves focus only to a row
   that exists, so with nothing realized past the viewport the arrow keys
   stopped at its bottom edge; `CacheLength="1"` keeps a viewport realized on
   each side. Loading a node's children is one `ReplaceAll`: an Add per child
   re-vetted the whole schema against the filter each time, which was
   quadratic, and the catalog match is one pass over the snapshot per filter
   pass (`SchemasWithMatches`), not schemas × relations.
   `UiThreadBudgetTests` holds the counts.

   **Expand all / collapse all live in the tree-options menu, not on the bar.**
   Four chips beside the filter box left it too narrow to read what was typed in
   it, so the advanced-objects toggle joined them under one ☰-style button
   (`Tree options`): a checkbox for advanced objects, then Expand All Schemas /
   Collapse All. Expand is one level deep on purpose — a schema fetches its
   tables on first open, so it already costs a catalog query per schema, and
   walking into each table's own sub-groups would multiply that by every table
   in the database.

   **The compact pass** (2026-09-28). The sidebar spent its first 60px on two
   44px left-nav pills saying which of two lists was showing, indented the tree
   a further 12px past its own filter box, and on the Queries tab gave an empty
   saved list half the height, printed each history entry's formatting as a
   staircase and its timestamp as `07/30/2026 09:41:00 +00:00`, and ended in two
   stock grey buttons. Now: (a) the tabs are one full-width capsule the filter
   box's height, `TabControl.capsule` (shared/nimbusUi's `Controls.axaml`) — not
   `segmented`, which hugs its segments and fades its selection in on a
   transition that a main-window baseline must never catch half-way (the
   security window has no baselines for exactly that). Since 2026-10 its
   selected segment is a raised thumb on a tinted track, macOS's segmented
   control (`AppSegmentThumbBrush` on `AppSegmentTrackBrush`), not the accent
   wash with accent SemiBold text, which read as a selected list row; and both
   sidebar filter boxes are `TextBox.sidebarFilter`, 26px instead of Fluent's
   32. **Landmine:** Fluent's TabControl theme gives any part named
   `PART_ItemsPresenter` a bottom margin (`TabControlTopPlacementItemMargin`)
   from an activated selector, and the capsule template keeps that name, so the
   selected segment sat 2px from the strip's top and 4px from its bottom (seen
   on a Mac); the capsule zeroes it, and `CapsuleTabsTests` measures the gaps.
   A 0.5px spread `BoxShadow` as a hairline ring left grey specks in the thumb's
   rounded corners; the ring is a 1px blur. (b) The tree's chevron
   column is 2+12+4px instead of Fluent's 12+12+12, and its root lines up with
   the filter box. **Landmine:** Fluent sets that margin in the template, at
   Template priority, which beats any plain style — the setter only lands from a
   selector with an activator (`TreeViewItem:not(:disabled) /template/ …`, which
   has StyleTrigger priority); a resource override of
   `TreeViewItemExpandCollapseChevronMargin` does nothing either. The header
   presenter is stretched the same way. (c) Metrics are a right-hand column: a
   table's size and a column's type (with its family glyph) dock right, the name
   trims with an ellipsis, and the tree no longer scrolls sideways
   (`HorizontalScrollBarVisibility="Disabled"`) — which also lets an error row
   wrap. Leaf rows with a trailing detail (functions, indexes, sequences, types,
   triggers, roles, extensions) are `DockPanel`s whose detail trims. (d) The
   Queries tab lost its cards (the tree beside it never had one); the saved
   list sizes to its rows up to 240px; a history row is the statement folded
   onto one line and a `09:41 · 18 ms · 50 rows` line under it (the Core-pure,
   unit-tested `Query/HistoryLabel`: invariant culture, "Yesterday"/weekday/
   `Jul 18`/ISO by age, the connection named only when it isn't this window's,
   the full timestamp on hover; `HistoryText.Now` is the clock seam the harness
   pins); the pin shows on a pinned row and on hover/selection only, in the
   row's own foreground so it survives the accent-filled selected row; and the
   Load selected / Clear history buttons became the list's right-click menu
   (Open in New Tab, Copy SQL, Pin/Unpin, Clear History). Tests:
   `SidebarTests`; scenario `main-window-queries`.

   **Completion ranks by what can legally be typed at the caret**, and the
   statement-start caret is its own context (2026-08):
   `SqlCompletionContext.IsAtStatementStart` (Core-pure, unit-tested) is true
   when nothing but whitespace, comments and the previous statement's `;`
   precedes the word being typed, and the list there is the commands alone
   (`SqlKeywordGrammar.StatementStarts`, since the second audit's package L),
   each ranked by its position in that list. Without it, typing `se` in an empty
   editor pre-selected a `search` column three schemas away: `CompletionRanker`
   breaks a fuzzy-score tie by priority, and keywords used to carry the
   *lowest* band of all (0, under catalog columns at 5), so SELECT lost to any
   same-prefixed column and — being longer — even to SET.
2. **Double-click triggers the default action.** Anywhere a list/tree item
   has an obvious primary action, double-clicking it must perform that
   action: schema-tree table → browse, function → source, saved query /
   history entry → open in a new tab, connection profile → connect, result
   cell → inline edit when the result set is editable, inspector when it's
   read-only (Space quick-peeks the current cell in the inspector in both
   modes, 2026-07). Apply the same rule to any new list-like UI. The default
   action is *all* a double-click does: a `TreeViewItem` toggles its own
   expansion on a double tap before the tree's handler sees it, so
   `SchemaTreePanel.OnSchemaTreeDoubleTapped` puts the expansion back after
   browsing a table or opening a function's source (it used to leave the
   table's columns open in the sidebar every time).
   The connection dialog goes further, because reconnecting to the same database
   is the most repeated action in the app (2026-07): the profile from last
   session is preselected on open (`AppSettings.LastConnectionProfileId`, written
   by `ConnectionDialogViewModel` on a successful connect), the list takes focus,
   and Connect is the window's `IsDefault` button — so launch + Enter reconnects,
   no click anywhere. `AppSettings.AutoConnectLastProfile` (preferences page, off
   by default) skips even that; the dialog still opens for the moment the connect
   is in flight, so a failure lands back in it with the error instead of leaving
   a blank screen. The dialog is resizable and remembers its own placement in
   `connection-window.json` (`WindowPlacementStore.ForConnectionDialog`) —
   deliberately a separate file from the main window's `window.json`.
   **The form's defaults are placeholders, not pre-filled text** (2026-08):
   host/port/database/username start empty and show `localhost` / `5432` /
   `postgres` / `postgres` dim, so typing needs no select-all first; a field
   left blank still connects to the default it names, because
   `ConnectionDialogViewModel.Effective{Host,Port,Database,Username,Name}` —
   not the raw fields — is what the built profile, the test/connect path and
   the connection-string preview all read. The Name placeholder is derived
   (`host/database`, tracking those fields as they're typed) and is what an
   unnamed profile saves as, which is why nothing writes `Name` on import
   anymore. An untouched form also leaves the paste-a-connection-string box
   empty rather than mirroring the defaults into it. SSL Mode is the one real
   value a blank form starts with: Require, not Prefer (hard rule 4's TLS
   paragraph), so the preview of a new profile carries `?sslmode=require`; the
   preview omits only Prefer, libpq's own default, and adds `sslrootcert=` when
   a verifying mode has a root certificate.
   **The form saves itself; there is no Save button** (2026-09). Save was a
   separate button and Connect wrote nothing, so the two things users did — edit
   a port and connect, or type a new connection and connect — were each used once
   and silently gone at the next launch. Now every edit a person makes is written
   at once (`ConnectionDialogViewModel.OnFormEdited` → `SaveProfile`: the record
   is swapped into `Profiles` and `connections.json` rewritten), and the first
   edit into a blank form *creates* the profile, which the list then selects.
   Passwords wait `CredentialSaveDelay` (400 ms) before reaching the credential
   store, since a keychain/Secret Service write per keystroke is churn; they are
   flushed with their values captured on a profile switch, New, Duplicate and
   Connect, and the dialog's `Closing` is held until `FlushAsync` finishes,
   because closing the last window ends the process under a queued write. Every
   store call (read, write, delete) goes through one chain (`EnqueueCredentialWork`)
   so a read never overtakes a write queued before it, and background writes
   never set `IsCredentialBusy` — that disables the whole form, which mid-typing
   would take focus away. Two guards keep the swap honest: `_loadingForm` (the
   app filling the form on a selection, New or password load is not an edit —
   selecting a profile writes nothing, which a test pins) and
   `_replacingProfile` (replacing the selected record makes the ListBox
   deselect for a moment; that must not reload the form, whose `Effective*`
   values would otherwise overwrite what is being typed). The one save not
   driven by an edit: Connect on an untouched draft (the placeholders'
   `localhost/postgres`) saves it after the probe succeeds. Autosave doesn't
   validate — a half-filled SSH block is kept as typed; Test/Connect report it.
   The hand-off carries a **live `NpgsqlDataSource`, not a connection string**:
   `NpgsqlDataSource.Create` opens no socket, so a wrong password used to
   surface as the new window's first schema-tree error rather than in the form
   that caused it. `ConnectAsync` opens one real connection (returned straight
   to the pool) before raising `Connected`, and ownership travels with the data
   source — the dialog disposes the pool *and* the SSH tunnel under it on any
   failure, the window's `Closed` handler does so afterwards. Net cost is zero
   round-trips: the window inherits a warm pool. The one deliberate exception is
   `BuildMainWindow`'s string overload behind `PGNIMBUS_CONN`, which stays lazy
   because no connect form is standing behind it.
   The dialog's **paste-a-connection-string box behaves like a browser address
   bar** (2026-08): focusing it selects everything, so a paste replaces the
   whole string. It has to, because the box is two things at once — the paste
   target *and* a mirror of the form below, rewritten as a `postgres://` URI on
   every field edit. So it is almost never empty, its content reads as a hint
   rather than as text anyone typed, and splicing a pasted string into the
   middle of it produced a hybrid of the two that then *parsed*, filling the
   form with nonsense (the reported bug). Select-all-on-focus is in
   `ConnectionDialog.axaml.cs`: a tunneled `PointerPressed` that only fires on
   the click bringing focus in (left button only — marking a right-click handled
   would swallow the box's own cut/copy/paste menu), plus a `GotFocus` handler
   for Tab/arrow entry. Clicks after that place the caret normally, so the
   string stays editable by hand.
   **The copy button beside it leaves the password out** (security audit
   2026-09, finding 18): Windows clipboard history, cloud clipboard and every
   clipboard manager keep what is copied, and the button used to copy the real
   password. The copy *with* it is the button's right-click menu ("Copy With
   Password"; no second button, rule 1), through `Platform/SecretClipboard`:
   the text goes on the clipboard beside the platform's do-not-keep markers
   (`ExcludeClipboardContentFromMonitorProcessing` and
   `CanIncludeInClipboardHistory`/`CanUploadToCloudClipboard` = DWORD 0 on
   Windows, nspasteboard.org's concealed/transient types on macOS, KDE's
   `x-kde-passwordManagerHint` on Linux), set as Avalonia *platform* formats,
   whose names reach the OS unchanged, so there is no P/Invoke; and it is cleared
   after 30 s if the clipboard still holds that text. The URI parser behind the
   paste box splits a URI's userinfo at its last '@' before anything else, so an
   unencoded password keeps its '/', '?' and '#' (`postgres://admin:1234/abcd@db/app`
   used to parse as host `admin`, port 1234, and autosave wrote the rest of the
   password to `connections.json` as the database name), and no parser error
   quotes a parsed value (`ConnectionStringParserTests`).
3. **Loading a query never overwrites the active tab.** Saved queries,
   history entries, and generated DDL all open in a *new* tab.
4. **Tabs drag-reorder; the ☰ app menu is the file-command home.** The query
   tab strip reorders by dragging (live, browser-style — pointer handlers in
   `MainWindow.axaml.cs`; the order persists via the workspace snapshot, which
   serializes `Tabs` in collection order). Right-clicking a tab opens a
   five-item flyout — Save Query… / Rename… / Close / Close Others / Close to
   the Right —
   built and shown from `MainWindow.OnTabStripContextRequested` (code, not XAML: the
   handler has to resolve the clicked `ListBoxItem` and re-target the menu
   before it opens, and a `ContextFlyout` on the strip would also fire on its
   empty space). Deliberately save plus rename plus the close family, and of the
   closes
   only the two verbs a tab bar can't express by pointing at one tab — the strip's own ✕, its ▾
   finder and drag-reorder cover the rest. **Save Query… is first, and it is
   there because it was missing** (2026-08): the only route into the Saved
   Queries list was a `Query name` text box parked *under* the sidebar list, in
   a button row with Load and Delete — which act on the list *selection*, so
   Save read as a third verb doing the same rather than as "save this tab".
   Meanwhile Ctrl+S was bound to the file save alone, so the one gesture
   everybody tries opened a `.sql` picker and left the list visibly unchanged.
   Users reported being unable to find saving at all, and they were right: it
   was on no menu, no palette row and no right-click anywhere. Right-click also makes the clicked
   tab active (VS / Notepad++ do the same) so the verbs read against what the
   user is looking at, and the bulk pair is in the catalog as `CloseOtherTabs`
   / `CloseTabsToTheRight` (palette-only, no chord — three tab commands
   already own one), so the palette reaches them too, acting on the active
   tab. **Rename** (2026-08) is `CommandId.RenameTab`, palette-only for the
   same reason (F2 is the results grid's cell edit) and edited **in place** in
   the strip: `QueryViewModel.IsRenaming` swaps the label for a `TextBox`,
   Enter/focus-loss commits and Escape drops it. Two traps behind
   `OnTabRenameBoxAttached`, both of which produce a box nobody can type into:
   `IsVisible="False"` leaves a control *in* the visual tree, so attachment
   fires when the tab's container is realized and never again — focus has to
   follow the visibility flip; and the flyout the rename starts from restores
   focus to the SQL editor as it closes, so the focus call must be posted a
   frame later or the new name lands in the user's query.
   **Closing the last tab empties it** rather than being refused
   (Notepad++): `CloseTab` creates the replacement scratch tab *before*
   removing the old one, so the strip is never momentarily empty and no
   binding sees a null `ActiveTab`. **Except on macOS once it is already
   empty** (2026-09, the Mac audit): there Cmd+W on the window's only tab, when
   that tab is an untouched scratch tab (`QueryViewModel.IsUntouchedScratch`:
   the scratch text or nothing, no file, no saved-query link, no chosen name,
   no browsed table), closes the *window*, as in every Mac app — before that,
   Cmd+W could never close a window at all. The app keeps running (see
   "closing the last window does not quit" in `.claude/rules/window-chrome.md`). A tab with content is still
   emptied first, so the window only closes on a second Cmd+W, after the work is
   on the reopen stack. `MainViewModel.CloseWindowWithLastEmptyTab` (defaults
   to `OperatingSystem.IsMacOS()`, settable for the tests) raises
   `CloseWindowRequested`, which `MainWindow` answers with `Close()`. Windows
   and Linux are unchanged, since closing the last window quits there.
   **Close is undoable** (2026-09, same audit). Cmd+W took a tab holding
   unsaved typed SQL with no prompt and no way back. `CommandId.ReopenClosedTab`
   (Cmd/Ctrl+Shift+T, the browser gesture; palette, ☰ menu and the macOS File
   menu) brings back the most recently closed tab from a session-only stack of
   `MainViewModel.MaxClosedTabs` (20) `ClosedTab` records: text, title and
   override, file path *and the file baseline* (so the dirty dot comes back
   without reading the disk), saved-query link, browsed table, caret, and the
   strip position it returns to. The caret is put back through
   `QueryViewModel.PendingCaretOffset`, which `QueryEditorPanel.AttachQuery`
   consumes once, so the reopened tab is filled in (`CreateTab`) *before* it
   becomes active, or the editor attaches to it half-built. Every close pushes
   (Close others / to the right included, via `RemoveTab`), except an untouched
   scratch tab, which has nothing to bring back; and a lone untouched scratch
   tab (usually the one that replaced the closed last tab) gives way to the
   reopened one instead of staying beside it. A close that took SQL saved
   nowhere (a scratch tab with text, a dirty file tab, a saved query that
   differs from its entry) says so on the status line with the reopen gesture,
   read from the catalog, one line for a bulk close. Tests: `ClosedTabTests`.
   **A tab the user asks for takes focus** (2026-10): Ctrl/Cmd+T (and the
   strip's +, the ☰ menu, the palette) and Reopen Closed Tab raise
   `MainViewModel.EditorFocusRequested`, which `QueryEditorPanel` answers by
   focusing the editor, posted so a closing palette or menu can't hand focus
   back after it. Ctrl+T used to leave focus where it was, so typing went
   nowhere until the editor was clicked. Only `AddTab` and `ReopenClosedTab`
   raise it, never `NewTab`: browse, imports, generated SQL, templates and the
   workspace restore open tabs without taking the keys from what the user was
   typing in. A new tab's placeholder (`QueryViewModel.ScratchSql`) is
   selected as focus lands, so typing replaces it (1.1.0 release pass): with
   the caret in front of it, `SELECT * FROM or` became `SELECT * FROM
   orSELECT 1;`, and accepting a completion then swallowed the rest of that
   word. Tests: `NewTabFocusTests`.
   **A tab's name is either a label or an override, and the difference is the
   bug this fixed**: `QueryViewModel.TitleOverride` is for names a *person*
   chose (the backing file, a saved query, a rename) and survives every later
   edit; `DefaultTitle` is the fallback an app-assigned label uses (browse's
   table name, `x · source`, `s · new table`, "Imported plan"), so it yields
   to the SQL-derived name the moment the buffer says something else. Browsing
   `customers` and then typing a query against `products` used to leave the
   tab named `customers` forever, because the label had been written as an
   override. Only `TitleOverride` rides the workspace snapshot. **An imported
   plan rides it as its content, not as a name** (2026-10, 1.0.1 release pass):
   a tab from Import query plan came back after a restart as an empty "Query N"
   holding only the import's comment, because its label was not an override and
   the plan, unlike a query's result, has no statement to run again. Writing the
   label as an override would have frozen it against later typing, so instead
   `QueryViewModel.ShownImport` keeps the `ImportedPlan` while it is on screen
   (typing, a run and the ✕ hide it, which clears it), `WorkspaceTab.ImportedPlan`
   carries its pasted text (JSON, or the cleaned text; through `SecretRedactor`
   like the tab text, since node lines quote literals), and the restore puts the
   label back at once and imports the plan again off the UI thread
   (`MainViewModel.WorkspacePlansRestored`; a plan that no longer parses leaves
   the named tab and a status line). `ClosedTab` keeps it too, so Reopen Closed
   Tab brings the plan back. Tests: `ImportedPlanRestoreTests`. The tab's text
   rides it through `SecretRedactor` (`WorkspaceStore.Save`, hard rule 7), so a
   restored tab that held a password shows `'<redacted>'` in its place.
   The ☰ button (top-left, 2026-07) opens the one discoverable menu for file/tab-level commands: New Query Tab,
   Open… / Open Recent, Save / Save As… / Save to Saved Queries… /
   Save to File…, Close Tab, Reopen Closed Tab, Switch Connection…,
   New Connection Window…, Settings…, **Keyboard Shortcuts and About pgNimbus**
   (Title Case and the macOS menu bar's own names since 2026-09, DESIGN.md
   rule 18; the palette rows of the same commands say "Open file…", "Save to
   Saved Queries…" and "Save to file…" in the palette's sentence case). Those last
   two were reachable only from the macOS native menu (About) or a single unlabelled
   `?` button (shortcuts), so on Windows and Linux the About box had no entry point
   at all; the menu's tail now matches kubeNimbus's, which is the whole argument for
   having a ☰ menu in both. Neither needed a `BuildMacNativeMenu` addition — View
   already carries Keyboard Shortcuts and the app menu already carries About, which
   is the pairing rule below being satisfied rather than skipped. The command bar's
   centered "Search" pill
   (VS Code-style, same date) opens the command palette — the palette's
   one visible entry point besides Ctrl+K/P. Both deliberately duplicate palette entries
   (discoverability);
   that doesn't loosen rule 1 — new always-visible controls still default
   to no, and new secondary actions go to the palette first, not this menu.
   **macOS exception (2026-07): the native menu bar is the file-command home
   there** — ☰ (and the "pgNimbus" wordmark) are hidden on macOS, and every
   ☰ command lives in the real menu bar instead (see "Platform window
   chrome"). A command added to the ☰ menu must be added to
   `BuildMacNativeMenu` in the same change, and vice versa.
   **Ctrl+S follows the tab, and both destinations are also named outright**
   (2026-08).
   pgNimbus can save a query to two places — the Saved Queries list and a `.sql`
   file — and the obvious gesture used to reach only the file one: a user
   pressing Ctrl+S over an ordinary query tab got a file picker while the Saved
   Queries list sat in the sidebar, unchanged, three inches away. `MainViewModel.Save` now routes on
   `ActiveTab.FilePath`: a tab opened from disk saves back to its file (so nobody
   who was using file save loses their Ctrl+S), anything else goes to the list.
   `SaveAs` splits the same way, into a new file or a new saved query.
   **A mode on the most-pressed key is only safe because it is not the only
   route**: `CommandId.SaveQuery` and `CommandId.SaveFile` are palette-and-menu
   entries that each name one destination, so nothing is reachable only by
   guessing what Ctrl+S will do here. Three supporting pieces, each of which was
   a real defect before:
   `QueryViewModel.SavedQueryId` links a tab to the entry it was saved as, so a
   second save is an **overwrite** — saving used to mint a fresh `Guid` every
   time and the list filled with same-named rows nothing could tell apart. It
   rides the workspace snapshot (`WorkspaceTab.SavedQueryId`) so the link
   survives a restart, and a *stale* id (the user deleted the row from the
   sidebar while the tab stayed open) is treated as never-saved rather than
   resurrecting the deleted entry — which is why `MainWindow.SaveQueryAsync`
   re-resolves it through `SavedQueriesViewModel.FindById` instead of trusting
   it. And a save that already has an entry **skips the dialog entirely**; the
   naming modal (`SaveQueryDialog`, shared with the list's Rename) appears only
   the first time, which is what keeps Ctrl+S feeling like Ctrl+S rather than
   like a prompt. That first time it opens holding the tab's title, selected,
   so Enter saves (`SaveQueryDialog.SuggestName`, 2026-09): it used to open
   empty for a "Query N" tab, which left Save disabled until something was
   typed. A title already in the list gets a number (`orders 2`), because a
   taken name turns Save into Replace and Enter on a suggestion must never
   overwrite a saved query. Its name-collision check is case-insensitive on purpose: the
   list is read by eye, so "Daily report" and "daily report" as two rows is the
   duplicate bug wearing a different hat.

5. **One command catalog, no hardcoded gestures.** Every command and every
   documented keyboard gesture is declared exactly once, in the Core-pure,
   unit-tested `PgNimbus.Core.Commands.CommandCatalog` (a read-only sibling of
   `Json/JsonTree` and `Monitoring/BlockingTree`): id, palette title, cheat-sheet
   title, category, glyph, `Chord`, and which surfaces it appears on. Chords are
   abstract — `ChordModifiers.Command` resolves to Ctrl or ⌘ per `ChordScheme`
   (the platform, or the persisted scheme preference; `Hotkeys.Scheme` in the
   App); `ChordModifiers.Control` is a literal Ctrl for the deliberate
   exceptions, completion's Ctrl+Space (⌘Space is Spotlight) and Ctrl+Tab.
   **The scheme also decides the spelling** (2026-09, Mac audit): the Ctrl
   scheme writes words, "Ctrl+Shift+F", exactly as before; the Cmd scheme writes
   Apple's glyphs in Apple's order, ⌃⌥⇧⌘ then the key — `⇧⌘F`, `⌘↩`, `⇧⌘⌫`,
   `⌥⇧F`, `⎋`, `⇥`, `⌦`, `⇞`/`⇟`, and `⌘?` rather than `⇧⌘/` (`Chord.Caps` /
   `Chord.Label(scheme)`). A Mac user was reading "Cmd+Enter / F5" in the
   palette, "Alt+Shift+F" for a key the keyboard labels ⌥, and keycaps saying
   "Cmd" "Shift" "Enter" in F1. `CommandCatalogTests` holds the Cmd scheme to no
   modifier or special key spelled as a word. Inter has none of those glyphs and
   macOS's own fallback (Apple Symbols) draws them at half height, so every text
   that spells a gesture uses the shared `KeyCapFont` token (DESIGN.md rule 22:
   the interface face, then Lucida Grande / Segoe UI Symbol): the F1 keycaps, the palette's
   shortcut column, the search pill, the empty grid's hint and every `ToolTip`.
   **A platform's own convention is a scheme-only chord** (`MoreChords`, a list
   of `SchemeChord`s): Next/Previous tab answer ⇧⌘] / ⇧⌘[ on the Cmd scheme (a
   Mac keyboard has no PgUp/PgDn; the sheet used to advertise ⌘PgDn), Cancel
   answers ⌘., and the cheat sheet ⌘? (F1 needs Fn there; not ⌘/, which is line
   comment). One marked `Primary` is listed first on its scheme and is what
   `CommandBindings.GestureFor`, tooltips and menus name, while `Chord` stays a
   synonym; Ctrl+Tab / Ctrl+Shift+Tab are unscoped synonyms on both.
   `ChordsFor(scheme)` is the display list, `SynonymsFor(scheme)` what
   `CommandBindings.Matches` accepts (everything but `AltChord`, which can be
   the other half of a pair, Escape beside Enter). The duplicate and shadowing
   tests compare chords *resolved to physical keys, per scheme*, since a
   literal-Ctrl chord and a Command chord are one gesture on the Ctrl scheme.
   Everything downstream is a *projection* of that list, never a second copy:
   `MainWindow.BuildKeyBindings` loops the `WindowBinding` entries,
   `MainViewModel.BuildActionItems` loops the `Palette` entries, the F1 window
   renders `ShortcutsViewModel` (which loops the `CheatSheet` entries — that XAML
   authors no rows), the macOS native menu keeps its hand-curated structure but
   takes each item's gesture *and* command by `CommandId`, and
   [`docs/reference/keyboard-shortcuts.md`](../docs/reference/keyboard-shortcuts.md)
   is generated by `ShortcutDocs.ToMarkdown()` with a golden-file test
   (regenerate with `PGNIMBUS_UPDATE_DOCS=1 dotnet run --project
   tests/PgNimbus.Core.Tests`). Adding a shortcut is therefore **one catalog entry**
   plus a resolver line in `src/PgNimbus.App/Commands/CommandBindings.cs` — the
   App-side half that maps `CommandKey`→Avalonia `Key` and `CommandId`→the
   view-model command. Both halves of the contract are enforced: Core tests
   reject duplicate chords within a scope (and panel gestures carrying the
   command modifier that would shadow a global one), and `CommandBindings`'
   static constructor throws at startup if an invocable entry has no resolver.
   Gestures a `KeyBinding` can't express (focus toggles, keys a panel binds
   itself) still match the catalog via `CommandBindings.Matches(id, e)` rather
   than comparing keys inline — that's how `MainWindow.OnKeyDown` and
   `QueryEditorPanel`'s editor gestures stay in sync. **Tooltips are a
   projection too** (2026-07): a control that names a command writes
   `cmd:CommandTip.Text="…" cmd:CommandTip.Command="Run"` and the attached
   property in `src/PgNimbus.App/Commands/CommandTip.cs` renders "text (Ctrl+Enter)",
   re-rendering itself on a scheme change — never type a gesture into a
   `ToolTip.Tip` string. `CommandTip.Command` is deliberately `CommandId?`:
   the enum's zero value is a real command (`Run`), so a non-nullable property
   reads a set of `Run` as "no change", raises nothing, and silently drops the
   chord. A key no command owns (a search box's Enter, a close button's Esc)
   goes through `cmd:CommandTip.Keys="Shift+Enter"`, parsed by `Chord.TryParse`
   and spelled per scheme the same way; other text that names a gesture is
   composed from `CommandBindings.LabelFor`/`LabelsFor` — the empty grid's
   "Run a query with ⌘↩ or F5" (`ResultsGridPanel.UpdateRunHint`, re-spelled on
   `Hotkeys.Changed`) used to be typed into the XAML as Ctrl+Enter.
   Where the gestures differ per menu item (the Explain flyout), set
   `MenuItem.InputGesture` from `CommandBindings.GestureFor` instead of listing
   both in one tooltip. Two documented exceptions:
   Ctrl/Cmd+1…9 (`CommandId.GoToTabByNumber`) is bound in a loop because nine
   near-identical palette rows would be noise, and Format SQL deliberately also
   accepts Alt+Shift+F whatever the scheme. User-facing settings live on the
   preferences page (`PreferencesWindow`, opened from the palette), persisted in
   `AppSettings`.
   **The platform-text exception: macOS text keys are not catalog commands**
   (2026-09). Measured on a real Mac, the SQL editor ignored ⌥⌫, ⌘⌫, ⌘↑/⌘↓ and
   every Cocoa ⌃ key (⌃A/⌃E/⌃K …), and the TextBoxes ignored ⌘⌫ and ⌃A/⌃E.
   Those are how text behaves on the platform, not things pgNimbus does, so
   `Platform/MacTextKeys` answers them outside the catalog — no palette row, no
   cheat-sheet line, no window binding: ⌥⌫/⌥⌦ (word), ⌘⌫/⌘⌦ (to the line's
   start/end; at the edge, the line break), ⌘↑/⌘↓ (document, ⇧ selects), ⌃A/⌃E
   (line, ⇧ selects), ⌃K (kill to line end) and ⌃Y (yank it back), ⌃D/⌃H, ⌃F/⌃B,
   ⌃N/⌃P (lines; in an open completion list, its rows), ⌃T (transpose). It is two
   tunnelled **class** handlers, on `TextArea` and `TextBox`, registered once from
   `App.Initialize` (not `OnFrameworkInitializationCompleted`, which the headless
   tests never reach) — so every editor (SQL, the cell inspector's JSON editor)
   and every text box (sidebar filter, connection form, find box, grid cell
   editors) gets them with no view opting in, and a class handler runs before the
   element's own handlers, which is why ⌘↑ moves the caret rather than an open
   completion list's selection. Four things keep it safe: modifiers match
   exactly (⌘⇧⌫ is still Rollback); `MacTextKeysTests.No_command_chord_on_the_cmd_scheme_is_a_text_key`
   checks every catalog chord against the table; it is active only on macOS
   *under the Cmd scheme* (`Hotkeys.Command == Meta`) — with the Windows scheme
   on a Mac, Ctrl is the command key and ⌃A is Select All; and every edit is one
   undo step (the editor's own `EditingCommands` where AvaloniaEdit has the
   action, a selection deleted through its Delete where it does not — ⌥⌦ is
   ours, since AvaloniaEdit's `DeleteNextWord` stops at the *next* word's start;
   `SelectedText = ""` on a TextBox). The TextBox keeps its own ⌥⌫/⌥⌦ and ⌘↑/⌘↓,
   which already worked there. Tests force the macOS path per window with the
   inherited `MacTextKeys.ForceProperty` rather than flipping the process-wide
   scheme under tests running beside them.
   **Typing undoes a word at a time** (same change, all platforms,
   `Platform/EditorTypingUndo`). AvaloniaEdit gives each `TextInput` its own
   undo group, so one ⌘Z took back one character (`EditorTypingUndoTests`
   measured `select abc` → `select ab`). A tunnelled `TextInput` class handler
   on `TextArea` opens a group before the insert — continuing the previous one
   through `UndoStack.StartContinuedUndoGroup` when the stack's last group is
   this editor's typing run and the caret has not moved — and the event's
   `RouteFinished` closes it. A run is a word plus the spaces after it; any
   other character starts a new one, so a completion accepted on `(` and an
   auto-closed bracket stay their own steps.
6. **Shared control vocabulary — don't hand-roll button/tab looks.** Every
   button uses one of the style classes in `Styles/Theme.axaml`, never an
   ad-hoc `Background`/`Foreground`: `accent` (filled brand-blue, the one
   primary affirmative per dialog — Connect/Import/Commit/Add), `danger`
   (filled red, the affirmative of a *destructive* confirm — the shared
   `ConfirmDialog`'s confirm button, always destructive), `soft` (neutral
   card-toned outline pill with an accent-tint hover — every secondary
   action: Cancel/Close/Save-as-secondary/Test/New/Refresh), `soft danger`
   (outline red — a secondary destructive action sitting next to a
   non-destructive primary: Delete a profile, Drop a column, Discard all,
   the activity window's Terminate), and `chip` (small toggle/close pills).
   `ToggleButton.soft` is the on/off variant of `soft`, for a toggle that
   belongs in the same button group as its neighbours (the activity window's
   "Auto" beside "Refresh") — a `chip` there sits at a different height and
   radius, and its checked blue wash reads as a *selected tab*, which the
   segmented strip beside it already means.
   Horizontal tab strips use `TabControl.segmented` — a retemplated
   macOS-style segmented capsule (the monitoring windows' Backends/Blocking
   and Database Overview's tabs); the sidebar's Schemas/Queries switch is its
   own `TabControl.capsule`, shared with kubeNimbus (UI rule 1's compact pass has why it is not
   `segmented`), and there is no bare global `TabItem` style any more. Its header line also
   carries a **trailing actions region**: whatever a window puts in the
   `TabControl`'s `Tag` is presented right-aligned on the tab baseline (hosted
   by a `ContentPresenter` inside the template, so it inherits the
   TabControl's DataContext and bindings resolve normally). That's what keeps
   a monitoring window to one band of chrome instead of stacking a toolbar
   above the tabs — the 2026-07 activity-window polish collapsed three bands
   (Refresh/Auto, the tab strip, and a per-tab Cancel/Terminate pair) into
   one, which is also why `ActivityViewModel` resolves a single `TargetPid`
   from the visible tab's selection rather than carrying a command pair per
   tab. Destructive colors come from the `AppDanger*` tokens
   (theme-independent fixed red) and attention-but-not-danger amber (a
   lock-waiting backend, a seq-scan-heavy table, `TextBlock.statusText.warn`)
   from the single `AppWarningBrush` token — never a hand-rolled hex. This
   vocabulary is app-wide across the secondary windows and dialogs; the main
   window's command bar deliberately keeps its flat minimalist `toolbar`
   buttons (rule 1) and is the one surface exempt.
   **A status line is `TextBlock.statusMessage`** (2026-10, 1.0.1 release
   pass; shared since, DESIGN.md rule 21, in nimbusUi's `Theme/Theme.axaml` with
   `Nimbus.Ui.Converters.CutTextTip`): one line (`MaxLines=1`: Npgsql puts a
   server error's `DETAIL` after a newline, which grew the bar a line), cut with
   an ellipsis, and its whole text in a tooltip only while it is cut
   (`CutTextTip` reads the block's `TextLayout` for a collapsed line or lines
   left out). The main window's message and
   cap warning, and the Activity, Database Overview and Security windows' status
   lines use it; Slow Queries keeps its wrapping line, a paragraph of caveats
   meant to be read. Found because a failed safe-mode commit read "Commit
   failed…" in a narrow window with no way to see why: the main message had
   carried a `ToolTip.Tip` since the bar was built, and it never once opened.
   **Every tooltip answers the pointer, through one class handler**
   (2026-10, DESIGN.md rule 21). A `TextBlock` or panel with no `Background`
   is not hit-testable, not even over its glyphs (0 of 4,536 points over the
   status text reached it), so a tooltip on it is dead and the pointer lands on
   whatever has a background behind it: a list item, a card, a column header's
   border. #332 patched the status bar with two `Background="Transparent"`
   setters; a walk of every scenario then found the same defect in about 50
   more places — history and saved-query rows, the connection list's
   endpoints, the Activity and Slow Queries query text, Database Overview's
   cache-hit tiles, the Security window's "connected as", raw filter chips, row
   details' hint, the schema tree's column types, and every results-grid column
   header (its tip is set in code, `ResultsGridPanel.CreateColumnHeader`). So it is
   not fixed per site: `Nimbus.Ui.Controls.ToolTipHitTesting`, installed from
   `App.Initialize`, gives any `Panel`, `TextBlock`, `Border`,
   `ContentPresenter` or templated control that gets a tooltip a transparent
   background as a *current* value. A background the markup or a style sets
   still wins, one that goes back to null is filled again, and nothing changes
   on screen (two renders of every scenario, before and after, are
   byte-identical outside the windows whose tab animation always differs).
   Don't write `Background="Transparent"` beside a `ToolTip.Tip`; the handler
   does it. A **disabled** control still shows no tooltip: Avalonia skips it in
   the hit test on purpose, and `ToolTip.ShowOnDisabled` is the opt-in.
   `TooltipReachTests` walks every window in `Scenarios.All`, hit-tests the
   middle of each tooltip-bearing element's text and fails on any that the
   pointer passes through (203 failures without the handler); it also hovers
   a history row for real and pins the handler's priority. `StatusLineTests`
   hovers the status lines with real pointer input.
   **Surfaces and dialogs have a vocabulary too** (2026-09, DESIGN.md rules 15
   and 16). Every secondary window sits on the shell tone — a `Window` style in
   `Styles/Theme.axaml` sets it, and Fluent's `ContentControlThemeFontFamily` gives
   every window the interface face (UI rule 11), so no window sets either —
   and groups its content in `card`s. A dialog is assembled from
   `TextBlock.dialogTitle`, `TextBlock.dialogHint` and `StackPanel.dialogButtons`
   (the macOS order on every platform: other secondaries, then Cancel, then the
   primary rightmost) on a 20px margin; an overlay over the shell is
   `Border.scrim` holding `Border.overlayCard`; what floats over the editor paints
   with `AppPopupBrush`. **Never `SystemControlPageBackgroundAltHighBrush`**: it
   is pure black in the dark theme, and it was the work area, every popup, the
   palette, the cell inspector and every dialog's body until this pass, which is
   most of why the dark theme read as harsh. The dialogs had also drifted to three
   heading sizes, two hint opacities and both button orders; the scenarios
   `confirm-dialog`, `pending-changes-dialog`, `import-plan-dialog`,
   `import-dialog` and `bulk-grant-dialog` exist so they are seen side by side.
   **The button order flipped to Apple's** (2026-09, macOS UI audit): it had been
   the Windows order, primary first, and on a Mac "Save | Cancel" and "Connect |
   Test" put the affirmative where every native dialog keeps Cancel. It flipped
   everywhere, not per OS, because a per-platform order is the very drift rule 16
   was written to end; `IsDefault`/`IsCancel` carry Enter and Esc wherever the
   buttons sit. **Every modal dialog attaches `DialogChrome`** (not
   `ThemedWindowChrome`, which it wraps): no minimize or maximize on any platform
   (a minimized modal leaves its owner blocked with nothing to answer), and on
   macOS the caption text is hidden by extending the client area under the title
   bar, so the body's `dialogTitle` is the only title on screen and AppKit no
   longer repeats "pgNimbus — Save query" above it. `Window.Title` stays set for
   VoiceOver, Mission Control and the Window menu. `DialogChrome` re-parents the
   dialog's content under a `Panel` with a transparent `TitleBar`-role strip over
   the band (drag) and pushes the content down by `WindowDecorationMargin.Top`,
   so nothing sits under the traffic lights. **Not a sheet:** Avalonia 12 exposes
   no sheet presentation, and driving `beginSheet:` through the native handle
   under Avalonia's own modal loop is not a risk worth a dialog helper.
   The reference windows (activity, overview, notify, slow queries, security) are
   not modal and keep `ThemedWindowChrome`; so does `CrashWindow`, which is shown
   with `Show` and must stay self-contained.
7. **Views compose from focused `UserControl`s — no god-view.** Following
   Avalonia's MVVM guidance (<https://docs.avaloniaui.net/docs/fundamentals/architecture>):
   code-behind is the *right* home for purely visual interaction logic that
   touches `Control` types directly — tab drag-reorder, completion popups,
   `DataGrid` column building, syntax-highlighting theming, cell-edit events,
   pointer/scroll handlers. Pushing that into a ViewModel would be *worse*: it
   leaks `Avalonia.*` types into the layer that (per hard rule 1) must stay
   engine-clean. So the smell to watch is **not** "code in code-behind" — it's
   **one code-behind owning many unrelated responsibilities**. When a view's
   code-behind approaches god-class size (schema tree + tabs + completion +
   cell editing + import/export + palette + follow-FK all in one file), split
   the view into `UserControl`s, each with its own `.axaml` + `.axaml.cs` that
   owns *its* interaction logic and binds to a focused sub-ViewModel. Genuine
   business operations invoked from a handler (import/export orchestration,
   value-cast conversion) belong in a service the ViewModel calls, not inline
   in the handler. `MainWindow` was the standing decomposition target
   (2026-07); the peel-off is now **complete** — `SchemaTreePanel`,
   `SavedQueriesPanel`, `NotifyMonitorPanel`, `QueryEditorPanel` (editor +
   completion + highlighting), and `ResultsGridPanel` (grid + column build +
   cell edit + follow-FK + cell inspector + copy/export/import) each shipped as
   its own `verify`-checked PR, one panel at a time, not one big-bang rewrite.
   What's left in `MainWindow` is the shell that composes them (command bar,
   sidebar tabs, editor/results split, status bar, the command-palette overlay)
   plus window-only concerns (chrome, key bindings, the native macOS menu, file
   open/save dialogs) — new view code still follows the same rule: a focused
   `UserControl` per responsibility, never a god-view (row details and the
   browse filter chips arrived that way: `RowDetailPanel`, `BrowseFilterBar` and
   `FilterEditorView`, hosted by `ResultsGridPanel`, each owning its own keyboard
   model). `ResultsGridPanel` is
   window-central like `QueryEditorPanel` (it inherits the `MainViewModel`
   DataContext and tracks the active tab itself). The cell inspector overlay is
   *defined* inside `ResultsGridPanel` (it owns the JSON editor, its two-way
   sync, and its highlighting) but **reparented into the window's root `Grid`**
   at attach time (`HoistCellInspectorToWindowRoot`) so its scrim and centered
   card cover the whole window and center in the middle — a child overlay would
   otherwise be clipped to the panel's results-pane row. It ends up a sibling of
   the command-palette overlay there; the root inherits the window's
   `MainViewModel` DataContext, so the `{Binding CellInspector…}` paths resolve
   unchanged. **Extraction landmine: a panel's constructor can't see app-level
   resources.** `TryFindResource` walks the logical parent chain, and a
   `UserControl` under construction has none — only a `TopLevel` has the
   `Application` wired in as its styling parent from the start. So code moved
   verbatim from a `Window` constructor into a panel constructor silently finds
   nothing and keeps the framework default (that's how the SQL editor's and the
   JSON inspector's brand-blue `TextArea.SelectionBrush` reverted to the OS
   accent after the extraction, twice). Resolve app resources from
   `OnAttachedToVisualTree` (`ApplyTextSelectionBrush` in both panels), where
   `ActualThemeVariant` is final too.
8. **A panel you open, use and dismiss is an `OverlayPanel`, not a window.** Shared;
   canonical text is [`DESIGN.md`](../shared/nimbusUi/DESIGN.md) rule 13. Three windows
   became overlays: the cheat sheet, the preferences page and the About box —
   `ShortcutsView`, `PreferencesView`, `AboutView`, hosted from `MainWindow.axaml`
   against `MainViewModel.IsShortcutsOpen` / `IsPreferencesOpen` / `IsAboutOpen`. That
   is why those three stopped following this file's usual "raise an event, let the
   view open a `Window`" shape: with no window to own there is nothing left for the
   view to do, so the state lives on the view model and binds two-way.
   **What stays a window, and why**, since this is the line the rule turns on: an
   overlay covers the shell, so anything you need to *watch* while it is open cannot be
   one. `ActivityWindow`, `DatabaseOverviewWindow` and `NotifyMonitorWindow` are
   reference views you read beside your query — the notify one is watched *while*
   the application under test runs, which is the clearest case in the list — and
   are deliberately untouched.
   **The preferences page is titled "Settings"** (2026-09, macOS audit): the
   overlay, its ☰ entry, the cog's tooltip and the palette row (`CommandId.Preferences`
   keeps its name; only the catalog `Title` changed, and the generated shortcut
   reference with it). macOS's app menu already said "Settings…" (Ventura renamed
   Preferences), so the menu opened a page with another name. One name on every
   platform, since the page is the same page. The About overlay carries the vector
   mark (`LogoMarkImage`, 72px) above the name, the way a native About panel carries
   the app icon. **Settings is four tabs** (2026-10): General (startup, shortcut
   modifier, history), Appearance, Editor (completion) and Data (editing, filter
   bar). Seven sections on one page had outgrown the overlay. The strip is
   nimbusUi's `TabControl.capsule` (the sidebar switch's strip, shared when kubeNimbus's
   page went to tabs too), not `segmented`, whose selection fades in and would make
   the `preferences-window` baselines flaky. The page is one height on every tab
   (`PreferencesView.PageHeight`, measured in code-behind), because the overlay's
   card is centred and sized to its content, so a tab change moved the strip
   under the pointer. It opens on the tab it was left on
   (`MainViewModel.PreferencesTab`, session only). A new setting goes on one of
   the four tabs, never a fifth for one card. Tests: `SettingsTabsTests`.
   `ConnectionDialog` and
   `CrashWindow` cannot be overlays at all — both exist before, or instead of, a main
   window. The modal dialogs (`ConfirmDialog`, `AddRowDialog`, `AlterTableDialog`,
   `ImportDialog`, `ImportPlanDialog`, `PendingChangesDialog`, `StagedConflictDialog`)
   each return a result through `ShowDialog`, which an overlay would have to
   re-express as an awaited completion; that is a real change to seven call sites
   and has not been made.
   **An overlay takes focus when it opens and gives it back when it closes**
   (2026-09-29). Opened from the macOS app menu (⌘,) it used to leave focus in the SQL
   editor: Escape, which `OverlayPanel` handles at the `TopLevel` in the bubble phase,
   was answered by the editor first, so Settings could not be dismissed from the
   keyboard, and typed text went into the query behind it. Through the gear button it
   worked, because a click moves focus. `ShellTests.An_overlay_takes_focus_from_the_editor_and_gives_it_back`
   holds it; the rule text is DESIGN.md rule 13.
   The command palette and the cell inspector are also **not** OverlayPanels, and for
   a better reason than inertia: both are focus-driven surfaces with their own
   keyboard model, not panels you read. The inspector's Escape is its own for the
   same reason: tunnelled on the card, ahead of the editor that would swallow it
   (see "Escape closes the inspector" under the json/jsonb convention).
9. **A change to the UI updates the published screenshots in the same PR**
   (2026-09, carried over from kubeNimbus's rule 21). The docs site and landing
   page (`docs/screenshots/`) and the Microsoft Store listing
   (`design/store/screenshots/`) are the first thing anyone judges the app by,
   and they drift silently: nothing fails when a screen they show changes. So a
   PR that changes what any of them shows — a surface, a control, a colour, a
   column, a label — runs `scripts/screenshots/update-published.sh` and commits
   the result with the change, not before the next release. The Store set is
   half light and half dark, alternating, and each file names its theme (its
   README maps file to scenario); keep that balance when swapping a shot. If a PR
   cannot render them, it says so in its description.
10. **Native density and one look for focus and selection** (2026-09-28, macOS
   audit; canonical text is DESIGN.md rule 20). The audit found the app a size
   larger and a shade heavier than every native app beside it: 15px grid text in
   32px rows, 26px tree rows with bold schema names, Fluent's square black focus box
   around tree rows and toolbar buttons, an outlined Windows switch, and title-bar
   icon buttons with no hover. The shared layer fixed the defaults; what is
   pgNimbus's own:
   - **Where the selection faces are adjusted.** `TabsList` and the results panel's
     script-section list are `ListBox.strip` (a tab strip is not a list of rows);
     `PaletteList` and the tab finder's `TabSearchList` are `ListBox.emphasized`
     (driven from a search box that keeps focus). The completion list keeps its
     light wash in `Styles/Theme.axaml`: it never holds focus, and a solid accent
     slab would leave the `tentative` outline nothing to be told apart from.
   - **The schema tree** names schemas in regular weight (Roles/Extensions keep
     semibold as root groups), and its secondary text is `TextBlock.secondary`, so
     the focused tree's accent row can bring it (and a dimmed excluded schema) up.
     That lift in `SchemaTreePanel.axaml` uses a child chain
     (`TreeViewItem:selected > ContentControl > StackPanel > …`) on purpose: a
     descendant selector would also brighten every row of the selected node's
     expanded children.
   - **The results grid is zebra-striped.** `ResultsGridPanel.ApplyRowStaging` sets
     the `odd` class from the row's index as the grid loads each row, and *clears*
     the row background instead of setting it Transparent (a local value outranks
     the zebra style); staged rows still set their own wash. `OnGridRowsChanged`
     re-stripes after a mid-list insert or remove, which shifts indexes without the
     grid loading those rows again (`SharedControlStylingTests` removes a row to
     prove it). Every pgNimbus `DataGridCell` is 13px in a 25px row
     (`Styles/Theme.axaml`, per app by DESIGN.md rules 12 and 14).
   - The command bar's theme button carries a `CommandTip` for `ToggleTheme` like
     its three neighbours.
   The scenario `controls-gallery` shows the focus ring, both selection faces, the
   switches and a highlighted menu item together, since no app screen holds
   keyboard focus; `SharedControlStylingTests` reads each painted part.
11. **Fonts are settings, and no view writes a font name** (2026-10; canonical
   text is DESIGN.md rule 22). Reported from a Mac: the interface wasn't in the
   system face. Three findings shaped it:
   - **The app was never in Inter, and on a Mac it was in Helvetica.** Seventeen
     windows carried `FontFamily="{StaticResource InterFont}"` and the `Window`
     style `{DynamicResource InterFont}`; no such resource was ever defined. The
     `StaticResource` on a control is deferred and resolves to nothing, but the
     style's `DynamicResource` setter put the font's default value (`$Default`)
     at Style priority, above Fluent's Inter. So every window drew in the
     platform default: Segoe UI on Windows (the published screenshots show it),
     DejaVu in the CI container (the baselines show it), and **Helvetica on
     macOS**, which is what CoreText gives for `$Default`
     (AvaloniaUI/Avalonia#21565). Only popups (menus, tooltips, completion),
     which that style didn't match, were Inter. The references are gone; the
     interface face is Fluent's `ContentControlThemeFontFamily`, set by
     `NimbusFonts.Apply` from `AppSettings.InterfaceFont`. `"auto"` is System on
     every platform, which keeps Windows and Linux as they looked and moves the
     Mac to San Francisco; Inter is the opt-in, and the popups now match the
     windows whichever is chosen.
   - **Code had four spellings of one stack** at ~30 sites, plus a copy in
     `ResultTextColumn` for numeric cells, and the Mac got Menlo from it. All of
     them are `Classes="mono"` now (the grid adds the class in code), reading
     `MonoFont`: the bundled JetBrains Mono NL unless `AppSettings.CodeFont`
     names an installed family. NL because `CellValueView`'s JSON path already
     had to dodge Cascadia Code's `->>` ligature.
   - **The editor's size is a resource too.** `SqlEditor.FontSize` binds
     `{DynamicResource EditorFontSize}` (`AppSettings.EditorFontSize`, 8 to 32);
     the zoom gestures use `SetCurrentValue`, which keeps that binding, so a size
     chosen in Settings still reaches a zoomed editor and ⌘0 returns to it. A
     plain assignment would have replaced the binding.
   Settings shows the three under Appearance; the code font list is the installed
   monospace families (`Platform/MonospaceFonts`, through Skia rather than
   Avalonia's `FontManager`, which would keep every face it opened for the life
   of the process), each row drawn in its own face. Tests: `FontSettingsTests`
   (the bundled face's name and its missing `calt`, a change reaching open text,
   the mono class keeping spacing 0, the editor size after a zoom). Not testable
   headlessly: how San Francisco looks, which is release checklist row 28.

## Platform window chrome

Moved to [`.claude/rules/window-chrome.md`](rules/window-chrome.md), which loads when working on the window chrome, menus, `MainWindow`, `ConnectionDialog`, `App.axaml` or `Platform/`.

## App icon / logo assets

Moved to [`.claude/rules/logo-assets.md`](rules/logo-assets.md), which loads when working on `design/`, `scripts/design/` or `src/PgNimbus.App/Assets/`.

## Tech stack

- Packages: see the `.csproj` files (Core's three are hard rule 1).
- No `AvaloniaUI.DiagnosticsSupport` in the App: the Avalonia
  DevTools MCP wiring (Debug-only `.WithDeveloperTools()`) was removed in
  2026-09 (#264) — the MCP tool is not on the current subscription tier, it
  could not reach the completion popup (its own top-level window) or a
  `ContextMenu` anyway, and it cost CI a separate Debug build. Live checks
  go through the headless UI tests, the screenshot harness and computer-use.
- Tests: `PgNimbus.Core.Tests` — TUnit on Microsoft.Testing.Platform. Run
  `dotnet test --project tests/PgNimbus.Core.Tests` (MTP mode comes from the
  `test.runner` opt-in in the repo-root `global.json`) or plain
  `dotnet run --project tests/PgNimbus.Core.Tests`. Never add
  `Microsoft.NET.Test.Sdk` to a TUnit project — it breaks test discovery.
- UI tests: `PgNimbus.App.Tests` — same platform, plus `Avalonia.Headless`.
  Real windows, real key input, no display and no Postgres; see
  `.claude/rules/headless-tests.md`.
- Benchmarks: `PgNimbus.Benchmarks` — a plain console project (Core-only, no
  UI deps) measuring the query engine through its streaming API; see
  `.claude/rules/release-ci.md`.
- `AvaloniaUseCompiledBindingsByDefault` is on — don't add uncompiled
  (reflection) bindings.
- **A RID-less build keeps native assets for the host RID only, and no native
  `.pdb`s** (repo-root `Directory.Build.targets`, 2026-09). Without it every
  `dotnet build` copied SkiaSharp/HarfBuzz for all 17 RIDs plus 100 MB of
  Windows symbols per architecture into each executable's `bin/` (~575 MB →
  ~42 MB), and a built worktree weighed ~4.6 GB. Consequence: a Debug `bin/`
  isn't portable across OS/arch — publish with `-r` for that, as the release
  pipeline already does. The host RID is `NETCoreSdkPortableRuntimeIdentifier`:
  a distro-built SDK (the apt `dotnet-sdk-10.0` the sandbox bootstrap installs)
  reports `ubuntu.24.04-x64` as its own RID, which matches no package asset, so
  every native library was trimmed and nothing that draws could start.

## Coding conventions

- DTOs are `record`s (see `QueryResult.cs`, `SchemaService.cs`).
- MVVM via CommunityToolkit source generators (`[ObservableProperty]`,
  `[RelayCommand]`) — no hand-written `INotifyPropertyChanged`.
- Async all the way; no sync-over-async, no blocking `.Result`/`.Wait()`.
- `Nullable` is enabled — respect it, don't silence with `!` unless truly
  provably non-null.
- `AvaloniaEdit.TextEditor` does not expose `Text` as a bindable
  `AvaloniaProperty` — it's a plain CLR property backed by a `TextDocument`.
  Two-way sync with the ViewModel is done manually in `MainWindow.axaml.cs`
  (via `TextChanged` + `PropertyChanged`, with a re-entrancy guard), not via
  XAML `Binding`. Both the main SQL editor (`_suppressEditorSync`) and the
  cell inspector's JSON editor (`_suppressInspectorSync`) follow this pattern.
- **UI-thread work must not grow with the data** (2026-09, UI-thread audit,
  [`docs/dev/design/ui-thread-audit.md`](../docs/dev/design/ui-thread-audit.md);
  kubeNimbus had the same two freezes first). Five rules:
  (a) **A list that is rebuilt or trimmed changes with one notification per
  operation**: `ViewModels/RangeObservableCollection` (the class kubeNimbus has,
  by the same name) with `ReplaceAll` (one Reset), `AddRange`/`InsertRange` and
  `RemoveRange` (one Add or Remove each). Never `Clear` and an `Add` per item.
  A DataGrid is bound to a collection changed only through `ReplaceAll`; a
  range Add or Remove is for items controls, and keeps their selection, which
  a Reset drops. (b) **Anything that can hold thousands of rows virtualizes**:
  `ListBox` does by default (inside an outer `ScrollViewer` too, since the panel
  reads the effective viewport); a tree is `TreeView.virtualizing`; a custom
  `ItemsPanel` is a `VirtualizingStackPanel`. Filter such a list by what it is
  bound to, never by `IsVisible`. A virtualizing tree reuses a row for another
  node, so its `IsExpanded` binds two-way to the node (`SchemaTreeNode`,
  `JsonTreeNode`, `ExplainNodeViewModel`, `BlockingNode`), never a style's fixed
  value. (c) **Text that can be long goes to a read-only editor**: a
  `ReadOnlyTextView` (`Views/ReadOnlyTextView.cs`; the plan text, the pending
  changes' SQL) is a `SelectableTextBlock` up to 32 Ki characters and a
  read-only AvaloniaEdit editor past that, and the cell inspector and the notify
  payload are `CellValueView`, which is one already; a text block
  lays out every line it holds before drawing one. A preview of a statement or
  value is cut before it reaches a text block (`HistoryText.PreviewLength`,
  `HistoryLabel.Tip`, `CellText.Preview`). (d) **A read or a loop over big data
  runs on the thread pool**: PgNimbus.Core awaits without `ConfigureAwait(false)`,
  and a reader over buffered rows doesn't yield, so a Core call awaited from the
  UI thread does its row loop there. The catalog reads, the palette's relation
  list, the monitor windows' reads, the import's type inference and COPY, the
  history writes and the grid's copy are wrapped in `Task.Run`. (e) **The
  editor's per-keystroke readers get the statement, not the document**, past
  `BackgroundCompletionThreshold` (`QueryEditorPanel.StatementAround` over a
  `SqlStatementBoundaries` cache; `.claude/rules/sql-completion.md`).
  `UiThreadBudgetTests` holds the counts (rows realized, notifications per
  operation) on every build; `tools/UiBench` times the views and each release
  charts it (`.claude/rules/release-ci.md`).

- **A nullable view model never sits *inside* a binding path.** Avalonia logs
  `[Binding] … 'Value is null.'` on every re-evaluation where an *intermediate*
  link of a path is null — a real binding bug then hides in the noise. Two
  shapes produced ~65 of those messages per closed tab (fixed 2026-08): the
  status bar's browse paging reached through `ActiveTab.Browse.PageLabel`, and
  `Browse` is null on every tab that isn't browsing a table — it now hangs off
  `DataContext="{Binding ActiveTab.Browse}"` (+ `x:DataType`, the same shape the
  command-palette overlay uses), so the null lands at the *end* of the path
  where it is merely an unset binding; and `MainViewModel.CloseTab` removed the
  tab strip's selected item, which makes the two-way `SelectedItem` binding push
  `ActiveTab = null` synchronously and every `ActiveTab.*` binding in the window
  log against that transient null — it now moves the selection to the neighbour
  (right, or left when the last tab closes) *before* `Tabs.RemoveAt`, so the
  removal never touches the selection.

- **A read-only connection is the server's to enforce** (2026-09, ROADMAP T3,
  first slice). `ConnectionProfile.ReadOnly` adds
  `Options=-c default_transaction_read_only=on` to the connection string and
  nothing else: no statement is inspected client-side, because a keyword check
  misses the import's `COPY`, a schema action's DDL and a function that writes,
  and the server catches all of them (25006). As a *startup* option it is the
  session default, so the pool's `DISCARD ALL` restores it on every reuse even
  after a session turned it off (`ReadOnlyConnectionTests` pins that). What the
  window shows comes from the server, not the profile:
  `SchemaService.GetWriteStateAsync` (`pg_is_in_recovery()` and
  `default_transaction_read_only`) runs once when the window opens, so a
  read-only role and a standby replica get the same `ReadOnlyMark` beside
  the breadcrumb (profile name › database); the profile's flag only seeds it (read in `BuildMainWindow`
  from the data source's `Options`) so no tab is ever briefly editable on a
  read-only profile. Tabs read `MainViewModel.ConnectionReadOnlyHint` through a
  `Func` and refuse an `EditContext` while it is set (`ApplyConnectionReadOnly`
  withdraws one already on screen when the server's answer lands late). It is
  deliberately not in the connection-string preview, like the accent colour:
  it is this app's setting, not part of the target.
  **Every profile also forces `standard_conforming_strings=on`** (2026-09,
  security audit finding 13). `ConnectionProfile.BuildConnectionString` always
  sets `Options` through `SessionOptions(readOnly)` — the standard-strings
  option alone, or `-c default_transaction_read_only=on -c
  standard_conforming_strings=on` for a read-only profile, and `BuildMainWindow`
  still finds the read-only one by `Contains`. Why: `SqlLiteral.Quote` doubles
  only the quote, and that text is *executed* — browse filters (including
  filter-by-cell), the FK hop's seed, a role's `VALID UNTIL`/`COMMENT` — because
  browse mode's WHERE round-trips through the editor as text (`BrowseSqlParser`
  reads it back into chips), where a parameter cannot live, and `COMMENT ON` /
  `VALID UNTIL` are utility statements, which take no bind parameters. With the setting
  off (a database owner can `ALTER DATABASE … SET` it) a backslash escapes too,
  and a stored `x\'' OR 1=1 --` filtered by cell ran as SQL. A startup option
  beats the database's and the role's defaults and survives the pool's reset,
  so `SqlLiteral`, `SqlLexer` and `SqlScriptSplitter` read literals the one
  way the server does. The `PGNIMBUS_CONN` path adds the
  same option through `ConnectionProfile.WithStandardStrings`, *appended* even
  when the string already names the setting: the server applies `-c` switches
  in order, so the last wins and a string carrying `=off` cannot keep it.
  `StandardConformingStringsTests` turns the test database's default off and
  proves a profile's session still says `on` and the hostile filter matches
  only its row. **The option is not the only guard**: a pooler that drops
  startup options (finding 17's PgBouncer case) leaves the database default in
  place, so `SqlLiteral.Quote` writes text holding a backslash as `E'…'` with
  the backslash doubled too, which reads the same under either setting (the
  same test shows the old plain form returning every row without the option).
  `BrowseSqlParser` reads that exact form back as a typed value (only `\\` and
  `''` escapes), so a LIKE chip's escaped `%`/`_` survives the round trip; any
  other `E'…'` stays a raw chip. `SqlLexer`, `SqlScriptSplitter` and #286's
  Explain check still assume `on` for text the user types.
  **A profile's read-only is never downgraded by the server's "writable"**
  (2026-09 security audit, finding 17). The startup option travels through
  whatever sits in front of the server, and PgBouncer with
  `ignore_startup_parameters = options` (a common workaround for clients that
  send options) drops it: the server then reports a writable session, and
  `DetectWriteStateAsync` used to replace the profile's hint with that `null`,
  so the lock left the title bar and the grid became editable on a profile the
  user had marked read-only. Now a read-only profile that the server reports
  writable keeps a (reworded) hint, so every tab still refuses an edit context,
  sets `MainViewModel.IsReadOnlyNotEnforced`, which turns `ReadOnlyMark` amber
  (`AppWarningBrush`, text "read-only not applied", the tooltip says typed SQL can
  still write), and writes one status-line warning. Nothing blocks typed SQL:
  the server is the only thing that could, and here it didn't get the option.
  A pooler that *rejects* the option fails the connect, which is the right
  outcome. A tab whose hint was the old wording takes the new one
  (`ApplyConnectionReadOnly(previousHint)`), so no chip keeps saying "the server
  refuses writes". The server query is replaceable for tests
  (`DetectWriteStateAsync(probe)`); scenario `main-window-read-only-not-applied`.
- **json/jsonb are a first-class editable type.** `ColumnValueEditorClassifier`
  maps them to `ColumnValueEditor.Json` (jsonpath isn't JSON-shaped so it takes
  the plain-cast `CastText` path below; hstore stays `Text` — its display needs
  an extension mapping), which does two things every edit path (inline F2, staged edits,
  the Add-row dialog) honors: the value is validated client-side by
  `PgValueSyntax.ValidateJson` (a `JsonDocument.Parse` structure check — a bare
  scalar is valid json, so it accepts any JSON value) and stored via
  `CAST(@value AS jsonb)`. The cast is **load-bearing**: Npgsql surfaces
  json/jsonb as `string`, and Postgres has no implicit text→json[b] assignment
  cast, so an uncast `UPDATE`/`INSERT` of a json column fails with a type error.
  The cell inspector (`CellInspectorViewModel`) pretty-prints JSON, offers a
  read-only collapsible tree (`PgNimbus.Core.Json.JsonTree` builds the node
  model — pure Core, unit-tested), and edits an editable cell in place via a
  **View / Edit** segmented-tab header (one click each way; the in-progress edit
  buffer survives a hop to View and back — the `_editSeeded` flag reseeds only on
  first entry or a Cancel/Save). Editing is offered for the **free-text editor
  kinds** (`ResultsGridPanel.IsFreeTextEditor`: `Text`/`Array`/`Composite`/`Json`/
  `CastText`) — everything the commit path can take as typed text — but **not**
  the typed-widget kinds (`Boolean`/`Enum`/`Date`/`Timestamp`), which stay
  inline-only (a text box is a downgrade from their checkbox/dropdown/picker).
  JSON keeps its extras: Format / Minify / client-side validation / `Json.xshd`
  highlighting / the tree toggle, all gated on *json-ness*. Crucially, validation
  is **type-derived** (`validatesAsJson`, set from the column's `Json` editor),
  not content-derived (`IsJson`, which merely reflects whether the value parses)
  — so a plain `text` column holding a JSON-looking string still accepts any
  string. A double-click on an editable json/jsonb cell opens the inspector
  straight on the Edit tab (`OpenCellInspector(..., startEditing: true)`), since
  json is unusable in a one-line inline editor; `ResultsGridPanel.OnResultsGridBeginningEdit`
  cancels the grid's own inline edit for that gesture. Other editable types keep
  their fast inline double-click; the inspector's Edit tab is reached via Space /
  "Inspect Cell…". **Space always opens View** (2026-10): the DataGrid also
  begins an edit on one slow click on the cell that is already current, and for
  a json or previewed cell `OnResultsGridBeginningEdit` turned that into the
  inspector's Edit tab (a short json value got a one-line inline editor), which
  a release pass read as Space opening Edit; a single click
  (`EditingEventArgs` a press with `ClickCount < 2`) on such a cell now edits
  nothing (`InspectorSpaceTests`). Completion carries the jsonb function
  family (`SqlCompletionProvider.Functions`); JSON operators (`->`, `@>`, `?`,
  `@?`, …) are punctuation, out of the identifier-triggered completion model.
  **The JSON polish pass** (2026-09-30, reported from real use: "part of my JSON
  was randomly highlighted blue — a nested list of e-mails"). Five defects, each
  reproduced in a headless test before it was fixed (`JsonInspectorTests`):
  (a) **AvaloniaEdit draws every URL and e-mail address as a link**, in pure
  `Blue`, over the highlighter's colours (`TextEditorOptions.EnableHyperlinks` /
  `EnableEmailHyperlinks` default on): that was the "random" blue, and an
  address in a SQL string literal did the same. `Views/EditorDefaults.Apply`
  turns both off, and every AvaloniaEdit editor in the app calls it — any new
  one must. (b) **Object keys were never coloured as keys.** Json.xshd had a
  `Rule` for them, and AvaloniaEdit tries a RuleSet's Rules only on the text
  *between* its Spans; every key starts with the quote that begins the string
  Span, so the rule never matched once. Keys are a Span now, with a lookahead
  begin (`"(?=(?:[^"\\]|\\.)*"\s*:)`) defined before the string Span so it
  wins the tie. The colours are the `Json*Brush` theme resources in
  `Styles/Theme.axaml`, which the tree paints with directly and
  `Views/JsonSyntax` copies into one highlighting definition per theme.
  (c) **The formatter escaped what JSON doesn't need escaped.** Pretty-printing
  went through `Utf8JsonWriter` with `JavaScriptEncoder.Create(UnicodeRanges.All)`,
  which still escapes HTML-sensitive characters: `"Ann <ann@example.com>"` read
  as `"Ann <ann@example.com>"`, `'`, `&`, `+` the same, and even
  `UnsafeRelaxedJsonEscaping` writes an emoji as two surrogate escapes; a
  Format then Save wrote that back into a `json` column. The Core-pure
  `Json/JsonText` writes the tokens itself (a `Utf8JsonReader` pass), escaping
  only the quote, the backslash and control characters, keeping a number's
  source text byte for byte, reading to `JsonText.MaxDepth` (256; JsonDocument's
  default 64 had shown a deeper jsonb as plain text), always with `\n` breaks.
  (d) **Ctrl/Cmd+Enter in the inspector's editor ran the query behind the
  overlay**, reloading the grid under the cell being edited, and Ctrl+W closed
  the tab it sat on. The window's key bindings see a key *before* the focused
  control does, so `MainWindow.ResolveCommand` returns no command while
  `CellInspector.IsOpen` (the inspector is modal), `OnKeyDown` returns early
  for it (routing the Find chord to the value's own find bar), and
  `CellValueView` answers the Run chord with Save — documented in the catalog as
  `CommandId.SaveInspectedValue`, a gesture note that borrows Run's chord through
  the new `{chord:Run}` placeholder (a second Ctrl+Enter entry would fail the
  shadowing test; spelling "Enter" would fail the Cmd scheme's no-words test).
  (e) **The read view was a monochrome `SelectableTextBlock`** and the tree
  opened as one collapsed `$`. Both the inspector and the notify monitor's
  payload pane now host `Views/CellValueView` (UI rule 7 — the two had carried
  copies of the same XAML): a read-only AvaloniaEdit viewer in the editor's
  colours, with line numbers, folding (`Json/JsonFolding`, a Core-pure bracket
  pass that never parses, so half-typed text still folds, titled like the tree's
  summaries) and its own find bar, and laying out only the lines on screen; the
  tree, which opens breadth first while the rows fit (`JsonTree.OpenRows`, 100),
  colours keys and values like the viewer, and names the selected row's path in
  a footer as SQL over the column (`JsonPaths.ToSql`: `metadata->'cc'->>0`,
  `::jsonb` first for a column that isn't json/jsonb, a JSON path for the
  payload pane, which has no column); its right-click menu copies the value
  (`JsonTree.ValueAt`, by member *position*, since a `json` value can repeat a
  key), the SQL path or the JSON path. A JSON value gets a larger card (up to
  1040 wide, the window's height); the size is a style on the card itself,
  because the hoisted overlay leaves `ResultsGridPanel`'s styles behind. The
  Tree/Text choice is a preference that survives the next Space. The built-in
  `TextEditor.SearchPanel` exists only once the editor's template is applied,
  and the compact find-bar template's buttons need wiring by name
  (`EditorDefaults.WireSearchButtons`, once per panel), as the SQL editor's did.
  **Escape closes the inspector from anywhere on its card, and asks before it
  drops an edit** (2026-10-01, found by `InspectorSpaceTests` in #333). With
  focus in the editor or the viewer, Escape did nothing: AvaloniaEdit's
  `TextArea` marks every Escape handled in its own bubble phase, find bar or
  not, so `MainWindow.OnKeyDown` never saw it. A tunnelled handler on
  `CellInspectorCard` (`ResultsGridPanel.OnCellInspectorCardKeyDown`) closes
  the card first, except while a find bar is open (`CellValueView.IsSearchOpen`):
  then the first Escape closes the bar and the next one closes the card. In Edit,
  Escape closes the card. It does not just leave the edit: a double-click opens
  a json cell straight on Edit, so "back to View" would cost a peek two Escapes,
  and Cancel is a click away. Closing never drops typed text silently, though.
  Escape, ✕ and the scrim all go through `CellInspectorViewModel.CloseCommand`,
  which, when `HasUnsavedEdit` (the buffer was seeded and differs from the value,
  on View too, since the buffer survives the hop), asks through
  `ConfirmDiscardRequested`: a `ConfirmDialog` reading `Discard your changes to
  "column"?`. Its Cancel is `IsCancel` now (DESIGN.md rule 16, missing from every
  confirm until then), so Escape, Escape keeps the edit and focus returns to the
  editor. Only Discard closes. `InspectorEscapeTests` drives all of it with real
  keys.
- **Cell edits round-trip through a server-side cast, not a CLR conversion, for
  types Postgres won't assign from text.** Inline edits send the cell text as a
  parameter and let the engine convert it (`QueryViewModel.ConvertEditedValue`:
  string/Guid/DateOnly/TimeOnly/TimeSpan/DateTime — the last with deliberate
  `DateTime.Kind` handling for timestamp vs timestamptz — and the IConvertible
  numeric family). That path is *wrong* for any type with no implicit text→type
  assignment cast: Npgsql returns it as `string` (xml, tsvector, tsquery,
  jsonpath, pg_lsn) or a non-`IConvertible` CLR type (inet→IPAddress,
  cidr→IPNetwork, macaddr→PhysicalAddress, bytea→byte[], ranges→NpgsqlRange,
  geometric→Npgsql* structs, bit/varbit→BitArray), and an uncast parameter fails
  with "column is of type X but expression is of type text". These are classified
  `ColumnValueEditor.CastText` (whole pg_type categories — network `I`, geometric
  `G`, range/multirange `R`, bit-string `V` — plus named category-`U` types), and
  every edit path (inline F2, staged edits, Add-row) routes them through
  `CAST(@value AS <declared type>)`, exactly as enum/array/composite/json already
  do — no client-side syntax check (Postgres is the parser; the cast surfaces a
  precise error). **The cast target is `ColumnDetail.CastTargetType`, not
  `DataType`** (security audit 2026-09, finding 18): `DataType` is `format_type`
  for the connection's search_path, so a user type on the path came back bare
  and a schema created later that shadowed the name changed what a tab's cached
  cast resolved to. `SchemaService.GetColumnsAsync` also reads every column's
  `format_type` inside a rolled-back transaction whose search_path is narrowed
  to pg_catalog (`set_config(…, true)`), which qualifies exactly the non-built-in
  types, arrays and typmods included (`public.mood[]`), and keeps `integer` or
  `character varying(20)` bare. `DataType` stays the display spelling; the Add-row
  dialog casts through `NewRowField.CastType` (`SchemaServiceCastTypeTests`).
  `money` and `uuid` deliberately stay `Text` (they round-trip
  through decimal/Guid). The value shown in the grid must itself be a valid input
  literal for the cast to accept the round-trip, so `Converters/CellText` formats
  the CLR types whose `ToString` is useless: `byte[]`→`\x`-hex (capped preview),
  `Array`→Postgres `{…}` literal (`PgValueSyntax.FormatArray`), and
  `BitArray`→bit string (`10110001`, MSB first). **Every bit type is read as
  text, by its Postgres type** (2026-10, #354): Npgsql reads `bit(n)` as a
  `BitArray` but `bit(1)` as a `bool`, and the describe-first text request
  (`QueryEngine.NeedsTextFormat`) used to key on the `BitArray`, so a `bit(1)`
  column showed `True`/`False` and every write of its value failed (`cannot
  cast type boolean to bit`): an inline edit, Copy as INSERT, filter by cell, a
  `bit(1)[]` array. It now matches `bit`/`varbit` as `pg_catalog` base types, so
  `bit(1)`, `bit(n)`, arrays and domains over them arrive as the literal psql
  prints (`1`, `10110001`, `{1,0}`) on every read path, and that literal casts
  back. Do not fix this in `CellText` with a `bool` arm: a `boolean` is a `bool`
  too (PR #363 turned every boolean in the inspector into `1`).
  `QueryEngineBitStringTests` holds it against a real server. The `BitArray`
  arm stays for the one read the mask can't reach (a multi-statement command).
  **An array literal keeps the value's shape** (2026-10, #366 and the bytea[]
  fix after it). Npgsql reads a 2-D array as a CLR `T[,]`, whose enumerator
  runs flat, and `FormatArray` wrote `{{1,2},{3,4}}` as `{1,2,3,4}`; it reads
  `bytea[]` as `byte[][]`, and each `byte[]` went down the nested-array branch
  as `{{222,173,190,239}}`. Both cast back without an error, the first as a
  1-D array and the second as a 2-D bytea[] of the digit strings, so an inline
  edit that changed nothing else saved a different value. `FormatArray` now
  walks by dimension (`GetLength(d)`) and writes a bytea element as quoted
  `\x`-hex, `{"\\xDEADBEEF"}` (upper-case hex, as a bytea cell shows it); the
  INSERT copy and `CellDisplay` share it, and JSON export writes nested arrays
  per dimension too. Never `foreach` an `Array` that came from a reader where
  the shape matters. Not kept: a lower bound other than 1
  (`'[2:3]={5,6}'`), which Npgsql drops on read, so an edit re-bases it; and
  CSV, TSV and Markdown still join a 2-D array's elements with `;`, flat, as
  they join a 1-D one.
  `ArrayLiteralRoundTripTests` casts every shape back on a real server, and
  stages the shown text as an edit through safe mode's row check.
  **Value-type arrays are read with nullable elements** (2026-10). Npgsql's
  default `ArrayNullabilityMode.Never` reads an `integer[]` as `int[]` and
  throws for `{1,NULL,3}`, so every int/date/uuid/numeric/range array holding a
  NULL showed `<unreadable integer[]>`: no value and no edit. It also hid a
  concurrent change: the row check reads a placeholder as Incomparable, so a
  commit overwrote another session's edit that had put a NULL into the array.
  Now `ConnectionProfile.ArrayNullability` is `Always`. `BuildConnectionString`
  sets it, which covers `CreateDataSource` and the tester, and
  `ConnectionProfile.ForAppSession` sets it for `PGNIMBUS_CONN`. Every data
  source the app builds must go through one of the two. **Always, not
  PerInstance**: PerInstance reads `int[]` or `int?[]` by row, two CLR types in
  one column (the grid's sort falls back to `ToString` for arrays, so it would
  group rows by whether they held a NULL). Always gives one shape whatever the
  data, so a path tested on arrays without a NULL is the path that runs on
  arrays with one. Npgsql reports `System.Array` as the field type under every
  mode, so `ColumnInfo.ClrType` and the describe's text mask do not change, and
  elements box the same (`CellValueComparer` finds `int[]` and `int?[]` equal
  element by element). Filter by cell writes an array as its literal
  (`RowFilterSql.ValueText`, which needs the column's type for a multirange); it
  wrote `System.Int32[]` and matched nothing. `QueryEngineNullableArrayTests`
  reads, shows, filters and saves such arrays through safe mode (row check
  included) against a real server, and checks that a NULL put in elsewhere is a
  conflict.
- **Safe mode's commit is optimistic-concurrency checked, and a conflict rolls
  back the whole batch** (2026-09). Staging an edit or delete hands
  `PendingChangeSet` a `RowSnapshot` — the row's loaded table columns as the grid
  held them, taken *before* the staged value is shown, and only the first per
  row (a later one would already carry staged values). At commit,
  `BuildRowCheck` produces a `StagedRowCheck` that `QueryEngine.ApplyBatchAsync`
  runs inside the batch's own transaction before any staged statement: one
  `SELECT … WHERE (key) IN (…) ORDER BY key FOR UPDATE` per 500 rows re-reads
  and locks every staged row, and `Evaluate` (Core-pure, unit-tested) compares
  it with the snapshot. **Every loaded column is compared, not just the edited
  ones** — a delete of a row somebody just modified is the case this exists
  for — and any difference or missing row throws
  `StagedChangesConflictException` with before / current / proposed per column,
  which `StagedConflictDialog` lays out. The ways out are
  `PendingChangeSet.Rebase` ("Reload and restage": staged values kept,
  snapshot replaced by the server's current row, rows gone elsewhere dropped)
  and `Unstage` (just the conflicting rows). The staged set is ordered and
  indexed by key (a linked list plus a dictionary per kind): its comment used to
  call it "hand-sized, so linear lookups are fine", and select all + Delete
  staged 100,000 rows with a `Contains` each, then asked about every row the
  grid tinted. Five details that are load-bearing:
  (a) comparison is `CellValueComparer`, not `Equals` — two reads of one
  unchanged array are two instances — and it reports **Incomparable**, not
  Different, for an `<unreadable …>` placeholder or a column read once as a
  text literal and once as a typed value (bit, hstore via the text fallback),
  so a type the client can't read degrades to "unchecked" (listed in the review
  dialog) instead of a conflict nobody caused; (b) the lock waits at most
  `StagedRowCheck.LockTimeout` (5 s, set transaction-locally with
  `set_config`) so someone's forgotten open transaction reads as "locked",
  not a hung commit; (c) inside the user's explicit transaction the batch runs
  under a `SAVEPOINT`, so a conflict undoes only the batch, and `lock_timeout` is
  put back once the rows are locked, since a local setting would otherwise
  outlive the batch; (d) UPDATE/DELETE carry `ExpectedRowsAffected = 1`, so a
  statement that touches anything but its one row aborts the batch rather than
  "succeeding" at nothing, checked per statement once its round trip is back:
  the statements go `QueryEngine.StatementsPerBatch` (500) to an `NpgsqlBatch`
  (2026-09), where they used to take a round trip each (1,000 staged edits:
  ~300 ms locally, and seconds against a remote server; `batch_apply_ms`), which
  is safe because the whole batch is one transaction or savepoint, so a miss in
  a later round trip undoes the earlier ones (`QueryEngineBatchTests`); (e) key parts are cast to their declared type like
  edited values are (`keyCastTypes`), because an enum key part arrives as text
  and `enum = text` has no operator. **Row identity that can't be supported is
  refused up front:** no primary key was already read-only; a key column whose
  type the client can't read (`EditBlocker.UnreadableKey`, CLR type `object`)
  now is too, with its own read-only hint, and a stray unreadable key cell is
  refused at staging. **So is a relation read twice** (`EditBlocker.RepeatedTable`,
  2026-09 security audit, finding 14): `SELECT c.id, p.name FROM items c JOIN
  items p ON p.id = c.parent_id` passes `CheckSingleTable` (one OID, distinct
  attnums), and an edit of `name`, the parent's, updated the child. The wire
  metadata can't see it, so `EditableResultDetector.CheckRepeatedTable` reads
  the text through `SqlScopeModel`: every relation of the blocks whose columns
  can reach the result (branches, FROM subqueries, LATERAL, CTE bodies, each CTE
  reference) counted by bare name, expression subqueries skipped (their columns
  never carry an OID). Nothing offers an undo after a successful commit — the
  batch is then the server's. Real-server coverage is
  `QueryEngineStagedConflictTests` (gated on `PGNIMBUS_TEST_CONN`, drives a real
  second session, including the lock case).
- **Row details and browse filters: out of the way, findable, and neither
  touches a query someone wrote** (2026-09, ROADMAP.md T4).
  **Where they live was the design question, and it took three answers.** The
  first cut put row details in a column beside the grid and the filters in a bar
  of full-size inputs above it — both took the grid's space for an occasional
  task. The second moved them out of the way (row details a card over the
  window, filters a line of chips) *and* put both behind an opt-in that defaulted
  off — which hid them so well that the first person to try couldn't find either.
  Now: row details needs no setting at all (an overlay costs nothing until it's
  opened, and it follows the grid's row only while open: its form, a stack of
  editors per column, used to be rebuilt on every arrow key with nobody looking)
  and has a status-bar button (`RowDetailsIconGeometry`, shown whenever
  there are rows, negative margin so the bar doesn't grow when they arrive) next
  to Ctrl/Cmd+I and the grid menu. The filter chips appear whenever a condition
  filters the rows, and the status bar's funnel (browse mode only) pins the line
  open even when empty — `AppSettings.ShowFilterBar`, off by default,
  `TableBrowseViewModel.AlwaysShowBar` per tab.
  **A WHERE you type comes back as chips** (`Query/BrowseSqlParser`, Core-pure,
  unit-tested). A browse tab's page query edited by hand drops browse mode on the
  first keystroke as always, but a *run* of it that still has the browse shape
  (`SELECT * FROM` the same table, optional `WHERE`, `ORDER BY` one column or the
  key, `LIMIT` required, parentheses nested at most `MaxParenDepth` (64) deep: the
  split is quadratic in nesting, and 12,000 levels cost 5 s after a Run) resumes
  browse mode via `TableBrowseViewModel.FromParsed`
  — running exactly the text typed, recomposing nothing until a later explicit
  chip/page/sort action. The WHERE is split on top-level `AND` (not the one in
  `BETWEEN`); a part `RowFilterSql` could have written and the column's operator
  picker can show becomes a typed chip, **everything else is kept verbatim** in
  `RawConditions` (an `OR`, a function, a subquery, `IN`, `LIKE` …), shown as a
  removable raw chip and ANDed back first. So the chips always account for the
  whole WHERE, and parsing never guesses at an expression it can't reproduce.
  The FK hop's seeded condition is a raw condition too (`FilterText` is now a
  wrapper over `RawConditions`).
  **The shape is not the proof; the rows' OID is** (2026-09 security audit,
  finding 14). The parser accepts `FROM orders` for a tab browsing
  `sales.orders`, and `search_path` may resolve that to `public.orders`: the
  resume used to hand out an edit context for `sales.orders` over
  `public.orders`' rows, so an inline edit (safe mode off) updated
  `sales.orders` by the other table's keys. Browse mode now resumes, and
  `EstablishBrowseEditContext` hands out a context, only when every result
  column's `TableOid` is the browsed table's (`EditableResultDetector.ReadsOnlyTable`).
  That OID is learned for free from each composed page (it names the table in
  full), or for a restored tab looked up by exact name
  (`SchemaService.GetRelationOidAsync`) alongside its columns. A browse-shaped
  query that fails the check is an ordinary, read-only query: no chips, no edit
  context, and the status line and the read-only hint say it doesn't read the
  browsed table. Live coverage is `BrowseEditTargetTests`.
  **It survives a restart.** `WorkspaceTab.BrowseSchema`/`BrowseTable` carry
  the name of the table a tab browses (`QueryViewModel.BrowsedTableName`); a
  restored tab gets `RestoreBrowsedTable`, and its *first run* reads the columns
  (`LoadRestoredBrowseTableAsync`) — not the restore, which would cost a catalog
  round-trip per tab on every launch — then resumes browse mode the same way a
  hand-edited run does. Before this, every restored browse tab was a plain query
  for good, so a typed WHERE never came back as chips after a restart.
  Four rules hold the pieces together:
  (a) **One set of type-aware inputs.** `Views/ColumnValueEditorView` (bound to a
  `NewRowField`) is the Add-row dialog's editor stack pulled out whole, and it is
  also every row-details field and the filter editor's value box.
  `NewRowField.Seed` loads an existing value into whichever control the type
  shows; `NewRowField.For(column, placeholder)` is the one constructor. Don't grow
  a fourth copy of the checkbox/dropdown/picker switch.
  (b) **Row details never writes.** `RowDetailViewModel` (per tab, on
  `QueryViewModel.RowDetail`) collects edits and Stage hands them to
  `QueryViewModel.StageRowEdits`, which stages **whatever safe mode says**: a form
  of edits is one change to review, and `PendingChanges` gives it the review
  dialog and the T2 conflict check for free. All values are converted before any
  is staged, and `StageCellValueCore` returns the replacement row instance,
  because staging replaces the row wholesale. The baseline for "changed" is what
  seeding *produced*, not the raw value. Unstaged edits pin the form to its row
  against selection changes; a new `EditContext` (any run or page load) drops
  them, since field column indexes may mean something else in the new result.
  (c) **A chip is applied, a draft is not.** `TableBrowseViewModel.Filters` holds
  only applied conditions — what `BuildSql` composes, so paging and sorting run
  exactly what the chips say. The flyout edits `Draft`, a *copy* when editing a
  chip, swapped in on Apply (`CommitDraftCommand`); closing the flyout any other
  way drops it (`CancelDraft`). Predicates come from the Core-pure, unit-tested
  `Query/RowFilterSql` (operators per type family, NULL tests on every column,
  LIKE-wildcard escaping, untyped quoted literals so Postgres types each
  comparison by its column, `json` offered text search because it has no `=`);
  the editor shows the draft's SQL before it runs. The FK-seeded `FilterText`
  stays a raw, removable chip, ANDed first. Those literals are executed as
  text, and their `''` escape is complete only because every session forces
  `standard_conforming_strings=on` (the read-only paragraph above, finding 13).
  (d) **Filters exist only in browse mode.** The strip's host is bound to
  `ActiveTab.IsBrowsing`, and `MainViewModel.FilterRows` on a non-browse tab only
  says where filters live — there is no path from a filter gesture to the text of
  a query that isn't a browse page query. Ctrl/Cmd+F in a *browsed grid* opens a new condition
  (documented on `CommandId.FilterRows` as a `GestureNote`, not a second chord,
  which the catalog test would rightly reject as shadowing the global Find).
  An empty browse result has its own states — "No rows match these conditions"
  with Clear, or the page label for an empty table — instead of the never-ran
  "Run a query" hint (DESIGN rule 7).
  **The chrome must not follow the caret.** Found live: the first keystroke in a
  browse tab's editor ends browse mode (`OnSqlChanged`), and the chip line and
  paging were bound to `Browse`, so they vanished as you typed and came back on
  Run — the grid jumping under them, which read as the panel appearing at random.
  They now bind to `QueryViewModel.ShownBrowse`, which outlives the edit and is
  shown disabled/dimmed (`IsBrowseEdited`) until the next full run either brings
  browse back (browse shape) or clears it (any other query).
  Also found live: a raw chip drew blank — its `TextTrimming` layout had been
  computed while the line was hidden. Chips shorten text in a converter
  (`Converters/ChipText`) instead.
  **The parser runs on the UI thread, so it must terminate on any text.** Found
  live: a lone `:` (a stray "Warcraft 3: …" pasted into a browse WHERE) produced a
  zero-width operator token forever, and Run hung the app. It now tokenizes
  through the shared `SqlLexer`, whose every branch consumes at least one
  character, and `BrowseSqlParserTests.Any_text_at_all_finishes_parsing` still
  feeds it every printable ASCII character in every position under a timeout —
  keep the adapter's operator-run loop honest against it.
  One landmine: a chip label is a `TextBlock` inside the Button, not string
  `Content`, because string content treats `_` as an access key (`placed_at`
  rendered as `placedat`) — the same trap `EscapeMenuHeader` exists for.
  UI tests: `tests/PgNimbus.App.Tests/RowDetailAndFilterTests`; parser tests
  `tests/PgNimbus.Core.Tests/Query/BrowseSqlParserTests`; screenshot scenarios
  `main-window-browse-row-details`, `main-window-browse-no-match`,
  `main-window-browse-typed-where`, `filter-editor`.
- **A grid cell shows a preview, and a previewed cell never opens the inline
  editor** (2026-09). `CellText` is the one place a result value becomes text: in
  full for the cell inspector (`CellText.Full`), and capped at
  `PreviewLength` (256) characters and folded onto a single line for the grid
  (`CellText.Preview`, behind `RowIndexConverter`). The cap is what makes a
  jsonb-heavy table scroll at all: a `DataGrid` cell is a bare `TextBlock` with
  no wrapping and no trimming, so it shapes every character it is handed — even
  the ones clipped off the right-hand edge of a 560 px column — and it does that
  as each row is realized. On `scripts/demo/06_telemetry.sql` (47 columns, jsonb
  payloads averaging 37 KB and reaching 300 KB) one wheel-scroll's worth of new
  rows cost ~450 ms of layout, and ~30 ms with the cap; 256 is the smallest value
  that leaves the floor untouched while staying ~3× what the widest column can
  actually show. Folding newlines to spaces is the same argument from the other
  side: a 40-line stack trace made its row 40 lines tall.
  **An array, multirange or hstore literal stops at the cap too** (2026-10,
  #365): it used to be built whole and then cut, so a ten-thousand-element array
  cost ten thousand conversions per realized cell. `PgValueSyntax.FormatArray`,
  `FormatMultirange` and `FormatHstore` take a `maxLength` and return exactly
  the literal's first characters, and `Preview`/`IsShortened` ask for
  `PreviewLength + 1`, the one extra character being what tells a literal of
  256 from a longer one; `Full` asks for all of it. Two things are still read
  whole, because the text before the cap depends on them: an element's quoting
  (a delimiter past the cap changes its opening quote), and a multirange's
  elements unless the array is of one range struct type (a single non-range
  element makes the value fall back to the array literal). Escaping goes a run
  at a time between the characters it escapes: a loop per character had made a
  4 MB element five times slower to export. The walk over a 2-D array's
  dimensions stops at the cap the same way, and a bytea element converts only
  the bytes whose hex can still fit. `CellTextPrefixTests` and
  `PgValuePrefixTests` hold the prefix, the formatter-call budget and the
  allocations.
  **The safety half is not optional.** The DataGrid pre-fills its inline editor
  from the column's own display binding — i.e. from the preview — so a cell
  showing less than it holds must not be edited inline, or committing an
  untouched editor would save the preview over the real value.
  `ResultsGridPanel.OnResultsGridBeginningEdit` asks `CellText.IsShortened` and
  cancels into the cell inspector instead, exactly as json/jsonb already did
  (that also closed the pre-existing hole where inline-editing a large `bytea`
  committed its 24-byte hex preview). Everything else — sorting, copy, export,
  the commit path itself — reads the raw row values and is untouched by the cap.
- **Dates and times in the grid are ISO, whatever the region** (2026-09, the
  Mac audit: a Czech Mac saw US `03/23/2026 02:03:29`, because `CellText`
  passed `DateTime` through to the binding's culture). `CellText.Temporal`
  writes what Postgres prints with `DateStyle = ISO`: `2026-03-23`,
  `2026-03-23 02:03:29`, `02:03:29`, fractional seconds only when non-zero (up
  to six digits), and `infinity`/`-infinity` for the Min/Max values Npgsql maps
  those to. **timestamptz shows the UTC instant with `+00`**, exactly what psql
  prints in a UTC session: Npgsql converts every timestamptz to UTC and does not
  say which zone the session used, and the suffix stops anyone reading it as
  local time. timetz keeps its own offset (`+01`, `+05:30`). Intervals are left
  as they were. **The column's type is load-bearing**: Npgsql hands a `date` and
  a `timestamp` over as one `DateTime`, and a `time` and an `interval` as one
  `TimeSpan`, so `Preview`/`Full` take the wire type name (`RowIndexConverter`,
  the cell inspector's `Open` and row details pass `ColumnTypeName(i)`); without
  it a UTC `DateTime` is still marked `+00` by its Kind. **Every rendering must
  read back**, since the grid pre-fills its inline editor from it:
  `QueryViewModel.ParseEditedText` (the static half of `ConvertEditedValue`,
  public so `TemporalCellTextTests` can hold the round trip) reads `+00` back to
  a UTC `DateTime` for timestamptz, an offset-less wall clock to `Unspecified`
  for timestamp, `24:00:00` and the infinity words too. Export and copy write
  invariant round-trip (`"O"`) text for `DateTime`/`DateTimeOffset` and never
  read `CellText`, **except for infinity** (2026-10, 1.0.1 release pass):
  `"O"` wrote Npgsql's `DateTime.MaxValue` as `9999-12-31T23:59:59.9999999`,
  which Postgres reads back as a finite timestamp (rounded into the year 10000),
  and `-infinity` as the year 1. `PgValueSyntax.TemporalInfinity` is now the one
  place that knows Npgsql's mapping (Max/Min of `DateTime`, `DateOnly` and
  `DateTimeOffset`); `CellText.Temporal` and `ResultExporter`'s scalar text both
  ask it first, so CSV/TSV/Markdown/JSON/INSERT write `infinity`/`-infinity` in
  a cell, an array element and a range bound (`[2026-01-01,infinity)`, which is
  not the unbounded `[2026-01-01,)`). The FK hop's seed goes through
  `FormatSqlLiteral` and gets it too. Npgsql reads `0001-01-01 00:00:00` as
  `DateTime.MinValue` as well, so that one finite value shows and exports as
  `-infinity`. `ResultExporterInfinityTests` holds it, and with
  `PGNIMBUS_TEST_CONN` casts each written form back on the server.
  **Numbers are invariant text too** (2026-10, #360): `CellText.Number` writes
  `55.75`, `-128`, `NaN`, `-Infinity` for the grid, the inspector and row
  details, whatever the culture. The shipped app runs with
  `InvariantGlobalization`, so its binding already wrote them that way (a Czech
  Mac never saw `55,75`). The test host and the Screenshot/UiBench tools run in
  the machine's culture, though, and the app would too if the flag ever went.
  That matters because the inline editor is pre-filled from the cell text and
  `ParseEditedText` reads it invariantly, where a decimal comma is a thousands
  separator: `55,75` would be saved as 5575. `NumericCellTextTests` holds the round trip under cs-CZ, de-DE,
  en-US and ar-SA. **So are the date pickers** (2026-10, #353): a
  `CalendarDatePicker` style in `Styles/Theme.axaml` gives every picker in the
  app (row details, Add row, the filter editor, the grid's inline editors, the
  role dialog) `Converters/IsoDate` as its `TextConverter`, so it shows
  `2026-10-08` and reads `2026-10-08` or `2026-1-8`, nothing else; a bare picker's
  placeholder says `<yyyy-MM-dd>`. They used the culture's short pattern, which
  is `MM/dd/yyyy` in the shipped app, so row details showed a date in another
  shape than the grid cell it came from. `IsoDatePickerTests` types into a real
  picker under the same four cultures.
  **Ranges and multiranges are written by pgNimbus, never by Npgsql's
  `ToString`** (2026-10, 1.0.1 release pass). `NpgsqlRange<T>.ToString` writes
  each bound in the process culture: a CSV export of a `tstzrange` came out as
  `[07/22/2026 19:56:13,07/25/2026 19:56:13)` (the app's invariant culture is
  US-shaped), the fraction of a second and the zone gone, and a `numrange`
  under a decimal comma as `[1,5,2,25)`. `PgValueSyntax.FormatRange` writes the
  literal (`empty`, `(,6)`, bounds quoted as the server quotes them) with a
  bound writer the caller chooses: the grid's is `CellText.Temporal`, so a cell
  reads as psql prints it (`["2026-07-22 19:56:13.543613+00",…)`) and the inline
  edit casts it back unchanged; the exporter's is its scalar text, so a bound
  reads like the timestamptz cell beside it (`2026-07-22T19:56:13.5436130Z`).
  The subtypes are a closed `switch` over `NpgsqlRange<int|long|decimal|DateTime
  |DateOnly|…>`, not reflection (NativeAOT). **A multirange and an array of
  ranges are the same CLR value** (`NpgsqlRange<T>[]`) with different literals
  (`{[1,3),[5,7)}` against `{"[1,3)","[5,7)"}`), so only the column's wire type
  tells them apart: `CellText.Preview/Full/IsShortened` take it, and every
  `ResultExporter` writer takes an optional `columnTypes` list that export and
  copy pass. In CSV/TSV/Markdown a multirange is its literal while an array
  stays `;`-joined; an INSERT copy writes an array as `'{1,2}'` (it wrote
  `'1;2'`, which no array column takes). `DateOnly`/`TimeOnly` are ISO in export
  and in `PgValueSyntax.InvariantText`, the fallback every literal writer uses,
  and a `timestamptz[]` cell's elements are written like the scalar.
  `ResultExporterRangeTests` runs under cs-CZ and, with `PGNIMBUS_TEST_CONN`,
  casts what each range and multirange type was written as back on the server;
  `RangeCellTextTests` holds the grid side.
  **Infinity is written as the word in every text the server runs** (2026-10).
  Npgsql reads `infinity`/`-infinity` as the Max/Min of `DateTime`, `DateOnly`
  and `DateTimeOffset`, and the writers whose text is executed formatted those
  as dates: filter by cell on an infinite timestamptz wrote
  `9999-12-31 23:59:59.999999+00`, a finite timestamp, and matched nothing.
  `RowFilterSql.ValueText` (filter by cell, and each range bound through
  `FormatRange`), `SqlLiteral.Format` (the staged-batch row check, the review
  script, a role's `VALID UNTIL`) and `PgValueSyntax.InvariantText` (the
  fallback of the array and range writers) ask `PgValueSyntax.TemporalInfinity`
  first. `InfinityLiteralTests` holds the text and, with `PGNIMBUS_TEST_CONN`,
  runs a filter built from each infinite cell (timestamp, timestamptz, date,
  tstzrange, daterange) and checks it matches that row alone.
- **Export writes every row, not the grid's** (2026-09, ROADMAP D1). It used to
  write `Rows`: one 100-row page when browsing, at most `MaxDisplayRows` for a
  query, silently. `QueryViewModel.ChooseExportSource` now decides: a grid that
  holds the whole result is written as is; otherwise the statement runs again
  with no limit (`_resultSql`, or `TableBrowseViewModel.BuildExportSql` — the
  page query minus `LIMIT/OFFSET`) and `ResultExporter.WriteStreamingAsync`
  (Core-pure, unit-tested) writes batch by batch, flushing each before the next
  is read, so memory holds one batch. **Only the browse query is ever run
  again** (2026-09 security audit, finding 1): a hand-written query, a script
  section and any query in an explicit transaction (where the engine
  materializes) write the grid and say "Exported only the N rows shown". A
  lexical read-only check (`IsSafeToReExecute`, since deleted) used to vouch for
  plain SELECTs, and `SELECT create_order()` passed it and ran twice. The export
  runs like a query (`IsRunning`, its own CTS, so Cancel works) and the view
  deletes the file unless `ExportAsync` reports it complete. Two landmines:
  no token on the `Task.Run` around the writer (a task cancelled before it
  starts never enumerates the batches, and only enumerating them closes the
  engine's connection), and a progress tick still queued at the end must not
  overwrite the final status line (`finished`). Live coverage is
  `tests/PgNimbus.App.Tests/ResultExportTests`, gated on `PGNIMBUS_TEST_CONN`.
- **A result is bounded in rows, bytes and grid columns** (2026-09, security
  audit finding 16). `MaxDisplayRows` (100,000) was the only bound, and a row
  can be anything: `SELECT *` over the telemetry demo's 37 KB jsonb cells is
  several GB, one `repeat('x', 500000000)` a gigabyte, and a script kept
  100,000 rows per statement. Now `QueryViewModel.MaxDisplayBytes` (256 MiB) is
  charged per row through the Core-pure `ResultBudget` (`EstimateRow`: UTF-16
  strings, bytea, arrays by length, a slot per cell, cheap enough per row), and
  `CollectRowsAsync` stops enumerating at the first row either limit refuses.
  Four things make that hold: (a) **abandoning the stream cancels the query**:
  `StreamBatches` marks each yield, and a consumer that stops mid-result gets
  `command.Cancel()` in the finally instead of a reader disposal that drains
  every remaining row (`ResultBudgetTests` holds it to 15 s on 50M rows; it took
  21 s without); (b) a batch is also handed over at `BatchBytes` (8 MB), so a
  few huge rows reach the budget one at a time instead of 200 at once; (c) a
  materialized result (inside a transaction) takes `maxBytes` and stops like the
  row cap; (d) **a script's sections share one `ResultBudget`**, and a
  statement that starts after it is spent keeps nothing but is read to the end
  rather than cancelled (`ResultCap.Shared`), because it may be a write whose
  `RETURNING` rows nobody asked for and a cancel would abort it. The cap text
  names the limit (`CapTextFor`). **The grid builds at most `MaxGridColumns`
  (300)** and the status bar says "showing 300 of N columns"
  (`CapStatusText`, which is `CapText` plus that; export still reads `CapText`
  alone, since every column is in the rows). It was 1,000 until the 2026-09
  UI-thread audit: the DataGrid has no column virtualization, it lays out a
  header per column (about 0.8 ms each) and builds a cell per column of every
  row on screen, so 1,000 columns took 5 s to show and 3 s to come back to on a
  tab switch. Row details stop at the same column. An edit context arriving
  after the rows no longer rebuilds the columns
  (`ResultTextColumn.TryUpdateMetadata`; a rebuild only when a column would
  draw differently). `ColumnNames` is a
  `RangeObservableCollection` filled with one `ReplaceAll`: the grid rebuilds all
  its columns on every change, so an `Add` per column had been building
  n(n+1)/2 of them. Tests: `ResultLimitsTests` (in-memory batches, the column
  cap, and a gated `repeat('x', 100000000)` × 3 that keeps one row).
  **Not bounded yet**, as the audit also asked: a single cell is still read
  whole by Npgsql before the budget can refuse its row (a 500 MB cell costs
  500 MB). That still wants a per-cell read cap with the full value fetched on
  demand in the inspector. The audit's other gap, an array or hstore preview
  built whole before it was cut, closed in #365 (the grid-preview bullet above).
  **Safe for Spreadsheets** (security audit 2026-09, finding 18, CSV formula
  injection) is a checkbox at the foot of the command bar's Export menu,
  `AppSettings.SpreadsheetSafeExport`, off by default because the quote changes
  the data for every reader that isn't a spreadsheet. On, CSV export and the
  grid's TSV/CSV copies ("Copy" is TSV) put a `'` in front of a text cell or
  header starting with `=`, `+`, `-`, `@`, tab or CR (`ResultExporter.NeutralizeFormula`);
  numeric CLR values are never prefixed, since `-5` is a number to the
  spreadsheet too. JSON, Markdown and INSERT copies are untouched.
- **Import is capped and parsed off the UI thread** (security audit 2026-09,
  finding 18). The file is read whole and becomes a rows × columns matrix, so a
  20,000-object JSON file whose objects each had keys of their own was 400M
  cells. `TabularFileParser.ReadTextAsync` refuses a file over `MaxFileBytes`
  (512 MiB) before reading it when the length is known, and as the read passes
  it otherwise; the parsers stop past `MaxRows` (1,000,000), `MaxColumns`
  (1,000) and `MaxCells` (50M, the padded matrix: a wide header over many short
  rows passes the first two) with an `ImportLimitException` whose message says
  which. `ResultsGridPanel.ImportAsync` runs read and parse in `Task.Run`.
- **A type Npgsql can't materialize must never fail a whole result set.** An
  unmapped composite (or an array/domain/range over one), an extension type with
  no plugin loaded (pgvector, PostGIS), `bit`/`hstore` whose CLR mapping has a
  useless `ToString` — all of them make `GetValue` throw *"Reading as
  'System.Object' is not supported for fields having DataTypeName …"*, and
  `GetFieldType` throws it too, before the first row is even read. `QueryEngine`
  answers in two layers, both required:
  1. **Describe first, execute once** (`QueryEngine.DescribeAsync` →
     `NpgsqlCommand.UnknownResultTypeList`). Every statement the engine runs is
     first sent with `CommandBehavior.SchemaOnly` — Parse and Describe, no
     Execute — which returns the row description without running anything; the
     columns that need it are then requested as Postgres literals
     (`("246 Oak St",Milan,MI,20918,IT)`, the shape the grid shows and the
     composite editor casts back on edit) on the one real execution. **This
     replaced a second execution** (2026-09 security audit, finding 1): the old
     fallback re-ran the statement with the mask set, gated on a lexical
     read-only check, and `SELECT create_order()` — a read by its keyword, a
     write by its VOLATILE function — ran twice. No lexical check can tell what
     a function does, so the rule now is that **user SQL is never executed
     twice by the app, anywhere**; the describe costs one extra round trip per
     statement and is also what finding 2's fix uses as its liveness check.
     **It is skipped only where it has neither job** (2026-09, benchmark pass:
     the v1.0.0 release doubled `roundtrip_ms`, and a script paid two round
     trips per statement). A statement that is never retried (a script's second
     statement onwards, anything inside the user's transaction) needs no
     liveness check, and one that `SqlStatementInspector.CannotReturnRows`
     (an allowlist of leading words — INSERT/UPDATE/DELETE/MERGE, DDL, SET,
     BEGIN/COMMIT… — and no `RETURNING` token anywhere) has no columns to mask,
     so `QueryEngine.DescribeNeeded` sends it straight away. A wrong guess costs
     a placeholder cell, never a second run. A seed script's round trips drop
     by half (`script_ms`); single statements keep the describe.
     The mask is skipped for a multi-statement command (`SELECT a, b; SELECT 1`):
     Npgsql applies it to every statement and its length must match each one.
  2. **The per-cell guard** (`QueryEngine.ReadValue` / `FieldType`) catches the
     `InvalidCastException`/`NotSupportedException` for everything layer 1 can't
     cover and yields `QueryEngine.UnreadableCell(dataTypeName)` —
     `<unreadable commerce.address>` — so the rest of the row still renders. Only
     those two exception types are caught; a dropped connection mid-row must stay
     an error. Integration coverage is `QueryEngineCompositeTests` (gated on
     `PGNIMBUS_TEST_CONN` like the reconnect tests), which also holds the
     audit's live check: a volatile composite-returning function runs once.
- **SQL text, the lexer and completion** (packages A–R): see
  [`.claude/rules/sql-completion.md`](rules/sql-completion.md), which loads when working on
  `src/PgNimbus.Core/Text`, `src/PgNimbus.Core/Schema`, `src/PgNimbus.App/Completion`, `QueryEditorPanel` or
  `tools/CompletionBench`.
- `SqlFormatter` follows <https://www.sqlstyle.guide/> ("river" layout: root
  keywords right-aligned to a common column, content to its right). The tests
  in `PgNimbus.Core.Tests` assert exact spacing — a deliberate layout change
  must update them, and every layout must survive the formatter's token
  round-trip safety net. Text nesting deeper than `SqlFormatter.MaxNestingDepth`
  (64) parentheses is handed back as it is: indentation grows with depth, so the
  output grows with its square, and 100,000 nested subqueries threw from the
  StringBuilder on the Format gesture (2026-09, review of the audit fixes).

## NativeAOT constraints

The linux-x64 NativeAOT publish works and is the build to use for
startup-time claims (`dotnet publish src/PgNimbus.App -c Release -r linux-x64
-p:PublishAot=true`, ~100 ms launch-to-window vs ~700 ms JIT). Three
AOT-specific landmines are already handled in the codebase — keep them
that way: `SatelliteResourceLanguages=en` in the App csproj (a
culture-named satellite assembly + InvariantGlobalization crashes
Avalonia's asset resolver at startup under AOT, surfacing as a bogus
"avares://... not found"); no reflection binding paths (the results
grid binds columns via `RowIndexConverter`, not `"[i]"` indexer paths);
and **no reflection-based `System.Text.Json`** — AOT turns
`JsonSerializerIsReflectionEnabledByDefault` off, so any
`JsonSerializer.Serialize/Deserialize` overload that doesn't take a
`JsonTypeInfo`/`JsonSerializerContext` throws at runtime ("Reflection-based
serialization has been disabled for this application"). Every persisted store
uses a source-generated context (`AppSettingsJsonContext`,
`WorkspaceJsonContext`, …); everything that touches arbitrary user JSON —
`ResultExporter`'s JSON export, `JsonTree`, `ExplainService`, and the cell
inspector's Format/Minify (`Json/JsonText`, a `Utf8JsonReader` pass) — goes
through `JsonDocument`, `Utf8JsonReader` or `Utf8JsonWriter` by hand, which
needs no type model at all. That's enforced at build time:
`PgNimbus.Core` carries `IsAotCompatible`, and `PgNimbus.App` sets
`EnableTrimAnalyzer`/`EnableAotAnalyzer` directly (it's an exe, so
`IsAotCompatible`'s implied `IsTrimmable` doesn't fit), so an offending call
surfaces as IL2026/IL3050 on an ordinary `dotnet build` instead of as a crash
in a shipped release. Both projects are currently at zero IL warnings — keep
them there.

The Linux sandbox bootstrap (apt packages, Xvfb, a seeded Postgres, driving the UI) is in the `verify` skill.

## Headless screenshot harness and UI tests

Moved to [`.claude/rules/headless-tests.md`](rules/headless-tests.md), which loads when working on `tools/Screenshot`, `PgNimbus.App.Tests` or `scripts/screenshots`.

## Benchmarks, release pipeline, Store, website

Moved to [`.claude/rules/release-ci.md`](rules/release-ci.md), which loads when working on `.github/`, `scripts/`, `packaging/`, `website/`, `docs/` or `PgNimbus.Benchmarks`.
