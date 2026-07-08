# pgNimbus — project memory

## What this is

A fast, open-source PostgreSQL GUI client (.NET 10 + Avalonia 12), MIT
licensed. Windows is the primary target; the core engine stays
cross-platform-capable. The thesis: **truly fast + open source + modern UI** —
a gap none of pgAdmin/DBeaver (heavy), TablePlus (fast but paid/closed), or
HeidiSQL (fast but dated, MySQL-first) fill. pgNimbus aims for HeidiSQL's
speed with TablePlus's polish, PostgreSQL-first.

## Keep this file current

Whenever a change touches something this file documents — tech stack
versions, architectural rules, coding conventions, the sandbox bootstrap
steps — update the corresponding section in the same commit/PR. Treat a
stale `CLAUDE.md` (e.g. it still saying "Avalonia 11" after an upgrade to
12) as a bug, not a nitpick: it's the first thing a fresh session reads,
and wrong project memory is worse than none.

## Hard architectural rules

1. **`PgNimbus.Core` has zero Avalonia/UI dependencies.** It references only
   `Npgsql`. Anything UI-related belongs in `PgNimbus.App`. This keeps the
   engine reusable for a future CLI or test harness — don't leak
   `Avalonia.*` or `CommunityToolkit.Mvvm` types into `Core`.
2. **Streaming + cancellation are non-negotiable.** `QueryEngine.ExecuteAsync`
   returns result rows via `IAsyncEnumerable<RowBatch>` in ~200-row batches so
   the UI can render before the full result set arrives. Every execution
   takes a `CancellationToken` and must actually stop mid-flight, not just at
   the start. The one deliberate exception: inside an explicit transaction
   (`BeginTransactionAsync`), statements run on the single held session
   connection and return a fully-materialized `MaterializedResultSet` instead —
   a lazily-streaming reader would pin that connection open and block the next
   statement in the transaction. A failed statement inside a transaction
   auto-rolls-back the block (so the connection never lingers in Postgres's
   aborted-transaction state), and `TransactionStateChanged` is how the App's
   "in transaction" indicator stays in sync no matter which path changed it.
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

- `net10.0` for all projects.
- Core: `Npgsql`.
- App: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`,
  `Avalonia.Fonts.Inter`, `Avalonia.Controls.DataGrid`, `Avalonia.AvaloniaEdit`,
  `CommunityToolkit.Mvvm`, `AvaloniaUI.DiagnosticsSupport` (DevTools/MCP —
  wired via `.WithDeveloperTools()` in `Program.cs`, see below).
- Tests: `PgNimbus.Core.Tests` — TUnit on Microsoft.Testing.Platform. Run
  `dotnet test --project PgNimbus.Core.Tests` (MTP mode comes from the
  `test.runner` opt-in in the repo-root `global.json`) or plain
  `dotnet run --project PgNimbus.Core.Tests`. Never add
  `Microsoft.NET.Test.Sdk` to a TUnit project — it breaks test discovery.
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
- `SqlFormatter` follows <https://www.sqlstyle.guide/> ("river" layout: root
  keywords right-aligned to a common column, content to its right). The tests
  in `PgNimbus.Core.Tests` assert exact spacing — a deliberate layout change
  must update them, and every layout must survive the formatter's token
  round-trip safety net.

## Avalonia DevTools MCP

The app exposes its live visual tree / runtime state to an MCP client (Claude
Code, VS, Rider) via the Avalonia DevTools MCP server. Two pieces make it work:

1. **In the app** — `AvaloniaUI.DiagnosticsSupport` is referenced and
   `.WithDeveloperTools()` is on the `AppBuilder` in `Program.cs`. Without
   this, a running app can't be discovered by the MCP server. Keep it wired
   up (it's the discovery hook, not a Debug-only convenience).
2. **The MCP server** — the `avdt` global .NET tool runs as `avdt mcp`.
   Register it once at user scope; it reads its license from the
   `AVALONIA_TOOLS_LICENSE_KEY` env var (`ACCELERATE_LICENSE_KEY` on
   Avalonia 11.x and earlier):

   ```bash
   claude mcp add --scope user avalonia_devtools \
     -e AVALONIA_TOOLS_LICENSE_KEY=<key> -- avdt mcp
   claude mcp list   # avalonia_devtools: avdt mcp - ✓ Connected
   ```

   The server only sees the app while it's running, so launch the app before
   asking the MCP to inspect it. Docs:
   https://docs.avaloniaui.net/tools/developer-tools/mcp

## Bootstrapping a fresh Linux/CI sandbox (no .NET, no display, no Postgres)

A bare container has none of this preinstalled. All of it installs cleanly
via `apt-get` (no external downloads needed — `dotnet-install.sh` /
`dot.net` are typically blocked by sandboxed network policies, but the
Ubuntu `dotnet-sdk-10.0` apt package works and is the reliable path):

```bash
apt-get update -qq
apt-get install -y dotnet-sdk-10.0          # build/run the app
apt-get install -y xvfb imagemagick xdotool # headless display + screenshot + input
apt-get install -y postgresql               # a real DB to click through, not just mocks
apt-get install -y clang zlib1g-dev         # only for NativeAOT publish (linux-x64)
```

The linux-x64 NativeAOT publish works and is the build to use for
startup-time claims (`dotnet publish PgNimbus.App -c Release -r linux-x64
-p:PublishAot=true`, ~100 ms launch-to-window vs ~700 ms JIT). Two
AOT-specific landmines are already handled in the codebase — keep them
that way: `SatelliteResourceLanguages=en` in the App csproj (a
culture-named satellite assembly + InvariantGlobalization crashes
Avalonia's asset resolver at startup under AOT, surfacing as a bogus
"avares://... not found") and no reflection binding paths (the results
grid binds columns via `RowIndexConverter`, not `"[i]"` indexer paths).

Then, to actually see and drive the UI:

```bash
# 1. A virtual display, once per sandbox lifetime:
Xvfb :99 -screen 0 1280x800x24 &

