# Release checklist

The list to walk before every public release, kept current as releases teach
us things. [`PRE-LAUNCH-CHECKLIST.md`](PRE-LAUNCH-CHECKLIST.md) was the one-time
list for going public; this one is for the weekly cadence after it.

How to use it: copy the checkbox sections into the release PR description (or
tick them in a scratch copy), walk them top to bottom, and add a row to the
[release log](#release-log) at the end. When a release finds something this
list would have caught earlier, change the list in the same PR. A step that
nobody can do in the time a weekly release allows gets automated or deleted,
not skipped silently (see `docs/design/release-checks.md`).

Most of the manual pass can be handed to Claude Code: "go through
docs/RELEASE-CHECKLIST.md for vX.Y.Z". Simple steps (test runs, the hygiene
sweep, release-note drafting) are fine for a Sonnet subagent; the click-through
needs a model that can drive the desktop.

---

## 1. Scope and version

- [ ] `git fetch --all --prune`, then `git log --oneline <last-tag>..origin/main`.
      Nothing half-merged: every PR in a GitHub stack is either all in or all out.
- [ ] Pick the version. A new user-visible feature is a minor bump, fixes only
      a patch bump. Nothing in the repo needs a hand edit: the tag drives
      `$VERSION` everywhere (`.github/actions/version/action.yml`).
- [ ] Draft release notes from the merged PRs: New / Improved / Fixed, written
      for users. Run them through the `humanizer` skill (no em or en dashes).
      Build and CI internals don't belong in them.

## 2. Automated gates

- [ ] `main` is green: `gh run list --branch main --limit 10` (ci, docs).
- [ ] No open bug that is a crash, data loss, broken install or security issue:
      `gh issue list --state open`. Triage anything new since the last release.
- [ ] No open security alerts: Dependabot
      (`gh api repos/Shman4ik/pgNimbus/dependabot/alerts?state=open`) and secret
      scanning. No pending Dependabot PR marked as a security update.
- [ ] The full test suites pass **with a live server**, including the
      pg_stat_statements reads that CI's plain `postgres:17` skips:
      ```powershell
      wslc run -d --name pgn-release -e POSTGRES_PASSWORD=postgres -p 5441:5432 pgvector/pgvector:pg17 postgres -c shared_preload_libraries=pg_stat_statements
      wslc exec pgn-release psql -U postgres -c "CREATE DATABASE pgn_tests"
      $env:PGNIMBUS_TEST_CONN = "Host=127.0.0.1;Port=5441;Database=pgn_tests;Username=postgres;Password=postgres"
      dotnet test --project PgNimbus.Core.Tests
      dotnet test --project PgNimbus.App.Tests
      ```
      Port 55432 falls in a Windows excluded TCP range on the dev machine; use
      544x. Expected skips: the two native credential-store tests and the two
      "pg_stat_statements not installed/loaded" tests (this server has it).
- [ ] `dotnet build` shows zero warnings, zero IL2026/IL3050.
- [ ] The screenshot harness renders every scenario:
      `dotnet run --project tools/Screenshot -- <scratch>`. CI already compares
      against the Linux baselines; locally this is a smoke run.
