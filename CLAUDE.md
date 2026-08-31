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
steps — update the corresponding section in the same commit/PR. Treat a
stale `CLAUDE.md` (e.g. it still saying "Avalonia 11" after an upgrade to
12) as a bug, not a nitpick: it's the first thing a fresh session reads,
and wrong project memory is worse than none.

## The sibling project, and what is shared with it

kubeNimbus (`X:\source\kubeNimbus`, normally checked out beside this repo) is the
same product for Kubernetes, and the two must look and behave like one family.
The shared half lives in **[`shared/nimbusUi`](shared/nimbusUi/)** — a git subtree
of [nimbusUi](https://github.com/Shman4ik/nimbusUi), referenced as an ordinary
`ProjectReference`:

- `Theme/Tokens.axaml` — the palette, radii, scrollbars, Fluent resource overrides.
- `Theme/Icons.axaml` — the MDI glyphs both apps draw.
- `Theme/Theme.axaml` — the shared style classes (`card`, `layer`, `chip`,
  `toolbar`, `searchpill`, `statusBar`, …).
- `Theme/Controls.axaml` — the Fluent **control** retheming: `TextBox`/`ComboBox`/
  `NumericUpDown` radius and brand text selection, `SelectableTextBlock`,
  `ListBox`/`ListBoxItem`/`TreeView`/`TreeViewItem` rounded rows, `DataGrid` soft
  rules, the `.soft` and `.soft.danger` button families, `ToggleButton.soft`, the
  chip checked/active washes, `TabControl`. **These moved out of
  `Styles/Theme.axaml`**, where they had been defined for pgNimbus alone: kubeNimbus
  had none of them and was drawing stock Fluent inputs, lists and grids next to
  these, which is what made the two apps stop looking like one family. Change them
  there, not here. What is left in this app's own `Styles/Theme.axaml` is `TabItem`,
  `TabControl.segmented` and the AvaloniaEdit completion/search themes — all
  genuinely pgNimbus's (see DESIGN.md's not-shared table).
- `Chrome/` — the one-bar window chrome and its drawn caption buttons.
- `Hotkeys.cs` — Ctrl/Cmd resolution; `PgNimbus.App.Hotkeys` forwards to it.
- **[`DESIGN.md`](shared/nimbusUi/DESIGN.md) — the UI rules, single source.**

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
   Auto-reconnect (2026-07): `QueryEngine` classifies a failure as connection
   loss (Postgres class-08 `SqlState`s / an admin or crash shutdown, or an
   `NpgsqlException` wrapping a socket/IO exception — deliberately not
   `TimeoutException`, which Npgsql also uses for command timeouts and pool
   exhaustion where a silent re-run could double-apply work) versus an
   ordinary statement error, and on loss flushes the whole pool before
   silently retrying once on a fresh connection — runs, single-statement
   edits, and pre-commit batches all get this; a script retries only its
   first statement, since session state from earlier statements can't be
   resurrected. A failure mid-stream (rows already delivered) or after a
   batch's `COMMIT` was attempted never retries. An explicit transaction is
   never silently re-established: a lost connection there clears
   `_transactionConnection` without sending `ROLLBACK` (no live socket to
   send it down) and returns a `QueryError` with `ConnectionLost`/`RolledBack`
   set, stating plainly that the transaction is gone and nothing from it
   committed.
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
   than being formatted ad hoc in the App. All three monitoring windows follow
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
   recursion). The tree's nodes auto-expand so the whole wait chain shows at a
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
   otherwise grow it all afternoon. (d) **It can publish**, through
   `pg_notify(@channel, @payload)` on a pooled connection (the listening one is
   parked in a wait, and `NOTIFY` takes literals rather than parameters). pgAdmin
   needs a second session to produce a test event; this is one button.
4. **No passwords on `ConnectionProfile`.** Passwords come from
   `ICredentialStore` (DPAPI on Windows via `WindowsDpapiCredentialStore`, a
   permission-restricted file fallback elsewhere via
   `PlainFileCredentialStore`) at connect time, never persisted on the
   profile record itself.
5. **Crashes are logged and shown, never silent.** Critical/unhandled errors
   append to a plain-text log at `<appdata>/pgNimbus/logs/pgnimbus.log`
   (`PgNimbus.Core.Diagnostics.CrashLog` does the file I/O — directory-injectable
   and unit-tested — with the process-wide `CrashLogger` static as the facade;
   1 MiB rolling to `pgnimbus.log.old`, every write swallows its own failure so
   logging a crash can never itself throw). The App wires three global hooks in
   `PgNimbus.App/Diagnostics/CrashReporter.cs`: `AppDomain.UnhandledException`
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
   including version + OS).
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
   one-liner and a conservative threshold constant. The App wraps them in
   `PlanWarningViewModel` (glyph + severity brush) for the warnings strip;
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
   unit-tested sibling of `PlanAnalyzer`, which also strips psql framing). The
   command palette's "Import query plan…" opens `ImportPlanDialog` and, on a
   successful parse, shows the plan in a **new tab**
   (`MainViewModel.OpenImportedPlan` → `QueryViewModel.ShowImportedPlan`) — same
   warnings strip and time-heat as a live plan. **Sharing back out**: the plan
   header's "Export ▾" flyout copies/saves the plan as JSON or rendered text —
   `ExplainService.ExplainAsync` returns an `ExplainRun` that keeps the raw
   server JSON, carried on `QueryViewModel.PlanJson` (null, and the JSON actions
   hidden, for a text import). **Re-color by metric**: the plan header (tree view)
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
   whole script failed at the second one ("syntax error at or near SET"). The design
   doc + competitive research is in
   [`docs/design/explain-improvements.md`](docs/design/explain-improvements.md).
7. **Permissions are answered, not dumped — and never applied behind the user's
   back.** `PgNimbus.Core/Security/` reads roles and ACLs, but the headline is
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
   `SecurityEditor` instead, are never shown, and `SecretRedactor` guards
   `SavedQueriesViewModel.RecordExecution`, the one choke point into
   `QueryHistoryStore`, for the case where a user types one by hand.
   `GrantScriptBuilder.BuildBulk` is deliberately more correct than pgAdmin's
   Grant Wizard: `GRANT USAGE ON SCHEMA` comes first (theirs skips it and the
   user still gets `permission denied`), revoke is a preset rather than an
   impossibility, and the matching `ALTER DEFAULT PRIVILEGES` is offered with
   the creating role named, since one set for the wrong creator silently does
   nothing. `RoleScriptBuilder.Drop` emits the whole `REASSIGN OWNED` →
   `DROP OWNED` → `DROP ROLE` recipe with its "current database only" caveat —
   the answer to 2BP01, which Postgres reports without naming either the
   blocking objects or the fix. The research and the plan are in
   [`docs/design/accounts-permissions.md`](docs/design/accounts-permissions.md).

## UI design rules

> Several of these are shared with kubeNimbus, and their canonical statement is in
> [`shared/nimbusUi/DESIGN.md`](shared/nimbusUi/DESIGN.md): minimalism (1),
> double-click as the default action (2), never overwriting the active tab (3),
> drag-reorderable tabs (4), the Ctrl/Cmd resolver (5), and the merged title bar
> (DESIGN.md rule 9, adopted on Windows in the same change that created that
> file). What is kept below is the pgNimbus-specific evidence behind each — the
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
   18-item Create submenu; pgNimbus's schema menu is six — New table…, Copy
   name, Refresh, Exclude from autocomplete, Drop schema…, Drop schema
   (cascade)… — and each earns its place the same way a toolbar button would.
   "New table…" is deliberately a `CREATE TABLE` template opened in a new tab
   (`Schema/DdlTemplates`, Core-pure and unit-tested), not a dialog: a form that
   can only express the column types a combo box lists is a worse tool than the
   statement itself, sitting in the editor where it can be edited and run. The
   two Drops go through `SchemaEditor.DropSchemaAsync` behind the shared
   `ConfirmDialog`; the plain one is Postgres's own RESTRICT (it fails on a
   non-empty schema, and that refusal lands in the sidebar's error strip), and
   CASCADE is a separate item with a confirm that says what it takes with it.
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
   (otherwise the schema flashes open with every table it owns); and a
   *loaded* schema is still judged by its own children rather than the
   snapshot, which is the fresher of the two.

   **Completion ranks by what can legally be typed at the caret**, and the
   statement-start caret is its own context (2026-08):
   `SqlCompletionContext.IsAtStatementStart` (Core-pure, unit-tested) is true
   when nothing but whitespace, comments and the previous statement's `;`
   precedes the word being typed, and `SqlCompletionProvider` then prepends its
   `StatementStartKeywords` at a priority band above everything else, each
   ranked by its position in that list. Without it, typing `se` in an empty
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
   modes, 2026-07). Apply the same rule to any new list-like UI.
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
   empty rather than mirroring the defaults into it.
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
3. **Loading a query never overwrites the active tab.** Saved queries,
   history entries, and generated DDL all open in a *new* tab.
