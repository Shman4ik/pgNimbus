# Release stability checks

Internal notes, not part of the published docs site (`docs/design/` is in
`exclude_docs`).

## The problem this solves

The project ships roughly weekly. Before this existed, the checks were:
`PgNimbus.Core.Tests` (engine and pure logic), a build, and a screenshot
artifact somebody could open if they thought to. Everything else — does the
installer install, does the app start, did the UI move — was a person clicking
through the app.

Three gates replace that, plus one automation so the published screenshots stop
going stale.

## 1. Release smoke: launch what was actually built

`scripts/release/smoke-launch.sh` (Linux/macOS) and
`scripts/release/Smoke-Launch.ps1` (Windows) start a built pgNimbus with
`PGNIMBUS_STARTUP_PROBE=1`, which makes the app print one line after its first
window has rendered its first frame and then exit
(`PgNimbus.App/StartupProbe.cs`). Both the exit code and the line are asserted:
an app that quit before drawing anything also exits 0.

`release.yml` runs this against every artifact it produces:

| Job | What gets launched |
| --- | --- |
| `build-windows` | the win-x64 publish output, then the MSI after a silent per-user install (uninstalled again in the same step) |
| `build-macos` | the osx-arm64 publish output, then the binary inside the mounted `.dmg` |
| `build-linux` | the publish output, the `.tar.gz`, the `.AppImage`, and the `.deb` after `apt-get install` resolves its own `Depends` |

The `release` job already `needs` all three, so a package that cannot start
cannot reach a GitHub Release.

This is the layer that catches what a compiler cannot: NativeAOT trimming
something needed at runtime, an asset missing from a package, a `Depends` list
that forgot a library the X11 backend loads, a bundle layout mistake.

Notes and caveats:

- No display on CI, so the Linux legs run under `xvfb-run`. macOS runners have a
  real window server; that leg is the one least proven, since it cannot be
  rehearsed anywhere but a macOS runner. A `workflow_dispatch` run of
  `release.yml` exercises the whole pipeline without publishing anything.
- `PgNimbus.App` is a `WinExe`, so it has no console of its own. The probe line
  is still readable because redirecting stdout gives the process a handle to
  write to — verified, not assumed.
- Without `PGNIMBUS_CONN` the first window is the connection dialog. That is a
  legitimate smoke target: it proves the app started, resolved its theme and
  rendered.

## 2. Visual regression: the screenshots became a gate

`tools/Screenshot` renders every scenario in both themes. With `--baseline` it
also compares each frame against the committed baseline in
`tools/Screenshot/baselines/`, and writes a `*.diff.png` (the baseline
desaturated, changed pixels in magenta) for anything that moved. `ci.yml` runs
it that way on every PR.

Tolerance, and why it is where it is:

- Two renders of the same commit on the same OS are **bit-identical** — zero
  differing pixels.
- The same frames rendered on Windows versus Linux differ by **0.6–6%**, purely
  from glyph rasterization.
- The gate fails above **0.1%** of pixels, with a per-channel tolerance of 8.

So the threshold sits far above the noise floor and far below any real change
(moving one row of text lights up several thousand pixels) — and baselines are
strictly OS-specific. CI renders on `ubuntu-latest`, so baselines must be
rendered on Linux.

Refreshing them:

```bash
scripts/screenshots/update-baselines.sh
```

On Linux this renders directly. Anywhere else it goes through
`scripts/screenshots/render-linux.sh`, which runs the harness in the .NET SDK
container (the image needs `libfontconfig1`, which the script installs — Skia is
bundled but links against the system fontconfig). The
[Screenshots workflow](../../.github/workflows/screenshots.yml) does the same
thing on a real CI runner and opens a PR.

A missing baseline is reported as `NEW` and does **not** fail the run: a
developer adding a scenario cannot render a Linux baseline without Docker, and
blocking that would only teach people to skip the check.

**Reviewing a baseline change is the point.** It is the moment somebody signs off
on how the app now looks; approving it without opening the images gives the gate
away.

## 3. Headless UI tests: the clicking, in code

`PgNimbus.App.Tests` runs real windows on Avalonia's headless platform and sends
real key input. No display, no database — the fixture data source
(`tools/Screenshot/Fixtures.cs`) opens no socket.

It covers what neither of the other two reaches: that a gesture reaches its
command, that the palette invokes the entry it highlights, that a saved query
opens a *new* tab, that the results grid builds a column per result column and
re-points when the tab changes, and that every window opens **and closes**
(the detach path a render-and-exit pass never runs).

Two things worth knowing before adding to it:

- Test bodies go through `Ui.Run(async () => …)`, which is deliberately the only
  overload. Handing an async lambda to Avalonia's other `Dispatch` overloads
  compiles and is silently wrong — the dispatcher stops pumping the moment the
  body returns its task, so every assertion after the first `await` lands on a
  task nobody observes and the whole suite passes without running. That is not
  hypothetical: it is how the file was written first, and it was only caught by
  deliberately breaking an assertion to check the tests could still fail.
- Gestures are resolved from `CommandCatalog` via `Ui.Press(window, CommandId.X)`
  rather than typed in, so a test cannot keep passing after a chord moves, and
  works on macOS where the same entry resolves to Cmd.

If you write a test that cannot fail, nothing tells you. Break one assertion on
purpose once, and check the count goes red.

## 4. Published screenshots are generated

`tools/Screenshot/Marketing.cs` maps rendered scenarios to the images that face
users: `docs/screenshots/` (README + docs site) and `design/store/screenshots/`
(Microsoft Store listing, padded to the Store's 1366x768 minimum on a backdrop
sampled from the shot's own chrome, so the padding matches its theme).

```bash
scripts/screenshots/update-published.sh
```

Run it before cutting a release. The previous shots were captured by hand
against a live database, which made them go stale silently and leaked real
detail into public assets — the old main-window screenshot published a live Neon
hostname.

The README's animated GIFs are **not** covered: they show motion (a cold start,
completion being typed, a side-by-side race against pgAdmin) and are still
recorded by hand.

## The weekly release, end to end

1. Merge work as usual; `ci.yml` gates each PR on build, both test suites and the
   visual regression.
2. If a UI change was intended, run the Screenshots workflow (or
   `update-baselines.sh`) and review the image diff.
3. Before tagging, refresh the published screenshots the same way.
4. Push the `vX.Y.Z` tag. `release.yml` builds every package, smoke-launches each
   one, and only then publishes.

What is still manual, deliberately: anything needing a real server (SSH
tunnelling, a huge result set, an actual `EXPLAIN ANALYZE` against production
shapes) and the demo GIFs.