# 2. A local Postgres with seed data:
service postgresql start
su - postgres -c "psql -c \"ALTER USER postgres PASSWORD 'postgres';\""
su - postgres -c "createdb demo"
PGPASSWORD=postgres psql -h localhost -U postgres -d demo -c "CREATE TABLE ..."

# 3. Build once, then run against DISPLAY=:99. Set PGNIMBUS_CONN so the
#    app opens straight to MainWindow instead of the connection dialog —
#    App.axaml.cs reads this env var and skips ConnectionDialog entirely.
#    Any format ConnectionStringParser understands works here (postgres://
#    URI, JDBC, Key=Value;, libpq keywords, psql command line):
dotnet build
DISPLAY=:99 PGNIMBUS_CONN="Host=localhost;Port=5432;Database=demo;Username=postgres;Password=postgres" \
    timeout 15 dotnet run --project PgNimbus.App --no-build &

# 4. Drive it (optional) and capture a screenshot:
DISPLAY=:99 xdotool mousemove <x> <y> click 1   # click/expand/select
DISPLAY=:99 xdotool key ctrl+a; xdotool type "SELECT * FROM t;"
DISPLAY=:99 import -window root screenshot.png  # ImageMagick, captures the whole root window
```

Notes:
- `dotnet run` under `timeout` is normal — the app has no natural exit, so
  screenshot then let the timeout reap it.
- Test both themes by toggling `RequestedThemeVariant` in `App.axaml`
  (`Default`/`Dark`) between runs — revert it before committing.
- This is how the Avalonia 11→12 upgrade and the PowerToys-style UI polish
  were actually verified (not just built) in a Claude Code sandbox with no
  prior .NET/GUI tooling.

## Release pipeline

`.github/workflows/release.yml` runs on every `vX.Y.Z` tag push (or manually
via `workflow_dispatch`, which builds everything but skips the "release"
job so it never publishes). It produces, per tag:

- **Windows** — `dotnet publish -r win-x64 -p:PublishAot=true`, then a
  per-user WiX v5 MSI built from [`installer/windows/Product.wxs`](installer/windows/Product.wxs)
  via the `wix` .NET global tool (`wix build ... -d PublishDir=... -d
  Version=...`). Per-user (installs to `%LocalAppData%`, no elevation) is
  deliberate: the MSI is currently **unsigned** (no code-signing cert yet),
  and per-machine + unsigned is a much worse UAC/SmartScreen experience.
  The `UpgradeCode` GUID in `Product.wxs` is fixed forever — never
  regenerate it, that's what makes installing a newer tag upgrade in place
  instead of side-by-side.
- **macOS** — `osx-arm64` only, built on a `macos-14` runner. GitHub retired
  the last Intel macOS runner image (`macos-13`) in December 2025 and has
  said x86_64 macOS support ends entirely once the `macos-15` image retires
  (Fall 2027) — there's no GitHub-hosted way to build `osx-x64` anymore, so
  don't re-add an Intel matrix leg without a self-hosted Intel Mac runner.
  Also pins to the newest pre-installed Xcode below major version 26:
  Xcode 26 changed Swift auto-linking in a way that breaks NativeAOT's
  static link of `libSystem.Security.Cryptography.Native.Apple.a`
  ("symbol(s) not found for architecture arm64" / `pal_swiftbindings`),
  closed "not planned" upstream
  ([dotnet/runtime#116448](https://github.com/dotnet/runtime/issues/116448)).
  The publish output is wrapped into an unsigned/unnotarized `.app` + `.dmg`
  by [`scripts/macos/build-app-bundle.sh`](scripts/macos/build-app-bundle.sh),
  which also generates `.icns` from `PgNimbus.App/Assets/icon-256.png` via
  `sips`/`iconutil` (stock macOS tools, no extra dependency).
- **winget** — the workflow renders (via
  [`scripts/winget/render-manifest.sh`](scripts/winget/render-manifest.sh)
  and the templates in `packaging/winget/`) the three manifest files
  winget requires and validates them with `winget validate`, but does
  **not** submit them anywhere. `winget-pkgs` needs a manual first PR
  (registers the `pgNimbus.pgNimbus` identifier) before any automated
  submission could work — the generated `winget-manifests.zip` release
  asset is for that manual step.

No code signing (Windows Authenticode or Apple Developer ID) is wired up
yet — both platforms ship unsigned until certs/accounts are available.
When they are, signing slots in as an extra step in `build-windows` /
`build-macos` before packaging, gated on whether the relevant secret is
present, so the pipeline doesn't need restructuring.
