---
description: "Headless screenshot harness (tools/Screenshot) and headless UI tests (PgNimbus.App.Tests)."
paths:
  - "tools/Screenshot/**"
  - "tests/PgNimbus.App.Tests/**"
  - "scripts/screenshots/**"
---

<!-- Moved out of the root CLAUDE.md so it loads only when working on these paths. Same rule applies: keep it current in the same PR. -->

## Headless screenshot harness (`tools/Screenshot`)

Renders the real Views bound to fixture ViewModels through `Avalonia.Headless`
(Skia software rendering, `UseHeadlessDrawing = false`) and writes PNGs — one
`<scenario>.<light|dark>.png` per scenario × theme. Works on Windows, Linux and
CI alike, with no display, no Xvfb/xdotool, and **no Postgres**:

```bash
dotnet run --project tools/Screenshot -- <outputDir> [scenario-substring]
                                        [--baseline <dir>] [--publish <repo-root>]
```

Pass a scratch directory — nothing it writes is committed. Omit the filter to
render every scenario in `Scenarios.All` (the single list: `Program` walks it,
the baseline set is exactly its names × {light,dark}, and `Marketing` picks its
sources out of it by name).

The harness wears three hats, and the second and third were added because a PNG
artifact nobody opens is not a check:

1. **Smoke** — a view that throws while loading, or renders no frame at all,
   fails the run.
2. **Visual regression** (`--baseline`) — each frame is compared against the
   committed baseline in `tools/Screenshot/baselines/`, and anything past
   tolerance fails the run and leaves a `*.diff.png` (baseline desaturated,
   changed pixels magenta) behind. `ci.yml` runs it this way on every PR.
   **Baselines are OS-specific**: two renders of one commit on the same OS are
   bit-identical, while the same frames on Windows vs Linux differ by 0.6–6%
   from glyph rasterization alone — hence the 0.1%-of-pixels threshold, and
   hence `scripts/screenshots/update-baselines.sh` reaching for the .NET SDK
   container (plus `libfontconfig1`, which Skia links against and the image
   lacks) when it isn't already on Linux. The `screenshots.yml` workflow does
   the same on a real runner and opens a PR. A missing baseline is reported
   `NEW` and doesn't fail — a developer adding a scenario can't render a Linux
   baseline without Docker, and blocking that would only teach people to skip
   the check.
   **Four scenarios deliberately have no baseline**: the tabbed security
   window (`security-window`, `-permissions`, `-default-privileges`, `-rls`).
   Its segmented tab strip animates the selected tab and the harness catches it
   at a different moment each render (0.3–0.4% on CI), so a baseline only makes
   false `CHANGED` reports. `update-baselines.sh` leaves them out after a
   wholesale refresh; one refresh that didn't (#261) turned `main` red (2026-09).
   Give them baselines back only once that render is deterministic.
   **Take baselines from a full render, never a filtered one** (2026-09). The
   harness renders every scenario in one process, and which Inter face a SemiBold
   request resolves to depends on what earlier scenarios loaded: baselines from a
   run filtered to `connection` drew every bold label heavier than CI's full run
   and failed it by 1.3%. A filter is for looking, not for committing.
   **A new scenario goes at the end of `Scenarios.All`** (2026-09-28): the
   headless clock advances with every frame rendered, so one inserted mid-list
   moves the moment every later window's transitions are caught at. Below the
   diff tolerance, but `update-baselines.sh` replaces files wholesale and
   rewrote a dozen untouched windows' baselines anyway.
3. **Publishing** (`--publish`) — `Marketing.cs` maps scenarios to the images
   that face users: `docs/screenshots/` (README + docs site) and
   `design/store/screenshots/` (Store listing, padded to the Store's 1366×768
   minimum on a backdrop sampled from the shot's own chrome so it matches its
   theme). Run `scripts/screenshots/update-published.sh` in any PR that changes
   what they show (UI design rule 9), and before a release — **on Windows**:
   unlike the baselines it renders on the host, because the monospace panes need
   Cascadia Code or Consolas and the CI container has neither (until 2026-09 the
   Store listing showed its SQL in a proportional font).
   These used to be hand-captured against a live database, which made them go
   stale silently and leaked real detail — the old main-window shot published a
   live Neon hostname. The README's animated GIFs are deliberately **not**
   covered: they show motion and are still recorded by hand.

