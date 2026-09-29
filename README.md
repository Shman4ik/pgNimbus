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
  <a href="https://buymeacoffee.com/shman4ik"><img src="website/assets/bmc-badge.svg" alt="Buy me a coffee"></a>
</p>

<p align="center">
  <a href="https://shman4ik.github.io/pgNimbus/">Website</a> ·
  <a href="https://shman4ik.github.io/pgNimbus/docs/">Docs</a> ·
  <a href="#-installation">Install</a> ·
  <a href="#-features">Features</a> ·
  <a href="ROADMAP.md">Roadmap</a>
</p>

---

## 🚀 pgNimbus 1.0

pgNimbus reached 1.0 three months after its first commit on July 4, 2026, and 32 releases later. It started as a weekend experiment typed on a phone and grew, evening by evening, into the client its author keeps open at work all day.

Before tagging 1.0, the whole codebase was read the way an attacker would read it, and the way a DBA connecting to production would. That review found 18 problems. The worst were a query that could run twice, a statement a DBA had just killed being sent again after a reconnect, and SSH host keys that were never checked. All of them are fixed in 1.0. The review was done in house, with the same Claude Code agents that write most of the code, so it isn't a third-party audit. [See what's in 1.0.](https://github.com/Shman4ik/pgNimbus/releases/tag/v1.0.0)

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
| ![pgNimbus starting cold from a NativeAOT build, with the main window drawn about half a second after launch](docs/screenshots/cold-start.gif) | ![Typing FROM and part of a table name, then JOIN and a few letters of customers; picking the foreign key suggestion writes the whole ON condition in one step](docs/screenshots/completion-demo.gif) |

| EXPLAIN ANALYZE as a tree | Safe mode: review, then commit once |
| --- | --- |
| ![EXPLAIN ANALYZE output as text, then the Tree view with a heat bar, cost and time on every node, and the Color switch moving from time to rows, cost and buffers](docs/screenshots/explain-tree-demo.gif) | ![Two cell edits staged in safe mode (the rows turn amber), reviewed as SQL, then committed together in one transaction](docs/screenshots/safe-mode-commit-demo.gif) |

## 📦 Installation

| Platform | How |
| --- | --- |
| Windows (recommended) | **[Microsoft Store](https://apps.microsoft.com/detail/9N6SZT42XJ24)**, signed and self-updating, or `winget install pgNimbus --source msstore` |
| Windows (direct) | `pgNimbus-<version>-win-x64.msi` from [Releases](https://github.com/Shman4ik/pgNimbus/releases). Per-user, no admin rights. Unsigned, so SmartScreen warns on first run. |
| macOS (beta) | `pgNimbus-<version>-macos-arm64.dmg`, Apple Silicon only, macOS 12 or later. Ad-hoc signed and not notarized: the first launch needs **Open Anyway**, and an update can leave saved passwords unreadable until you delete the old `pgNimbus` Keychain items (see the installation guide). |
| Linux (beta) | AppImage, `.deb` or `.tar.gz` for x64 and arm64 from [Releases](https://github.com/Shman4ik/pgNimbus/releases). |

The [installation guide](https://shman4ik.github.io/pgNimbus/docs/getting-started/installation/) has the step-by-step for each platform, the macOS Gatekeeper dialogs, and where pgNimbus keeps its files. Every release asset carries signed build provenance, so you can check where a download came from. Pass `--signer-workflow` and `--source-ref`, or the check also accepts an attestation from any other workflow or ref in the repo:

```bash
gh attestation verify pgNimbus-<version>-win-x64.msi --repo Shman4ik/pgNimbus \
  --signer-workflow Shman4ik/pgNimbus/.github/workflows/release.yml \
  --source-ref refs/tags/v<version>
```

Without the `gh` CLI, check the download against `SHA256SUMS.txt` from the same release, which is attested too:

```bash
sha256sum -c SHA256SUMS.txt --ignore-missing
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
- A closed tab comes back with <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>T</kbd>, and on macOS the editor answers the usual text keys (<kbd>⌥</kbd>+<kbd>⌫</kbd>, <kbd>⌃</kbd>+<kbd>A</kbd>, <kbd>⌃</kbd>+<kbd>K</kbd> and the rest).

**Results and editing** ([guide](https://shman4ik.github.io/pgNimbus/docs/guide/results/))

- Streaming, virtualized grid. Browse a table without SQL; sorting, paging and filter chips run on the server.
- Safe mode: edits, inserts and deletes are staged, you review the exact SQL, and everything commits as one transaction. At commit each staged row is re-read under a lock, and if another session changed it the batch rolls back and shows before, current and proposed values.
- Type-aware editors (enum dropdowns, booleans, dates, arrays, composites, json/jsonb), a row-details form, a cell inspector, and follow-the-foreign-key from any key cell.
- CSV/JSON import through `COPY`. Export writes every row of a browsed table, not only the page on screen. Copy results as CSV, TSV, JSON, Markdown or `INSERT` statements.

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
- SSH tunnels with agent, key file or password auth. The jump host's key is checked against `known_hosts`, and a key pgNimbus has not seen before is shown with its fingerprint before you accept it.
- TLS that verifies: new profiles start at Require, Verify full works through an SSH tunnel, and a profile can point at a provider's root certificate (RDS, Cloud SQL, Supabase).
- Read-only profiles that the server enforces, so a stray `UPDATE` is refused rather than trusted to a keyword check.
- Several databases side by side, each window with its own pool and tunnel. Auto-reconnect after sleep, without ever silently re-opening a transaction.
- Tabs, including unsaved ones, come back after a restart.

## 📊 Benchmarks

Every tagged release runs a benchmark job and publishes the history at **<https://shman4ik.github.io/pgNimbus/dev/bench/>**. At v1.0.0, on a GitHub Ubuntu runner:

| Metric | Result |
| --- | --- |
| Process start to first rendered frame, NativeAOT | ~0.2 s (~2 s for the same code JIT-compiled) |
| First row batch of a 100,000-row `SELECT` | ~10 ms |
| Full stream of those 100,000 rows | ~150 ms |

The numbers are machine-relative; the point of the chart is that a regression shows up as a step in the release that caused it. `scripts/benchmarks/run-benchmarks.sh` runs the same suite locally.

## 🔒 Privacy

pgNimbus sends zero telemetry: no usage analytics, no automatic crash uploads, no update pings. There's no account and no cloud sync, and nothing you query, browse or type is sent anywhere except the servers you configure. Saved passwords are encrypted by the OS (DPAPI-encrypted files on Windows, the Keychain on macOS, Secret Service on Linux) and never go into the profile file.

Two things to know. Query history, open tabs, saved queries and the local crash log can contain sensitive data and are stored on disk unencrypted. Passwords found in history and tab SQL are masked before saving (a saved query is kept as you saved it), and history can be turned off in Settings. And if the OS store is unavailable, a warning says so and the password is kept only for the current session. The [privacy page](https://shman4ik.github.io/pgNimbus/docs/privacy/) lists every file and what is in it; [where your password goes](https://shman4ik.github.io/pgNimbus/docs/getting-started/connecting/#where-your-password-goes) covers migration from older unencrypted files.

Two things outside the app report data. Building from source runs Avalonia's build tooling, which sends anonymous build statistics to Avalonia, and the .NET SDK, which sends usage data to Microsoft unless `DOTNET_CLI_TELEMETRY_OPTOUT` is set. Neither ships in the binaries you download. And the Microsoft Store can show the developer aggregate install and crash numbers, based on your Windows diagnostic data settings.

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

## ☕ Support

pgNimbus is free and MIT licensed, and it stays that way. If it saves you time at work or you simply enjoy using it, you can support the work with a coffee:

<a href="https://buymeacoffee.com/shman4ik"><img src="website/assets/bmc-button.svg" alt="Buy me a coffee" height="50"></a>

A star, a bug report or a word to a colleague helps too.

## 📄 License

MIT, see [LICENSE](LICENSE).