4. **Tabs drag-reorder; the ☰ app menu is the file-command home.** The query
   tab strip reorders by dragging (live, browser-style — pointer handlers in
   `MainWindow.axaml.cs`; the order persists via the workspace snapshot, which
   serializes `Tabs` in collection order). Right-clicking a tab opens a
   five-item flyout — Save query… / Rename… / Close / Close others / Close to
   the right —
   built and shown from `MainWindow.OnTabStripContextRequested` (code, not XAML: the
   handler has to resolve the clicked `ListBoxItem` and re-target the menu
   before it opens, and a `ContextFlyout` on the strip would also fire on its
   empty space). Deliberately save plus rename plus the close family, and of the
   closes
   only the two verbs a tab bar can't express by pointing at one tab — the strip's own ✕, its ▾
   finder and drag-reorder cover the rest. **Save query… is first, and it is
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
   binding sees a null `ActiveTab`.
   **A tab's name is either a label or an override, and the difference is the
   bug this fixed**: `QueryViewModel.TitleOverride` is for names a *person*
   chose (the backing file, a saved query, a rename) and survives every later
   edit; `DefaultTitle` is the fallback an app-assigned label uses (browse's
   table name, `x · source`, `s · new table`, "Imported plan"), so it yields
   to the SQL-derived name the moment the buffer says something else. Browsing
   `customers` and then typing a query against `products` used to leave the
   tab named `customers` forever, because the label had been written as an
   override. Only `TitleOverride` rides the workspace snapshot.
   The ☰ button (top-left, 2026-07) opens the one discoverable menu for file/tab-level commands: New tab,
   Open .sql / Open recent, Save / Save as / Save query to Saved Queries /
   Save tab to a .sql file, Close tab, Switch connection,
   New window, Preferences, **Keyboard shortcuts and About pgNimbus**. Those last
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
   like a prompt. Its name-collision check is case-insensitive on purpose: the
   list is read by eye, so "Daily report" and "daily report" as two rows is the
   duplicate bug wearing a different hat.

5. **One command catalog, no hardcoded gestures.** Every command and every
   documented keyboard gesture is declared exactly once, in the Core-pure,
   unit-tested `PgNimbus.Core.Commands.CommandCatalog` (a read-only sibling of
   `Json/JsonTree` and `Monitoring/BlockingTree`): id, palette title, cheat-sheet
   title, category, glyph, `Chord`, and which surfaces it appears on. Chords are
   abstract — `ChordModifiers.Command` renders as Ctrl or Cmd per platform or the
   persisted scheme preference; `ChordModifiers.Control` is a literal Ctrl for the
   one deliberate exception, completion's Ctrl+Space (Cmd+Space is Spotlight).
   Everything downstream is a *projection* of that list, never a second copy:
   `MainWindow.BuildKeyBindings` loops the `WindowBinding` entries,
   `MainViewModel.BuildActionItems` loops the `Palette` entries, the F1 window
   renders `ShortcutsViewModel` (which loops the `CheatSheet` entries — that XAML
   authors no rows), the macOS native menu keeps its hand-curated structure but
   takes each item's gesture *and* command by `CommandId`, and
   [`docs/reference/keyboard-shortcuts.md`](docs/reference/keyboard-shortcuts.md)
   is generated by `ShortcutDocs.ToMarkdown()` with a golden-file test
   (regenerate with `PGNIMBUS_UPDATE_DOCS=1 dotnet run --project
   PgNimbus.Core.Tests`). Adding a shortcut is therefore **one catalog entry**
   plus a resolver line in `PgNimbus.App/Commands/CommandBindings.cs` — the
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
   property in `PgNimbus.App/Commands/CommandTip.cs` renders "text (Ctrl+Enter)",
   re-rendering itself on a scheme change — never type a gesture into a
   `ToolTip.Tip` string. `CommandTip.Command` is deliberately `CommandId?`:
   the enum's zero value is a real command (`Run`), so a non-nullable property
   reads a set of `Run` as "no change", raises nothing, and silently drops the
   chord. Where the gestures differ per menu item (the Explain flyout), set
   `MenuItem.InputGesture` from `CommandBindings.GestureFor` instead of listing
   both in one tooltip. Two documented exceptions:
   Ctrl/Cmd+1…9 (`CommandId.GoToTabByNumber`) is bound in a loop because nine
   near-identical palette rows would be noise, and Format SQL deliberately also
   accepts Alt+Shift+F whatever the scheme. User-facing settings live on the
   preferences page (`PreferencesWindow`, opened from the palette), persisted in
   `AppSettings`.
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
   and Database Overview's tabs); the bare global `TabItem` style is the
   *vertical* left-nav look and must stay untouched. Its header line also
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
   `UserControl` per responsibility, never a god-view. `ResultsGridPanel` is
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
   canonical text is [`DESIGN.md`](shared/nimbusUi/DESIGN.md) rule 13. Three windows
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
   are deliberately untouched. `ConnectionDialog` and
   `CrashWindow` cannot be overlays at all — both exist before, or instead of, a main
   window. The modal dialogs (`ConfirmDialog`, `AddRowDialog`, `AlterTableDialog`,
   `ImportDialog`, `ImportPlanDialog`, `PendingChangesDialog`) each return a result
   through `ShowDialog`, which an overlay would have to re-express as an awaited
   completion; that is a real change to six call sites and has not been made.
   The command palette and the cell inspector are also **not** OverlayPanels, and for
   a better reason than inertia: both are focus-driven surfaces with their own
   keyboard model, not panels you read.

## Platform window chrome

- **The command bar IS the title bar, on Windows and macOS.**
  `MainWindow.SetUpTitleBar()` calls `NimbusWindowChrome.Attach` (shared with
  kubeNimbus — `shared/nimbusUi/Chrome/`, and DESIGN.md rule 9 states the four
  platform traps, three of which fail silently). Linux keeps its system
  decorations deliberately.

  This replaced a **macOS-only** version that hand-rolled the drag from
  `BeginMoveDrag` plus a `ClickCount == 2` zoom: that reproduced two of the four
  gestures a title bar owes the user (drag, double-click-maximize) and lost the
  right-click window menu and Win11 Snap Layouts, all four of which now come from
  the OS via `WindowDecorationProperties.ElementRole="TitleBar"` on the bar. It
  also returned early on Windows, so Windows carried two bars until 2026-08.
  On Windows the caption buttons are now **ours to draw** — Avalonia 12's Win32
  backend disables the system ones under an extended client area — from the
  `CommandBarWindowDecorations` theme in `shared/nimbusUi/Chrome/Decorations.axaml`.

  The "pgNimbus" wordmark is gone on every platform (rule 9), not just macOS. The
  ☰ button is still hidden on macOS only, and that is not chrome: the native menu
  bar (`BuildMacNativeMenu`) is the file-command home there, so it would be a
  second copy of the same commands. The sidebar toggle icon is platform-picked
  via `{OnPlatform}` (SF-style geometry on macOS).
- **The connected window opens in the display mode the connect form was left
  in (2026-08).** `App.CarryWindowState`, called from the dialog's `Connected`
  handler before `Show()`. Connecting reads as one continuous act — the form is
  the app's first screen, not a separate program — so a full-screen (macOS
  green button) or maximized dialog handing off to a small window on the
  desktop behind it reads as the app losing the user's place. It only ever
  *promotes*: a normal-state dialog leaves the window on its own restored
  placement (`WindowPlacementPersistence`, which may itself be maximized).
  macOS enters full screen through an animated Space transition that a window
  which has not been shown yet can drop, so the state is re-asserted once from
  `Opened`.
