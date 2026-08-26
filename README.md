<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="design/masters/logo/wordmark-dark.png">
    <img src="design/masters/logo/wordmark-light.png" alt="pgNimbus logo: an elephant riding a broom" width="300">
  </picture>
</p>

<h1 align="center">pgNimbus</h1>

<p align="center">
  <b>A fast, open-source PostgreSQL GUI client with a modern, native UI.</b><br>
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
  <a href="https://shman4ik.github.io/pgNimbus/docs/">Docs</a> ·
  <a href="#-installation">Installation</a> ·
  <a href="#-quick-start">Quick Start</a> ·
  <a href="#-features">Features</a> ·
  <a href="#-benchmarks">Benchmarks</a> ·
  <a href="#-roadmap">Roadmap</a>
</p>

---

## 🎯 Why pgNimbus?

The PostgreSQL client market has a gap: fast, open source, and modern UI, all at once.

- **pgAdmin / DBeaver.** Powerful, but heavy and slow.
- **TablePlus.** Fast and polished, but closed-source and paid.
- **Beekeeper Studio.** Open source, but Electron.
- **HeidiSQL.** Native and fast, but dated and MySQL-first.

pgNimbus aims for HeidiSQL's speed with TablePlus's polish, PostgreSQL-first from the ground up. Built with .NET 10 and Avalonia 12, compiled to a NativeAOT binary, MIT licensed.

## 🎬 See It in Action

| Instant launch (NativeAOT) | SQL completion that predicts the next move |
| --- | --- |
| ![pgNimbus launching from a cold NativeAOT process to a fully rendered main window in well under a second](docs/screenshots/cold-start.gif) | ![Typing FROM + a partial table name, JOIN with an FK-ranked table suggestion, then ON auto-completing the full join condition](docs/screenshots/completion-demo.gif) |

