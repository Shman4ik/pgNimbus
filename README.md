<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="design/masters/logo/wordmark-dark.png">
    <img src="design/masters/logo/wordmark-light.png" alt="pgNimbus logo — an elephant riding a broom" width="300">
  </picture>
</p>

<h1 align="center">pgNimbus</h1>

<p align="center">
  <b>A blazing-fast, open-source PostgreSQL GUI client with a modern, native UI.</b><br>
  Launches in ~100 ms. Streams results before your query finishes. Sends zero telemetry.
</p>

<p align="center">
  <a href="https://github.com/Shman4ik/pgNimbus/releases"><img src="https://img.shields.io/github/v/release/Shman4ik/pgNimbus?label=release" alt="Latest release"></a>
  <a href="https://apps.microsoft.com/detail/9N6SZT42XJ24"><img src="https://img.shields.io/badge/Microsoft%20Store-install-0078D4?logo=windows" alt="Microsoft Store"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT license"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet" alt=".NET 10">
  <img src="https://img.shields.io/badge/Avalonia-12-8B44AC" alt="Avalonia 12">
  <img src="https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey" alt="Platforms">
</p>

<p align="center">
  <a href="https://shman4ik.github.io/pgNimbus/">Website</a> ·
  <a href="#-installation">Installation</a> ·
  <a href="#-quick-start">Quick Start</a> ·
  <a href="#-features">Features</a> ·
  <a href="#-benchmarks">Benchmarks</a> ·
  <a href="#-roadmap">Roadmap</a>
</p>

---

## 🎯 Why pgNimbus?

The PostgreSQL client market has a gap: **truly fast + open source + modern UI**.

- **pgAdmin / DBeaver** — powerful, but heavy and slow.
- **TablePlus** — fast and polished, but closed-source and paid.
- **Beekeeper Studio** — open source, but Electron.
- **HeidiSQL** — native and fast, but dated and MySQL-first.

pgNimbus delivers HeidiSQL's speed with TablePlus's polish — **PostgreSQL-first, from the ground up**. Built with .NET 10 + Avalonia 12, compiled to a NativeAOT binary, MIT licensed.

## 🎬 See It in Action

| Instant launch (NativeAOT) | SQL completion that predicts the next move |
| --- | --- |
| ![pgNimbus launching from a cold NativeAOT process to a fully rendered main window in well under a second](docs/screenshots/cold-start.gif) | ![Typing FROM + a partial table name, JOIN with an FK-ranked table suggestion, then ON auto-completing the full join condition](docs/screenshots/completion-demo.gif) |

## 📦 Installation

### Microsoft Store (recommended)

The Store package is signed and auto-updated — no SmartScreen warnings.

