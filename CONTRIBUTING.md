# Contributing to pgNimbus

Thanks for your interest! pgNimbus is a fast, open-source PostgreSQL GUI
client (.NET 10 + Avalonia 12, MIT). Contributions of all sizes are
welcome — the [README backlog](README.md#backlog) is intentionally scoped
as individually shippable pieces, and issues labeled `good first issue`
are a fine place to start.

## Building

See [Building and running](README.md#building-and-running) in the README.
Short version: install the .NET 10 SDK, then

```bash
dotnet build
dotnet run --project PgNimbus.App
```

Run the tests with:

```bash
dotnet test --project PgNimbus.Core.Tests
```

```bash
dotnet test --project PgNimbus.App.Tests
```

The first covers the engine and the pure logic. The second drives real
windows on Avalonia's headless platform with real key input, so it needs no
display and no database.

(Both use TUnit on Microsoft.Testing.Platform. Never add
`Microsoft.NET.Test.Sdk` to either, that breaks test discovery.)

## The two hard architectural rules

Every PR is gated on these — they're what keeps the project's thesis
(truly fast, PostgreSQL-first) intact:

1. **`PgNimbus.Core` has zero UI dependencies.** It references only
   `Npgsql` (plus credential/SSH support libraries). Nothing from
   `Avalonia.*` or `CommunityToolkit.Mvvm` may leak into it — anything
   UI-related belongs in `PgNimbus.App`. This keeps the engine reusable
   for a future CLI or test harness.
2. **Streaming + cancellation are non-negotiable.**
   `QueryEngine.ExecuteAsync` returns rows via `IAsyncEnumerable<RowBatch>`
   in ~200-row batches so the UI renders before the full result set
   arrives. Every execution takes a `CancellationToken` and must actually
   stop mid-flight, not just at the start. (The one deliberate exception —
   materialized results inside an explicit transaction — is documented in
   [CLAUDE.md](CLAUDE.md).)

Also: PostgreSQL-first, not lowest-common-denominator. Schema
introspection reads `pg_catalog` directly, not `information_schema`. And
passwords never live on `ConnectionProfile` — they come from
`ICredentialStore` at connect time.

## Coding conventions

- DTOs are `record`s.
- MVVM via CommunityToolkit source generators (`[ObservableProperty]`,
  `[RelayCommand]`) — no hand-written `INotifyPropertyChanged`.
- Async all the way; no sync-over-async, no blocking `.Result`/`.Wait()`.
- `Nullable` is enabled — respect it; don't silence warnings with `!`
  unless the value is truly provably non-null.
- `AvaloniaUseCompiledBindingsByDefault` is on — don't add uncompiled
  (reflection) bindings. This also matters for NativeAOT: the app ships
  as an AOT binary, so no reflection-dependent code paths.

## Verifying UI changes

UI work should be verified in the running app, not just by compilation.
On a headless Linux box (or CI-like sandbox) the loop is: `Xvfb` for a
virtual display, a local PostgreSQL with seed data, `PGNIMBUS_CONN` to
skip the connection dialog, `xdotool` to drive, and ImageMagick's
`import` for screenshots. The exact commands are in
[CLAUDE.md](CLAUDE.md#bootstrapping-a-fresh-linuxci-sandbox-no-net-no-display-no-postgres).
Please check both themes (the in-app light/dark toggle) for visual
changes.

CI compares every screen against a committed reference image, so a UI
change makes the screenshot check go red. That is expected. When the change
is intended, refresh the reference set:

```bash
scripts/screenshots/update-baselines.sh
```

The images are pixel data and only comparable against the operating system
that made them, and CI renders on Linux. On Linux the script renders
directly; anywhere else it uses Docker. You can also run the Screenshots
workflow from the Actions tab, which renders on a real runner and opens a
pull request.

Include the updated images in your PR. Reviewing them is the point: it is
where we agree on how the app now looks.

## Pull requests

- Keep PRs focused — one feature or fix per PR.
- CI must pass (`dotnet build` + tests on every PR).
- If your change alters behavior documented in README or CLAUDE.md,
  update those files in the same PR — a stale doc is treated as a bug.
- New pure logic in `PgNimbus.Core` (parsers, formatters, matchers…) is
  the easiest thing to test — please add TUnit tests alongside it.
