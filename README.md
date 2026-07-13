<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="design/masters/logo/wordmark-dark.png">
    <img src="design/masters/logo/wordmark-light.png" alt="pgNimbus logo — an elephant riding a broom" width="300">
  </picture>
</p>

# pgNimbus

A fast, native-feeling, open-source **PostgreSQL** GUI client, built with **.NET
10 + Avalonia 12**. MIT licensed. Windows is the primary target, but the
codebase stays cross-platform-capable — no Windows-only APIs live in the core
engine.

## The market thesis

The gap in the Postgres client market: **truly fast + open source + modern
UI**.

- pgAdmin and DBeaver are heavy and slow.
- TablePlus is fast, but closed-source and paid.
- Beekeeper Studio is open source, but Electron.
- HeidiSQL is native and fast, but dated in its UI and MySQL-first.

pgNimbus targets HeidiSQL's speed with TablePlus's polish — PostgreSQL-first,
from the ground up.

### Differentiators

1. **Native performance** — launch-to-window in the ~100 ms range as a
   NativeAOT binary. Not a one-off claim: startup, memory, and query-engine
   numbers are measured on every release — see [Benchmarks](#benchmarks).
2. **PostgreSQL-first** — deep `pg_catalog` introspection (materialized views,
   real types, primary-key flags, and EXPLAIN visualization); never the
   lowest-common-denominator SQL dialect.
3. **Keyboard-first** — run, cancel, and navigate without touching the mouse.
4. **Streaming results** — the first screenful renders before the full result
   set arrives, backed by a virtualized grid for large results.

## See it in action

| Instant launch (NativeAOT) | SQL completion that predicts the next move |
| --- | --- |
| ![pgNimbus launching from a cold NativeAOT process to a fully rendered main window in well under a second](docs/screenshots/cold-start.gif) | ![Typing FROM + a partial table name, JOIN with an FK-ranked table suggestion, then ON auto-completing the full join condition](docs/screenshots/completion-demo.gif) |

## Screenshots

| Query editor + results (light) | Query editor + results (dark) |
| --- | --- |
| ![Main window, light theme](docs/screenshots/main-light.png) | ![Main window, dark theme](docs/screenshots/main-dark.png) |

| EXPLAIN ANALYZE visualization | Command palette (Ctrl+K) |
| --- | --- |
| ![Graphical EXPLAIN ANALYZE plan tree with per-node cost and timing](docs/screenshots/explain-visualization.png) | ![Command palette fuzzy-jumping to a table](docs/screenshots/command-palette.png) |

| Server activity (pg_stat_activity) | Connection manager |
| --- | --- |
| ![Server activity window showing a live backend and its wait event](docs/screenshots/server-activity.png) | ![Connection dialog with saved profiles and paste-anything import](docs/screenshots/connection-dialog.png) |

Keyboard shortcuts cheat sheet (<kbd>F1</kbd>):

<img src="docs/screenshots/shortcuts.png" alt="Keyboard shortcuts cheat sheet" width="360">

## Download

### Windows

The **Microsoft Store is the preferred way to install** on Windows: the Store
signs the package with its own trusted certificate (no SmartScreen warnings)
and keeps it updated automatically.

- **Microsoft Store** — [pgNimbus on the Microsoft Store](https://apps.microsoft.com/detail/9N6SZT42XJ24).
  *The app has been submitted and is currently in Store certification — the
  listing goes live as soon as that completes.*
- **winget** — Store apps are installable through winget's built-in
  `msstore` source, so once the listing is live:

  ```text
  winget install --id 9N6SZT42XJ24 --source msstore
  ```

- **Direct download** — `pgNimbus-<version>-win-x64.msi` from
  [Releases](https://github.com/Shman4ik/pgNimbus/releases), a per-user
  installer (no admin rights needed). The direct MSI is unsigned, so Windows
  SmartScreen will warn on first run — click "More info" → "Run anyway".
  Prefer the Store/winget path above if you can.

### macOS (very early beta)

The macOS build is a **very early beta**: it's produced by the same CI
pipeline but has seen far less real-world testing than Windows, and full
macOS support (signing, notarization, broader testing) is planned for
later. If you want to try it anyway:

- `pgNimbus-<version>-macos-arm64.dmg` from
  [Releases](https://github.com/Shman4ik/pgNimbus/releases) (Apple Silicon
  only; GitHub retired hosted Intel macOS runners in December 2025, and
  Apple hasn't sold an Intel Mac since 2023).

#### Fixing the Gatekeeper "App is damaged" error

Because the beta binary is currently unsigned and unnotarized, macOS
Gatekeeper will block it on the first launch, showing a misleading security
warning:

> *"pgNimbus" is damaged and can't be opened. You should move it to the
> Trash.*

This is standard macOS behavior for unsigned apps. The file is completely
safe. To fix this and open the app, you need to clear the quarantine flag
via Terminal:

1. Drag `pgNimbus.app` from the DMG into your **Applications** folder (or
   keep it in `Downloads`).
2. Open **Terminal** (`Terminal.app`) and run the corresponding command:

   ```bash
   # If you moved the app to the Applications folder:
   xattr -cr /Applications/pgNimbus.app

   # If the app is still in your Downloads folder:
   xattr -cr ~/Downloads/pgNimbus.app
   ```

3. Launch `pgNimbus.app` normally — the warning is gone for good (each
   downloaded update needs the command once).

Every tag push (`vX.Y.Z`) builds all of the above via
[`.github/workflows/release.yml`](.github/workflows/release.yml) — see
[CLAUDE.md](CLAUDE.md) for how the pipeline is put together.

## Features

- **Schema tree sidebar** — schemas → tables/views → columns, reading
  `pg_catalog` directly (materialized views, partitioned tables, real
  primary-key flags), with an "Alter Table" UI for no-SQL column add/rename/drop
  and a **"Source (DDL)"** action that reconstructs an object's
  `CREATE TABLE`/`CREATE VIEW` — columns, defaults, identity, constraints,
  partition key, and secondary indexes — into a new editor tab.
- **Refresh database & schema** — a sidebar refresh button (or
  <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>R</kbd>) reloads the schema tree,
  autocomplete cache, and command-palette table list from the server, so
  objects created or altered elsewhere appear without reconnecting.
- **No-SQL table browsing** — previewing a table opens a browse bar with a
  `WHERE` filter, `ORDER BY` from clicking a column header, and prev/next
  paging — all pushed down to Postgres (`WHERE`/`ORDER BY`/`LIMIT`/`OFFSET`),
  so browsing a huge table stays as cheap as one page.
- **Connection manager** — saved profiles with a per-connection accent color
  (so production doesn't look like staging), SSH tunnel support, and
  passwords held by the OS credential store (DPAPI on Windows) instead of
  being written to disk with the profile.
- **Switch connection without restarting** — the ⇄ button next to the
  title-bar breadcrumb (or "Switch connection…" in the command palette)
  reopens the connection dialog; the current window stays fully usable until
  the new connection succeeds, then hands over cleanly (LISTEN subscriptions
  and SSH tunnels for the old connection are torn down).
- **Paste-anything connection strings** — drop whatever is on your clipboard
  into the connection dialog and it fills the form: `postgres://` URIs
  (Heroku/Supabase/Neon-style), `jdbc:postgresql://` URLs, ADO.NET/Npgsql
  `Key=Value;` strings, libpq `host=… dbname=…` keyword strings, and even
  full `psql` command lines (including `PGPASSWORD=… psql -h …` prefixes).
- **Command palette** — press <kbd>Ctrl</kbd>+<kbd>K</kbd> (or
  <kbd>Ctrl</kbd>+<kbd>P</kbd>) to fuzzy-jump to any table, saved query, or
  action from one keyboard-driven control.
- **Keyboard shortcuts cheat sheet** — press <kbd>F1</kbd> (or the `?`
  title-bar button) for an overview of every binding.
- **Preferences page** — theme (system/light/dark), editor behavior
  (auto-alias on completion), and the shortcut modifier (Ctrl/Cmd,
  auto-detected per platform) on one page, opened via the gear title-bar
  button, <kbd>Ctrl</kbd>+<kbd>,</kbd>, or "Preferences…" in the command
  palette; every change applies immediately.
- **Multi-tab query editor** — schema-aware SQL autocomplete (schema-qualified
  tables after `FROM`/`JOIN`, `FROM`-scoped columns in `WHERE`/`ON`/`ORDER BY`,
  columns with their data types elsewhere, `alias.` member access, CTE names
  *and their output columns* — `cte.` completes what the CTE's SELECT list
  yields, with `SELECT *` bodies resolved through the catalog,
  common built-in functions *and the schema's own user-defined
  functions/procedures/aggregates*, inserted as calls with the argument list
  and return type shown as a tooltip in place of a separate parameter-hints
  popup), saved queries,
  run history, current-line and matching-bracket highlighting, and font-size
  zoom (<kbd>Ctrl</kbd>+wheel / <kbd>Ctrl</kbd>+<kbd>±</kbd>).
- **SQL formatting** — <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F</kbd> (or "Format
  SQL" in the command palette) pretty-prints the statement under the cursor in a
  readable block style — clauses on their own lines, list items and JOINs one per
  line, `AND`/`OR` stacked, subqueries indented, keywords upper-cased — replacing
  just that statement so the rest of a multi-statement script is left alone. A
  self-check guarantees it never alters a query's tokens, only its whitespace.
- **Run a whole script** — execute several `;`-separated statements at once on a
  single connection (so `BEGIN…COMMIT`, `SET`, and temp tables carry across
  them); each statement gets its own selectable result section with per-statement
  timing, and the run stops at the first error (psql `ON_ERROR_STOP` style).
- **Transaction control** — an explicit **Begin** toolbar button opens a
  transaction that every statement (and inline grid edit) then runs inside on
  one held connection; **Commit**/**Rollback** end it, a status-bar "in
  transaction" indicator shows while it's open, and a failed statement
  auto-rolls-back the block so you're never stranded in Postgres's
  aborted-transaction state.
- **Streaming, cancellable results** — the first screenful renders before the
  full result set arrives, backed by a virtualized grid with inline cell
  editing and CSV/JSON export.
- **Grid CRUD** — beyond inline cell edits: an "Add row" dialog (each column
  cast to its real type server-side, blanks fall back to defaults),
  "Delete selected row(s)" with a confirmation, and "Set cell to NULL" (the
  gesture inline editing can't express), all keyed on the primary key.
- **Cell inspector** — double-click a cell (or "Inspect cell…" on the grid
  context menu) to read the full value of a long `text`/`jsonb` cell in an
  overlay: JSON pretty-printed, word wrap on by default with a toggle, and a
  one-click copy.
- **EXPLAIN visualization** — a graphical plan tree for `EXPLAIN` and
  `EXPLAIN ANALYZE`, not just raw text output.
- **Server activity dashboard** — a live `pg_stat_activity` view (state, wait
  events, running statement) with per-backend **cancel statement** and
  **terminate session** actions, so a runaway query is one click to stop.
- **CSV/JSON import** — load a file into a new or existing table with
  delimiter/header detection and per-column type inference, streamed via
  `COPY`.
- **Query history search & pinning** — history is searchable, pinnable, and
  scoped per connection; entries open in a new tab.
- **Copy results as…** — <kbd>Ctrl</kbd>+<kbd>C</kbd> copies selected rows as
  TSV; the grid context menu adds CSV, JSON, Markdown table, and
  `INSERT`-statement formats.
- **LISTEN/NOTIFY monitor** — subscribe to channels and watch notifications
  arrive live.

## Keyboard shortcuts

Press <kbd>F1</kbd> in the app for the full cheat sheet. On macOS,
<kbd>Cmd</kbd> takes the place of <kbd>Ctrl</kbd> automatically (except SQL
autocomplete, which stays on <kbd>Ctrl</kbd>+<kbd>Space</kbd> — Cmd+Space is
Spotlight); the modifier can also be forced either way from Preferences.
The highlights:

| Action | Shortcut |
| --- | --- |
| Command palette (jump to table / query / action) | <kbd>Ctrl</kbd>+<kbd>K</kbd> or <kbd>Ctrl</kbd>+<kbd>P</kbd> |
| Refresh database & schema | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>R</kbd> |
| Collapse / show the sidebar | <kbd>Ctrl</kbd>+<kbd>B</kbd> |
| Run query | <kbd>Ctrl</kbd>+<kbd>Enter</kbd> or <kbd>F5</kbd> |
| Run just the statement under the cursor | <kbd>Shift</kbd>+<kbd>Enter</kbd> |
| Format the statement under the cursor | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F</kbd> |
| Cancel running query | <kbd>Esc</kbd> |
| New / close query tab | <kbd>Ctrl</kbd>+<kbd>T</kbd> / <kbd>Ctrl</kbd>+<kbd>W</kbd> |
| Next / previous tab | <kbd>Ctrl</kbd>+<kbd>PageDown</kbd> / <kbd>Ctrl</kbd>+<kbd>PageUp</kbd> |
| SQL autocomplete | <kbd>Ctrl</kbd>+<kbd>Space</kbd> (also triggers while typing) |
| Switch focus: editor ↔ results grid | <kbd>F6</kbd> |
| Preferences | <kbd>Ctrl</kbd>+<kbd>,</kbd> |
| Edit selected result cell | <kbd>F2</kbd>, then <kbd>Enter</kbd> to commit / <kbd>Esc</kbd> to cancel |
| Inspect a result cell (full value, pretty-printed JSON) | Double-click, or "Inspect cell…" on the grid context menu |
| Keyboard shortcuts window | <kbd>F1</kbd> |

## Architecture

```
pgNimbus/
├── PgNimbus.sln
├── PgNimbus.Core/                # Engine. Zero Avalonia/UI dependencies.
│   ├── Connections/ConnectionProfile.cs
│   ├── Query/QueryResult.cs
│   ├── Query/QueryEngine.cs
│   └── Schema/SchemaService.cs
├── PgNimbus.App/                 # Avalonia MVVM front-end.
│   ├── ViewModels/QueryViewModel.cs
│   ├── Views/MainWindow.axaml(.cs)
│   ├── App.axaml(.cs)
│   └── Program.cs
├── PgNimbus.Core.Tests/          # TUnit tests for the engine.
└── PgNimbus.Benchmarks/          # Query-engine benchmarks (see Benchmarks).
```

`PgNimbus.Core` is a plain class library that depends only on `Npgsql`. It
knows nothing about Avalonia or any UI framework, which keeps the engine
reusable for a future CLI or test harness. `PgNimbus.App` is the Avalonia
MVVM shell built on top of it, using CommunityToolkit.Mvvm source generators.

## Building and running

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build

dotnet run --project PgNimbus.App
```

### Connecting to a database

On launch, pgNimbus opens a connection dialog where you can create and save
connection profiles (host, port, database, credentials, optional SSH tunnel,
accent color) — saved profiles never store the password, only the OS
credential store does.

The **paste box at the top of the dialog** accepts a connection string in any
common syntax and fills the form from it — only the fields the string
mentions are overwritten:

```text
postgres://alice:s3cret@db.example.com:5433/appdb?sslmode=require
jdbc:postgresql://db.example.com:5433/appdb?user=alice&ssl=true
Host=db.example.com;Port=5433;Database=appdb;Username=alice;Password=s3cret
host=db.example.com port=5433 dbname=appdb user=alice sslmode=require
PGPASSWORD=s3cret psql -h db.example.com -p 5433 -U alice appdb
```

For scripted or repeated local testing, you can skip the dialog entirely by
setting `PGNIMBUS_CONN`, which opens straight to the main window. It accepts
the same formats as the paste box:

```bash
export PGNIMBUS_CONN="postgres://postgres:secret@localhost:5432/mydb"
dotnet run --project PgNimbus.App
```

### Publishing a NativeAOT build

```bash
dotnet publish PgNimbus.App -c Release -r win-x64 -p:PublishAot=true    # Windows
dotnet publish PgNimbus.App -c Release -r linux-x64 -p:PublishAot=true  # Linux (needs clang + zlib1g-dev)
```

The linux-x64 AOT binary is exercised as part of development: the query grid,
EXPLAIN visualization, and profile stores all work under AOT (JSON persistence
uses source-generated serializer contexts, and the results grid binds columns
via converters instead of reflection paths, both of which trimming/AOT
require).

## Benchmarks

"Fast" is the thesis, so it's measured, not asserted. The
[Benchmarks workflow](.github/workflows/benchmark.yml) runs as part of the
release pipeline (so every tagged build is measured) and on demand via
`workflow_dispatch`:

| Metric | What it proves |
| --- | --- |
| Startup, launch → first frame (NativeAOT and JIT) | The app is on screen in the ~100 ms range as an AOT binary — measured from OS process start to the first rendered frame, median of 7 runs |
| Memory at first frame, AOT binary size | The footprint stays "native app", not "Electron app" |
| Connect (cold pool) / round-trip (`SELECT 1`, warm) | Interactive latency of the query path |
| First row batch of a 100 000-row `SELECT` | Streaming works: the first screenful arrives long before the full result |
| Full 100 000-row stream (rows/s) | Sustained throughput of the `IAsyncEnumerable<RowBatch>` engine path |

Each run writes the numbers to the workflow's job summary and a
`bench-results` artifact; tagged releases also append to the historical
charts at <https://shman4ik.github.io/pgNimbus/dev/bench/>, so a regression
shows up as a visible step in the graph of the release that introduced it.
(Shared CI runners are noisy — read trends and big jumps, not single-percent
wiggles.)

To run the suite locally (Linux, needs Xvfb and a reachable PostgreSQL):

```bash
PGNIMBUS_BENCH_CONN="Host=localhost;Database=postgres;Username=postgres;Password=postgres" \
    scripts/benchmarks/run-benchmarks.sh          # add PGNIMBUS_BENCH_SKIP_AOT=1 to skip the slow AOT publish
```

Two small pieces make it work: `PGNIMBUS_STARTUP_PROBE=1` makes the app print
launch-to-first-frame time and RSS and exit
([`StartupProbe.cs`](PgNimbus.App/StartupProbe.cs)), and the
[`PgNimbus.Benchmarks`](PgNimbus.Benchmarks/Program.cs) console project
measures the query engine through the same streaming API the UI uses.

## Backlog

Prioritized by how much they advance the thesis (fast + open + modern,
PostgreSQL-first). Contributions welcome — these are intentionally scoped as
individually shippable pieces. Shipped items graduate from this list into
[Features](#features) above.

### Now — release blockers

- [ ] **Microsoft Store certification** — the MSIX has been submitted to
  the Store and is in certification. Once live, the Store listing becomes
  the trusted, SmartScreen-clean install path on Windows (the Store re-signs
  the package with its own certificate), and `winget install` works through
  the built-in `msstore` source. Buying an Authenticode cert for the direct
  MSI is deliberately *not* planned — the Store covers the trust story for
  $0; the direct MSI stays as an unsigned convenience download.
- [ ] **winget-pkgs submission** — manifests are generated and validated per
  release, but the first manual `winget-pkgs` PR (which registers the
  `pgNimbus.pgNimbus` identifier for the classic community source) hasn't
  been made yet. Lower priority now that the `msstore` source will cover
  `winget install`.
- [ ] **Full macOS support** — the current `.dmg` is a very early beta:
  arm64-only, unsigned/unnotarized, lightly tested. Proper support (Apple
  Developer ID signing + notarization, real-world testing) is planned for
  later and needs an Apple developer account.

### Polish

- [ ] **Mica/acrylic backdrop on Windows** — the two-tone shell is ready for
  it; deliberately deferred until it can be verified on a real Windows
  desktop (transparency fallbacks can't be seen headless).
- [ ] **Verify results-grid trackpad scrolling on a real mac** — the fix
  (horizontal wheel deltas are fed to the grid's horizontal scrollbar on
  hover, no click-into-grid needed) is implemented but was exercised on
  Windows only; needs a pass with an actual macOS trackpad, ideally as part
  of a broader macOS input/gesture sweep.
- [ ] **Per-action hotkey remapping** — the Ctrl/Cmd scheme switch
  (Preferences → Keyboard, `Hotkeys.cs`) is the foundation; the next step is
  letting users rebind individual actions, persisted in `AppSettings`, with
  conflict detection and a reset-to-defaults.
- [ ] **State the privacy guarantee** — a short README/docs section pledging
  zero telemetry and zero network traffic beyond the database and SSH hosts
  you configure. Costs nothing (it's already true) and is something users
  explicitly probe new clients for
  ([Show HN: DB Pro](https://news.ycombinator.com/item?id=46078571)).

### SQL editor — completion that predicts the next move

Findings from a hands-on Windows testing session (2026-07-09) against a live
multi-schema database. The context-aware core (clause detection, alias/CTE
resolution, `qualifier.` member access, auto-open after FROM/JOIN) all works —
these are the gaps between "correct" and "feels like magic", roughly in
impact order:

- [x] **First-keystroke preselection (bug — fix first)** — the popup opened
  by the first typed letter shows the *unfiltered* list with nothing
  selected, so `f` → Enter inserts nothing instead of `FROM`; filtering and
  preselection only kick in from the second character. Apply the initial
  character as the filter when the window opens.
- [x] **FK-aware JOIN magic** — the flagship. After `JOIN`, rank tables
  connected by a foreign key to the statement's tables first (today it's the
  same flat catalog dump as `FROM`); after `ON `, auto-open and offer the
  complete join condition (`oi.order_id = o.id`) as the top, one-keystroke
  item. Needs FK edges (`pg_constraint contype = 'f'`) in the completion
  catalog — the same data the ER-diagram backlog item wants.
- [x] **Fuzzy matching + smarter ranking** — filtering is strict-prefix:
  `dr` offers only `DROP` and never finds `daily_revenue`; `ord` preselects
  `order_items` when `orders` is at least as likely. Reuse the command
  palette's `FuzzyMatcher` (subsequence + word-boundary + adjacency bonuses)
  in the completion list, breaking ties by exact-prefix, then shorter name,
  then recency of use. Requires replacing the stock AvaloniaEdit
  `CompletionList` filter.
- [x] **Auto-open in more predictable spots** — the list opens by itself
  only after FROM/JOIN/INTO/UPDATE today. `WHERE `, `ON `, `AND `/`OR `,
  `SELECT ` and a comma inside a select list are just as predictable (the
  statement's own columns are already scoped and floated there) but
  currently require Ctrl+Space.
- [x] **Auto-alias on table accept** — accepting `sales.orders` after
  FROM/JOIN could also insert a short alias (`o`, dedup as `o2`…) so the
  `o.` member-access flow is immediately available; make it a setting.
- [x] **Auto-close pairs** — `(`, `'`, `"` should insert their closer with
  type-over on the closing character; today `coalesce()` from a completion
  accept is the only paired insert in the editor.
- [x] **Completion popup visual polish** — stock AvaloniaEdit look: no kind
  icons, no type column. Give items a kind glyph + color (table / column /
  function / keyword / schema / alias / CTE), right-align the column data
  type that's currently buried in the description panel, and restyle the
  card (radius, shadow, padding) to match the Files-style shell in both
  themes.
- [x] **CTE output columns** — `WITH recent AS (SELECT id, total FROM orders)`
  followed by `recent.` used to complete nothing; now the CTE's declared
  column list or SELECT-list aliases complete like a table's columns
  (`SELECT *` bodies resolve through the source tables' catalog columns,
  chained/recursive CTEs included), and WHERE over a CTE narrows to them.
- [x] **`SELECT *` expansion** — "Expand SELECT * into columns" in the
  command palette replaces the statement's `*` / `alias.*` with the explicit
  column list of its FROM tables (qualified by alias when the statement joins
  more than one; CTEs resolve through their derived output columns).
  All-or-nothing: an unresolvable table means no rewrite, never a wrong one.
  Deliberately palette-only — no popup item on every typed `*`.

### Next — the gaps users of competing tools keep hitting

Sourced from a research pass (2026-07) over what people praise, request, and
abandon tools over: Hacker News client threads
([Good UI for PostgreSQL?](https://news.ycombinator.com/item?id=33776831),
[What PostgreSQL client do you use?](https://news.ycombinator.com/item?id=23208181),
[Show HN: DB Pro](https://news.ycombinator.com/item?id=46078571)), the
top-👍 issues on the TablePlus and Beekeeper Studio trackers, and what
Postico/DataGrip users single out. Multi-database support dominates those
trackers and is deliberately out of scope — PostgreSQL-first *is* the thesis.
Everything below is what survives that filter, roughly in impact order:

- [ ] **Pending-changes review ("safe mode")** — stage grid edits, inserts,
  and deletes locally, highlight the dirty cells/rows, show the generated SQL
  for review, then commit everything as one transaction (or discard). Today
  each inline edit fires immediately, which is scary on production; a staged
  mode is TablePlus's single most-praised trust feature. The transaction
  machinery (held session connection, auto-rollback) is most of the plumbing
  already.
- [ ] **Follow a foreign key from the grid** — a result/browse cell whose
  column has an FK gets a click-through (context menu or Ctrl+click) that
  jumps to the referenced row in browse mode, plus the reverse ("rows
  referencing this one") from a key cell. The most-praised "little feature"
  in DBeaver's HN comments, and the heart of Postico's row editing (FK
  picker). The FK edges are already loaded for JOIN completion — reuse them.
- [ ] **Workspace restore** — reopen the last session's tabs, including
  never-saved SQL, exactly as they were — no save prompts on exit
  (Notepad++-style, called out on HN as what makes DBeaver safe to close).
  Persist per-connection alongside history in `AppSettings`-style JSON.
- [ ] **Open/save `.sql` files** — Ctrl+O / Ctrl+S on a tab, a recent-files
  list in the palette, and a dirty marker that distinguishes "unsaved
  scratch" from "file changed on disk". A top-voted Beekeeper Studio ask
  (it appears twice in their top 20).
- [ ] **Multiple simultaneous connections** — today the ⇄ switch *replaces*
  the connection; users working against dev + prod side by side want both at
  once (top-10 Beekeeper ask). Cleanest fit for the one-window shell:
  connection-per-window, with the palette able to open a profile in a new
  window. Per-connection accent colors already exist to keep them apart.
- [ ] **Auto-reconnect** — after laptop sleep or an SSH-tunnel drop, the next
  run should quietly reopen the connection instead of surfacing a broken-pipe
  error (or hanging — an HN complaint about other clients). Needs care with
  the held transaction connection: an open transaction can't be silently
  re-established, so that path surfaces a clear "transaction lost" state.
- [ ] **Postgres-native value editing** — the grid and Add-row dialog treat
  every column as text today. Make the editors type-aware: `enum` columns get
  a dropdown of `pg_enum` labels, `boolean` a checkbox, `date/timestamp` a
  picker, arrays and composites get validation, domains resolve to their base
  type. DataGrip's missing array support was a stated deal-breaker on HN, and
  ENUM dropdowns / user-defined types are open TablePlus asks — exactly the
  "PostgreSQL-first, not lowest-common-denominator" differentiator.
- [ ] **Table & index sizes and usage** — sizes in the schema tree (tooltip or
  detail column) and a per-database overview: largest tables/indexes
  (`pg_total_relation_size`), seq-vs-index scan counts, unused indexes, cache
  hit rate (`pg_stat_user_tables`/`_indexes`, `pg_statio_*`). "No list of
  tables or indexes with their sizes and usage" is a direct HN complaint
  about DataGrip; pgAdmin buries it. Read-only `pg_catalog` queries — squarely
  in `SchemaService`/`ActivityService` territory.
- [ ] **Locks & blocking tree** — extend the server-activity window with a
  who-blocks-whom view (`pg_locks` joined to `pg_stat_activity`, or
  `pg_blocking_pids()`), so a stuck migration is a one-click
  cancel/terminate of the *blocker*. The signal plumbing
  (`CancelBackendAsync`/`TerminateBackendAsync`) already exists.
- [ ] **Row detail sidebar** — a vertical name/value view of the selected row
  (Postico's much-loved "row sidebar"), for tables too wide to read as a grid
  row; doubles as a form-style editor and complements the cell inspector.
- [ ] **Find & replace in the editor** — AvaloniaEdit ships a `SearchPanel`;
  wire it up (Ctrl+F / Ctrl+H via `Hotkeys.cs`) and restyle it to match the
  shell. A standing Beekeeper ask; table-stakes for an editor.
- [ ] **Linux builds** — the linux-x64 NativeAOT publish already works (it's
  how the app is exercised in CI sandboxes and benchmarks); what's missing is
  a release-pipeline leg and packaging. Flatpak is the #2 top-voted Beekeeper
  Studio request, and HeidiSQL adding native Linux builds in late 2025 shows
  the demand; start with an AppImage or tarball from `release.yml`, Flatpak
  after.

### Later — bigger bets

- [ ] **ER diagram** — auto-laid-out foreign-key graph of a schema, exportable
  as SVG.
- [ ] **EXPLAIN plan diffing** — run the same query twice (e.g. before/after an
  index) and diff the plan trees node-by-node.
- [ ] **Backup/restore UI** — `pg_dump`/`pg_restore` orchestration with
  progress streaming.
- [ ] **AI, privacy-first** — the 2026 table stakes in every competitor is a
  schema-aware assistant; the differentiator for an OSS client is doing it
  without a data-leak worry: bring-your-own-key (or local model), explicit
  opt-in, nothing leaves the machine otherwise. Two shapes, possibly both:
  an in-app "explain this query / write this query" assistant fed the same
  completion catalog, and/or a built-in MCP server exposing the current
  connection so Claude Code / Cursor / VS Code can query it (TablePlus has an
  open ask for exactly this).
- [ ] **Vim keybindings in the editor** — the single most-upvoted
  editor-related request on TablePlus's tracker (180+ 👍); a modal-editing
  layer over AvaloniaEdit, opt-in from Preferences.
- [ ] **Parameterized queries** — recognize `:name` / `$1` placeholders and
  prompt for values on run (remembering the last ones), so shared saved
  queries stop being edit-before-every-run.
- [ ] **Quick chart of a result set** — one click from a result grid to a
  bar/line/scatter view of two chosen columns; DataGrip's inline charts get
  cited as the reason analysts stay there.
- [ ] **PostGIS geometry viewer** — render `geometry`/`geography` cells on a
  map/canvas the way the cell inspector renders JSON; the one PostGIS
  feature HN users single out pgAdmin and DBeaver for having.
- [ ] **Notebook mode** — mixed SQL + Markdown documents with inline result
  snapshots, saved as shareable files.
- [ ] **Plugin/extension API** — a stable surface for community panels
  (initially: custom result visualizers).
- [ ] **Localization** — externalize UI strings; ship Russian and German first.

## License

MIT — see [LICENSE](LICENSE).
