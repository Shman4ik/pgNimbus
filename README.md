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

| Connection manager |
| --- |
| ![Connection dialog with saved profiles](docs/screenshots/connection-dialog.png) |

## Features

- **Schema tree sidebar** — schemas → tables/views → columns, reading
  `pg_catalog` directly (materialized views, partitioned tables, real
  primary-key flags), with an "Alter Table" UI for no-SQL column add/rename/drop.
- **Connection manager** — saved profiles with a per-connection accent color
  (so production doesn't look like staging), SSH tunnel support, and
  passwords held by the OS credential store (DPAPI on Windows) instead of
  being written to disk with the profile.
- **Multi-tab query editor** — schema-aware SQL autocomplete, saved queries,
  and run history.
- **Streaming, cancellable results** — the first screenful renders before the
  full result set arrives, backed by a virtualized grid with inline cell
  editing and CSV/JSON export.
- **EXPLAIN visualization** — a graphical plan tree for `EXPLAIN` and
  `EXPLAIN ANALYZE`, not just raw text output.
- **LISTEN/NOTIFY monitor** — subscribe to channels and watch notifications
  arrive live.

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

For scripted or repeated local testing, you can skip the dialog entirely by
setting `PGNIMBUS_CONN`, which opens straight to the main window:

```bash
export PGNIMBUS_CONN="Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=secret"
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

## Roadmap

- Command palette
- Extension manager

## License

MIT — see [LICENSE](LICENSE).
