<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="design/masters/logo/wordmark-dark.png">
    <img src="design/masters/logo/wordmark-light.png" alt="pgNimbus logo: an elephant riding a broom" width="300">
  </picture>
</p>

<h1 align="center">pgNimbus</h1>

<p align="center">
  <b>A fast PostgreSQL client that talks to your database and nothing else.</b><br>
  On screen in about 0.2 s. Results stream while the query runs. No telemetry, no account, no cloud.
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
  <a href="#-installation">Install</a> ·
  <a href="#-features">Features</a> ·
  <a href="ROADMAP.md">Roadmap</a>
</p>

---

## 🎯 Why pgNimbus?

pgNimbus is written by a developer who spends most working days in Postgres, for people who do the same. Three things don't bend:

- **It's fast where you wait.** A NativeAOT binary, not Electron: about 0.2 s to a window, rows on screen while the query is still running, and <kbd>Esc</kbd> stops a query mid-flight. Measured on every release.
- **Your data stays yours.** No telemetry, no update checks, no crash uploads, no account, no AI features. The only connections it opens go to your Postgres servers and SSH tunnels.
- **It's built for the keyboard.** <kbd>Ctrl</kbd>+<kbd>K</kbd> reaches any table or command, a connection string in any format fills the form, and autocomplete reads SQL the way the server does.

Where it sits among the alternatives: pgAdmin and DBeaver are powerful but heavy. TablePlus is fast and polished, but closed source and paid. Beekeeper Studio is open source but runs on Electron. HeidiSQL is native and fast, but dated and MySQL-first.

**And next to an AI agent?** pgNimbus itself is built with Claude Code, so this isn't a knock on agents. Let the agent write the hard query. Looking at the rows, fixing one value in production, or finding who holds a lock right now is quicker in a client, and the rows never end up in a model's context.

## 🎬 See it in action

| Cold start (NativeAOT) | Completion that knows your foreign keys |
| --- | --- |
| ![pgNimbus launching from a cold NativeAOT process to a fully rendered main window in well under a second](docs/screenshots/cold-start.gif) | ![Typing FROM + a partial table name, JOIN with an FK-ranked table suggestion, then ON auto-completing the full join condition](docs/screenshots/completion-demo.gif) |

| EXPLAIN ANALYZE as a tree | Safe mode: review, then commit once |
| --- | --- |
| ![Raw EXPLAIN ANALYZE text next to the graphical plan tree pgNimbus renders from it, with per-node cost and actual timing](docs/screenshots/explain-tree-demo.gif) | ![Editing cells across two tabs in safe mode, then committing both staged changes together in a single transaction](docs/screenshots/safe-mode-commit-demo.gif) |

## 📦 Installation

