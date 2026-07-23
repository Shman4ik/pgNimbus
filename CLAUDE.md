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

## Hard architectural rules

1. **`PgNimbus.Core` has zero Avalonia/UI dependencies.** It references only
   `Npgsql`. Anything UI-related belongs in `PgNimbus.App`. This keeps the
   engine reusable for a future CLI or test harness — don't leak
   `Avalonia.*` or `CommunityToolkit.Mvvm` types into `Core`.
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
   than being formatted ad hoc in the App. Both monitoring windows follow the
   same shape: one-live-instance, opened from the command palette (and the
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
   text view stays clean). `Monitoring`-style separation applies:
   `Query/PlanAnalyzer` is a **Core-pure, unit-tested** walker (a read-only
   sibling of `Json/JsonTree` and `Monitoring/BlockingTree`) that emits named
   `PlanWarning`s — bad row estimates, disk-spilled sorts/hashes, wasteful
   sequential scans, lossy bitmap heap blocks — each with an actionable
   one-liner and a conservative threshold constant. The App wraps them in
   `PlanWarningViewModel` (glyph + severity brush) for the warnings strip;
   `ExplainNodeViewModel` computes each node's exclusive **self time** so the
   tree's bar becomes a time-heat profile (falling back to cost when there's
   no ANALYZE timing) and tints the single slowest node as the bottleneck.
   The design doc + competitive research is in
   [`docs/design/explain-improvements.md`](docs/design/explain-improvements.md)
   (it also tracks the deferred follow-ups: write-statement `ROLLBACK` guard,
   paste-a-plan, copy/export, re-color-by-metric).

## UI design rules

1. **Minimalist design is a priority.** Every new always-visible control —
   especially a toolbar button — must be explicitly discussed and justified
   before it's added; the default answer is no. Secondary/rare actions belong
   in the command palette (Ctrl+K) or a context menu, not on the toolbar
   (that's why the auto-alias "AS" toggle moved from the toolbar to the
   palette, 2026-07).
2. **Double-click triggers the default action.** Anywhere a list/tree item
   has an obvious primary action, double-clicking it must perform that
   action: schema-tree table → browse, function → source, saved query /
   history entry → open in a new tab, connection profile → connect, result
   cell → inline edit when the result set is editable, inspector when it's
   read-only (Space quick-peeks the current cell in the inspector in both
   modes, 2026-07). Apply the same rule to any new list-like UI.
3. **Loading a query never overwrites the active tab.** Saved queries,
   history entries, and generated DDL all open in a *new* tab.
