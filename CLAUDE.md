# pgNimbus — project memory

## What this is

A fast, open-source PostgreSQL GUI client (.NET 10 + Avalonia 11), MIT
licensed. Windows is the primary target; the core engine stays
cross-platform-capable. The thesis: **truly fast + open source + modern UI** —
a gap none of pgAdmin/DBeaver (heavy), TablePlus (fast but paid/closed), or
HeidiSQL (fast but dated, MySQL-first) fill. pgNimbus aims for HeidiSQL's
speed with TablePlus's polish, PostgreSQL-first.

## Hard architectural rules

1. **`PgNimbus.Core` has zero Avalonia/UI dependencies.** It references only
   `Npgsql`. Anything UI-related belongs in `PgNimbus.App`. This keeps the
   engine reusable for a future CLI or test harness — don't leak
   `Avalonia.*` or `CommunityToolkit.Mvvm` types into `Core`.
2. **Streaming + cancellation are non-negotiable.** `QueryEngine.ExecuteAsync`
   returns result rows via `IAsyncEnumerable<RowBatch>` in ~200-row batches so
   the UI can render before the full result set arrives. Every execution
   takes a `CancellationToken` and must actually stop mid-flight, not just at
   the start.
3. **PostgreSQL-first, not lowest-common-denominator.** `SchemaService` reads
   `pg_catalog` directly (not `information_schema`) so it can see materialized
   views, partitioned tables, and real Postgres semantics (e.g. primary-key
   flags via `pg_constraint`).
4. **No passwords on `ConnectionProfile`.** Passwords come from
   `ICredentialStore` (DPAPI on Windows via `WindowsDpapiCredentialStore`, a
   permission-restricted file fallback elsewhere via
   `PlainFileCredentialStore`) at connect time, never persisted on the
   profile record itself.

## Tech stack

- `net10.0` for both projects.
- Core: `Npgsql`.
- App: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`,
  `Avalonia.Fonts.Inter`, `Avalonia.Controls.DataGrid`, `Avalonia.AvaloniaEdit`,
  `CommunityToolkit.Mvvm`, `Avalonia.Diagnostics` (Debug only).
- `AvaloniaUseCompiledBindingsByDefault` is on — don't add uncompiled
  (reflection) bindings.

## Coding conventions

- DTOs are `record`s (see `QueryResult.cs`, `SchemaService.cs`).
- MVVM via CommunityToolkit source generators (`[ObservableProperty]`,
  `[RelayCommand]`) — no hand-written `INotifyPropertyChanged`.
- Async all the way; no sync-over-async, no blocking `.Result`/`.Wait()`.
- `Nullable` is enabled — respect it, don't silence with `!` unless truly
  provably non-null.
- `AvaloniaEdit.TextEditor` does not expose `Text` as a bindable
  `AvaloniaProperty` — it's a plain CLR property backed by a `TextDocument`.
  Two-way sync with the ViewModel is done manually in `MainWindow.axaml.cs`
  (via `TextChanged` + `PropertyChanged`, with a re-entrancy guard), not via
  XAML `Binding`.