- **macOS: closing the last window does not quit the app (2026-08).** Closing a
  window and quitting are two separate actions there, and the app that exits
  when its last window closes is the one Mac users report as a bug. So
  `App.KeepRunningWithNoWindowsOnMac` sets `ShutdownMode.OnExplicitShutdown` on
  macOS only — Windows and Linux keep Avalonia's default `OnLastWindowClose`,
  where a windowless background app would read as "close did nothing" — and
  subscribes to `IActivatableLifetime.Activated` (via
  `Application.TryGetFeature`) for `ActivationKind.Reopen`, the Dock-icon click.
  Reopen raises an existing window if there is one, and otherwise builds a fresh
  connection dialog: the closed `MainWindow`'s `Closed` handler already disposed
  its data source and SSH tunnel, so there is nothing to resurrect. What still
  quits, and why `OnExplicitShutdown` is safe: Cmd+Q and the app menu's Quit
  arrive as a platform shutdown request, which
  `ClassicDesktopStyleApplicationLifetime` routes straight to `DoShutdown`
  without consulting `ShutdownMode`, as does the `Shutdown()` that
  `CrashReporter` and `StartupProbe` call directly.
- **macOS: the app ends its own process, and must (2026-08).** Shipped 0.7.5
  aborted with SIGABRT on every quit, *after* the shutdown had already run
  cleanly (windows closed, workspace and placement saved). AppKit's
  `-[NSApplication terminate:]` asks Avalonia's delegate first — that is the
  whole managed shutdown, answering `NSTerminateNow` — and then calls C's
  `exit()`, which runs libAvaloniaNative's C++ static destructors. One of them
  releases a `ComPtr<IAvnDispatcher>` whose vtable is a managed MicroCom proxy,
  so `__cxa_finalize` reverse-P/Invokes into managed code on a main thread whose
  NativeAOT runtime state is already torn down: a `RhFailFast`, not a catchable
  exception (`ThreadStore::AttachCurrentThread` → "Attempt to execute managed
  code after the .NET runtime thread state has been destroyed";
  AvaloniaUI/Avalonia#12459). No frame in that trace is ours, so the fix is to
  never reach `__cxa_finalize`: `MacShutdown.ExitProcessOnShutdown` hooks the
  lifetime's `Exit` event — raised after every window has closed and only when
  the shutdown really goes through — and calls libc `_exit(2)`, which skips
  atexit handlers and static destructors entirely. Consequences to keep in mind:
  an `Exit` handler registered after that one never runs, nothing `Program.Main`
  would do on the way out runs either, and `_exit` flushes nothing — hence the
  explicit `Console` flush, without which `StartupProbe`'s single line (the
  release smoke gate) can be lost. `Environment.Exit` is not a substitute: it
  runs the very `exit()` teardown this avoids. macOS-only; Windows and Linux
  exit through their own teardown cleanly.
- **Windows** — every remaining window still calls `ThemedWindowChrome.Attach(this)`
  for the **icon** (details in the icon section below). Its caption-colour half is
  moot on `MainWindow`, whose caption is ours, and still applies to the dialogs and
  the two reference windows. kubeNimbus deleted its copy outright once its last two
  secondary windows became overlays; ours stays because the connection dialog and the
  crash reporter exist *before* or *instead of* a main window and can never be one.
- **macOS native menu bar (2026-07)** — two layers. App-level (`App.axaml`,
  needs `Name="pgNimbus"` or Avalonia shows "Avalonia Application"): About
  pgNimbus, pgNimbus on GitHub, and Settings… (Cmd+,). The first and last both
  route to the *active* MainWindow's view model through
  `App.ActiveMainViewModel()`, because both are overlays on a window now rather
  than free-standing boxes. **About falls back to the connection dialog** when
  there is no main window yet (2026-08): `ConnectionDialog` hosts its own
  `AboutView` overlay against `ConnectionDialogViewModel.IsAboutOpen`, because
  the connect form is the app's first screen and often its only one — with the
  ☰ menu absent there and no window for the overlay to land on, the menu item
  used to do nothing exactly where a Mac user is most likely to reach for it.
  One consequence to keep: `ConnectAsync` returns early while that overlay is
  open, since the profiles list binds Enter to Connect and the Connect button is
  the window's `IsDefault`, so Escape-the-overlay's sibling gesture would
  otherwise connect instead of dismissing. Settings… keeps the no-op — the
  preferences page hangs off a connected window's view model, so there is
  nothing for it to show. Window-level:
  `MainWindow.BuildMacNativeMenu()` builds File / Query / View / Window via
  `NativeMenu.SetMenu`, rebuilt from `BuildKeyBindings` so gestures track
  the live Ctrl/Cmd scheme. Landmines, all learned the hard way: (a) menu
  items use `Click` + a CanExecute check, **not** `NativeMenuItem.Command` —
  the exporter snapshots enabled-state from `CanExecute` at assignment time
  (before the DataContext exists), and a wrapper that never raises
  `CanExecuteChanged` leaves every item permanently grayed out; (b) there is
  deliberately **no Help menu** — AppKit force-inserts a search field into
  any menu named "Help" (searching a help book the app doesn't have), so
  Keyboard Shortcuts lives in View and the GitHub link in the app menu;
  (c) don't add an "Enter Full Screen" item — AppKit appends its own to the
  menu titled "View"; (d) the File → Open Recent submenu rebuilds on the
  menu's `NeedsUpdate`, same contract as the ☰ menu's, and View's
  Show/Hide Sidebar header re-resolves the same way.
- **Results-grid columns resize by dragging, and the drag lifts the auto-width
  cap.** Every generated column is `Width=Auto` with `MaxWidth=AutoWidthCap`
  (560), so one long value can't blow a column past the viewport — but
  `DataGridColumnHeader` clamps *every* step of a resize drag to the column's
  `ActualMaxWidth` too, so with the cap left on, widening a capped column stops
  dead with nothing on screen saying why. `ResultsGridPanel` therefore lifts the
  cap for the column a press is about to resize. Three things make that work:
  the handler is **tunneled** (the header marks a resize press handled before it
  bubbles, so `DataGridColumn.HeaderPointerPressed` — an ordinary bubbling
  subscription — never fires for the very presses that matter); it repeats the
  grid's own 5px-edge test to decide *which* column the press resizes (the right
  grip resizes this column, the left one its neighbour); and it pins
  `Width = ActualWidth` **before** raising `MaxWidth`, because
  `DataGrid.OnColumnMaxWidthChanged` re-expands a column sitting exactly at its
  cap to the full width its content wants — without the pin the column jumps on
  mouse-down, before the drag. The header→column mapping rides on the header
  content's `Tag` (`DataGridColumnHeader.OwningColumn` is internal). Dragged
  widths are handed back to the tab in `QueryViewModel.ColumnWidths`, keyed by
  column name, because the grid is window-central and rebuilds its columns from
  scratch on every re-run, page turn, `EditContext` arrival and tab switch;
  only dragged columns are saved, so an untouched column keeps growing with the
  values that scroll into view.
- **Results-grid scrolling is the DataGrid's own.** Avalonia 12's DataGrid
  handles both wheel axes natively (`UpdateScroll`). Don't reintroduce a
  tunneled wheel handler that writes `ScrollBar.Value` directly — the
  DataGrid only reacts to user `Scroll` events, so that moves the bar
  without the content (the 2026-07 macOS "scrollbar moves, results don't"
  bug, since removed).

## App icon / logo assets

Full reference: [`design/LOGO-ASSETS.md`](design/LOGO-ASSETS.md); the
designer hand-off brief is [`design/DESIGNER-BRIEF.md`](design/DESIGNER-BRIEF.md).
**Keep both current** when assets or the pipeline change.

**One drawing feeds everything** (2026-08). `design/logo.af` is where the mark
is drawn; `design/logo.svg` + `logo-dark.svg` are generated from it; every
raster below is generated from those. Nothing in `design/masters/**` or
`PgNimbus.App/Assets/**` is hand-edited any more — regenerate, don't retouch.
The chain, each step a script:

```
design/logo.af                     Affinity, the editable master
  → scripts/design/dump-af.js      geometry out to JSON (run via the Affinity MCP)
  → scripts/design/af-to-svg.py    design/logo.svg + logo-dark.svg
  → scripts/design/make-masters.ps1        design/masters/**
  → scripts/windows/make-app-icons.ps1     PgNimbus.App/Assets/**
  → scripts/windows/make-store-logos.ps1   design/store/**
```

What this replaced: masters that were **hand-drawn per size**, because the mark
was a traced raster whose downscale turned to mud below 32px. The modular
vector master rasterises cleanly, so the six icon tiles are now six renders of
one file rather than six drawings that drift apart. If a size ever does stop
reading, the fix is a simplified *mark* fed into `make-masters.ps1` (a
`logo-small.svg`, the way kubeNimbus does it) — never a hand-painted PNG that
nothing can regenerate. Layout:

- `design/masters/icon/icon-{16,24,32,48,256,1024}.png` — the app tile, every
  size rendered from `logo.svg`. All of them keep the plate: these feed
  `app.ico`, which Windows hands the taskbar, Alt+Tab and the title bar
  through one `WM_SETICON` slot, so it cannot be theme-aware, and unplated
  dark line art vanishes on a dark taskbar. The corners outside the plate are
  transparent, which is fine — what must not be transparent is the middle.
- `design/masters/window/window-{light,dark}-256.png` — the same mark with the
  full-bleed plate stripped (`make-masters.ps1` matches the `<circle>` on
  `r="512"`), leaving two-tone line art on transparency. The name is the theme
  the icon is used *on*, not the colour it is drawn in, so `window-light` is
  cut from `logo-dark.svg`. Currently unused in-app, see Part 2 of
  LOGO-ASSETS.md.
- **`design/logo.svg` + `design/logo-dark.svg` — the committed vector master**,
  generated from the `.af` and never hand-edited (`af-to-svg.py` overwrites
  them). `viewBox="0 0
  1024 1024"`; three modules (`#base`, `#mascot-elephant`, `#brand-broom`) as
  plain `<path>` geometry in the root coordinate system — no `transform`, no
  `mask`, no `<use>`, no CSS variables — which is what makes it survive
  Inkscape / Illustrator / Figma and what lets a module be lifted whole into a
  sibling mark. Colour is two classes, `.ink` and `.paper`, with the value
  repeated as a plain attribute so tools that ignore `<style>` still render;
  `logo-dark.svg` is the same bytes with the two exchanged. Two rules hold it
  together. **Nothing changes colour where it crosses the field's rim**: the
  broom's handle and the tip of the trunk both carry on past the light field
  onto the plate and stay ink the whole way, carried by a `.paper` clearance
  halo drawn underneath — the raster-era master flipped them to white instead,
  which is the same drawing but a different object every time the rim crosses
  it. And **each module carries its own clearance**, so hiding `#base` leaves a
  whole elephant and a whole broom rather than a heap of fragments. That halo
  is 39.451 in both modules, the width kubeNimbus's broom already used: at the
  trunk it has to *fill* the hollow between the trunk's two walls out on the
  plate, not merely outline them, or the trunk ends with a black wedge inside
  it. What this replaced is `design/archive/logo-raster-era.svg` — one compound
  path with seven subpaths, in which neither the elephant nor the broom was an
  object: both were white showing through a solid ink disc, so hiding the disc
  left nothing.
  **The `.af` mirrors this structure exactly** — same three groups, same
  clearance subgroups, one node per `<path>` — which is what lets `af-to-svg.py`
  be a transcription rather than an interpretation. Keep it that way: a node
  renamed or regrouped in Affinity changes the generated SVG's ids, and those
  ids are load-bearing (`make-masters.ps1` finds the plate by radius, kubeNimbus
  lifts `#brand-broom` by id).
- `design/masters/logo/` — README/website assets: `logo-{light,dark}.png`
  (the mark at 1024 on transparency), `wordmark-{light,dark}.{svg,png}` (the
  mark at 240px beside "pgNimbus" in Segoe UI Bold, text baked to paths by
  Inkscape so it renders on a machine without that font), and
  `social-preview.png` (1280×640, the wordmark on a solid ink card — solid
  because a transparent one renders white in some clients and black in
  others). All five come out of `make-masters.ps1`.
- `design/store/` — **generated**, not hand-edited: Microsoft Partner Center
  listing images from `icon-1024.png`, via
  `scripts/windows/make-store-logos.ps1`. Checked into git so a Partner
  Center re-upload doesn't depend on someone remembering to run the script.
- `design/archive/` — superseded concepts (old `icon-tile.png`, `simple/`, …).

Everything in `PgNimbus.App/Assets/` is **generated** by
`scripts/windows/make-app-icons.ps1` (Windows-only, System.Drawing) —
regenerate via that script, don't hand-edit. Output filenames are stable so
csproj / WiX / MSIX manifest reference them unchanged:

- `app.ico` — 16–256px multi-size tile; the exe (`ApplicationIcon`), the MSI
  icon, *and* the runtime window icon. Windows don't set `Icon` in XAML;
  each window calls `ThemedWindowChrome.Attach(this)` in its constructor,
  which sets `Window.Icon` to this plated tile *and* sends `WM_SETICON`
  directly via P/Invoke (built from the same `.ico` bytes) because
  Avalonia's `Window.Icon` reliably updates the title bar but not the
  Windows 11 taskbar button (a known Avalonia/Win32 gap). One plated icon
  for every surface on purpose: the title bar, taskbar and Alt+Tab all read
  the same `WM_SETICON` slots (they cannot diverge), Avalonia re-asserts
  `Window.Icon` on its own schedule (racing any divergent native icon back),
  and theme-swapped transparent line art was unreadable on the
  (almost always dark) taskbar whenever the app ran the light theme.
- `window-icon-light.ico` / `window-icon-dark.ico` — theme-tinted
  transparent line-art icons (16/24/32/48/256, all PNG entries) built from
  the `window/` masters. **Not consumed in-app anymore** — superseded by the
  plated `app.ico` window icon above (2026-07); still generated in case
  transparent themed line art is wanted later.
- `Assets/Msix/*` — MSIX tiles, packaging-time-only. Each of
  `Square44x44Logo`/`Square150x150Logo`/`StoreLogo` ships as
  `.scale-{100,125,150,200,400}.png` (not one flat file — Windows will
  backplate/blur a lone unqualified asset when a surface asks for a size it
  doesn't have), plus `Square44x44Logo.targetsize-{16,24,32,48,256}_altform-
  {unplated,lightunplated}.png` (transparent, reused from the `window/`
  masters) for the taskbar/Start/Alt+Tab/install-dialog surfaces that expect
  an unplated icon. `build-msix.ps1` compiles these into `resources.pri` via
  `makepri` — see "Microsoft Store (MSIX)" below; the qualified filenames do
  nothing on their own without that resource index.

## Tech stack

- `net10.0` for all projects.
- Core: `Npgsql`, `System.Security.Cryptography.ProtectedData`, `SSH.NET`.
- App: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`,
  `Avalonia.Fonts.Inter`, `Avalonia.Controls.DataGrid`, `Avalonia.AvaloniaEdit`,
  `CommunityToolkit.Mvvm`, `AvaloniaUI.DiagnosticsSupport` (DevTools/MCP —
  Debug-only, wired via `.WithDeveloperTools()` in `Program.cs`, see below).
- Tests: `PgNimbus.Core.Tests` — TUnit on Microsoft.Testing.Platform. Run
  `dotnet test --project PgNimbus.Core.Tests` (MTP mode comes from the
  `test.runner` opt-in in the repo-root `global.json`) or plain
  `dotnet run --project PgNimbus.Core.Tests`. Never add
  `Microsoft.NET.Test.Sdk` to a TUnit project — it breaks test discovery.
- UI tests: `PgNimbus.App.Tests` — same platform, plus `Avalonia.Headless`.
  Real windows, real key input, no display and no Postgres; see "Headless UI
  tests" below.
- Benchmarks: `PgNimbus.Benchmarks` — a plain console project (Core-only, no
  UI deps) measuring the query engine through its streaming API; see
  "Benchmarks pipeline" below.
- `AvaloniaUseCompiledBindingsByDefault` is on — don't add uncompiled
  (reflection) bindings.

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
  kinds** (`MainWindow.IsFreeTextEditor`: `Text`/`Array`/`Composite`/`Json`/
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
  json is unusable in a one-line inline editor; `MainWindow.OnResultsGridBeginningEdit`
  cancels the grid's own inline edit for that gesture. Other editable types keep
  their fast inline double-click; the inspector's Edit tab is reached via Space /
  "Inspect cell…". Completion carries the jsonb function
  family (`SqlCompletionProvider.Functions`); JSON operators (`->`, `@>`, `?`,
  `@?`, …) are punctuation, out of the identifier-triggered completion model.
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
  precise error). `money` and `uuid` deliberately stay `Text` (they round-trip
  through decimal/Guid). The value shown in the grid must itself be a valid input
  literal for the cast to accept the round-trip, so `RowIndexConverter` formats
  the CLR types whose `ToString` is useless: `byte[]`→`\x`-hex (capped preview),
  `Array`→Postgres `{…}` literal (`PgValueSyntax.FormatArray`), and
  `BitArray`→bit string (`10110001`, MSB first). Known edge: a `bit(1)` column
  surfaces from Npgsql as `bool` (displays `True`/`False`), so an inline edit of
  it fails loudly at the cast rather than corrupting — the inspector or a `bit(n)`
  column edits cleanly.
- **A type Npgsql can't materialize must never fail a whole result set.** An
  unmapped composite (or an array/domain/range over one), an extension type with
  no plugin loaded (pgvector, PostGIS), `bit`/`hstore` whose CLR mapping has a
  useless `ToString` — all of them make `GetValue` throw *"Reading as
  'System.Object' is not supported for fields having DataTypeName …"*, and
  `GetFieldType` throws it too, before the first row is even read. `QueryEngine`
  answers in two layers, both required:
  1. **Text-format re-execution** (`BuildTextFallbackMask` →
     `NpgsqlCommand.UnknownResultTypeList`) re-requests just those columns as
     Postgres literals (`("246 Oak St",Milan,MI,20918,IT)`) — the shape the grid
     shows and the composite editor casts back on edit. It costs a second
     execution, so it's gated on `MayReExecute`: either the caller vouched
     (`allowTextFallback: true`, only for app-composed browse SELECTs) or
     `SqlStatementInspector.IsSafeToReExecute` proves it lexically — a read-shaped
     leading keyword, no data-modifying CTE, no `SELECT … INTO`, no
     side-effecting function call (`nextval`, advisory locks, `dblink*`, …), and a
     single statement (the simple query protocol would happily re-run
     `SELECT 1; DROP TABLE t`). Deliberately conservative: a false negative costs
     a placeholder, a false positive applies a side effect twice. Scripts and
     transaction statements are vetted per statement this way and never vouch.
  2. **The per-cell guard** (`QueryEngine.ReadValue` / `FieldType`) catches the
     `InvalidCastException`/`NotSupportedException` for everything layer 1 refuses
     and yields `QueryEngine.UnreadableCell(dataTypeName)` —
     `<unreadable commerce.address>` — so the rest of the row still renders. Only
     those two exception types are caught; a dropped connection mid-row must stay
     an error. Integration coverage is `QueryEngineCompositeTests` (gated on
     `PGNIMBUS_TEST_CONN` like the reconnect tests).
- `SqlFormatter` follows <https://www.sqlstyle.guide/> ("river" layout: root
  keywords right-aligned to a common column, content to its right). The tests
  in `PgNimbus.Core.Tests` assert exact spacing — a deliberate layout change
  must update them, and every layout must survive the formatter's token
  round-trip safety net.