| Platform | How |
| --- | --- |
| Windows (recommended) | **[Microsoft Store](https://apps.microsoft.com/detail/9N6SZT42XJ24)**, signed and self-updating, or `winget install pgNimbus --source msstore` |
| Windows (direct) | `pgNimbus-<version>-win-x64.msi` from [Releases](https://github.com/Shman4ik/pgNimbus/releases). Per-user, no admin rights. Unsigned, so SmartScreen warns on first run. |
| macOS (beta) | `pgNimbus-<version>-macos-arm64.dmg`, Apple Silicon only. Ad-hoc signed and not notarized: the first launch needs **Open Anyway**. |
| Linux (beta) | AppImage, `.deb` or `.tar.gz` for x64 and arm64 from [Releases](https://github.com/Shman4ik/pgNimbus/releases). |

The [installation guide](https://shman4ik.github.io/pgNimbus/docs/getting-started/installation/) has the step-by-step for each platform, the macOS Gatekeeper dialogs, and where pgNimbus keeps its files. Every release asset carries signed build provenance, so you can check where a download came from:

```bash
gh attestation verify pgNimbus-<version>-win-x64.msi --repo Shman4ik/pgNimbus
```

## 🚀 Quick start

1. Launch pgNimbus. The connection dialog opens first.
2. Paste a connection string into the box at the top. A `postgres://` URI, JDBC URL, `Host=…;Port=…`, libpq keywords and a whole `psql` command line all work.
3. Connect. The form saves itself as you type; there is no Save button. Next time, launch and press <kbd>Enter</kbd> to reconnect.
4. Run with <kbd>Ctrl</kbd>+<kbd>Enter</kbd>, jump anywhere with <kbd>Ctrl</kbd>+<kbd>K</kbd>, and press <kbd>F1</kbd> for every shortcut.

On macOS, <kbd>Cmd</kbd> replaces <kbd>Ctrl</kbd>, except autocomplete, which stays on <kbd>Ctrl</kbd>+<kbd>Space</kbd>. More in [Connecting to a database](https://shman4ik.github.io/pgNimbus/docs/getting-started/connecting/).

## ✨ Features

**SQL editor** ([guide](https://shman4ik.github.io/pgNimbus/docs/guide/editor/))

- Autocomplete that resolves names the way the server does: along `search_path`, per query block (subqueries, CTEs, `LATERAL`), with one `JOIN … ON` condition offered per foreign key, argument hints for functions and only types after `::`.
- Formatting that only ever changes whitespace, `;`-separated scripts with a result section per statement, find and replace, `.sql` files, searchable query history and saved queries.

**Results and editing** ([guide](https://shman4ik.github.io/pgNimbus/docs/guide/results/))

- Streaming, virtualized grid. Browse a table without SQL; sorting, paging and filter chips run on the server.
- Safe mode: edits, inserts and deletes are staged, you review the exact SQL, and everything commits as one transaction. At commit each staged row is re-read under a lock, and if another session changed it the batch rolls back and shows before, current and proposed values.
- Type-aware editors (enum dropdowns, booleans, dates, arrays, composites, json/jsonb), a row-details form, a cell inspector, and follow-the-foreign-key from any key cell.
- CSV/JSON import through `COPY`; copy results as CSV, TSV, JSON, Markdown or `INSERT` statements.

**PostgreSQL tooling** ([plans](https://shman4ik.github.io/pgNimbus/docs/guide/explain/), [monitoring](https://shman4ik.github.io/pgNimbus/docs/guide/monitoring/))

- Schema tree read from `pg_catalog`: materialized views, partitioned tables, relation sizes, DDL for any table or view.
- `EXPLAIN` and `EXPLAIN ANALYZE` as a tree with a self-time heat map and plain warnings (disk spills, bad row estimates). Paste a plan from anywhere and read it with no connection.
- Server activity with cancel and terminate, plus a who-blocks-whom lock tree from `pg_blocking_pids`.
- Database overview: largest relations, unused indexes, seq vs index scans, cache hit ratios.
- Slow queries from `pg_stat_statements`: ranked by total or mean time, measured since the last reset or over just the workload you ran, one double-click from the editor.
- Roles and permissions that answer "can this role do that, and why" from the server's own `has_*_privilege()`, including grants inherited through roles and PUBLIC. Changes come out as a script, never applied behind your back.
- LISTEN/NOTIFY monitor with JSON payloads as a tree and a button to publish a test event.

**Connections** ([guide](https://shman4ik.github.io/pgNimbus/docs/getting-started/connecting/))

- Saved profiles with accent colours, so production never looks like staging.
- SSH tunnels with agent, key file or password auth.
- Several databases side by side, each window with its own pool and tunnel. Auto-reconnect after sleep, without ever silently re-opening a transaction.
- Tabs, including unsaved ones, come back after a restart.

## 📊 Benchmarks

Every tagged release runs a benchmark job and publishes the history at **<https://shman4ik.github.io/pgNimbus/dev/bench/>**. At v0.13.1, on a GitHub Ubuntu runner:

| Metric | Result |
| --- | --- |
| Process start to first rendered frame, NativeAOT | ~0.2 s (~2 s for the same code JIT-compiled) |
| First row batch of a 100,000-row `SELECT` | ~10 ms |
| Full stream of those 100,000 rows | ~150 ms |

The numbers are machine-relative; the point of the chart is that a regression shows up as a step in the release that caused it. `scripts/benchmarks/run-benchmarks.sh` runs the same suite locally.

## 🔒 Privacy

pgNimbus sends zero telemetry: no usage analytics, no automatic crash uploads, no update pings. There's no account and no cloud sync, and nothing you query, browse or type is sent anywhere except the servers you configure. Saved passwords go to the OS store (DPAPI on Windows, Keychain on macOS, Secret Service on Linux), never into the profile file.

Two things to know. Query history, workspace SQL and the local crash log can contain sensitive data and are stored on disk unencrypted. And if the OS store is unavailable, a warning says so and the password is kept only for the current session. Details, including migration from older unencrypted files, are in [where your password goes](https://shman4ik.github.io/pgNimbus/docs/getting-started/connecting/#where-your-password-goes).

Building from source is the one place anything is reported, and it isn't the app: Avalonia's build tooling sends anonymous build statistics to Avalonia while the project compiles. Nothing of it ships in the binaries you download.

## 🗺️ Roadmap

Next up (a direction, not a commitment):

- **Production connection policies:** environment labels, on top of the read-only sessions that shipped.
- **Query Lab:** save EXPLAIN runs and compare them before and after a change.
- **Signed and notarized macOS builds.**

The full backlog, with the competitive research behind each item, is in [ROADMAP.md](ROADMAP.md). Pick one scoped item if you'd like to contribute; [CONTRIBUTING.md](CONTRIBUTING.md) has the details.

## 🧱 Building and running

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet run --project PgNimbus.App
dotnet test --project PgNimbus.Core.Tests
```

`PgNimbus.Core` is the engine: a plain class library over Npgsql with no UI dependencies, streaming results as `IAsyncEnumerable<RowBatch>` with real mid-flight cancellation. `PgNimbus.App` is the Avalonia front end. To skip the connection dialog while developing, set `PGNIMBUS_CONN` to any connection string the paste box accepts.

A NativeAOT build:

```bash
dotnet publish PgNimbus.App -c Release -r win-x64 -p:PublishAot=true    # Windows
dotnet publish PgNimbus.App -c Release -r linux-x64 -p:PublishAot=true  # Linux (needs clang + zlib1g-dev)
```

The docs site is MkDocs Material: `pip install -r docs/requirements.txt && mkdocs serve`.

## 📄 License

MIT, see [LICENSE](LICENSE).