- [ ] Docs build: `mkdocs build --strict` (after `pip install -r docs/requirements.txt`).
- [ ] The privacy claim still holds: `grep -rn "HttpClient\|WebRequest" --include=*.cs PgNimbus.Core PgNimbus.App`
      finds nothing. (The build itself sends Avalonia's build-time telemetry, see
      [known caveats](#known-caveats); that is not the app.)

## 3. Manual pass on the shipping build

Test the NativeAOT publish, not a Debug build: trimming and AOT fail in ways
JIT never shows (reflection JSON, bindings).

- [ ] Publish it, stamped with the version so the UI shows the right number:
      ```powershell
      $env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH
      dotnet publish PgNimbus.App -c Release -r win-x64 -p:PublishAot=true -p:Version=X.Y.Z -o artifacts\aot
      ```
      (from PowerShell: Git Bash fails at link on `vswhere.exe`).
- [ ] **Keep the pass out of your real app data.** Launch the build with
      `PGNIMBUS_DATA_DIR` set to an empty scratch folder (`$env:PGNIMBUS_DATA_DIR = "<scratch>"`
      before `Start-Process`); every store, the credential files and the crash
      log follow it, and you delete the folder afterwards. With the real folder,
      row 2's "press Enter" reconnects to whichever database you used last, which
      on the 1.0.0 pass was a remote one. Back up `%APPDATA%\pgNimbus` anyway
      and compare it afterwards; a launch alone must leave it byte-identical.
- [ ] Seed the stand: `scripts/demo/seed.ps1` (or pipe the six files through
      `wslc exec -i pgn-release psql -U postgres -d demo`), then
      `CREATE EXTENSION pg_stat_statements` in `demo`, run a few queries, and
      create two roles for the permissions check:
      ```sql
      CREATE ROLE app_reader NOLOGIN;
      CREATE ROLE analyst LOGIN PASSWORD 'analyst' IN ROLE app_reader;
      CREATE ROLE intern LOGIN PASSWORD 'intern';
      GRANT USAGE ON SCHEMA commerce TO app_reader;
      GRANT SELECT ON commerce.orders, commerce.customers TO app_reader;
      GRANT SELECT ON commerce.products TO intern;  -- no USAGE on commerce
      ```

Walk each flow; the expected result is what "pass" means.

| # | Flow | Pass when |
|---|------|-----------|
| 1 | Connection dialog: New, paste `psql -h 127.0.0.1 -p 5441 -U postgres -d demo`, type the password, Test, Connect | Fields fill from the string, the profile appears in the list without a Save button, Test reports the server version, Connect opens the main window |
| 2 | Relaunch, press Enter | The last profile is preselected and Enter reconnects |
| 3 | Schema tree: expand `commerce`, double-click a table | Browse opens in a new tab with `LIMIT 100`, timing in the status bar, the node does not stay expanded (relation sizes only with "Show relation sizes" on; it is off by default) |
| 4 | Completion: `SELECT * FROM or` Enter, ` JOIN `, pick the FK row | `orders o JOIN customers c ON c.id = o.customer_id` as one accept |
| 5 | Run (Ctrl+Enter) a join | Rows stream, status shows rows / ms / first byte |
| 6 | Explain and Explain Analyze (Ctrl+E / Ctrl+Shift+E), Text and Tree, Color metrics | Plan renders with buffers, the tree opens expanded, heat bars, bottleneck in red, the Color chips rescale it. A healthy plan has no warnings strip; row 7 shows it |
| 7 | Import query plan (palette), paste a text plan | New tab, warnings for bad estimates |
| 8 | Safe mode: edit a cell, change the same row from psql, Review, Commit | Conflict dialog with before / now / yours; Reload and restage, then Commit succeeds |
| 9 | Browse filters: Ctrl+F in the grid, add a condition; Ctrl+I | A chip, the WHERE in the editor, row details card |
| 10 | `telemetry.api_events`: scroll down and right, Space on a jsonb cell | Scrolls without stalls; inspector pretty-prints, Tree view works |
| 11 | Server activity (palette): hold a row lock in psql, block a second session | Blocking tab shows holder over waiter; Terminate on the holder clears it |
| 12 | Database overview, Slow queries (palette) | Sizes and cache hit; statements ranked, interval restarts |
| 13 | Notify monitor: add a channel, Start listening, send a JSON payload | It arrives and the payload pane pretty-prints it |
| 14 | Roles and permissions: Permissions tab, `commerce.products`, `intern` | The explanation names the missing USAGE on the schema |
| 15 | Preferences, theme toggle, F1, About | Both themes readable, overlays close |
| 16 | Tabs: right-click Rename, Ctrl+S | Rename in place; Ctrl+S saves to Saved Queries without a file picker |
| 17 | Export a browsed table to CSV | The file holds every row, not the 100 on screen |
| 18 | Read-only profile: toggle it, reconnect, try an UPDATE | The server refuses it (25006) and the grid is read-only |
| 19 | Every documented chord in F1 | Each one does what the sheet says, including the punctuation ones (Ctrl+, Ctrl+/ Ctrl+= Ctrl+-) |
| 20 | SSH tunnel to the sshd container (Password auth, `Host=127.0.0.1;Port=2299`, target the Postgres container's bridge IP): Test | First connect shows the unknown-host dialog with the key type and a `SHA256:` fingerprint equal to `ssh-keygen -l` on the server; Enter does not accept; Accept connects; a second Test asks nothing. Replace the stored key in `<appdata>/known_hosts` and Test again: it stops and names the host, the file and line, both fingerprints |
| 21 | TLS defaults: new profile on `127.0.0.1`, then on a network host, then Verify CA | Local starts at Prefer, a network host at Require, Verify CA shows a Root Certificate field with Browse; Require against a server without TLS says so and names Prefer/Disable |
| 22 | Close a tab holding unsaved SQL (Ctrl+W), then Ctrl+Shift+T | The status line says how to get it back; the text comes back |
| 23 | Settings, Completion: keyword case, always write the table's schema, Enter accepts | `SEL` + Tab writes `select` on "lower"; a table accepts as `public.customers` with the schema switch on; with Enter off, Enter after `cus` only ends the line |
| 24 | `CREATE ROLE tmp_x LOGIN PASSWORD 'secret123'`, run it, close the app, look at the data folder, relaunch | The history row and the workspace hold `'<redacted>'`, no file contains `secret123`, the tab reopens with the marker. `DROP ROLE tmp_x` afterwards |
| 25 | Alter Table: Drop selected column; right-click an available extension: Install; stage a multi-row delete where a trigger refuses one row, Commit | Drop and Install ask first; the failed batch deletes nothing (count unchanged) |
| 26 | Slow queries after restarting the interval and running one heavy query from psql | Only that query (and the pool's `DISCARD ALL`) is listed; the footer counts pgNimbus's own reads as left out |
| 27 | Rows 6, 9, 10, 15, 20 once more in the other theme | Active chips (Color, Text/Tree, Wrap, View), the host-key dialog and every overlay stay readable |

Driving it with Claude Code's computer-use: grant the exe by its **full path**,
type in chunks of 15 characters or fewer (longer strings go through the
clipboard and skip completion triggers), and read small UI by capturing the
window region with ffmpeg rather than the downscaled screenshot. Escape sent by
computer-use does not reach the app; check Escape in headless tests instead.
**Don't report a chord as broken on the strength of one input tool.** On
2026-09-28 Ctrl+, "did nothing" through computer-use (which can't send it) and
through `SendKeys` (whose Ctrl never reaches Avalonia), yet opened Preferences
at once when sent as real `keybd_event` key presses. Check a failing chord that
way, or by hand, before it goes in the log. On 2026-09-29 the same script
(`VK` + `MapVirtualKey` scan code, key-down in order, key-up reversed) drove
Ctrl+/, Ctrl+=, Ctrl+- and Ctrl+, correctly. Two more tool artifacts: a click on
a flyout's Apply straight after `type` can land before the field commits (wait a
second); and parking the pointer on a window's maximize button opens Windows'
Snap Layouts over the app, so move the pointer away before reading the screen.

## 4. Published media

- [ ] Screenshots: `scripts/screenshots/update-published.sh` on Windows if any
      surface they show changed (UI design rule 9).
- [ ] GIFs and videos: re-record the ones whose screens changed. The README
      GIFs are not covered by the screenshot harness. (Scripted recording is
      being built; until it lands, the recipe is the "Screen recording demo
      pipeline" note in Claude's project memory.)

## 5. Ship

- [ ] Tag `vX.Y.Z` on `main` and push it. The pipeline refuses a tag on any
      commit that is not on `main`. Watch `release.yml`: every package is
      launched before it ships, so a red smoke step is a real failure.
- [ ] Paste the release notes into the GitHub release.
- [ ] Microsoft Store: download the `windows-msix` artifact (kept 14 days) and
      submit it in Partner Center.
- [ ] Check the benchmark chart for a regression in startup or streaming; the
      public copy says "about 0.2 s", so it has to stay true.
- [ ] Landing page and docs, if an install channel or a headline feature changed.

## 6. After

- [ ] Clean up merged branches and worktrees (global CLAUDE.md "Git housekeeping").
- [ ] "What's new" post if the release has a visible feature (`docs/marketing/plan.md`).
- [ ] Add the release to the log below with anything the pass found.

---

## Known caveats

Things that are true, known, and not release blockers. Keep them honest; they
are what gets asked in a launch thread.

- **Build-time telemetry.** Building from source pulls in
  `Avalonia.BuildServices`, which reports anonymous build statistics to Avalonia
  at compile time ("Avalonia Accelerate Community requires telemetry" in the
  publish log). It does not run in the shipped app, which still makes no network
  connection except to your databases and SSH hosts. The publish log says an
  opt-out needs a paid Avalonia tier.
- **Unsigned direct downloads.** MSI and Linux packages are unsigned, the dmg is
  ad-hoc signed. The Store package is signed by Microsoft.

## Release log

| Version | Date | Pass by | What the pass found |
|---------|------|---------|---------------------|
| 0.14.0 | 2026-09-28 | Claude Code (AOT build, Windows 11) | No blockers. Fixed in the checklist's own PR: plan tree opened collapsed and every plan opened as text; checked `ToggleButton.chip` (Wrap, filter pin, history scope) drew white text on the light wash (since at least 0.13); the permissions strip said "can SELECT" for a role blocked by missing schema USAGE; Slow queries listed pgNimbus's own catalog reads first; an imported plan's tab carried `SELECT 1;`; double-clicking a table also expanded its node; the F1 row for Ctrl+1…9 was grey text. Documented: Avalonia's build-time telemetry. A reported "Ctrl+, does nothing" was an input-tool artifact, not a bug. |
| 1.0.0 | 2026-09-29 | Claude (Sonnet 5.5; AOT build stamped 1.0.0, Windows 11, PostgreSQL 17.11) | No blockers, no code changes. Rows 1 to 18 and 20 to 26 passed. The first launch showed the real profile list with a remote database preselected, so the pass moved to `PGNIMBUS_DATA_DIR` (now in the checklist); the real app data was byte-identical to its backup afterwards. Checklist wording fixed: relation sizes are off by default, and a healthy plan has no warnings strip. Observations, not failures: after Ctrl+S a renamed tab takes the saved query's name again; a failed staged commit's red status is cut to "Commit…" in a narrow window; the AOT publish prints IL2104/IL3053 for `Avalonia.Controls.DataGrid` (the package, not our code; zero IL2026/IL3050). Not fully exercised: row 19 covered about 20 chords (the punctuation four included; the rest are pinned by the catalog and binding tests), row 18 did not click into the read-only grid, and row 27 saw the host-key dialog only in the light theme. |
