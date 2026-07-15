---
name: verify
description: Build, launch, and drive pgNimbus headlessly to verify a change end-to-end (Xvfb + xdotool + ImageMagick screenshots against a local Postgres).
---

# Verifying pgNimbus changes

The full sandbox bootstrap (apt packages, Xvfb, Postgres seed, launch
command) lives in `CLAUDE.md` → "Bootstrapping a fresh Linux/CI sandbox" —
follow it verbatim; it works as written. Extras learned from real runs:

- **Connection dialog vs. main window**: launching *without* `PGNIMBUS_CONN`
  opens `ConnectionDialog` (the login screen); setting it skips straight to
  `MainWindow`. Pick per what you're verifying.
- The dialog is 640×680, centered on the 1280×800 Xvfb screen → it spans
  roughly x 320–960, y 60–740. Screenshot first (`DISPLAY=:99 import -window
  root shot.png`), read the PNG to find exact control coordinates, then drive
  with `xdotool mousemove <x> <y> click 1` / `xdotool type ...` /
  `xdotool key ctrl+a Delete`.
- Run the app in the background under `timeout 180 dotnet run --project
  PgNimbus.App --no-build` so one launch survives several drive/screenshot
  Bash calls.
- `SELECT count(*) FROM pg_stat_activity WHERE application_name='pgNimbus'`
  is a handy probe for leaked/pooled connections.
- Ubuntu 24.04's apt Postgres is 16.x; `service postgresql start` +
  `ALTER USER postgres PASSWORD 'postgres'` is all the seed the connection
  dialog needs.