## Avalonia DevTools MCP

The app exposes its live visual tree / runtime state to an MCP client (Claude
Code, VS, Rider) via the Avalonia DevTools MCP server. Two pieces make it work:

1. **In the app** — `AvaloniaUI.DiagnosticsSupport` is referenced and
   `.WithDeveloperTools()` is on the `AppBuilder` in `Program.cs`. Without
   this, a running app can't be discovered by the MCP server. Both are
   **Debug-only** (a `Condition` on the `PackageReference`, `#if DEBUG`
   around the call): the package is part of AvaloniaUI's commercial
   Developer Tools and ships no explicit redistribution license, so it must
   not be linked into public Release/AOT binaries. Consequence: MCP
   inspection only works against a Debug build — `dotnet run` (default
   Debug) is fine, a `-c Release` or published AOT binary won't be
   discoverable.
2. **The MCP server** — the `avdt` global .NET tool runs as `avdt mcp`.
   Register it once at user scope; it reads its license from the
   `AVALONIA_TOOLS_LICENSE_KEY` env var (`ACCELERATE_LICENSE_KEY` on
   Avalonia 11.x and earlier):

   ```bash
   claude mcp add --scope user avalonia_devtools \
     -e AVALONIA_TOOLS_LICENSE_KEY=<key> -- avdt mcp
   claude mcp list   # avalonia_devtools: avdt mcp - ✓ Connected
   ```

   The server only sees the app while it's running, so launch the app before
   asking the MCP to inspect it. Docs:
   https://docs.avaloniaui.net/tools/developer-tools/mcp

## Bootstrapping a fresh Linux/CI sandbox (no .NET, no display, no Postgres)

A bare container has none of this preinstalled. All of it installs cleanly
via `apt-get` (no external downloads needed — `dotnet-install.sh` /
`dot.net` are typically blocked by sandboxed network policies, but the
Ubuntu `dotnet-sdk-10.0` apt package works and is the reliable path):

```bash
apt-get update -qq
apt-get install -y dotnet-sdk-10.0          # build/run the app
apt-get install -y xvfb imagemagick xdotool # headless display + screenshot + input
apt-get install -y postgresql               # a real DB to click through, not just mocks
apt-get install -y clang zlib1g-dev         # only for NativeAOT publish (linux-x64)
```

