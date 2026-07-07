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

1. **Native performance** — measured launch-to-window: ~100 ms as a NativeAOT
   binary, ~0.7 s under JIT (Release, Linux container, 5-run spread).
2. **PostgreSQL-first** — deep `pg_catalog` introspection (materialized views,
   real types, primary-key flags, and EXPLAIN visualization); never the
   lowest-common-denominator SQL dialect.
3. **Keyboard-first** — run, cancel, and navigate without touching the mouse.
4. **Streaming results** — the first screenful renders before the full result
   set arrives, backed by a virtualized grid for large results.

## Screenshots

| Query editor + results (light) | Query editor + results (dark) |
| --- | --- |
| ![Main window, light theme](docs/screenshots/main-light.png) | ![Main window, dark theme](docs/screenshots/main-dark.png) |

| Connection manager | Keyboard shortcuts (F1) |
| --- | --- |
| ![Connection dialog with saved profiles and paste-anything import](docs/screenshots/connection-dialog.png) | ![Keyboard shortcuts cheat sheet](docs/screenshots/shortcuts.png) |

## Download

Grab the latest build from [Releases](https://github.com/Shman4ik/pgNimbus/releases):

- **Windows** — `pgNimbus-<version>-win-x64.msi`, a per-user installer (no
  admin rights needed). It's unsigned for now, so Windows SmartScreen will
  warn on first run — click "More info" → "Run anyway".
- **macOS** — `pgNimbus-<version>-macos-arm64.dmg` (Apple Silicon only;
  GitHub retired hosted Intel macOS runners in December 2025, and Apple
  hasn't sold an Intel Mac since 2023). Unsigned/unnotarized: right-click
  the app → "Open" the first time to bypass Gatekeeper.
- **winget** — a manifest is generated per release but not yet submitted to
  the community `winget-pkgs` repo; `winget install` support is coming once
  that's done.

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
- **Multi-tab query editor** — schema-aware SQL autocomplete (schema-qualified
  tables after `FROM`/`JOIN`, `FROM`-scoped columns in `WHERE`/`ON`/`ORDER BY`,
  columns with their data types elsewhere, `alias.` member access, CTE names,
  common functions inserted as calls), saved queries,
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
- **LISTEN/NOTIFY monitor** — subscribe to channels and watch notifications
  arrive live.

## Keyboard shortcuts

Press <kbd>F1</kbd> in the app for the full cheat sheet. The highlights:

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
└── PgNimbus.App/                 # Avalonia MVVM front-end.
    ├── ViewModels/QueryViewModel.cs
    ├── Views/MainWindow.axaml(.cs)
    ├── App.axaml(.cs)
    └── Program.cs
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

The linux-x64 AOT binary is exercised as part of development: it launches to a
window in ~100 ms and the query grid, EXPLAIN visualization, and profile
stores all work (JSON persistence uses source-generated serializer contexts,
and the results grid binds columns via converters instead of reflection
paths, both of which trimming/AOT require).

## Backlog

Prioritized by how much they advance the thesis (fast + open + modern,
PostgreSQL-first). Contributions welcome — these are intentionally scoped as
individually shippable pieces.

### Now — the daily-driver gap

Things a person needs before pgNimbus can be their only Postgres client:

- [x] **Refresh database & schema** — reload the schema tree and autocomplete
  cache from the server on demand (toolbar button / shortcut), so newly created
  or altered tables, columns, and other objects show up without reconnecting.
- [x] **Command palette** (`Ctrl+K`/`Ctrl+P`) — fuzzy-jump to any table, saved
  query, or action; the keyboard-first differentiator in one control.
- [x] **Data browsing without SQL** — filter bar, ORDER BY on header click, and
  paging when previewing a table, pushed down to the server (`WHERE`/`LIMIT`/
  `OFFSET`), not client-side.
- [x] **Row insert & delete from the grid** — cell editing exists; complete the
  CRUD triangle with "add row" and "delete selected rows" for tables with a
  primary key.
- [x] **Multiple result sets per script** — run a whole script; each statement
  gets its own result tab/section, with per-statement timings.
- [x] **DDL view** — a "Source" tab per object: reconstructed
  `CREATE TABLE`/`CREATE VIEW`/index definitions from `pg_catalog`.
- [x] **Windows installer + releases** — a per-user MSI and a macOS `.dmg`
  built and published by CI on every tag
  ([`release.yml`](.github/workflows/release.yml)), plus generated winget
  manifests.
- [x] **From-scoped WHERE suggestions** — column autocomplete in the query
  editor's `WHERE`/`ON`/`HAVING`/`GROUP BY`/`ORDER BY` clauses now offers only the
  columns of the tables in the statement's `FROM` (plus its aliases, CTEs,
  functions and keywords) instead of the entire schema; it falls back to the full
  catalog when no `FROM` table has resolved yet, so an incomplete query is never
  left with a near-empty list.
- [x] **Automatic schema completion** — accepting a table in table position
  (after `FROM`/`JOIN`/`INTO`/`UPDATE`) inserts it schema-qualified
  (`public.users`, `analytics.events`), so the reference resolves regardless of
  the server's `search_path`; the bare name still filters the list, and a table
  referenced elsewhere (or picked after typing `schema.`) stays unqualified.
- [ ] **Code signing** — Authenticode for the MSI, Developer ID +
  notarization for the `.dmg`. Both ship unsigned today, so SmartScreen and
  Gatekeeper warn on first run — the single biggest first-impression blocker
  for a production release. The pipeline already has the slot for it; needs
  a cert and an Apple developer account.
- [ ] **winget submission** — manifests are generated and validated per
  release, but the first manual `winget-pkgs` PR (which registers the
  `pgNimbus.pgNimbus` identifier) hasn't been made yet.

### Next — depth on the Postgres-first promise

- [ ] **Transaction control** — explicit BEGIN/COMMIT/ROLLBACK toolbar state,
  with a visible "in transaction" indicator and auto-rollback on error.
- [x] **SQL formatting** — one-keystroke pretty-printing of the statement under
  the cursor (<kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F</kbd>, or "Format SQL" in the
  command palette): each clause on its own line, select-list/`SET`/`GROUP BY`
  items and JOINs broken one-per-line, `AND`/`OR` predicates stacked, subqueries
  indented, and reserved keywords upper-cased. It re-tokenizes its own output and
  compares it to the input, so if a layout would ever alter a token it returns the
  text untouched — it can never corrupt a query.
- [ ] **CSV/JSON import** — the inverse of export: load a file into a new or
  existing table with type inference.
- [ ] **Server activity dashboard** — `pg_stat_activity` live view with
  cancel/terminate backend actions; lock waits highlighted.
- [ ] **Roles, extensions, and functions in the schema tree** — browse (and for
  extensions, install/enable) beyond tables and views.
- [ ] **Query history search** — full-text search over history with
  per-connection scoping and pinning.
- [x] **In-app theme toggle** — light/dark switch in the title bar (sun/moon
  button) instead of following the OS only; the SQL syntax palette repaints
  with it, and the choice is remembered across launches.

### Polish — UX/UI fit and finish

Small, individually shippable refinements toward the TablePlus-level polish
bar (the Files community app remains the visual north star):

- [x] **Schema-tree filter box** — type-to-filter above the sidebar tree
  (schemas and loaded tables, case-insensitive substring); a schema stays
  when its name matches or a loaded table inside it does, auto-expanding to
  reveal the match, with a clear (✕) button.
- [x] **Copy from the results grid** — <kbd>Ctrl</kbd>+<kbd>C</kbd> copies the
  selected rows (or all rows) as TSV, plus "Copy as" (CSV, JSON, Markdown table,
  `INSERT` statements) on the grid context menu.
- [x] **Cell inspector** — a detail pane (or popover) for the selected cell so
  long `text`/`jsonb` values are readable and copyable without inline-edit
  tricks; pretty-print JSON. Word wrap is on by default (Notepad++-style),
  with a "Wrap" toggle in the header, and non-ASCII text (e.g. Cyrillic)
  displays as itself rather than `\uXXXX` escapes.
- [x] **Set a cell to NULL from the grid** — inline editing can't express
  "make it NULL" (empty string ≠ NULL); a "Set cell to NULL" context-menu
  action on the results grid issues the targeted UPDATE.
- [x] **Editor niceties** — current-line highlight, matching-bracket
  highlight, and font-size zoom (<kbd>Ctrl</kbd>+wheel /
  <kbd>Ctrl</kbd>+<kbd>±</kbd>, <kbd>Ctrl</kbd>+<kbd>0</kbd> resets).
- [x] **Alias-aware autocomplete** — complete column names after
  `alias.`/`table.`, not just bare identifiers.
- [x] **Context-aware IntelliSense** — inside a `SELECT`, rank the current
  table's columns first and hide noise like `pg_catalog`; in the results-grid
  `WHERE` filter box, suggest *only* the current dataset's columns (no SQL
  functions or unrelated tables).
