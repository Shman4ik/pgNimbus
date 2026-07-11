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

## App icon / logo assets

Full reference: [`design/LOGO-ASSETS.md`](design/LOGO-ASSETS.md); the
designer hand-off brief is [`design/DESIGNER-BRIEF.md`](design/DESIGNER-BRIEF.md).
**Keep both current** when assets or the pipeline change.

Sources live in `design/masters/` and are **hand-drawn per size** — the
scripts *copy/assemble* them, they do **not** downscale one master into every
tiny icon (that produced muddy 16–32px icons; fixed 2026-07). Icon tiles are
**square full-bleed** (no baked rounding); the OS/store rounds them. Layout:

- `design/masters/icon/icon-{16,24,32,48,256,1024}.png` — the app tile,
  square solid-bg, hand-tuned/simplified at small sizes.
- `design/masters/window/window-{light,dark}-256.png` — transparent line-art
  window icons.
- `design/masters/logo/` — README logo (`logo.svg`, `logo-{light,dark}.png`),
  plus planned `wordmark-*` lockup and `social-preview.png` (1280×640).
- `design/archive/` — superseded concepts (old `icon-tile.png`, `simple/`, …).

Everything in `PgNimbus.App/Assets/` is **generated** by
`scripts/windows/make-app-icons.ps1` (Windows-only, System.Drawing) —
regenerate via that script, don't hand-edit. Output filenames are stable so
csproj / WiX / MSIX manifest reference them unchanged:

- `icon-256-light.png` / `icon-256-dark.png` — window (title-bar/taskbar)
  icons, copied verbatim from `window/`. Windows don't set `Icon` in XAML;
  each window calls `ThemedWindowIcon.Attach(this)` in its constructor, which
  picks the variant for the actual theme and re-applies it on live switches.
- `app.ico` — 16–256px multi-size tile; the exe (`ApplicationIcon`) and MSI
  icon.
- `Assets/Msix/*` — MSIX tiles (44/150/50), packaging-time-only.

## Tech stack

- `net10.0` for all projects.
- Core: `Npgsql`.
- App: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`,
  `Avalonia.Fonts.Inter`, `Avalonia.Controls.DataGrid`, `Avalonia.AvaloniaEdit`,
  `CommunityToolkit.Mvvm`, `AvaloniaUI.DiagnosticsSupport` (DevTools/MCP —
  Debug-only, wired via `.WithDeveloperTools()` in `Program.cs`, see below).
- Tests: `PgNimbus.Core.Tests` — TUnit on Microsoft.Testing.Platform. Run
  `dotnet test --project PgNimbus.Core.Tests` (MTP mode comes from the
  `test.runner` opt-in in the repo-root `global.json`) or plain
  `dotnet run --project PgNimbus.Core.Tests`. Never add
  `Microsoft.NET.Test.Sdk` to a TUnit project — it breaks test discovery.
- Benchmarks: `PgNimbus.Benchmarks` — a plain console project (Core-only, no
  UI deps) measuring the query engine through its streaming API; see
  "Benchmarks pipeline" below.
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
   this, a running app can't be discovered by the MCP server. Both are
   **Debug-only** (a `Condition` on the `PackageReference`, `#if DEBUG`
   around the call): the package is part of AvaloniaUI's commercial
   Developer Tools and ships no explicit redistribution license, so it must
   not be linked into public Release/AOT binaries. Consequence: MCP
   inspection only works against a Debug build — `dotnet run` (default
   Debug) is fine, a `-c Release` or published AOT binary won't be
   discoverable.
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

## Benchmarks pipeline

"Fast" is measured, not asserted. `.github/workflows/benchmark.yml` runs
[`scripts/benchmarks/run-benchmarks.sh`](scripts/benchmarks/run-benchmarks.sh)
(ubuntu runner + a `postgres:17` service container). It's a reusable
workflow (`workflow_call`) invoked as a job from `release.yml` — it no
longer runs on every PR or push to `main`, only as part of the release
pipeline (tag push, or a manual `workflow_dispatch` test run of
`release.yml`), so it measures a real tagged build rather than every commit.
It's also directly `workflow_dispatch`-able on its own for ad hoc
measurement. Results go to the job summary and a `bench-results` artifact;
real tag-triggered releases also append to the gh-pages history via
`benchmark-action/github-action-benchmark` (charts at
`https://shman4ik.github.io/pgNimbus/dev/bench/`) — controlled by the
`record_history` input, which `release.yml` sets from
`startsWith(github.ref, 'refs/tags/v')` so `workflow_dispatch` test runs of
the release pipeline don't pollute the trend history. Three moving parts:

1. **Startup probe** — `PGNIMBUS_STARTUP_PROBE=1` makes the app print
   `PGNIMBUS_STARTUP_PROBE window_ms=… rss_bytes=…` after its first window
   renders its first frame, then exit (`PgNimbus.App/StartupProbe.cs`, armed
   in `App.OnFrameworkInitializationCompleted`). `window_ms` is measured from
   OS process start, so it captures AOT-vs-JIT differences honestly.