The linux-x64 NativeAOT publish works and is the build to use for
startup-time claims (`dotnet publish PgNimbus.App -c Release -r linux-x64
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
inspector's Format/Minify — goes through `JsonDocument` + `Utf8JsonWriter`
by hand, which needs no type model at all. That's enforced at build time:
`PgNimbus.Core` carries `IsAotCompatible`, and `PgNimbus.App` sets
`EnableTrimAnalyzer`/`EnableAotAnalyzer` directly (it's an exe, so
`IsAotCompatible`'s implied `IsTrimmable` doesn't fit), so an offending call
surfaces as IL2026/IL3050 on an ordinary `dotnet build` instead of as a crash
in a shipped release. Both projects are currently at zero IL warnings — keep
them there.

Then, to actually see and drive the UI:

```bash
# 1. A virtual display, once per sandbox lifetime:
Xvfb :99 -screen 0 1280x800x24 &

# 2. A local Postgres with seed data:
service postgresql start
su - postgres -c "psql -c \"ALTER USER postgres PASSWORD 'postgres';\""
su - postgres -c "createdb demo"
PGPASSWORD=postgres psql -h localhost -U postgres -d demo -c "CREATE TABLE ..."

# 3. Build once, then run against DISPLAY=:99. Set PGNIMBUS_CONN so the
#    app opens straight to MainWindow instead of the connection dialog —
#    App.axaml.cs reads this env var and skips ConnectionDialog entirely.
#    Any format ConnectionStringParser understands works here (postgres://
#    URI, JDBC, Key=Value;, libpq keywords, psql command line):
dotnet build
DISPLAY=:99 PGNIMBUS_CONN="Host=localhost;Port=5432;Database=demo;Username=postgres;Password=postgres" \
    timeout 15 dotnet run --project PgNimbus.App --no-build &

# 4. Drive it (optional) and capture a screenshot:
DISPLAY=:99 xdotool mousemove <x> <y> click 1   # click/expand/select
DISPLAY=:99 xdotool key ctrl+a; xdotool type "SELECT * FROM t;"
DISPLAY=:99 import -window root screenshot.png  # ImageMagick, captures the whole root window
```

Notes:
- `dotnet run` under `timeout` is normal — the app has no natural exit, so
  screenshot then let the timeout reap it.
- Test both themes by toggling `RequestedThemeVariant` in `App.axaml`
  (`Default`/`Dark`) between runs — revert it before committing.
- This is how the Avalonia 11→12 upgrade and the PowerToys-style UI polish
  were actually verified (not just built) in a Claude Code sandbox with no
  prior .NET/GUI tooling.
- For a *visual* check the live sandbox above is the heavy path — prefer the
  headless harness below, which needs no display, no input tool and no
  database. The live path is still the one for anything interactive
  (completion popups, drag-reorder, real catalog shapes).

## Headless screenshot harness (`tools/Screenshot`)

Renders the real Views bound to fixture ViewModels through `Avalonia.Headless`
(Skia software rendering, `UseHeadlessDrawing = false`) and writes PNGs — one
`<scenario>.<light|dark>.png` per scenario × theme. Works on Windows, Linux and
CI alike, with no display, no Xvfb/xdotool, and **no Postgres**:

```bash
dotnet run --project tools/Screenshot -- <outputDir> [scenario-substring]
                                        [--baseline <dir>] [--publish <repo-root>]
```

Pass a scratch directory — nothing it writes is committed. Omit the filter to
render every scenario in `Scenarios.All` (the single list: `Program` walks it,
the baseline set is exactly its names × {light,dark}, and `Marketing` picks its
sources out of it by name).

The harness wears three hats, and the second and third were added because a PNG
artifact nobody opens is not a check:

1. **Smoke** — a view that throws while loading, or renders no frame at all,
   fails the run.
2. **Visual regression** (`--baseline`) — each frame is compared against the
   committed baseline in `tools/Screenshot/baselines/`, and anything past
   tolerance fails the run and leaves a `*.diff.png` (baseline desaturated,
   changed pixels magenta) behind. `ci.yml` runs it this way on every PR.
   **Baselines are OS-specific**: two renders of one commit on the same OS are
   bit-identical, while the same frames on Windows vs Linux differ by 0.6–6%
   from glyph rasterization alone — hence the 0.1%-of-pixels threshold, and
   hence `scripts/screenshots/update-baselines.sh` reaching for the .NET SDK
   container (plus `libfontconfig1`, which Skia links against and the image
   lacks) when it isn't already on Linux. The `screenshots.yml` workflow does
   the same on a real runner and opens a PR. A missing baseline is reported
   `NEW` and doesn't fail — a developer adding a scenario can't render a Linux
   baseline without Docker, and blocking that would only teach people to skip
   the check.
3. **Publishing** (`--publish`) — `Marketing.cs` maps scenarios to the images
   that face users: `docs/screenshots/` (README + docs site) and
   `design/store/screenshots/` (Store listing, padded to the Store's 1366×768
   minimum on a backdrop sampled from the shot's own chrome so it matches its
   theme). Run `scripts/screenshots/update-published.sh` before a release.
   These used to be hand-captured against a live database, which made them go
   stale silently and leaked real detail — the old main-window shot published a
   live Neon hostname. The README's animated GIFs are deliberately **not**
   covered: they show motion and are still recorded by hand.

Full rationale, thresholds and the weekly-release loop:
[`docs/design/release-checks.md`](docs/design/release-checks.md).

How the fixtures work, and why they're shaped this way:

- **The data source is offline but unroutable.** Every service takes an
  `NpgsqlDataSource`, and `NpgsqlDataSource.Create` opens no socket, so the whole
  graph constructs with no server. `Fixtures` points it at TEST-NET-3
  (`203.0.113.1`) rather than a closed local port on purpose: windows that
  refresh when they open (activity, database overview, schema tree) would get a
  fast connection-refused back from a closed port and overwrite the seeded status
  line with an error a fraction of a second after the window shows. An address
  that never answers leaves the seeded state alone.
- **Scenarios drive the public ViewModel surface**, the same properties and
  commands production sets — not the views. Two seams exist purely for this:
  `SchemaTreeNode.SeedChildren` (fills a node's children and marks it loaded, so
  expanding never reaches for the catalog) and `QueryViewModel.SeedResult`
  (points the grid at a result set that was never run). Both are documented as
  harness-only; production still goes through the lazy-load and run paths.
- **Nothing reads or writes the developer's real app data.** No workspace is
  restored and no settings are persisted, and because `MainViewModel` news up its
  own `SavedQueryStore`/`QueryHistoryStore`, `Fixtures` clears what those loaded
  before seeding its own — otherwise a screenshot would carry whatever is in the
  running developer's saved queries and history. The connection-dialog scenario
  points `ConnectionProfileStore`/`PlainFileCredentialStore` at a throwaway
  directory for the same reason: their real paths are the developer's own saved
  connections and passwords.

## Headless UI tests (`PgNimbus.App.Tests`)

Real windows on Avalonia's headless platform, driven with real key input — the
layer that used to be a person clicking through the app. It reuses
`tools/Screenshot`'s fixture graph (hence the `ProjectReference` to it) rather
than growing a second set that would drift from what the screenshots show.

What it covers that nothing else does: that a gesture reaches its command, that
the palette invokes the entry it highlights, that a saved query opens a *new*
tab (UI design rule 3), that the results grid builds a column per result column
and re-points on a tab switch, and that every window opens **and closes** — the
detach path a render-and-exit pass never runs.

Two landmines, both load-bearing:

- **`Ui.Run(async () => …)` is deliberately the only overload.** Avalonia's
  `HeadlessUnitTestSession` has a `Dispatch<T>(Func<T>)` that an async lambda
  binds to with `T = Task`, handing back a `Task<Task>` whose outer task
  completes the moment the body *returns* its task. The dispatcher then stops
  pumping and every assertion after the first `await` lands on a task nobody
  observes — **the whole suite passes without running**. That is how this was
  written the first time; it was caught only by deliberately breaking an
  assertion to check the tests could still go red. Do that check when adding
  tests here.
- **Gestures come from the catalog**, via `Ui.Press(window, CommandId.X)`, never
  typed in — otherwise a test keeps passing after a chord moves, and fails on
  macOS where the same entry resolves to Cmd (UI design rule 5).

The session runs the app with **no lifetime**, asserted by a test: with one,
`App.OnFrameworkInitializationCompleted` would read the developer's real
`AppSettings` and, with `AutoConnectLastProfile` on, try to connect to their
last database from a unit test.

## Benchmarks pipeline

"Fast" is measured, not asserted. `.github/workflows/benchmark.yml` runs
[`scripts/benchmarks/run-benchmarks.sh`](scripts/benchmarks/run-benchmarks.sh)
(ubuntu runner + a `postgres:17` service container). It's a reusable
workflow (`workflow_call`) invoked as a job from `release.yml` — it no
longer runs on every PR or push to `main`, only as part of the release
pipeline (tag push, or a manual `workflow_dispatch` test run of
`release.yml`), so it measures a real tagged build rather than every commit.
It's also directly `workflow_dispatch`-able on its own for ad hoc
measurement. Results go to the job summary and a `bench-results` artifact;
real tag-triggered releases also append to the gh-pages history via
`benchmark-action/github-action-benchmark` (charts at
`https://shman4ik.github.io/pgNimbus/dev/bench/`) — controlled by the
`record_history` input, which `release.yml` sets from
`startsWith(github.ref, 'refs/tags/v')` so `workflow_dispatch` test runs of
the release pipeline don't pollute the trend history. Three moving parts:

1. **Startup probe** — `PGNIMBUS_STARTUP_PROBE=1` makes the app print
   `PGNIMBUS_STARTUP_PROBE window_ms=… rss_bytes=…` after its first window
   renders its first frame, then exit (`PgNimbus.App/StartupProbe.cs`, armed
   in `App.OnFrameworkInitializationCompleted`). `window_ms` is measured from
   OS process start, so it captures AOT-vs-JIT differences honestly.
2. **`PgNimbus.Benchmarks`** — console project measuring connect (cold pool),
   `SELECT 1` round-trip, time-to-first-`RowBatch`, and full-stream
   throughput of a 100k-row mixed-type SELECT, through `QueryEngine`'s
   streaming path (the same API the UI uses). Prints `PGNIMBUS_BENCH
   name=value` lines; config via `PGNIMBUS_BENCH_CONN/ROWS/ITERS`.
3. **The script** — builds JIT Release, publishes linux-x64 NativeAOT (or
   measures an existing publish dir given via `PGNIMBUS_BENCH_PUBLISH_DIR` —
   the release pipeline passes build-linux's x64 output through the
   `publish_artifact` workflow input this way, as a `.tar.gz` because
   artifact zips drop the exec bit, so the slow AOT publish isn't done
   twice), runs
   the startup probe N times per mode under Xvfb (one discarded warm-up run,
   then medians), runs the query benchmarks, and writes
   `bench-results/benchmarks.json` (github-action-benchmark
   `customSmallerIsBetter` format — keep every metric smaller-is-better, so
   throughput is reported as stream *time*) plus `summary.md`.
   `PGNIMBUS_BENCH_SKIP_AOT=1` skips the slow AOT publish for local runs. Also
   tracks size: the AOT exe alone (`binary_size_mb`) and the shipped publish
   files (`publish_size_mb` — the publish output minus `*.pdb`/`*.dbg` debug
   symbols, mirroring the exclusion the MSI/MSIX packaging applies, so the
   metric tracks what installers actually package rather than what publish
   leaves on disk; the publish dir is wiped before publishing so repeated
   local runs never count stale leftovers) — the latter is the more honest
   "app size" number since side-car native libs bundled alongside the exe
   (`libSkiaSharp`, `libHarfBuzzSharp`) dwarf it.

Numbers are machine-relative (this sandbox: ~160 ms AOT / ~2 s JIT to first
frame; CI runners differ) — the point is the trend per commit, not the
absolute value. If a change renames a metric in `benchmarks.json`, its
gh-pages history starts over under the new name.

## Release pipeline

`.github/workflows/release.yml` runs on every `vX.Y.Z` tag push (or manually
via `workflow_dispatch`, which builds everything but skips the "release"
job so it never publishes).

**Every package is launched before it ships.** Each build job runs
`scripts/release/smoke-launch.sh` (or `Smoke-Launch.ps1` on Windows) against
its own artifacts with `PGNIMBUS_STARTUP_PROBE=1`, asserting both a clean exit
*and* the probe line — an app that quit before drawing anything also exits 0.
Windows smokes the publish output and the MSI after a silent per-user install
(uninstalled in the same step); macOS smokes the publish output and the binary
inside the mounted `.dmg`; Linux smokes the publish output, the `.tar.gz`, the
`.AppImage` (`--appimage-extract-and-run`, runners have no FUSE) and the `.deb`
after `apt-get install` resolves its own `Depends` — that last one is how a
missing runtime library gets caught here instead of on a user's machine. The
Linux legs need `xvfb`; macOS runners have a real window server. `release`
already `needs` all three jobs, so this is the publish gate. Note
`PgNimbus.App` is a `WinExe` with no console of its own — the probe line is
still readable because redirecting stdout gives the process a handle to write
to (verified, not assumed).

It produces, per tag:

- **Windows** — `dotnet publish -r win-x64 -p:PublishAot=true`, then a
  per-user WiX v5 MSI built from [`installer/windows/Product.wxs`](installer/windows/Product.wxs)
  via the `wix` .NET global tool (`wix build ... -d PublishDir=... -d
  Version=...`). Per-user (installs to `%LocalAppData%`, no elevation) is
  deliberate: the MSI is currently **unsigned** (no code-signing cert yet),
  and per-machine + unsigned is a much worse UAC/SmartScreen experience.
  The `UpgradeCode` GUID in `Product.wxs` is fixed forever — never
  regenerate it, that's what makes installing a newer tag upgrade in place
  instead of side-by-side.
- **macOS** — `osx-arm64` only, built on a `macos-14` runner. GitHub retired
  the last Intel macOS runner image (`macos-13`) in December 2025 and has
  said x86_64 macOS support ends entirely once the `macos-15` image retires
  (Fall 2027) — there's no GitHub-hosted way to build `osx-x64` anymore, so
  don't re-add an Intel matrix leg without a self-hosted Intel Mac runner.
  Also pins to the newest pre-installed Xcode below major version 26:
  Xcode 26 changed Swift auto-linking in a way that breaks NativeAOT's
  static link of `libSystem.Security.Cryptography.Native.Apple.a`
  ("symbol(s) not found for architecture arm64" / `pal_swiftbindings`),
  closed "not planned" upstream
  ([dotnet/runtime#116448](https://github.com/dotnet/runtime/issues/116448)).
  The publish output is wrapped into an **ad-hoc signed** (`codesign --sign -`),
  un-notarized `.app` + `.dmg` by
  [`scripts/macos/build-app-bundle.sh`](scripts/macos/build-app-bundle.sh),
  which also generates `.icns` directly from the `design/masters/icon/` tiles
  via `sips`/`iconutil` (stock macOS tools, no extra dependency) — each
  iconset slot uses the exact-size master when one exists, else downscales
  from `icon-1024.png`.
  **The ad-hoc signature is not decoration, it picks which Gatekeeper dialog a
  user sees** (added 2026-08, after 0.11.1 shipped with none): a quarantined
  bundle carrying *no* signature is reported as "pgNimbus is damaged and can't
  be opened. You should eject the disk image", which reads as a corrupt
  download, sends people back to Releases for the same bytes, and has no
  right-click → Open escape. The same bundle ad-hoc signed fails the same
  Gatekeeper check as "Apple cannot check it for malicious software" — true,
  and clearable by right-click → Open (System Settings → Privacy & Security →
  Open Anyway on Sequoia). It is also what makes an arm64 binary loadable at
  all. Two consequences for the script: the NativeAOT `*.dsym` is deleted
  before signing (a `.dsym` is itself a bundle directory, the one shape
  `codesign --deep` won't seal inside `Contents/MacOS`, mirroring the Linux
  packages' `*.dbg` exclusion), and dylibs are signed inside-out before the
  bundle. The `.dmg` also carries the `/Applications` symlink it had always
  claimed to (the volume used to hold the app alone, so the obvious gesture was
  double-clicking it on the read-only image — that is where "the disk image
  should be ejected" came from). `release.yml`'s mounted-`.dmg` smoke step
  gates both: `Signature=adhoc` present after `hdiutil`, and the symlink there.
  None of this substitutes for a Developer ID signature plus notarization,
  which needs a paid Apple account and would remove the warning outright.
- **Linux** — `linux-x64` + `linux-arm64` (the arm64 leg runs natively on
  GitHub's free `ubuntu-24.04-arm` runners — no cross-compile toolchain).
  Each RID is packaged three ways by
  [`scripts/linux/build-packages.sh`](scripts/linux/build-packages.sh):
  `.AppImage` (appimagetool downloaded at build time from its `continuous`
  release, run with `--appimage-extract-and-run` since CI runners lack
  FUSE; `AppRun` is a plain symlink to the binary — NativeAOT resolves the
  side-car `libSkiaSharp`/`libHarfBuzzSharp` next to `/proc/self/exe`, so
  no wrapper script), `.tar.gz` (the publish output under a versioned top
  dir), and `.deb` (`dpkg-deb`, package id `pgnimbus`, binary at
  `/usr/lib/pgnimbus/` + `/usr/bin/pgnimbus` symlink; `Depends` lists the
  X11-family libs Avalonia's X11 backend uses at runtime plus fontconfig
  for Skia — Skia/HarfBuzz themselves are bundled; a semver prerelease `-` becomes Debian `~` so CI test versions
  sort before releases). The desktop entry comes from
  [`installer/linux/pgnimbus.desktop.template`](installer/linux/pgnimbus.desktop.template)
  (`__EXEC__` placeholder: the AppImage execs `PgNimbus.App`, the deb
  `pgnimbus`), icons from the `design/masters/icon/` tiles. The NativeAOT
  `*.dbg` symbols side-file is excluded from all three packages. Unsigned,
  like the other direct-download channels.
- **winget** — the `build-windows` job renders (via
  [`scripts/winget/render-manifest.sh`](scripts/winget/render-manifest.sh)
  and the templates in `packaging/winget/`) the three manifest files
  winget requires and validates them with `winget validate` right after
  building the MSI (same job — the MSI and its SHA256 are already at
  hand, no separate runner), but does
  **not** submit them anywhere. `winget-pkgs` needs a manual first PR
  (registers the `pgNimbus.pgNimbus` identifier) before any automated
  submission could work — the generated `winget-manifests.zip` release
  asset is for that manual step.

The direct-download MSI/dmg are **unsigned** and stay that way — deliberately
**not** pursuing a paid signing service (Azure Artifact Signing / a purchased
Authenticode cert): pgNimbus is a free OSS project with no revenue. Microsoft
Store publishing gets the trust/SmartScreen benefit for $0 instead (Store
re-signs an uploaded MSIX with its own trusted certificate during
certification — the package only needs a throwaway self-signed cert to
satisfy the upload requirement, not a purchased one), and Store apps are
automatically discoverable via winget's built-in `msstore` source with no
separate winget submission. It's an *additional* channel, not a replacement
for the direct MSI + `winget-pkgs` path above — the two coexist.

### Actions storage is a 0.5 GB budget (2026-08)

The account's included GitHub Actions storage is **0.5 GB**, and it is a
*standing* budget, not a per-run one: an artifact counts for every day it
stays alive. So **every `upload-artifact` must set `retention-days`** — the
default is 90, and at 90 days this pipeline held ~6.8 GB (13x the allowance)
in copies of things that were already stored for free somewhere else. Two
rules keep it there:

1. **An artifact that ships in the GitHub Release gets `retention-days: 1`.**
   Release assets don't count against the Actions allowance, and the `release`
   job consumes these in the same run — the artifact is a job-to-job hand-off,
   not storage. That covers `windows-msi`, `macos-dmg-arm64`,
   `linux-packages-*`, `sbom`, `winget-manifests`, and `publish-linux-x64`
   (benchmark input). A day is still long enough for a human to grab a
   `workflow_dispatch` test build, where the `release` job never runs.
   `windows-msix` is the one exception at 14 days: Partner Center submission is
   a manual download-and-upload, so it has to outlive the run.
2. **Diagnostic artifacts upload on `failure()` only, for days not months.**
   The CI `screenshots` artifact is worth looking at exactly when the visual
   regression went red; on a green run it is a byte-for-byte re-render of
   `tools/Screenshot/baselines/`, which is already in git. Pair the condition
   with `if-no-files-found: ignore` — the usual failure is the render step
   throwing, which leaves the directory empty, and a missing diagnostic must
   not turn one red step into two.

Retention is **not retroactive**: lowering it leaves already-uploaded artifacts
on their original 90-day clock, so a change like this needs a one-time purge of
the backlog (`gh api repos/OWNER/REPO/actions/artifacts` → `DELETE`) to actually
free anything. The repo's default retention is set to 7 days as a backstop for
uploads that forget rule 1.

Runner *minutes* ride the same fix from the other side: every workflow that
triggers on both `push` and `pull_request` keys its `concurrency` group on
`github.event.pull_request.head.ref || github.ref`. Keyed on `github.ref` the
two triggers land in different groups (`refs/heads/x` vs `refs/pull/N/merge`)
and run the whole job twice for one commit — which is also two artifacts.

### Supply-chain proofs (2026-07)

Unsigned binaries still get verifiable provenance, three layers:

- **SLSA attestations** — the release job runs
  `actions/attest-build-provenance` over every published asset (needs the
  job's `id-token: write` + `attestations: write` permissions). Users
  verify a download with
  `gh attestation verify <file> --repo Shman4ik/pgNimbus` — proves it was
  built by this workflow from a specific commit. This is the $0 substitute
  for Authenticode on the direct-download channel; it does nothing for
  SmartScreen (the Store channel covers that).
- **SBOM** — the build-linux x64 leg generates a CycloneDX JSON SBOM of the
  App's full NuGet graph (`dotnet-CycloneDX` on `PgNimbus.App.csproj`,
  `-c Release` so the Debug-only conditional AvaloniaUI.DiagnosticsSupport
  reference stays out — it's not in shipped binaries and must not appear in
  the SBOM). Ships as the `pgNimbus-<ver>-sbom.cdx.json` release asset,
  checksummed and attested like the binaries. Generated once (x64 only) —
  the NuGet graph is RID-independent.
- **Vulnerability gates** — the repo-root `Directory.Build.props` sets
  `NuGetAuditMode=all` (transitive packages too) and promotes
  moderate/high/critical audit warnings (NU1902–NU1904) to errors, so any
  `dotnet build`/`restore` — local or CI — fails on a known advisory; ci.yml
  additionally runs `dependency-review-action` on PRs to block newly-added
  vulnerable packages at review time.

### Microsoft Store (MSIX)

`build-windows` also packs `publish/win-x64` into a self-signed `.msix` via
[`scripts/windows/build-msix.ps1`](scripts/windows/build-msix.ps1), uploaded
as the `windows-msix` CI artifact — **not** attached to the public GitHub
Release, since a self-signed MSIX can't be installed without the user
manually trusting the cert first, and Store re-signing only happens after
you upload it to Partner Center.

- **Manifest**: [`installer/msix/Package.appxmanifest`](installer/msix/Package.appxmanifest)
  is a template (`$VERSION$` placeholder) with `Identity/Publisher` hardcoded
  to this repo's reserved Partner Center product identity
  (`DmitriiShmanev.pgNimbus` / `CN=04FDF7B0-6D86-4EB7-B798-21CD434897BC`,
  Store ID `9N6SZT42XJ24` — the listing is **live** as of 2026-07:
  <https://apps.microsoft.com/detail/9N6SZT42XJ24>) — plain
  Win32/Desktop Bridge (`runFullTrust`
  capability, `EntryPoint="Windows.FullTrustApplication"`), not Windows App
  SDK, since the app is a native AOT exe with no WinUI dependency.
- **Tile assets**: `PgNimbus.App/Assets/Msix/*.png` (Square44x44Logo,
  Square150x150Logo, StoreLogo — each as 5 DPI-scale files, plus
  Square44x44Logo's 10 unplated targetsize files) are generated by
  [`scripts/windows/make-app-icons.ps1`](scripts/windows/make-app-icons.ps1)
  from the `design/masters/icon/` tiles (44/50/150 px scale-100 bases from the
  48/48/256 px masters respectively; scale-200/400 sizes that exceed their
  small master fall back to the 1024 px master to avoid upscale blur; the
  unplated variants reuse the transparent `window-{dark,light}-256.png`
  masters) — excluded from `AvaloniaResource` in the App csproj since they're
  packaging-time-only. A single flat file per logo used to be enough for the
  package to *build*, but Windows silently backplates/shrinks it on the
  taskbar, Start, and the sideload "Install app?" dialog when it can't find a
  qualifier-matched size — hence the scale/targetsize sets (fixed 2026-07).
- **`build-msix.ps1`**: stages the publish output + tile assets + rendered
  manifest, then runs `makepri.exe` (`createconfig` + `new`) to compile those
  qualified filenames into a single `resources.pri` — without it, Windows
  only ever resolves the scale-100/unqualified assets and the rest just sit
  in the package unused. `createconfig`'s default `priconfig.xml` splits
  scale-qualified resources into separate `resources.scale-*.pri` side files
  (meant for `AppxBundle` resource packages with matching manifest
  `<ResourcePackage>` entries); since this is one flat non-bundle package,
  the script strips that `<autoResourcePackage>` splitting so everything
  lands in the one `resources.pri` actually included in the package. Then
  packs with `makeappx.exe`, signs with an ephemeral
  `New-SelfSignedCertificate` (Subject matching the manifest's `Publisher`,
  deleted from the cert store right after signing). Resolves `makeappx`/
  `makepri`/`signtool` by globbing every installed Windows SDK's
  `bin\<ver>\x64` dir and taking the newest, so it doesn't hardcode an SDK
  version that'll drift on GitHub's runner images. MSIX versions are 4-part
  with the last field forced to `0` (Store convention) —
  `ConvertTo-MsixVersion` strips any prerelease suffix like `-ci.42` from
  `VERSION` before padding.
- **Submission** (manual, not automated yet): the first submission passed
  certification and the listing is live. For updates: download the
  `windows-msix` artifact from the release workflow run and upload it through
  Partner Center → this product → Packages, then submit for certification.
  Could move to the Microsoft Store submission API later (needs its own
  Entra ID app registration under the Partner Center account — free,
  unrelated to Azure Artifact Signing).

## Project website + docs (GitHub Pages)

The `gh-pages` branch hosts three independent things at three paths, and nothing
that writes to one may touch the others: `/` is the landing page, `/docs/` is
the documentation site, `/dev/bench/` is the benchmark history.

### Documentation site (`/docs/`)

MkDocs Material, configured in the repo-root [`mkdocs.yml`](mkdocs.yml), built
from `docs/`. `docs/` doubles as the repo's internal notes directory, so
`exclude_docs` keeps `marketing/`, `design/`, `PROGRESS.md` and
`PRE-LAUNCH-CHECKLIST.md` out of the published site — **only pages listed in
`nav` ship**. Published by
[`scripts/website/publish-docs.sh`](scripts/website/publish-docs.sh), which
replaces `gh-pages:/docs/` alone; `.github/workflows/docs.yml` builds it with
`--strict` on every PR touching `docs/`/`mkdocs.yml` (so a broken link or a page
missing from `nav` fails the check) and publishes on push to `main`. Local
preview: `pip install -r docs/requirements.txt && mkdocs serve`.
`docs/reference/keyboard-shortcuts.md` is **generated** — see UI design rule 5,
don't hand-edit it. `docs/assets/{logo,favicon}.png` are copies of the
`design/masters/icon/` tiles; refresh them if the masters change.

**User-facing prose goes through the `humanizer` skill.** `.claude/skills/humanizer/`
is vendored from <https://github.com/blader/humanizer> (MIT; see its `SOURCE.md`
for the update procedure and the one standing deviation — the README keeps its
emoji section headings). Apply it to the README, the `docs/` pages, release
notes and website copy — its hardest rule is no em/en dashes in user-facing prose,
which is why those files read differently from this one. `CLAUDE.md` and code
comments are internal and keep their own voice.

### Landing page (`/`)

<https://shman4ik.github.io/pgNimbus/> is a hand-written static landing page.
Source of truth is [`website/index.html`](website/index.html) (self-contained
HTML+CSS, light/dark via `prefers-color-scheme`, no external requests);
[`scripts/website/publish-site.sh`](scripts/website/publish-site.sh) assembles
it with assets copied from `design/masters/` and `docs/screenshots/` into the
**root of the `gh-pages` branch** and pushes. The same branch hosts the
benchmark history under `dev/bench/` (written by benchmark-action from the
release pipeline) — the publish script must never touch that directory.
Publishing is manual: edit `website/index.html`, run the script. If the
screenshots or download links change (e.g. a new install channel), update the
page in the same PR.
