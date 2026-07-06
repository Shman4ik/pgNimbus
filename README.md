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

## Features

- **Schema tree sidebar** — schemas → tables/views → columns, reading
  `pg_catalog` directly (materialized views, partitioned tables, real
  primary-key flags), with an "Alter Table" UI for no-SQL column add/rename/drop.
- **No-SQL table browsing** — previewing a table opens a browse bar with a
  `WHERE` filter, `ORDER BY` from clicking a column header, and prev/next
  paging — all pushed down to Postgres (`WHERE`/`ORDER BY`/`LIMIT`/`OFFSET`),
  so browsing a huge table stays as cheap as one page.
- **Connection manager** — saved profiles with a per-connection accent color
  (so production doesn't look like staging), SSH tunnel support, and
  passwords held by the OS credential store (DPAPI on Windows) instead of
  being written to disk with the profile.
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
- **Multi-tab query editor** — schema-aware SQL autocomplete, saved queries,
  and run history.
- **Streaming, cancellable results** — the first screenful renders before the
  full result set arrives, backed by a virtualized grid with inline cell
  editing and CSV/JSON export.
- **EXPLAIN visualization** — a graphical plan tree for `EXPLAIN` and
  `EXPLAIN ANALYZE`, not just raw text output.
- **LISTEN/NOTIFY monitor** — subscribe to channels and watch notifications
  arrive live.

## Keyboard shortcuts

Press <kbd>F1</kbd> in the app for the full cheat sheet. The highlights:

| Action | Shortcut |
| --- | --- |
| Command palette (jump to table / query / action) | <kbd>Ctrl</kbd>+<kbd>K</kbd> or <kbd>Ctrl</kbd>+<kbd>P</kbd> |
| Run query | <kbd>Ctrl</kbd>+<kbd>Enter</kbd> or <kbd>F5</kbd> |
| Cancel running query | <kbd>Esc</kbd> |
| New / close query tab | <kbd>Ctrl</kbd>+<kbd>T</kbd> / <kbd>Ctrl</kbd>+<kbd>W</kbd> |
| Next / previous tab | <kbd>Ctrl</kbd>+<kbd>PageDown</kbd> / <kbd>Ctrl</kbd>+<kbd>PageUp</kbd> |
| SQL autocomplete | <kbd>Ctrl</kbd>+<kbd>Space</kbd> (also triggers while typing) |
| Switch focus: editor ↔ results grid | <kbd>F6</kbd> |
| Edit selected result cell | <kbd>F2</kbd>, then <kbd>Enter</kbd> to commit / <kbd>Esc</kbd> to cancel |
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

- [x] **Command palette** (`Ctrl+K`/`Ctrl+P`) — fuzzy-jump to any table, saved
  query, or action; the keyboard-first differentiator in one control.
- [x] **Data browsing without SQL** — filter bar, ORDER BY on header click, and
  paging when previewing a table, pushed down to the server (`WHERE`/`LIMIT`/
  `OFFSET`), not client-side.
- [ ] **Row insert & delete from the grid** — cell editing exists; complete the
  CRUD triangle with "add row" and "delete selected rows" for tables with a
  primary key.
- [ ] **Multiple result sets per script** — run a whole script; each statement
  gets its own result tab/section, with per-statement timings.
- [ ] **DDL view** — a "Source" tab per object: reconstructed
  `CREATE TABLE`/`CREATE VIEW`/index definitions from `pg_catalog`.
- [ ] **Windows installer + releases** — signed MSI/winget package and a CI
  pipeline that publishes NativeAOT builds per tag.

### Next — depth on the Postgres-first promise

- [ ] **Transaction control** — explicit BEGIN/COMMIT/ROLLBACK toolbar state,
  with a visible "in transaction" indicator and auto-rollback on error.
- [ ] **SQL formatting** — one-keystroke pretty-printing of the current
  statement.
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
  with it.

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
- [ ] **Cell inspector** — a detail pane (or popover) for the selected cell so
  long `text`/`jsonb` values are readable and copyable without inline-edit
  tricks; pretty-print JSON.
- [ ] **Set a cell to NULL from the grid** — inline editing can't express
  "make it NULL" today (empty string ≠ NULL); needs an explicit gesture or
  context-menu action.
- [ ] **Editor niceties** — current-line highlight, matching-bracket
  highlight, and font-size zoom (<kbd>Ctrl</kbd>+wheel /
  <kbd>Ctrl</kbd>+<kbd>±</kbd>).
- [ ] **Alias-aware autocomplete** — complete column names after
  `alias.`/`table.`, not just bare identifiers.
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

Recently shipped: no-SQL table browsing (server-side WHERE filter, header-click
ORDER BY, LIMIT/OFFSET paging), the Ctrl+K/Ctrl+P command palette (fuzzy-jump to
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