**[Get pgNimbus on the Microsoft Store →](https://apps.microsoft.com/detail/9N6SZT42XJ24)**

### WinGet

```powershell
winget install pgNimbus --source msstore
```

### Direct download (MSI)

Grab `pgNimbus-<version>-win-x64.msi` from [Releases](https://github.com/Shman4ik/pgNimbus/releases) — a per-user installer, no admin rights needed.

> [!NOTE]
> The direct MSI is unsigned, so SmartScreen will warn on first run — click **More info → Run anyway**, or prefer the Store/WinGet path above.

### macOS (early beta)

`pgNimbus-<version>-macos-arm64.dmg` from [Releases](https://github.com/Shman4ik/pgNimbus/releases) (Apple Silicon only). The beta is unsigned and unnotarized, so Gatekeeper shows a misleading *"pgNimbus is damaged"* dialog on first launch.

<details>
<summary>Fixing the Gatekeeper "App is damaged" error</summary>

The file is safe — this is standard macOS behavior for unsigned apps. Clear the quarantine flag once per downloaded update:

```bash
# If you moved the app to the Applications folder:
xattr -cr /Applications/pgNimbus.app

# If the app is still in your Downloads folder:
xattr -cr ~/Downloads/pgNimbus.app
```

Then launch `pgNimbus.app` normally. Proper signing and notarization are planned — see [Roadmap](#-roadmap).

</details>

### Linux (early beta)

x64 and arm64 builds from [Releases](https://github.com/Shman4ik/pgNimbus/releases), in three formats:

- **AppImage** (any distro) — `chmod +x pgNimbus-<version>-linux-<arch>.AppImage`, then run it. Nothing to install.
- **Debian/Ubuntu** — `sudo apt install ./pgNimbus-<version>-linux-<arch>.deb`, then launch `pgnimbus` (or find pgNimbus in your app menu).
- **tar.gz** — unpack anywhere and run `./PgNimbus.App`.

Every `vX.Y.Z` tag builds all of the above via [`release.yml`](.github/workflows/release.yml).

## 🚀 Quick Start

1. **Launch pgNimbus** — the connection dialog opens first.
2. **Paste any connection string** into the box at the top; the form fills itself. All common syntaxes work:

   ```text
   postgres://alice:s3cret@db.example.com:5433/appdb?sslmode=require
   jdbc:postgresql://db.example.com:5433/appdb?user=alice&ssl=true
   Host=db.example.com;Port=5433;Database=appdb;Username=alice;Password=s3cret
   host=db.example.com port=5433 dbname=appdb user=alice sslmode=require
   PGPASSWORD=s3cret psql -h db.example.com -p 5433 -U alice appdb
   ```

3. **Connect** — your password goes to the OS credential store (DPAPI on Windows), never to disk.
4. **Run a query** with <kbd>Ctrl</kbd>+<kbd>Enter</kbd>, jump anywhere with the command palette (<kbd>Ctrl</kbd>+<kbd>K</kbd>), and press <kbd>F1</kbd> for the full shortcut cheat sheet.

For scripted or repeated local testing, set `PGNIMBUS_CONN` (same formats as the paste box) to skip the dialog entirely:

```bash
export PGNIMBUS_CONN="postgres://postgres:secret@localhost:5432/mydb"
dotnet run --project PgNimbus.App
```

## ✨ Features

### ⚡ Fast & Dependable

- **~100 ms launch-to-window** as a NativeAOT binary — measured on every release, not asserted ([Benchmarks](#-benchmarks)).
- **Streaming, cancellable results** — the first screenful renders before the full result set arrives, backed by a virtualized grid; <kbd>Esc</kbd> genuinely stops a query mid-flight.
- **Auto-reconnect** — a connection dropped by laptop sleep or an SSH-tunnel hiccup quietly reopens on the next run. An open explicit transaction is never silently re-established; it surfaces a clear "connection lost, nothing committed" state instead.
- **Workspace restore** — closing the app never prompts; the next session reopens your tabs, including never-saved scratch SQL, exactly as you left them.
- **Zero telemetry** — no analytics, no crash reporting, no update pings. The only connections the app opens are the ones you configure ([Privacy](#-privacy)).

### ✏️ A Smarter SQL Editor

- **Schema-aware autocomplete** — schema-qualified tables after `FROM`/`JOIN`, scoped columns in `WHERE`/`ON`/`ORDER BY`, `alias.` member access, CTE output columns (including `SELECT *` bodies resolved through the catalog), and user-defined functions with signature tooltips.
- **FK-aware JOIN magic** — after `JOIN`, tables connected by a foreign key rank first; after `ON`, the complete join condition (`oi.order_id = o.id`) is the top, one-keystroke suggestion.
- **SQL formatting** — <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F</kbd> pretty-prints the statement under the cursor; a token round-trip self-check guarantees only whitespace ever changes.
- **Script execution** — run several `;`-separated statements on one connection (`BEGIN…COMMIT`, `SET`, and temp tables carry across), each with its own result section and timing, stopping at the first error.
- **Multi-tab editor** with find & replace, current-line and bracket highlighting, font-size zoom, and `SELECT *` expansion into an explicit column list.
- **Open/save `.sql` files** — <kbd>Ctrl</kbd>+<kbd>O</kbd>/<kbd>Ctrl</kbd>+<kbd>S</kbd>, a recent-files list in the palette, and a dirty marker that distinguishes "unsaved scratch" from "diverges from disk".
- **Query history** — searchable, pinnable, scoped per connection; entries open in a new tab.
- **Command palette** — <kbd>Ctrl</kbd>+<kbd>K</kbd> fuzzy-jumps to any table, saved query, or action without touching the mouse.

### 🧮 Data Editing Without Fear

- **Safe mode (pending-changes review)** — stage grid edits, inserts, and deletes locally: dirty rows are highlighted (amber = edited, red = delete), "Review & commit…" shows the exact generated SQL, and everything applies as **one transaction** — or gets discarded with nothing ever sent. Built for the "inline edit on production" nerves.
- **No-SQL table browsing** — paged browsing with click-to-sort headers, all pushed down to Postgres (`ORDER BY`/`LIMIT`/`OFFSET`), so a huge table stays as cheap as one page. The composed SQL sits in the editor and doubles as the filter — add a `WHERE` and run.
- **Follow foreign keys from the grid** — right-click an FK cell to jump to the row it references, or a key cell to list all referencing rows, each hop opening a pre-filtered browse tab.
- **Full grid CRUD** — inline cell editing, an "Add row" dialog with server-side type casts, delete with confirmation, and "Set cell to NULL". Hand-typed `SELECT`s become editable too whenever the wire metadata proves it's safe.
- **Postgres-native value editors** — `enum` columns get a dropdown of their `pg_enum` labels, `boolean` a checkbox, `date`/`timestamp` a calendar picker; arrays and composites are syntax-checked before anything is sent, and domains resolve to their base type.
- **Transaction control** — an explicit **Begin/Commit/Rollback** flow on one held connection, with a status-bar indicator and automatic rollback on failure so you're never stranded in an aborted-transaction state.
- **Cell inspector** — double-click any cell to read the full value in an overlay, with JSON pretty-printed and one-click copy.
- **Import & export** — CSV/JSON import streamed via `COPY` with type inference; copy results as TSV, CSV, JSON, Markdown table, or `INSERT` statements.

### 🐘 PostgreSQL-First Tooling

- **Real `pg_catalog` introspection** — the schema tree sees materialized views, partitioned tables, and true primary-key flags; never the lowest-common-denominator `information_schema`.
- **DDL reconstruction** — a "Source (DDL)" action rebuilds an object's `CREATE TABLE`/`CREATE VIEW` — columns, defaults, identity, constraints, partition key, indexes — into a new tab; an "Alter Table" UI covers no-SQL column changes.
- **EXPLAIN visualization** — a graphical plan tree for `EXPLAIN` and `EXPLAIN ANALYZE` with per-node cost and timing, not just raw text.
- **Server activity dashboard** — a live `pg_stat_activity` view with per-backend **cancel statement** and **terminate session**, so a runaway query is one click to stop.
- **LISTEN/NOTIFY monitor** — subscribe to channels and watch notifications arrive live.
- **Connection manager** — saved profiles with per-connection accent colors (so production never looks like staging), SSH tunnels, and passwords held by the OS credential store — never written to disk.
- **Multiple simultaneous connections** — open profiles in separate self-contained windows (own pool, listener, tunnel, workspace), so dev and prod sit side by side; or switch the current window's connection without restarting.

## 📸 Screenshots

| Query editor + results (light) | Query editor + results (dark) |
| --- | --- |
| ![Main window, light theme](docs/screenshots/main-light.png) | ![Main window, dark theme](docs/screenshots/main-dark.png) |

| EXPLAIN ANALYZE visualization | Command palette (Ctrl+K) |
| --- | --- |
| ![Graphical EXPLAIN ANALYZE plan tree with per-node cost and timing](docs/screenshots/explain-visualization.png) | ![Command palette fuzzy-jumping to a table](docs/screenshots/command-palette.png) |

| Server activity (pg_stat_activity) | Connection manager |
| --- | --- |
| ![Server activity window showing a live backend and its wait event](docs/screenshots/server-activity.png) | ![Connection dialog with saved profiles and paste-anything import](docs/screenshots/connection-dialog.png) |

## ⌨️ Keyboard Shortcuts

Press <kbd>F1</kbd> in the app for the full cheat sheet. On macOS, <kbd>Cmd</kbd> takes the place of <kbd>Ctrl</kbd> automatically (except autocomplete, which stays on <kbd>Ctrl</kbd>+<kbd>Space</kbd> — Cmd+Space is Spotlight). The highlights:

| Action | Shortcut |
| --- | --- |
| Command palette | <kbd>Ctrl</kbd>+<kbd>K</kbd> or <kbd>Ctrl</kbd>+<kbd>P</kbd> |
| Run query / run statement under cursor | <kbd>Ctrl</kbd>+<kbd>Enter</kbd> / <kbd>Shift</kbd>+<kbd>Enter</kbd> |
| Cancel running query | <kbd>Esc</kbd> |
| Format statement under cursor | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F</kbd> |
| Find / find & replace | <kbd>Ctrl</kbd>+<kbd>F</kbd> / <kbd>Ctrl</kbd>+<kbd>H</kbd> |
| New / close query tab | <kbd>Ctrl</kbd>+<kbd>T</kbd> / <kbd>Ctrl</kbd>+<kbd>W</kbd> |
| Open / save a `.sql` file | <kbd>Ctrl</kbd>+<kbd>O</kbd> / <kbd>Ctrl</kbd>+<kbd>S</kbd> |
| SQL autocomplete | <kbd>Ctrl</kbd>+<kbd>Space</kbd> (also triggers while typing) |
| Refresh database & schema | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>R</kbd> |
| Toggle sidebar | <kbd>Ctrl</kbd>+<kbd>B</kbd> |
| Switch focus: editor ↔ results grid | <kbd>F6</kbd> |
| Edit selected result cell | <kbd>F2</kbd> |
| Preferences | <kbd>Ctrl</kbd>+<kbd>,</kbd> |
| Shortcuts cheat sheet | <kbd>F1</kbd> |

## 📊 Benchmarks

"Fast" is the thesis, so it's **measured, not asserted**. The [benchmark workflow](.github/workflows/benchmark.yml) runs on every tagged release and tracks:

| Metric | What it proves |
| --- | --- |
| Startup, launch → first frame (NativeAOT and JIT) | On screen in the ~100 ms range — measured from OS process start to first rendered frame |
| Memory at first frame, AOT binary size | The footprint stays "native app", not "Electron app" |
| Connect (cold pool) / `SELECT 1` round-trip | Interactive latency of the query path |
| First row batch / full stream of a 100 000-row `SELECT` | Streaming delivers the first screenful long before the full result |

Historical charts live at **<https://shman4ik.github.io/pgNimbus/dev/bench/>** — a regression shows up as a visible step in the release that introduced it.

<details>
<summary>Running the suite locally</summary>

Linux, needs Xvfb and a reachable PostgreSQL:

```bash
PGNIMBUS_BENCH_CONN="Host=localhost;Database=postgres;Username=postgres;Password=postgres" \
    scripts/benchmarks/run-benchmarks.sh          # add PGNIMBUS_BENCH_SKIP_AOT=1 to skip the slow AOT publish
```

Two pieces make it work: `PGNIMBUS_STARTUP_PROBE=1` makes the app print launch-to-first-frame time and RSS and exit ([`StartupProbe.cs`](PgNimbus.App/StartupProbe.cs)), and the [`PgNimbus.Benchmarks`](PgNimbus.Benchmarks/Program.cs) console project measures the query engine through the same streaming API the UI uses.

</details>

## 🏗️ Architecture

```
pgNimbus/
├── PgNimbus.Core/         # Engine. Depends only on Npgsql — zero UI dependencies.
├── PgNimbus.App/          # Avalonia MVVM front-end (CommunityToolkit.Mvvm).
├── PgNimbus.Core.Tests/   # TUnit tests for the engine.
└── PgNimbus.Benchmarks/   # Query-engine benchmarks.
```

`PgNimbus.Core` is a plain class library that knows nothing about Avalonia, keeping the engine reusable for a future CLI or test harness. Results stream as `IAsyncEnumerable<RowBatch>` with real mid-flight cancellation.

### Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet run --project PgNimbus.App
```

Publishing a NativeAOT build:

```bash
dotnet publish PgNimbus.App -c Release -r win-x64 -p:PublishAot=true    # Windows
dotnet publish PgNimbus.App -c Release -r linux-x64 -p:PublishAot=true  # Linux (needs clang + zlib1g-dev)
```

## 🔒 Privacy

pgNimbus sends **zero telemetry**. No usage analytics, no crash reporting, no update pings, no "anonymous statistics" — nothing. The only network connections the app ever opens are the ones you configure: your PostgreSQL servers and, if you use them, your SSH tunnel hosts. Queries, schemas, credentials, and history never leave your machine (passwords live in the OS credential store, everything else in local JSON files under your user profile). The code is MIT-licensed and open — verify it rather than take it on faith.

## 🗺️ Roadmap

Prioritized by how much it advances the thesis (fast + open + modern, PostgreSQL-first). Contributions welcome — items are intentionally scoped as individually shippable pieces; shipped items graduate into [Features](#-features) above.

**Next up**

- [ ] **Full macOS support** — Developer ID signing, notarization, real-world testing.
- [x] **Linux builds** — AppImage, .deb, and tar.gz for x64/arm64 ship from the release pipeline (Flatpak still a maybe-later).
- [ ] **Table & index sizes and usage** — sizes in the schema tree plus a per-database overview (largest relations, seq-vs-index scans, unused indexes, cache hit rate).
- [ ] **Locks & blocking tree** — a who-blocks-whom view in the activity window, with one-click cancel/terminate of the *blocker*.
- [ ] **Row detail sidebar** — a vertical name/value view of the selected row, doubling as a form-style editor.
- [ ] **winget-pkgs submission** — manifests are generated and validated per release; the first manual community-source PR is pending (the `msstore` source already covers `winget install`).
- [ ] **Windows polish** — Mica/acrylic backdrop; per-action hotkey remapping.

**Bigger bets**

- [ ] **ER diagram** — auto-laid-out foreign-key graph of a schema, exportable as SVG.
- [ ] **EXPLAIN plan diffing** — run a query before/after an index and diff the plan trees node-by-node.
- [ ] **Backup/restore UI** — `pg_dump`/`pg_restore` orchestration with progress streaming.
- [ ] **AI, privacy-first** — bring-your-own-key or local model, explicit opt-in, nothing leaves the machine otherwise; possibly an in-app assistant and/or a built-in MCP server exposing the current connection.
- [ ] **Vim keybindings** — opt-in modal editing over AvaloniaEdit.
- [ ] **Parameterized queries** — recognize `:name` / `$1` placeholders and prompt for values on run.
- [ ] **Quick chart of a result set** — one click from grid to a bar/line/scatter view.
- [ ] **PostGIS geometry viewer** — render `geometry`/`geography` cells on a map.
- [ ] **Notebook mode** — mixed SQL + Markdown documents with inline result snapshots.
- [ ] **Plugin/extension API** — a stable surface for community panels.
- [ ] **Localization** — externalize UI strings; Russian and German first.

## 📄 License

MIT — see [LICENSE](LICENSE).
