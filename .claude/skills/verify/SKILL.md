---
name: verify
description: Build, launch, and drive pgNimbus headlessly to verify a change end-to-end (Xvfb + xdotool + ImageMagick screenshots against a local Postgres).
---

# Verifying pgNimbus changes

Follow the sandbox bootstrap below verbatim; it works as written. Extras
learned from real runs are after it.

- **Connection dialog vs. main window**: launching *without* `PGNIMBUS_CONN`
  opens `ConnectionDialog` (the login screen); setting it skips straight to
  `MainWindow`. Pick per what you're verifying.
- The dialog is 640×680, centered on the 1280×800 Xvfb screen → it spans
  roughly x 320–960, y 60–740. Screenshot first (`DISPLAY=:99 import -window
  root shot.png`), read the PNG to find exact control coordinates, then drive
  with `xdotool mousemove <x> <y> click 1` / `xdotool type ...` /
  `xdotool key ctrl+a Delete`.
- Run the app in the background under `timeout 180 dotnet run --project
  src/PgNimbus.App --no-build` so one launch survives several drive/screenshot
  Bash calls.
- `SELECT count(*) FROM pg_stat_activity WHERE application_name='pgNimbus'`
  is a handy probe for leaked/pooled connections.
- Ubuntu 24.04's apt Postgres is 16.x; `service postgresql start` +
  `ALTER USER postgres PASSWORD 'postgres'` is all the seed the connection
  dialog needs.

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
    timeout 15 dotnet run --project src/PgNimbus.App --no-build &

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
- For a *visual* check the live sandbox above is the heavy path — prefer the
  headless harness below, which needs no display, no input tool and no
  database. The live path is still the one for anything interactive
  (completion popups, drag-reorder, real catalog shapes).