Full rationale, thresholds and the weekly-release loop:
[`docs/design/release-checks.md`](docs/design/release-checks.md).

How the fixtures work, and why they're shaped this way:

- **The data source is offline but unroutable.** Every service takes an
  `NpgsqlDataSource`, and `NpgsqlDataSource.Create` opens no socket, so the whole
  graph constructs with no server. `Fixtures` points it at TEST-NET-3
  (`203.0.113.1`) rather than a closed local port on purpose: windows that
  refresh when they open (activity, database overview, schema tree) would get a
  fast connection-refused back from a closed port and overwrite the seeded status
  line with an error a fraction of a second after the window shows. An address
  that never answers leaves the seeded state alone.
- **Scenarios drive the public ViewModel surface**, the same properties and
  commands production sets — not the views. Two seams exist purely for this:
  `SchemaTreeNode.SeedChildren` (fills a node's children and marks it loaded, so
  expanding never reaches for the catalog) and `QueryViewModel.SeedResult`
  (points the grid at a result set that was never run). Both are documented as
  harness-only; production still goes through the lazy-load and run paths.
- **Nothing reads or writes the developer's real app data**, and two layers
  make that hold. The claim used to rest on `Fixtures` clearing the lists the
  default stores had loaded, which kept real entries out of a screenshot but did
  nothing about writes: the save-query UI tests saved through those default
  stores, and on 2026-09-28 the owner's real `saved-queries.json` was found
  holding the fixture list. (1) **The whole app data root is redirected per
  process.** Every store falls back to `AppDataPaths.GetRootDirectory()`, which
  honours `PGNIMBUS_DATA_DIR` (`AppDataPaths.OverrideVariable`), and
  `IsolatedAppData.Enable` (in `tools/Screenshot`) points it at a throwaway
  temp directory, deleted on exit. That is what covers what a fixture can't
  inject into: `App`'s static settings and completion-usage stores (the
  Preferences page and the theme toggle write through them), the workspace,
  window placement, the crash log. It must run before anything builds a store —
  they resolve their path in their constructors, and `App`'s are static — so
  the harness calls it on `Program`'s first line and `PgNimbus.App.Tests` from
  a `[ModuleInitializer]`. It always overwrites the variable, so a developer
  who set it to a directory they use still doesn't get test writes there.
  (2) **`Fixtures.MainWindowViewModel` injects its own stores**:
  `MainViewModel` takes optional `savedQueryStore`/`historyStore`, and each
  fixture view model gets a fresh directory under the isolated root, so it
  starts empty and one test's saves never show up in another's list.
  `AppDataIsolationTests` fails if either layer goes (a fixture store under
  `AppDataPaths.GetDefaultRootDirectory()`, two fixtures sharing a file, or a
  Preferences write not landing in the redirected `settings.json`). The
  connection-dialog scenario also points `ConnectionProfileStore` at an
  isolated directory and uses `MemoryCredentialStore`, so it never opens the
  developer's saved connections or native password store.
- **Every app data file goes through `Core/Settings/AppDataFile`** (2026-09
  security audit, finding 10 and the "non-atomic writes" item of 18). Before
  it, each store called `File.WriteAllText`, which on Linux and macOS created
  `0644` files under a home that is often `0755`, so any local user could read
  the query history, the workspace SQL and the connection list; and a crash
  mid-write left a torn `connections.json` that `Load` read as "no profiles",
  after which the connection dialog's autosave wrote the empty list over every
  profile. Now: (a) files are written to a temp file in the same directory
  (`UnixCreateMode` 0600, never set on Windows, where it throws) and renamed
  over the target, so a reader sees the old file or the new one; the crash log
  is appended with the same create mode. (b) A directory the helper creates is
  `0700`; an **existing** one is tightened only if it is the app data root or
  inside it. A store handed an explicit path must never chmod a directory the
  app does not own: the Core tests write into `/tmp`, and in a root container
  that chmod would succeed. (c) A file that cannot be parsed is moved aside as
  `<name>.corrupt-<UTC stamp>` before the store starts over, so the next save
  cannot overwrite the only copy. (d) `App.TightenAppDataOnce` tightens the
  root, its files and `logs/`/`credentials/` once per launch on the thread
  pool, for what an older version left readable; a failure goes to the crash
  log. (e) **No temp fallback**: `AppDataPaths.ResolveDefaultRoot` answers null
  when neither `ApplicationData` nor `HOME` resolves, `Resolve(name)` is then
  null, and a null path reads as "nothing saved" and drops writes, so the
  session runs from memory. Core tests reach that state through the internal
  `AppDataPaths.RootResolverForTests` seam, never by blanking `HOME` for the
  whole process. The seam is an `AsyncLocal`, so it reaches only the test that
  set it: as a plain static it once handed a null root to the SSH host-key test
  running at the same moment, `[NotInParallel]` notwithstanding. The mode tests skip on Windows;
  they were run in the .NET SDK Linux container through `wslc`.
