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

3. **Connect.** Use **Save** to remember credentials: Windows encrypts local credential files with DPAPI; macOS uses Keychain; Linux uses Secret Service through libsecret. If storage is unavailable, the dialog warns and keeps the entered password in memory for this app session.
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

- **Schema-aware autocomplete.** Schema-qualified tables after `FROM`/`JOIN`, scoped columns in `WHERE`/`ON`/`ORDER BY`, `alias.` member access, CTE output columns (including `SELECT *` bodies resolved through the catalog), and user-defined functions with signature tooltips. Names resolve the way the server resolves them (per query block, along `search_path`), argument hints follow the cursor through a call, and `::` offers only types. [More](docs/guide/editor.md#completion).
- **FK-aware JOIN magic.** After `JOIN`, tables connected by a foreign key rank first. After `ON`, the complete join condition (`oi.order_id = o.id`) is the top, one-keystroke suggestion.
- **SQL formatting.** <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>F</kbd> pretty-prints the statement under the cursor; a token round-trip self-check guarantees only whitespace ever changes.
- **Script execution.** Run several `;`-separated statements on one connection (`BEGIN…COMMIT`, `SET`, and temp tables carry across), each with its own result section and timing, stopping at the first error.
- **Multi-tab editor** with find & replace, current-line and bracket highlighting, font-size zoom, line comment/duplicate/move, and `SELECT *` expansion into an explicit column list.
- **Open/save `.sql` files.** <kbd>Ctrl</kbd>+<kbd>O</kbd>/<kbd>Ctrl</kbd>+<kbd>S</kbd>, a recent-files list in the palette, and a dirty marker that distinguishes "unsaved scratch" from "diverges from disk".
- **Query history.** Searchable, pinnable, scoped per connection; entries open in a new tab.
- **Command palette.** <kbd>Ctrl</kbd>+<kbd>K</kbd> fuzzy-jumps to any table, saved query, or action without touching the mouse.

### 🧮 Data Editing Without Fear

- **Safe mode (pending-changes review).** Stage grid edits, inserts, and deletes locally: dirty rows are highlighted (amber = edited, red = delete), "Review & commit…" shows the exact generated SQL, and everything applies as **one transaction**, or gets discarded with nothing ever sent. Built for the "inline edit on production" nerves.
- **No-SQL table browsing.** Paged browsing with click-to-sort headers, all pushed down to Postgres (`ORDER BY`/`LIMIT`/`OFFSET`), so a huge table stays as cheap as one page. The composed SQL sits in the editor; a query you write yourself is never rewritten.
- **Row details and filter chips.** <kbd>Ctrl</kbd>+<kbd>I</kbd> (or the form icon in the status bar) opens the selected row as a form, with the grid's type-aware editors; its edits are staged and go through the same review and conflict-checked commit as Safe mode. While browsing, <kbd>Ctrl</kbd>+<kbd>F</kbd> adds a typed condition or NULL test as a chip, shows the SQL it adds, and runs it on the server. A `WHERE` you type into a browse query comes back as chips too.
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
- **LISTEN/NOTIFY monitor.** Subscribe to channels and watch notifications arrive live, with JSON payloads formatted and browsable as a tree rather than trimmed to one line. Channels are remembered per connection, a dropped connection is re-established with every channel re-subscribed, and you can publish a test notification from the window instead of opening a second session.
- **Connection manager.** Saved profiles with per-connection accent colors (so production never looks like staging), SSH tunnels, and protected password storage: DPAPI-encrypted files on Windows, Keychain on macOS, and Secret Service on Linux. Passwords are separate from connection profiles; unavailable storage falls back to session memory with a warning.
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

pgNimbus sends **zero telemetry**. No usage analytics, no automatic crash uploads, no update pings, no "anonymous statistics". Database and SSH credentials are sent to the servers you configure; pgNimbus does not upload credentials, queries, schemas, or history to an analytics or account service. Saved passwords use DPAPI-encrypted local files on Windows, Keychain on macOS, and Secret Service through libsecret on Linux. These mechanisms may persist protected data on disk; passwords are never part of the connection-profile JSON.

If the OS store is unavailable or locked, a visible warning explains that newly entered passwords may remain only in this app session. Linux needs `libsecret-1.so.0` and a running Secret Service provider such as GNOME Keyring. Unlock/configure the system store, then save the connection again. On macOS, access must be available without an interactive Keychain authorization prompt; use Keychain Access to resolve access restrictions before retrying.

Older macOS/Linux versions wrote base64-encoded, **unencrypted** `.cred` files. Loading a saved profile attempts migration for both database and SSH credentials; the old file is removed only after the OS store returns the saved value. Failed migrations retain the old file and show a warning. A different existing OS-store value takes precedence and leaves the legacy file for an explicit save to resolve. Profiles never opened after upgrading are not migrated yet. Deleting a saved profile also attempts to remove both credential copies and reports failures.

Query history, workspace SQL, and local diagnostic logs can contain sensitive SQL/data and are not encrypted by the credential store. SQL password-literal redaction is a limited safeguard, not general secret detection. The code is MIT-licensed and open, so storage behavior can be inspected.

## 🗺️ Roadmap

<a id="backlog"></a>

### Direction and competitive evidence

Research snapshot: **2026-09-21**. This is a proposed backlog, not a list of available features or a delivery commitment. Priorities are product hypotheses based on the source and public competitor documentation; validate demand with user interviews before investing in the larger items.

**Primary audience:** PostgreSQL developers who investigate slow queries, fix production data, and debug access problems. The proposed positioning is **a fast, local PostgreSQL workbench that makes changes reviewable and performance improvements explainable**. Keep startup speed, NativeAOT, streaming, cancellation, and zero telemetry as constraints on every release.

| Evidence from competitors | Implication for pgNimbus |
| --- | --- |
| TablePlus already offers Safe mode and generated-SQL review ([official overview](https://tableplus.com/blog/2017/07/10-hidden-gems-in-tableplus.html)). | Staging edits alone is not a new category. Extend it with conflict detection and consistent production safeguards. |
| DBeaver supports graphical execution plans and schema comparison; its documented Schema Compare is in Enterprise/Ultimate ([plans](https://dbeaver.com/docs/dbeaver/Query-Execution-Plan/), [schema comparison](https://dbeaver.com/docs/dbeaver/Schema-compare/)). | A plan viewer is baseline functionality. A compact before/after workflow and an MIT-licensed PostgreSQL schema diff are stronger reasons to switch. |
| pgAdmin offers AI reports and query/plan assistance ([official AI documentation](https://www.pgadmin.org/docs/pgadmin4/9.18/ai_tools.html)). | Adding a generic AI chat is unlikely to be a sufficient launch story. Prioritize useful workflows that work without a model or account. |
| Beekeeper Studio advertises AI assistance and shared cloud workspaces ([official product page](https://www.beekeeperstudio.io/)). | Test demand for file-based team workflows without an account; avoid taking on a collaboration backend before the core workflows are compelling. |

These are documented capability comparisons, not measured speed comparisons or claims that competitors lack every proposed workflow. Any future performance claim needs a reproducible, versioned benchmark.

### What the current implementation makes possible

The existing foundations are substantial: staged grid changes, FK navigation, offline plan import, plan warnings, database statistics, a blocking tree, and schema DDL reconstruction. The source also already includes a [security workspace](PgNimbus.App/ViewModels/Security/SecurityViewModel.cs), [effective-privilege explanations](PgNimbus.Core/Security/EffectivePrivilegeResolver.cs), and [RLS policy inspection](PgNimbus.App/ViewModels/Security/RlsTabViewModel.cs); role management and a policy list should not be proposed as new features.

Specific gaps drive the first priorities:

- [CredentialStore](PgNimbus.Core/Connections/CredentialStore.cs) now selects protected platform storage with verified legacy migration and a visible session-memory fallback; see [Privacy](#-privacy) for storage details and remaining legacy files.
- [PendingChangeSet](PgNimbus.Core/Query/PendingChangeSet.cs) now re-reads and locks every staged row at commit and compares it with the row as loaded (T2), so a concurrent change rolls the batch back instead of being overwritten.
- [ConnectionProfile](PgNimbus.Core/Connections/ConnectionProfile.cs) has connection colors and SSL modes, but no environment policy or configurable query deadline; its command timeout is currently unlimited.
- [ExplainService](PgNimbus.Core/Query/ExplainService.cs) and [PlanAnalyzer](PgNimbus.Core/Query/PlanAnalyzer.cs) already parse and explain individual plans. Comparing saved runs is the next increment, not rebuilding visualization.
- [TableBrowseViewModel](PgNimbus.App/ViewModels/TableBrowseViewModel.cs) uses `LIMIT/OFFSET`. Fast initial display alone does not establish fast browsing deep into a large table.

### P0 — Trust and everyday adoption

Ship these before a broad production-use or cross-platform launch. Scope labels are relative: **S** = contained change, **M** = workflow across a few components, **L** = substantial subsystem; they are not calendar estimates.

- [x] **T1 · Native credential storage and accurate privacy wording (M).** macOS Keychain and Linux Secret Service replace new base64-file writes; migration verifies the destination before deleting legacy files. Unavailable stores retain entered credentials in session memory with a warning. See [Privacy](#-privacy) for platform prerequisites, migration limits, and unencrypted query history/workspace data.
- [x] **T2 · Conflict-aware Safe mode (L).** At commit, every staged row is re-read and locked in the batch's own transaction and compared, column by column and NULL-aware, with the row as it was loaded. A row changed or deleted by another session rolls the whole batch back, and a dialog shows before, current and proposed values with **Reload and restage** or **Unstage these rows**. Composite keys are covered; a table without a primary key or with an unreadable key column stays read-only. There is no undo after a successful commit. See [Safe mode](docs/guide/results.md#safe-mode).
- [ ] **T3 · Production connection policies (L).** Add explicit dev/staging/production labels, an opt-in read-only session policy, per-profile statement/lock timeouts, and deliberate write enablement. Apply the policy consistently to the editor, grid, imports, schema/security actions, and diagnostic queries. **Done when:** reconnects and extra windows preserve it and a user can see the active environment and write state before execution. Explain the distinction between client safeguards and PostgreSQL role permissions; SQL keyword checks alone are insufficient.
- [x] **T4 · Row detail editor and visual filters (M).** <kbd>Ctrl</kbd>+<kbd>I</kbd> (or a status-bar icon) opens the selected row as a keyboard-driven form over the window, reusing the Add-row dialog's type-aware editors; its edits always stage and go through the existing review and conflict-checked commit. Table browsing gains filter chips with type-dependent comparisons and NULL tests; each shows its SQL before it runs and is applied on the server as part of the page query. A `WHERE` typed into a browse query comes back as chips, with anything the chips can't express kept verbatim. Any query that isn't a browse page query is never rewritten. See [Filtering rows](docs/guide/results.md#filtering-rows) and [Row details](docs/guide/results.md#row-details).
- [ ] **T5 · Cross-platform installation confidence (M).** Finish macOS Developer ID signing/notarization and real-device checks; submit the generated WinGet manifests to the community source. Linux AppImage/.deb/tar.gz already ship. **Done when:** clean-machine install/update/uninstall checks pass for each advertised platform, with credential behavior from T1 documented. Treat Windows direct-installer signing as a separate distribution follow-up.
- [ ] **T6 · Local hang diagnostics (M).** Add the UI-thread watchdog from the previous roadmap, with bounded local diagnostics and a recovery message. **Done when:** a deliberately stalled dispatcher is detected, normal long-running queries do not trigger it, and no dump/log is uploaded automatically; users can inspect sensitive diagnostic content before sharing.

### P1 — Features worth announcing

Deliver the following as small, reviewable increments. The first campaign should be **Query Lab**; T1–T3 remain trust work, not optional marketing polish.

- [ ] **Q1 · EXPLAIN baselines and plan diff (L).** Save named runs with query, parameters, PostgreSQL version, settings, and measurement context; compare estimated/actual rows, execution time, buffers, spills, and added/removed/changed nodes. Preserve the existing offline import path. **Done when:** users can compare two imported plans without a connection and two explicit live runs, with missing metrics and changed plan topology handled clearly. Never present estimated cost as elapsed time or a single warm-cache run as proof of improvement.
- [ ] **Q2 · Slow-query shortlist (M; depends on Q1 for comparison).** Add an optional `pg_stat_statements` view ranked by total execution time, calls, and mean duration, with interval deltas and a path into Query Lab. **Done when:** missing extension/permissions explain what is unavailable, resets invalidate the affected delta, and normalized SQL prompts for real typed parameters before any run. Do not enable extensions, change server configuration, or replay workload queries automatically. PostgreSQL documents the setup and visibility constraints in [pg_stat_statements](https://www.postgresql.org/docs/current/pgstatstatements.html).
- [ ] **Q3 · Reviewable performance report (M; depends on Q1).** Export a local HTML/Markdown before/after report with plan changes, observed timings, buffers, and reproducibility notes. Allow users to omit SQL, literals, identifiers, and connection metadata, then preview the exact export; do not promise automatic anonymization. **Done when:** the report opens without pgNimbus or an account and its conclusions trace back to saved runs. New runs must be explicit: [EXPLAIN ANALYZE executes the statement](https://www.postgresql.org/docs/current/sql-explain.html), and rollback does not undo every possible side effect.
- [ ] **S1 · Schema snapshots and drift report (L).** Compare two connections or a connection against a local snapshot. First cover tables, columns, defaults, PK/FK/unique constraints, and indexes; list unsupported object kinds explicitly. **Done when:** deterministic snapshots produce a readable diff, destructive changes are prominent, and renamed objects are shown as uncertain rather than silently inferred. Reuse catalog/DDL services. SQL migration export is a separate increment requiring dependency ordering and review; no automatic synchronization in the MVP.
- [ ] **R1 · RLS access investigation (L).** Extend the existing privileges/RLS workspace with a bounded read-only preview under a role the current connection is allowed to assume, plus explicit session context. Show relevant policies, `USING`/`WITH CHECK`, and owner/superuser/`BYPASSRLS` caveats. **Done when:** a seeded two-tenant example explains which rows are visible for each role, denied role switches are clear, and the temporary session is always cleaned up. Do not claim that a SELECT preview proves INSERT/UPDATE policy behavior or arbitrary policy causality.
- [ ] **D1 · Large-table browsing with bounded memory (L).** Add keyset pagination for supported stable unique sort orders, an explicit fallback for other queries, and a result row/memory budget with streaming export beyond it. **Done when:** deep-page and large JSON/text benchmarks report first-page latency, navigation latency, peak memory, and cancellation on a published dataset. Explain live-data changes between pages; do not promise a frozen snapshot without one.

### Launch backlog and evidence

The headlines below are proposals to use **after** the corresponding capabilities ship. Each campaign needs a short screencast, a reproducible example, and an explicit statement of limitations.

| Order / release story | Minimum deliverable | Demonstration and validation |
| --- | --- | --- |
| 1. **“See exactly what changed in your PostgreSQL query plan.”** | Q1 + Q3; Q2 can follow. | Compare a seeded query before/after a reviewed index change, including a case with no improvement. Publish the workload and measurement conditions. Ask pilot users to identify the reason for the difference without assistance. |
| 2. **“Catch conflicting data edits before they overwrite someone else's work.”** | T2 + T3. | Two sessions edit the same row; show the conflict, rollback, and successful restaging. Cover editor/import policy paths as well as the grid in release checks. |
| 3. **“Find PostgreSQL schema drift without a cloud account.”** | S1. | Compare dev/staging snapshots and expose a missing constraint and changed default. Publish supported object coverage and an example diff that can be reviewed in Git. |
| 4. **“See what each tenant role can read.”** | R1. | A two-tenant fixture with different policies, including an owner-bypass case. Pilot users should explain the visible rows and identify when the preview cannot answer a write-access question. |

- [ ] **L1 · Repeatable first-run demo (M).** Package an opt-in local sample database and guided tasks for Query Lab, conflicting edits, and tenant access. Use synthetic data, show setup/cleanup steps, and require no production connection or cloud account. Start with an SQL fixture; an embedded server is outside the first scope.
- [ ] **L2 · Fair comparison kit (S).** Extend the existing benchmarks with large-result memory, deep paging, and cancellation; record hardware, OS, app/database versions, data, and cold/warm conditions. Publish raw results before making new superiority claims. Retain the existing startup benchmark as a regression gate.
- [ ] **L3 · Validate adoption without telemetry (S).** Recruit an initial small pilot group and record consented task-completion observations, failure reasons, and voluntary follow-up feedback. Use public release downloads and substantive issue/discussion feedback only as supporting signals, not active-user or retention estimates. Decide whether to expand each campaign after users can complete its example unaided.

### P2 — Reduce switching costs and deepen PostgreSQL workflows

- [ ] **Typed query parameters (M).** Prompt for `:name` / `$1` values with PostgreSQL types, NULL support, and reusable parameter definitions; do not persist sensitive values by default. This extends editor execution, not the already-existing internal `ParameterizedStatement` used by grid writes. Implement the minimal typed input needed by Q2 first.
- [ ] **Portable SQL projects and connection import (M).** Group saved queries, snippets, parameter definitions, and schema snapshots in a versioned folder suitable for Git. Start with one documented external connection-profile format; preview imported fields and omit passwords/SSH secrets. Keep profiles local and resolve project connection aliases explicitly.
- [ ] **ER diagram (L).** Start with a selected table and its FK neighbors, then schema-wide layout and SVG export; link nodes to existing browse/DDL actions. Bound graph size so a large schema does not freeze the UI.
- [ ] **Backup/restore UI (L).** Discover compatible `pg_dump`/`pg_restore` binaries, preview commands without secrets, stream progress/errors, and require explicit restore target selection. Verify with an actual dump/restore round trip; downloading external tools is opt-in.
- [ ] **Maintenance insights (M).** Extend Database Overview with stale statistics, dead tuples, long transactions, and vacuum progress, including permissions and sampling context. Recommendations produce reviewable SQL, never automatic index drops or maintenance based on a single counter.
- [ ] **Result comparison (M).** Compare two bounded result snapshots by an explicit key; surface duplicate keys, NULL/type differences, and truncation. Export a local diff; cross-database data synchronization is outside the first increment.
- [ ] **PostGIS geometry viewer (L).** Render geometry/geography cells with SRID awareness and a visible size limit. Start with local rendering; external map tiles require explicit opt-in because they add network traffic beyond configured database connections.
- [ ] **Quick result charts (M).** Bar/line/scatter views with explicit axis and aggregation choices; show whether the chart uses the full result or a limited preview. Export locally.
- [ ] **Keyboard and platform polish (M, split by platform).** Hotkey remapping, optional Vim bindings, Windows Mica/acrylic; on macOS, vibrancy, sheet-style dialogs, Window menu, native context menus, and sidebar/title-bar integration. Preserve accessibility, contrast, and keyboard navigation.
- [ ] **Localization (L).** Externalize strings, then Russian and German; include pluralization, shortcuts, layout expansion, and untranslated-string checks.

### P3 — Validate before committing

- [ ] **Privacy-first AI / MCP (L).** Explore a narrow explanation or query-drafting workflow only after the deterministic workflows above. Local models first; remote providers require explicit opt-in and an exact data preview. Any MCP surface needs connection-scoped permissions, read-only defaults, bounded results, and explicit approval for execution. Revisit the current network/privacy wording before shipping; do not market “AI” alone as the differentiator.
- [ ] **Notebook mode (L).** Validate demand for mixed SQL/Markdown with local result snapshots after portable SQL projects; define secret handling and snapshot size limits first.
- [ ] **Plugin API (L).** Defer until repeated extension needs justify a stable, NativeAOT-compatible contract; investigate process isolation and permissions before allowing third-party code access to connections.
- [ ] **Flatpak distribution (M).** Validate demand beyond the existing Linux packages, including sandbox access to Secret Service and external PostgreSQL tools.

Completed items from the previous backlog: Linux release packages, table/index size and usage inspection, and the blocking tree. Keep them as shipped capabilities, not future launch promises. Contributions welcome; pick one scoped increment rather than an entire campaign.

## 📄 License

MIT, see [LICENSE](LICENSE).
