# pgNimbus

A fast, native-feeling, open-source **PostgreSQL** GUI client, built with **.NET
10 + Avalonia 11**. MIT licensed. Windows is the primary target, but the
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

1. **Native performance** — NativeAOT-friendly code, cold start under 500 ms.
2. **PostgreSQL-first** — deep `pg_catalog` introspection (materialized views,
   real types, primary-key flags, and later EXPLAIN visualization); never the
   lowest-common-denominator SQL dialect.
3. **Keyboard-first** — run, cancel, and navigate without touching the mouse.
4. **Streaming results** — the first screenful renders before the full result
   set arrives, backed by a virtualized grid for large results.

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

The app currently reads its connection string from the `PGNIMBUS_CONN`
environment variable (falling back to a `localhost:5432/postgres` default if
unset) — there's no connection-manager UI yet, see the roadmap below.

```bash
export PGNIMBUS_CONN="Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=secret"
dotnet run --project PgNimbus.App
```

### Publishing a NativeAOT build (Windows)

```bash
dotnet publish PgNimbus.App -c Release -r win-x64 -p:PublishAot=true
```

## Roadmap (post-MVP)

- Command palette
- Schema-aware SQL autocomplete
- EXPLAIN tree visualization
- Inline cell editing in the results grid
- Per-connection accent color (e.g. red for production)
- LISTEN/NOTIFY monitor
- Extension manager

## License

MIT — see [LICENSE](LICENSE).