4. **Tabs drag-reorder; the ☰ app menu is the file-command home.** The query
   tab strip reorders by dragging (live, browser-style — pointer handlers in
   `MainWindow.axaml.cs`; the order persists via the workspace snapshot, which
   serializes `Tabs` in collection order). The ☰ button (top-left, 2026-07)
   opens the one discoverable menu for file/tab-level commands: New tab,
   Open .sql / Open recent, Save / Save as, Close tab, Switch connection,
   New window, Preferences. The command bar's centered "Search" pill
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
5. **No hardcoded Ctrl gestures.** Every command shortcut resolves through
   `PgNimbus.App/Hotkeys.cs` (Ctrl vs Cmd, per platform or the persisted
   scheme preference): MainWindow builds its `KeyBindings` in code, palette
   labels use `Hotkeys.Label`, and the F1 cheat sheet relabels its `cmdKey`
   caps. The one deliberate exception is completion's Ctrl+Space (Cmd+Space
   is Spotlight). User-facing settings live on the preferences page
   (`PreferencesWindow`, opened from the palette), persisted in
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
   Horizontal tab strips use `TabControl.segmented` — a retemplated
   macOS-style segmented capsule (the monitoring windows' Backends/Blocking
   and Database Overview's tabs); the bare global `TabItem` style is the
   *vertical* left-nav look and must stay untouched. Destructive colors come
   from the `AppDanger*` tokens (theme-independent fixed red). This
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
   unchanged.

## Platform window chrome

- **Windows** — every window calls `ThemedWindowChrome.Attach(this)` (icon +
  caption color; details in the icon section below).
- **macOS** — `MainWindow.SetUpMacTitleBar()` merges the 40px command bar
  with the title bar (`ExtendClientAreaToDecorationsHint` +
  `ExtendClientAreaTitleBarHeightHint = 40`; Avalonia 12 dropped the old
  `ExtendClientAreaChromeHints` enum — native traffic lights stay by
  default). The bar gets 84px left padding to clear the traffic lights and
  drags the window from its empty space (`BeginMoveDrag`); it also hides the
  ☰ button and the "pgNimbus" wordmark there — the menu bar covers both
  (2026-07). The sidebar toggle icon is platform-picked via `{OnPlatform}`
  (SF-style geometry on macOS).
- **macOS native menu bar (2026-07)** — two layers. App-level (`App.axaml`,
  needs `Name="pgNimbus"` or Avalonia shows "Avalonia Application"): About
  pgNimbus (opens `AboutWindow` — name/version/license, version from the
  same `InformationalVersion` the connection-dialog footer reads), pgNimbus
  on GitHub, and Settings… (Cmd+, — routes to the active MainWindow's
  preferences via `OnSettingsMenuItemClicked`). Window-level:
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

Sources live in `design/masters/` and are **hand-drawn per size** — the
scripts *copy/assemble* them, they do **not** downscale one master into every
tiny icon (that produced muddy 16–32px icons; fixed 2026-07). Layout:

- `design/masters/icon/icon-{16,24,32,48,256,1024}.png` — the app tile.
  16/24/32/48 are square full-bleed (no transparency — these feed the
  taskbar/Explorer directly with no OS-drawn plate behind them, so a
  transparent icon disappears there) and simplified for legibility. 256/1024
  are a circular navy badge with transparent corners (2026-07) — those only
  feed contexts that supply their own backdrop (macOS icon mask, Store
  listing pages), so transparency is safe and reads better at that size.
- `design/masters/window/window-{light,dark}-256.png` — transparent line-art
  window icons (currently unused in-app, see Part 2 of LOGO-ASSETS.md).
- `design/masters/logo/` — README/website assets: `logo.svg`,
  `logo-{light,dark}.png`, `wordmark-{light,dark}.{svg,png}`,
  `social-preview.png` (1280×640).
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
- Core: `Npgsql`.
- App: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`,
  `Avalonia.Fonts.Inter`, `Avalonia.Controls.DataGrid`, `Avalonia.AvaloniaEdit`,
  `CommunityToolkit.Mvvm`, `AvaloniaUI.DiagnosticsSupport` (DevTools/MCP —
  Debug-only, wired via `.WithDeveloperTools()` in `Program.cs`, see below).
- Tests: `PgNimbus.Core.Tests` — TUnit on Microsoft.Testing.Platform. Run
  `dotnet test --project PgNimbus.Core.Tests` (MTP mode comes from the
  `test.runner` opt-in in the repo-root `global.json`) or plain
  `dotnet run --project PgNimbus.Core.Tests`. Never add
  `Microsoft.NET.Test.Sdk` to a TUnit project — it breaks test discovery.
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
-p:PublishAot=true`, ~100 ms launch-to-window vs ~700 ms JIT). Two
AOT-specific landmines are already handled in the codebase — keep them
that way: `SatelliteResourceLanguages=en` in the App csproj (a
culture-named satellite assembly + InvariantGlobalization crashes
Avalonia's asset resolver at startup under AOT, surfacing as a bogus
"avares://... not found") and no reflection binding paths (the results
grid binds columns via `RowIndexConverter`, not `"[i]"` indexer paths).

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
job so it never publishes). It produces, per tag:

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
  The publish output is wrapped into an unsigned/unnotarized `.app` + `.dmg`
  by [`scripts/macos/build-app-bundle.sh`](scripts/macos/build-app-bundle.sh),
  which also generates `.icns` directly from the `design/masters/icon/` tiles
  via `sips`/`iconutil` (stock macOS tools, no extra dependency) — each
  iconset slot uses the exact-size master when one exists, else downscales
  from `icon-1024.png`.
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

## Project website (GitHub Pages)

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
