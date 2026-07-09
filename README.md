<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="design/logo-dark.png">
    <img src="design/logo-light.png" alt="pgNimbus logo — an elephant riding a broom" width="180">
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
  Apple hasn't sold an Intel Mac since 2023). Unsigned/unnotarized:
  right-click the app → "Open" the first time to bypass Gatekeeper.

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

## License

MIT — see [LICENSE](LICENSE).