- **Workspace restore reads files off the UI thread** (same audit, finding
  18). Reattaching a restored tab to its `.sql` file was a synchronous
  `File.ReadAllText` in `MainViewModel`'s constructor, so a file on a stale UNC
  path held the window for the SMB timeout, and only IO/access errors were
  caught, so a path with a NUL in it (`ArgumentException`) crashed every
  launch. The reads now run on the thread pool, give up on
  `ArgumentException`/`NotSupportedException`/`SecurityException` too, and
  attach on the UI thread; `MainViewModel.WorkspaceFilesRestored` is the task a
  test waits on (`WorkspaceRestoreTests`, via the fixture's `workspace`
  parameter).

## Headless UI tests (`PgNimbus.App.Tests`)

Real windows on Avalonia's headless platform, driven with real key input — the
layer that used to be a person clicking through the app. It reuses
`tools/Screenshot`'s fixture graph (hence the `ProjectReference` to it) rather
than growing a second set that would drift from what the screenshots show. It
references `tools/CompletionBench` too, for the completion audit's stand
(catalog snapshot and corpus) that `CompletionTypingReplayTests` types through
the real editor — both of its measurements run in every build, about two
minutes of the suite between them.

What it covers that nothing else does: that a gesture reaches its command, that
the palette invokes the entry it highlights, that a saved query opens a *new*
tab (UI design rule 3), that the results grid builds a column per result column
and re-points on a tab switch, and that every window opens **and closes** — the
detach path a render-and-exit pass never runs.

Three landmines, all load-bearing:

- **`Ui.Run(async () => …)` is deliberately the only overload.** Avalonia's
  `HeadlessUnitTestSession` has a `Dispatch<T>(Func<T>)` that an async lambda
  binds to with `T = Task`, handing back a `Task<Task>` whose outer task
  completes the moment the body *returns* its task. The dispatcher then stops
  pumping and every assertion after the first `await` lands on a task nobody
  observes — **the whole suite passes without running**. That is how this was
  written the first time; it was caught only by deliberately breaking an
  assertion to check the tests could still go red. Do that check when adding
  tests here.
- **Gestures come from the catalog**, via `Ui.Press(window, CommandId.X)`, never
  typed in — otherwise a test keeps passing after a chord moves, and fails on
  macOS where the same entry resolves to Cmd (UI design rule 5).

- **Never await a catalog fetch against the fixture data source.** It points at
  TEST-NET-3 so nothing ever answers, which means an awaited fetch (e.g.
  `OpenCommandPaletteAsync`, which waits for the table list) returns only when
  the OS abandons the TCP connect: ~21 s on Windows, ~127 s on a Linux runner
  (six SYN retries). One such `await` was two of CI's five minutes until
  2026-09. Fire it and move on (`_ = …`), as the screenshot scenarios do.

The session runs the app with **no lifetime**, asserted by a test: with one,
`App.OnFrameworkInitializationCompleted` would read the `AppSettings` and, with
`AutoConnectLastProfile` on, try to connect to the last database from a unit
test. Those settings are no longer the developer's own — the app data root is
redirected for the test process (see the harness's "Nothing reads or writes
the developer's real app data" above) — but the no-lifetime rule stays.