2. **`PgNimbus.Benchmarks`** — console project measuring connect (cold pool),
   `SELECT 1` round-trip, time-to-first-`RowBatch`, and full-stream
   throughput of a 100k-row mixed-type SELECT, through `QueryEngine`'s
   streaming path (the same API the UI uses). Prints `PGNIMBUS_BENCH
   name=value` lines; config via `PGNIMBUS_BENCH_CONN/ROWS/ITERS`.
3. **The script** — builds JIT Release, publishes linux-x64 NativeAOT, runs
   the startup probe N times per mode under Xvfb (one discarded warm-up run,
   then medians), runs the query benchmarks, and writes
   `bench-results/benchmarks.json` (github-action-benchmark
   `customSmallerIsBetter` format — keep every metric smaller-is-better, so
   throughput is reported as stream *time*) plus `summary.md`.
   `PGNIMBUS_BENCH_SKIP_AOT=1` skips the slow AOT publish for local runs.

Numbers are machine-relative (this sandbox: ~160 ms AOT / ~2 s JIT to first
frame; CI runners differ) — the point is the trend per commit, not the
absolute value. If a change renames a metric in `benchmarks.json`, its
gh-pages history starts over under the new name.

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
  which also generates `.icns` directly from the `design/masters/icon/` tiles
  via `sips`/`iconutil` (stock macOS tools, no extra dependency) — each
  iconset slot uses the exact-size master when one exists, else downscales
  from `icon-1024.png`.
- **winget** — the workflow renders (via
  [`scripts/winget/render-manifest.sh`](scripts/winget/render-manifest.sh)
  and the templates in `packaging/winget/`) the three manifest files
  winget requires and validates them with `winget validate`, but does
  **not** submit them anywhere. `winget-pkgs` needs a manual first PR
  (registers the `pgNimbus.pgNimbus` identifier) before any automated
  submission could work — the generated `winget-manifests.zip` release
  asset is for that manual step.

The direct-download MSI/dmg are **unsigned** and stay that way — deliberately
**not** pursuing a paid signing service (Azure Artifact Signing / a purchased
Authenticode cert): pgNimbus is a free OSS project with no revenue. Microsoft
Store publishing gets the trust/SmartScreen benefit for $0 instead (Store
re-signs an uploaded MSIX with its own trusted certificate during
certification — the package only needs a throwaway self-signed cert to
satisfy the upload requirement, not a purchased one), and Store apps are
automatically discoverable via winget's built-in `msstore` source with no
separate winget submission. It's an *additional* channel, not a replacement
for the direct MSI + `winget-pkgs` path above — the two coexist.

### Microsoft Store (MSIX)

`build-windows` also packs `publish/win-x64` into a self-signed `.msix` via
[`scripts/windows/build-msix.ps1`](scripts/windows/build-msix.ps1), uploaded
as the `windows-msix` CI artifact — **not** attached to the public GitHub
Release, since a self-signed MSIX can't be installed without the user
manually trusting the cert first, and Store re-signing only happens after
you upload it to Partner Center.

- **Manifest**: [`installer/msix/Package.appxmanifest`](installer/msix/Package.appxmanifest)
  is a template (`$VERSION$` placeholder) with `Identity/Publisher` hardcoded
  to this repo's reserved Partner Center product identity
  (`DmitriiShmanev.pgNimbus` / `CN=04FDF7B0-6D86-4EB7-B798-21CD434897BC`,
  Store ID `9N6SZT42XJ24`) — plain Win32/Desktop Bridge (`runFullTrust`
  capability, `EntryPoint="Windows.FullTrustApplication"`), not Windows App
  SDK, since the app is a native AOT exe with no WinUI dependency.
- **Tile assets**: `PgNimbus.App/Assets/Msix/*.png` (Square44x44Logo,
  Square150x150Logo, StoreLogo) are generated by
  [`scripts/windows/make-app-icons.ps1`](scripts/windows/make-app-icons.ps1)
  from the `design/masters/icon/` tiles (44/50 px from the 48 px master,
  150 px from the 256 px master), same as the other shipped icons — excluded
  from `AvaloniaResource` in the App csproj since they're packaging-time-only.
- **`build-msix.ps1`**: stages the publish output + tile assets + rendered
  manifest, packs with `makeappx.exe`, signs with an ephemeral
  `New-SelfSignedCertificate` (Subject matching the manifest's `Publisher`,
  deleted from the cert store right after signing). Resolves `makeappx`/
  `signtool` by globbing every installed Windows SDK's `bin\<ver>\x64` dir
  and taking the newest, so it doesn't hardcode an SDK version that'll drift
  on GitHub's runner images. MSIX versions are 4-part with the last field
  forced to `0` (Store convention) — `ConvertTo-MsixVersion` strips any
  prerelease suffix like `-ci.42` from `VERSION` before padding.
- **Submission** (manual, not automated yet): download the `windows-msix`
  artifact from the release workflow run and upload it through Partner
  Center → this product → Packages, then fill in Store listing / age ratings
  / submit for certification. Could move to the Microsoft Store submission
  API later (needs its own Entra ID app registration under the Partner
  Center account — free, unrelated to Azure Artifact Signing).