| Startup race: pgNimbus vs. pgAdmin | Safe mode: stage edits, commit as one transaction |
| --- | --- |
| ![Side-by-side race from a cold start: pgNimbus is already showing query results while pgAdmin's splash screen is still waiting to launch](docs/screenshots/startup-race-reel.gif) | ![Editing cells across two tabs in safe mode, then committing both staged changes together in a single transaction](docs/screenshots/safe-mode-commit-demo.gif) |

## 📦 Installation

Full details, including where pgNimbus stores its files, are in the [installation guide](https://shman4ik.github.io/pgNimbus/docs/getting-started/installation/).

### Microsoft Store (recommended)

The Store package is signed and auto-updated, so there are no SmartScreen warnings.

**[Get pgNimbus on the Microsoft Store →](https://apps.microsoft.com/detail/9N6SZT42XJ24)**

### WinGet

```powershell
winget install pgNimbus --source msstore
```

### Direct download (MSI)

Grab `pgNimbus-<version>-win-x64.msi` from [Releases](https://github.com/Shman4ik/pgNimbus/releases). It is a per-user installer, so no admin rights are needed.

> [!NOTE]
> The direct MSI is unsigned, so SmartScreen will warn on first run. Click **More info → Run anyway**, or prefer the Store/WinGet path above. You can also [verify where the file came from](#verifying-a-download).

### macOS (early beta)

`pgNimbus-<version>-macos-arm64.dmg` from [Releases](https://github.com/Shman4ik/pgNimbus/releases) (Apple Silicon only). Open the disk image, drag pgNimbus to the Applications folder, then eject the image.

The build carries an ad-hoc signature rather than an Apple Developer ID one, so macOS asks about it the first time you open it:

1. Right-click (or Control-click) pgNimbus in Applications and choose **Open**, then **Open** again in the dialog.
2. On macOS 15 Sequoia and later, double-click it, dismiss the warning, then go to **System Settings → Privacy & Security** and click **Open Anyway**.

You do this once. Every later launch opens normally.

<details>
<summary>If macOS says the app is damaged</summary>

That is what Gatekeeper says about a download with no signature at all, which is what pgNimbus 0.11.1 and earlier shipped. Later builds are signed and give you the **Open Anyway** path above instead. To open an older download, clear the quarantine flag:

```bash
xattr -dr com.apple.quarantine /Applications/pgNimbus.app
```

The same command also works as a fallback on any version. Signing with a real Developer ID and notarizing, which removes the warning entirely, is on the [Roadmap](#-roadmap).

</details>

### Linux (early beta)

x64 and arm64 builds from [Releases](https://github.com/Shman4ik/pgNimbus/releases), in three formats:

- **AppImage** (any distro). `chmod +x pgNimbus-<version>-linux-<arch>.AppImage`, then run it. Nothing to install.
- **Debian/Ubuntu.** `sudo apt install ./pgNimbus-<version>-linux-<arch>.deb`, then launch `pgnimbus` (or find pgNimbus in your app menu).
- **tar.gz.** Unpack anywhere and run `./PgNimbus.App`.

Every `vX.Y.Z` tag builds all of the above via [`release.yml`](.github/workflows/release.yml).

### Verifying a download

The direct-download builds are unsigned, but every release asset carries [signed build provenance](https://docs.github.com/en/actions/security-for-github-actions/using-artifact-attestations). One command proves a file was built by this repo's release workflow from the tagged commit, rather than tampered with or rehosted:

```bash
gh attestation verify pgNimbus-<version>-win-x64.msi --repo Shman4ik/pgNimbus
```

Each release also ships `SHA256SUMS.txt` and a CycloneDX SBOM (`pgNimbus-<version>-sbom.cdx.json`) listing every bundled dependency.

## 🚀 Quick Start

1. **Launch pgNimbus.** The connection dialog opens first.
2. **Paste any connection string** into the box at the top; the form fills itself. All common syntaxes work:

   ```text
   postgres://alice:s3cret@db.example.com:5433/appdb?sslmode=require
   jdbc:postgresql://db.example.com:5433/appdb?user=alice&ssl=true
   Host=db.example.com;Port=5433;Database=appdb;Username=alice;Password=s3cret
   host=db.example.com port=5433 dbname=appdb user=alice sslmode=require
   PGPASSWORD=s3cret psql -h db.example.com -p 5433 -U alice appdb
   ```

3. **Connect.** Your password goes to the OS credential store (DPAPI on Windows), never to disk.
4. **Run a query** with <kbd>Ctrl</kbd>+<kbd>Enter</kbd>, jump anywhere with the command palette (<kbd>Ctrl</kbd>+<kbd>K</kbd>), and press <kbd>F1</kbd> for the full shortcut cheat sheet.

For scripted or repeated local testing, set `PGNIMBUS_CONN` (same formats as the paste box) to skip the dialog entirely:

```bash
export PGNIMBUS_CONN="postgres://postgres:secret@localhost:5432/mydb"
dotnet run --project PgNimbus.App
```

## ✨ Features

### ⚡ Fast & Dependable

- **~100 ms launch-to-window** as a NativeAOT binary, measured on every release rather than asserted ([Benchmarks](#-benchmarks)).
- **Streaming, cancellable results.** The first screenful renders before the full result set arrives, backed by a virtualized grid, and <kbd>Esc</kbd> genuinely stops a query mid-flight.
- **Auto-reconnect.** A connection dropped by laptop sleep or an SSH-tunnel hiccup quietly reopens on the next run. An open explicit transaction is never silently re-established; it surfaces a clear "connection lost, nothing committed" state instead.
- **Workspace restore.** Closing the app never prompts. The next session reopens your tabs, including never-saved scratch SQL, exactly as you left them.
- **Zero telemetry.** No analytics, no crash reporting, no update pings. The only connections the app opens are the ones you configure ([Privacy](#-privacy)).

### ✏️ A Smarter SQL Editor

- **Schema-aware autocomplete.** Schema-qualified tables after `FROM`/`JOIN`, scoped columns in `WHERE`/`ON`/`ORDER BY`, `alias.` member access, CTE output columns (including `SELECT *` bodies resolved through the catalog), and user-defined functions with signature tooltips.
- **FK-aware JOIN magic.** After `JOIN`, tables connected by a foreign key rank first. After `ON`, the complete join condition (`oi.order_id = o.id`) is the top, one-keystroke suggestion.
- **SQL formatting.** <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F</kbd> pretty-prints the statement under the cursor; a token round-trip self-check guarantees only whitespace ever changes.
- **Script execution.** Run several `;`-separated statements on one connection (`BEGIN…COMMIT`, `SET`, and temp tables carry across), each with its own result section and timing, stopping at the first error.
- **Multi-tab editor** with find & replace, current-line and bracket highlighting, font-size zoom, line comment/duplicate/move, and `SELECT *` expansion into an explicit column list.
- **Open/save `.sql` files.** <kbd>Ctrl</kbd>+<kbd>O</kbd>/<kbd>Ctrl</kbd>+<kbd>S</kbd>, a recent-files list in the palette, and a dirty marker that distinguishes "unsaved scratch" from "diverges from disk".
- **Query history.** Searchable, pinnable, scoped per connection; entries open in a new tab.
- **Command palette.** <kbd>Ctrl</kbd>+<kbd>K</kbd> fuzzy-jumps to any table, saved query, or action without touching the mouse.

### 🧮 Data Editing Without Fear

- **Safe mode (pending-changes review).** Stage grid edits, inserts, and deletes locally: dirty rows are highlighted (amber = edited, red = delete), "Review & commit…" shows the exact generated SQL, and everything applies as **one transaction**, or gets discarded with nothing ever sent. Built for the "inline edit on production" nerves.
- **No-SQL table browsing.** Paged browsing with click-to-sort headers, all pushed down to Postgres (`ORDER BY`/`LIMIT`/`OFFSET`), so a huge table stays as cheap as one page. The composed SQL sits in the editor and doubles as the filter: add a `WHERE` and run.
- **Follow foreign keys from the grid.** Right-click an FK cell to jump to the row it references, or a key cell to list all referencing rows, each hop opening a pre-filtered browse tab.
- **Full grid CRUD.** Inline cell editing, an "Add row" dialog with server-side type casts, delete with confirmation, and "Set cell to NULL". Hand-typed `SELECT`s become editable too whenever the wire metadata proves it's safe.
- **Postgres-native value editors.** `enum` columns get a dropdown of their `pg_enum` labels, `boolean` a checkbox, `date`/`timestamp` a calendar picker. Arrays and composites are syntax-checked before anything is sent, and domains resolve to their base type.
- **Transaction control.** An explicit Begin/Commit/Rollback flow on one held connection, with a status-bar indicator and automatic rollback on failure so you're never stranded in an aborted-transaction state.
- **Cell inspector.** Double-click any cell to read the full value in an overlay, with JSON pretty-printed and one-click copy.
- **Import & export.** CSV/JSON import streamed via `COPY` with type inference; copy results as TSV, CSV, JSON, Markdown table, or `INSERT` statements.

### 🐘 PostgreSQL-First Tooling

- **Real `pg_catalog` introspection.** The schema tree sees materialized views, partitioned tables, and true primary-key flags, never the lowest-common-denominator `information_schema`.
- **DDL reconstruction.** A "Source (DDL)" action rebuilds an object's `CREATE TABLE`/`CREATE VIEW` (columns, defaults, identity, constraints, partition key, indexes) into a new tab; an "Alter Table" UI covers no-SQL column changes.
- **EXPLAIN visualization.** A graphical plan tree for `EXPLAIN` and `EXPLAIN ANALYZE` with per-node cost and timing, plain-language warnings, and re-colouring by time/rows/cost/buffers. You can also paste a plan from elsewhere and read it with no connection at all.
- **Server activity dashboard.** A live `pg_stat_activity` view with per-backend **cancel statement** and **terminate session**, so a runaway query is one click to stop, plus a **who-blocks-whom lock tree** (`pg_blocking_pids`): lock holders at the top, waiters nested beneath with the lock they're stuck on, and one-click cancel/terminate of the *blocker* to unstick everyone below it.
- **Table & index sizes and usage.** Relation sizes right in the schema tree, plus a **Database Overview** panel: largest relations (heap/index split), seq-vs-index scan counts (missing-index suspects flagged), unused non-constraint indexes with the disk they waste, and buffer cache-hit ratios.
- **LISTEN/NOTIFY monitor.** Subscribe to channels and watch notifications arrive live.
- **Connection manager.** Saved profiles with per-connection accent colors (so production never looks like staging), SSH tunnels, and passwords held by the OS credential store, never written to disk.
- **Multiple simultaneous connections.** Open profiles in separate self-contained windows (own pool, listener, tunnel, workspace), so dev and prod sit side by side, or switch the current window's connection without restarting.

## 📸 Screenshots

| Query editor + results (light) | Query editor + results (dark) |
| --- | --- |
| ![Main window, light theme](docs/screenshots/main-light.png) | ![Main window, dark theme](docs/screenshots/main-dark.png) |

| EXPLAIN ANALYZE visualization | Command palette (Ctrl+K) |
| --- | --- |
| ![Raw EXPLAIN ANALYZE text next to the graphical plan tree pgNimbus renders from it, with per-node cost and actual timing](docs/screenshots/explain-tree-demo.gif) | ![Command palette fuzzy-jumping to a table](docs/screenshots/command-palette.png) |

| Server activity (pg_stat_activity) | Connection manager |
| --- | --- |
| ![Server activity window showing a live backend and its wait event](docs/screenshots/server-activity.png) | ![Connection dialog with saved profiles and paste-anything import](docs/screenshots/connection-dialog.png) |

## ⌨️ Keyboard Shortcuts

Press <kbd>F1</kbd> in the app for the full cheat sheet, or read the same list in the [keyboard shortcut reference](https://shman4ik.github.io/pgNimbus/docs/reference/keyboard-shortcuts/). Both are generated from one catalog in the source, so neither can drift from the real bindings.

On macOS, <kbd>Cmd</kbd> takes the place of <kbd>Ctrl</kbd> automatically, except autocomplete, which stays on <kbd>Ctrl</kbd>+<kbd>Space</kbd> because Cmd+Space is Spotlight. The ones worth learning first:

| Action | Shortcut |
| --- | --- |
| Command palette | <kbd>Ctrl</kbd>+<kbd>K</kbd> |
| Run query / run statement under cursor | <kbd>Ctrl</kbd>+<kbd>Enter</kbd> / <kbd>Shift</kbd>+<kbd>Enter</kbd> |
| Cancel running query | <kbd>Esc</kbd> |
| Explain / Explain Analyze | <kbd>Ctrl</kbd>+<kbd>E</kbd> / <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>E</kbd> |
| Format statement under cursor | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F</kbd> |
| SQL autocomplete | <kbd>Ctrl</kbd>+<kbd>Space</kbd> (also triggers while typing) |
| Shortcuts cheat sheet | <kbd>F1</kbd> |

## 📊 Benchmarks

"Fast" is the thesis, so it's **measured, not asserted**. The [benchmark workflow](.github/workflows/benchmark.yml) runs on every tagged release and tracks:

| Metric | What it proves |
| --- | --- |
| Startup, launch → first frame (NativeAOT and JIT) | On screen in the ~100 ms range, measured from OS process start to first rendered frame |
| Memory at first frame, AOT binary size | The footprint stays "native app", not "Electron app" |
| Connect (cold pool) / `SELECT 1` round-trip | Interactive latency of the query path |
| First row batch / full stream of a 100 000-row `SELECT` | Streaming delivers the first screenful long before the full result |

Historical charts live at **<https://shman4ik.github.io/pgNimbus/dev/bench/>**, where a regression shows up as a visible step in the release that introduced it.

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
├── PgNimbus.Core/         # Engine. Depends only on Npgsql, zero UI dependencies.
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

### Building the docs

The documentation site is MkDocs Material, built from `docs/`:

```bash
pip install -r docs/requirements.txt
mkdocs serve
```

## 🔒 Privacy

pgNimbus sends **zero telemetry**. No usage analytics, no crash reporting, no update pings, no "anonymous statistics". The only network connections the app ever opens are the ones you configure: your PostgreSQL servers and, if you use them, your SSH tunnel hosts. Queries, schemas, credentials, and history never leave your machine (passwords live in the OS credential store, everything else in local JSON files under your user profile). The code is MIT-licensed and open, so you can verify this rather than take it on faith.

## 🗺️ Roadmap

Prioritized by how much it advances the thesis (fast + open + modern, PostgreSQL-first). Contributions welcome. Items are intentionally scoped as individually shippable pieces; shipped items graduate into [Features](#-features) above.

**Next up**

- [ ] **Full macOS support.** Developer ID signing, notarization, real-world testing.
- [ ] **macOS look & feel polish.** The native menu bar, About box, and Settings… (Cmd+,) shipped 2026-07. Still open: title-bar vibrancy/translucency (NSVisualEffectView-style material behind the merged command bar), sheet-style modals instead of separate dialog windows, a proper Window menu with the open-windows list, native context-menu styling, and a full-height sidebar that tucks under the traffic lights (TablePlus-style).
- [x] **Linux builds.** AppImage, .deb, and tar.gz for x64/arm64 ship from the release pipeline (Flatpak still a maybe-later).
- [x] **Table & index sizes and usage.** Relation sizes in the schema tree plus a Database Overview panel (largest relations with heap/index split, seq-vs-index scans, unused indexes, cache hit ratios).
- [x] **Locks & blocking tree.** A who-blocks-whom view in the activity window (`pg_blocking_pids`), with one-click cancel/terminate of the *blocker*.
- [ ] **Row detail sidebar.** A vertical name/value view of the selected row, doubling as a form-style editor.
- [ ] **winget-pkgs submission.** Manifests are generated and validated per release; the first manual community-source PR is pending (the `msstore` source already covers `winget install`).
- [ ] **Windows polish.** Mica/acrylic backdrop; per-action hotkey remapping.
- [ ] **UI-thread watchdog.** A background timer that notices when the dispatcher stops responding for N seconds, captures a dump/log, and surfaces it. The crash reporter only catches thrown exceptions, not deadlocks or hangs (see the `Switch connection` compositor-deadlock fix).

**Bigger bets**

- [ ] **ER diagram.** Auto-laid-out foreign-key graph of a schema, exportable as SVG.
- [ ] **EXPLAIN plan diffing.** Run a query before and after an index, then diff the plan trees node-by-node.
- [ ] **Backup/restore UI.** `pg_dump`/`pg_restore` orchestration with progress streaming.
- [ ] **AI, privacy-first.** Bring-your-own-key or local model, explicit opt-in, nothing leaves the machine otherwise; possibly an in-app assistant and/or a built-in MCP server exposing the current connection.
- [ ] **Vim keybindings.** Opt-in modal editing over AvaloniaEdit.
- [ ] **Parameterized queries.** Recognize `:name` / `$1` placeholders and prompt for values on run.
- [ ] **Quick chart of a result set.** One click from grid to a bar/line/scatter view.
- [ ] **PostGIS geometry viewer.** Render `geometry`/`geography` cells on a map.
- [ ] **Notebook mode.** Mixed SQL + Markdown documents with inline result snapshots.
- [ ] **Plugin/extension API.** A stable surface for community panels.
- [ ] **Localization.** Externalize UI strings; Russian and German first.

## 📄 License

MIT, see [LICENSE](LICENSE).
