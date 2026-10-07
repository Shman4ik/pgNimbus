# Recording the README GIFs

One scene script per GIF in `docs/screenshots/`, driven by `demo-lib.ps1`
(Win32 window placement, real key and mouse events, an ffmpeg region capture).
Each scene starts the NativeAOT build against a scratch data folder, records the
capture box and writes `out\raw\<scene>.mp4`.

| Scene | GIF |
|---|---|
| `scenes/cold-start.ps1` | `cold-start.gif` (also prints the startup probe's own numbers) |
| `scenes/completion.ps1` | `completion-demo.gif` |
| `scenes/explain-tree.ps1` | `explain-tree-demo.gif` |
| `scenes/safe-mode.ps1` | `safe-mode-commit-demo.gif` |
| `scenes/store-trailer.ps1` | `design/store/trailer/pgnimbus-trailer.mp4` (see below) |
| `scenes/store-hero.ps1` | `design/store/SuperHeroArt-source.dark.png`, a still for `make-store-hero.ps1` |

## Rules

- Never point it at real app data. `Prep-Data` copies a pristine scratch folder
  (`PGN_DEMO_BASE`) to `run\` beside it for every scene and sets
  `PGNIMBUS_DATA_DIR` to that copy. `Start-App` refuses to run without it.
- Input is sent only while pgNimbus is in front (`Assert-Front`).
- **Never send Escape.** It is Claude's stop key and turns desktop control off.
  Close popups by clicking.
- The capture box is a 1424x892 region in the middle of a 3840x1600 desktop, well
  inside the edges (computer-use draws a glow on the monitor edges). Do not use
  `gdigrab title=`: Avalonia gives a black frame.

## Steps

```powershell
# 1. The shipping build (PowerShell only: Git Bash fails at link on vswhere)
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH
dotnet publish src/PgNimbus.App -c Release -r win-x64 -p:PublishAot=true -p:Version=X.Y.Z -o artifacts\aot

# 2. The demo database (port 55432 is in a Windows excluded range; use 544x)
wslc run -d --name pgn-demo -e POSTGRES_PASSWORD=postgres -p 5443:5432 pgvector/pgvector:pg17
wslc exec pgn-demo psql -U postgres -c "CREATE DATABASE demo"
foreach ($f in '01_public','02_commerce','03_iot','04_org','05_analytics','06_telemetry') {
  Get-Content scripts\demo\$f.sql -Raw | wslc exec -i pgn-demo psql -U postgres -d demo -v ON_ERROR_STOP=1 -q
}

# 3. A scratch base folder with settings.json {"Theme":"dark"} and
#    connection-window.json {"X":1200,"Y":350,"Width":1424,"Height":884,"IsMaximized":false}
$env:PGN_DEMO_BASE = "<scratch>\base"
$env:PGN_DEMO_OUT  = "<scratch>\out"

# 4. Run a scene (first with -Dry where it has one, to check coordinates), then look
.\scripts\demo\record\scenes\completion.ps1
```

The connection string is in `demo-lib.ps1` (`$script:Conn`, `127.0.0.1:5443`).
Use `127.0.0.1`: `localhost` added seconds to the first connect.

## Contact sheet and GIF

```bash
scripts/demo/record/sheet.sh out/raw/completion.mp4 sheet.png 5 4   # read it before trusting a take
```

Encode in two passes with a palette (width 1000, 10 to 12 fps, aim for 0.2 to 1.5 MB):

```bash
vf="fps=10,scale=1000:-1:flags=lanczos"
ffmpeg -y -ss 0.4 -t 12 -i in.mp4 -vf "$vf,palettegen=max_colors=128:stats_mode=diff" -update 1 -frames:v 1 pal.png
ffmpeg -y -ss 0.4 -t 12 -i in.mp4 -i pal.png -filter_complex "[0:v]$vf[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle" out.gif
```

`-update 1 -frames:v 1` on the palette pass is required on this ffmpeg build.

## The Microsoft Store trailer

`scenes/store-trailer.ps1` records five takes (start, palette, completion, safe mode,
explain) in a **16:9 box of 1600x900** (it sets `PGN_DEMO_BOX=1120,350,1600,900`
itself; its coordinates are for that box). Put `{"X":1120,"Y":350,"Width":1600,"Height":900,"IsMaximized":false}`
in the base folder's `connection-window.json` so the window opens there. `-Only palette,explain`
re-records single takes.

```powershell
.\scripts\demo\record\scenes\store-trailer.ps1
```

```bash
scripts/demo/record/make-trailer.sh "$PGN_DEMO_OUT" out/final
```

`make-trailer.sh` puts each take 1:1 on a 1920x1080 brand-navy canvas with a caption
under it (no scaling, so the UI text stays sharp), cross-fades them, adds an end card with
the wordmark and a silent audio track, and writes `pgnimbus-trailer.mp4` plus
`pgnimbus-trailer-thumbnail.png`. The trims and captions are the `takes` table at its top.

The promo video (`tools/promo-video`) cuts its clips out of the committed trailer by
time. A re-recorded trailer moves those times, so check the `clips` table in
`tools/promo-video/storyboard.mjs` against the new one (its README says how).
Both files are committed in `design/store/trailer/`; the listing also needs
`design/store/SuperHeroArt-16x9-1920x1080.png` (`scripts/windows/make-store-hero.ps1`),
or Partner Center won't show the trailer at the top.

## Cleanup

`wslc stop pgn-demo; wslc remove pgn-demo`, delete the scratch folder and `out\`.