- [x] **Clause-aware IntelliSense** — the list opens by itself after
  `FROM`/`JOIN`/`INTO`/`UPDATE` and offers only what can go there (tables,
  schemas, the statement's CTEs — no columns); column suggestions show their
  data type, the statement's aliases complete too, common functions insert as
  `name()` with the caret between the parens, and nothing pops up inside
  string literals or comments.
- [x] **`Shift`+`Enter` smart execution** — run with <kbd>Shift</kbd>+<kbd>Enter</kbd>
  (alongside <kbd>Ctrl</kbd>+<kbd>Enter</kbd>/<kbd>F5</kbd>), executing just the
  statement the cursor sits in (between `;`s) without having to select it first.
- [x] **Tab bar overflow scrolling** — with many tabs open, the strip now
  scrolls horizontally and keeps the active tab in view. (It used to clip:
  the "+" button overlapped the last visible tab and a newly opened tab
  could sit fully off-screen with no way to reach it.)
- [ ] **Tab bar navigation extras** — `<`/`>` scroll arrows and a dropdown
  listing all open tabs with type-to-search, on top of the basic scrolling.
- [x] **Capped results-grid column width** — auto column width sizes to the
  widest cell, so a single long `text` value used to push every other column
  out of view; columns now cap at 560 px, with the cell inspector
  (double-click) carrying the full value.
- [x] **Content-sized cell inspector** — the inspector card sizes to its
  value (small values get a small card) instead of always opening at full
  height.
- [x] **Window minimum size** — a 940×560 floor, below which the command bar
  and browse bar used to clip into unreadability.
- [ ] **Empty state for the connection dialog** — the Saved Connections list
  is a bare grey panel when empty; give it the same friendly hint the
  saved-queries and history lists already have.
- [x] **Persist the theme choice** — the in-app light/dark toggle is now
  remembered across launches (saved to `settings.json` alongside the other
  persisted app state) instead of snapping back to the OS default; a fresh
  install with no saved choice still follows the OS.
- [ ] **Drag-and-drop from the schema tree** — drag a table, column, or other
  object from the sidebar tree into the SQL editor and drop a valid, quoted
  identifier at the cursor (e.g. `"CreatedAt"`).
- [x] **Collapsible sidebar** — a 200px minimum width on manual resize (so it
  can't shrink to an unreadable sliver) plus a collapse button (the ☰ in the
  title bar) / <kbd>Ctrl</kbd>+<kbd>B</kbd> that fully hides the sidebar and gives
  the editor and results the full width, restoring to the last dragged width.
- [ ] **Abbreviated column types in the tree** — show long types compactly
  (e.g. `timestamp with time zone` → `timestamptz`), with the full type name in
  a tooltip on a ~150 ms hover delay.
- [x] **Smarter tab titles** — query tabs are named from their SQL (first table
  referenced) instead of "Query N", with a dirty-state dot when the SQL has
  changed since the last run.
- [ ] **Running-query feedback** — an indeterminate progress bar in the
  status bar and a live elapsed-time tick while a query runs (the row/timing
  segments only update per batch today).
- [x] **Empty states** — friendly hints in the blank results area ("No results
  yet — run a query with Ctrl+Enter or F5") and in empty saved-queries/history
  lists instead of bare cards.
- [ ] **Mica/acrylic backdrop on Windows** — the two-tone shell is ready for
  it; deliberately deferred until it can be verified on a real Windows
  desktop (transparency fallbacks can't be seen headless).

### Later — bigger bets

- [ ] **ER diagram** — auto-laid-out foreign-key graph of a schema, exportable
  as SVG.
- [ ] **EXPLAIN plan diffing** — run the same query twice (e.g. before/after an
  index) and diff the plan trees node-by-node.
- [ ] **Backup/restore UI** — `pg_dump`/`pg_restore` orchestration with
  progress streaming.
- [ ] **Notebook mode** — mixed SQL + Markdown documents with inline result
  snapshots, saved as shareable files.
- [ ] **Plugin/extension API** — a stable surface for community panels
  (initially: custom result visualizers).
- [ ] **Localization** — externalize UI strings; ship Russian and German first.

Recently shipped: a collapsible sidebar (Ctrl+B, 200px resize floor), a
remembered-across-launches theme choice, one-keystroke SQL
formatting (Ctrl+Shift+F, block-style
pretty-print with a never-corrupt token round-trip check), FROM-scoped
WHERE/ORDER BY column suggestions and
schema-qualified table completion after FROM/JOIN, tab-strip overflow scrolling,
capped results-grid column
widths, a content-sized cell inspector, a window minimum size,
clause-aware SQL IntelliSense (tables after FROM/JOIN,
typed columns, aliases, CTEs, function-call insertion, auto-popup, no popups
inside strings/comments), editor niceties (current-line highlight,
matching-bracket highlight, Ctrl+wheel / Ctrl+± font zoom), "Set cell to
NULL" on the results grid, DDL "Source" view (reconstructed CREATE TABLE/VIEW +
constraints + indexes from pg_catalog, opened in a new tab), multiple result
sets per script (one shared connection,
per-statement result sections + timings, stop-on-error), on-demand database &
schema refresh (tree + autocomplete +
palette), grid CRUD (add-row dialog + delete selected rows), no-SQL
table browsing (server-side WHERE filter, header-click ORDER BY, LIMIT/OFFSET
paging), the Ctrl+K/Ctrl+P command palette (fuzzy-jump to
any table, saved query, or action), SQL-derived tab titles with a dirty dot, results-grid copy
(Ctrl+C / Copy as CSV·JSON·Markdown·INSERT),
empty-state hints, the in-app light/dark theme toggle, the
schema-tree filter
box, paste-anything connection
string parsing (URI / JDBC /
ADO.NET / libpq / psql), the F1 shortcuts cheat sheet, theme-aware SQL
syntax highlighting, the segmented status bar, keyboard tab navigation,
EXPLAIN visualization, LISTEN/NOTIFY monitor, SSH tunnels, and the
Files-style two-tone UI.

## License

MIT — see [LICENSE](LICENSE).
