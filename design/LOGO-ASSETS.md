# pgNimbus — logo & icon assets (engineering reference)

The technical source of truth for every logo/icon: where each file lives, what
generates it, and where it's consumed. **For the designer hand-off, use
[`DESIGNER-BRIEF.md`](DESIGNER-BRIEF.md)** — this file is the plumbing behind it.

Pipeline model (changed 2026-07): the design masters are **hand-drawn per
size**, and the scripts **copy/assemble** them — they no longer downscale one
mid-size master into every tiny icon (which produced muddy 16–32px icons).
Legibility-critical small sizes are copied verbatim; only large, non-critical
sizes are derived.

- **Part 1 — Sources** (`design/masters/`, what the designer edits)
- **Part 2 — Shipped outputs** (`PgNimbus.App/Assets/`, generated)
- **Part 3 — The scripts** (source → output mapping)
- **Part 4 — GitHub surfaces**
- **Part 5 — Full store/platform resolution reference**

---

## Part 1 — Sources: `design/masters/`

These are the **only** files anyone edits. Everything shipped is regenerated
from them by the scripts in Part 3.

### `icon/` — app-icon tiles (square, **solid full-bleed**, no rounding)

| File | Size | Hand-drawn? | Feeds |
|---|---|---|---|
| `icon-1024.png` | 1024² | ⭐ master | macOS 512/1024, Store listing images |
| `icon-256.png` | 256² | yes (full detail) | window `icon-256.png`, `app.ico` 64/128/256, MSIX 150, macOS 64–256 |
| `icon-48.png` | 48² | yes | `app.ico` 48, MSIX 44 & 50 |
| `icon-32.png` | 32² | yes (**simplified**) | `app.ico` 32, macOS 32 |
| `icon-24.png` | 24² | yes (**simplified**) | `app.ico` 24 |
| `icon-16.png` | 16² | yes (**simplified**) | `app.ico` 16, macOS 16 |

### `window/` — in-app title-bar icons (**transparent** line art)

| File | Size | Feeds |
|---|---|---|
| `window-light-256.png` | 256² | `Assets/icon-256-light.png` (light theme) |
| `window-dark-256.png` | 256² | `Assets/icon-256-dark.png` (dark theme) |

### `logo/` — website / marketing (**transparent**, except the social card)

| File | Size | Feeds / used by |
|---|---|---|
| `logo.svg` | vector | archival master |
| `logo-light.png` / `logo-dark.png` | 1024² | README header `<picture>` (light/dark) |
| `wordmark-{light,dark}.svg` (+ `.png` @2×) | ≈3.5:1 | *planned* README wordmark (see Part 4) |
| `social-preview.png` | 1280×640 | *planned* GitHub repo social preview (has a bg) |

> The current files in `masters/` are **placeholders** seeded from the old art
> so the build keeps working. The designer overwrites them (see the brief).
> Superseded/old concepts live in `design/archive/`.

---

## Part 2 — Shipped outputs: `PgNimbus.App/Assets/`

**Generated — do not hand-edit.** Filenames are stable, so csproj / WiX /
MSIX manifest / CI reference them unchanged.

| File | Size(s) | Bg | Consumed by |
|---|---|---|---|
| `app.ico` | 16,24,32,48,64,128,256 | solid tile | exe icon (`ApplicationIcon` in csproj) + MSI (`Product.wxs` → `ARPPRODUCTICON`, shortcut) |
| `icon-256.png` | 256 | solid tile | macOS `.icns` source (`build-app-bundle.sh`) |
| `icon-256-light.png` | 256 | transparent | light-theme window icon (`ThemedWindowChrome.cs`) |
| `icon-256-dark.png` | 256 | transparent | dark-theme window icon (`ThemedWindowChrome.cs`) |
| `Msix/Square44x44Logo.png` | 44 | solid tile | MSIX small tile (`Package.appxmanifest`) |
| `Msix/Square150x150Logo.png` | 150 | solid tile | MSIX medium tile |
| `Msix/StoreLogo.png` | 50 | solid tile | MSIX `Properties/Logo` |
| `PostgreSql.xshd` | — | n/a | *(not a logo — syntax highlighting; listed to avoid confusion)* |

---

## Part 3 — The scripts (source → output)

### `scripts/windows/make-app-icons.ps1` (Windows, System.Drawing)
Run after the designer updates `masters/`. Copies exact-size masters verbatim,
derives only larger sizes:

```
window/window-light-256.png ── copy ─────────► Assets/icon-256-light.png
window/window-dark-256.png  ── copy ─────────► Assets/icon-256-dark.png
icon/icon-256.png           ── copy ─────────► Assets/icon-256.png
icon/icon-{16,24,32,48}.png ── copy (as-is) ─┐
icon/icon-256.png ── downscale → 64,128 ─────┼─► Assets/app.ico  (7 entries)
icon/icon-48.png  ── → 44 ───────────────────► Assets/Msix/Square44x44Logo.png
icon/icon-48.png  ── → 50 ───────────────────► Assets/Msix/StoreLogo.png
icon/icon-256.png ── → 150 ──────────────────► Assets/Msix/Square150x150Logo.png
```

### `scripts/macos/build-app-bundle.sh` (macOS, sips/iconutil)
Builds the `.icns` for the `.app`/`.dmg`. For each iconset slot it uses the
exact-size master when present, else downscales from `icon-1024.png`:

```
icon/icon-{16,32,256}.png ── copy ──┐
icon/icon-1024.png ── sips → 64,128,512,1024 ┼─► app.iconset → app.icns
```
(iconset needs 16,32,64,128,256,512 at @1× and @2× → 16…1024 px.)

### `scripts/windows/make-store-logos.ps1` (manual, upload-only)
Partner Center **Store-listing** images from `icon/icon-1024.png` (square):
BoxArt 2160, tile 300/150/71, 9:16 poster 1440×2160. Writes to `-OutDir`, not
wired into any build.

> Nothing consumes `masters/logo/*` via a script — the README references those
> files directly by path.

---

## Part 4 — GitHub page surfaces

**1. README header** (`README.md`, top). Currently the **glyph** at
`width="180"`, theme-switched:

```html
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="design/masters/logo/logo-dark.png">
  <img src="design/masters/logo/logo-light.png" alt="pgNimbus logo …" width="180">
</picture>
```

**Planned:** swap to the horizontal **wordmark** lockup once delivered. How
GitHub renders it: README column ≈980px on desktop, device-width on mobile,
with `max-width:100%` — so one image scales to both **if it stays legible
small**. Use **SVG** for crispness everywhere; display `width="440"`; PNG
fallback at 2× (~880px). Keep the lockup ≈3.5:1.

**2. Repo social preview** — the share/search card (Settings → Social preview,
*not* in the repo). Currently **unset**. Recommended **1280×640** PNG/JPG,
**solid branded background** (not transparent), under ~1 MB.

---

## Part 5 — Full store/platform resolution reference

What the pipeline produces today, and what a fuller store presence could add.
The **1024 master must be the largest single source** (nothing upstream is
bigger), so it bounds quality of every derived size.

**Windows exe/MSI (`app.ico`):** 16, 24, 32, 48, 64, 128, 256. *(Could add 20,
40, 96 for complete Explorer coverage.)*

**macOS (`app.icns`):** 16, 32, 64, 128, 256, 512 at @1×/@2× → real px 16, 32,
64, 128, 256, 512, **1024**. Square full-bleed; **no pre-rounding / no shadow**
(Apple masks). Mac App Store upload additionally wants a flat **1024×1024, no
alpha**.

**MSIX / Microsoft Store tiles** — shipped: 44, 150, 50 (required). Optional
for a richer Store tile set, each at scale 100/125/150/200/400 %: Square71x71,
Square310x310, Wide310x150, SplashScreen 620×300.

**Microsoft Store listing images** (Partner Center, upload-only; from
`make-store-logos.ps1`): box art 2160², tile 300²/150²/71², 9:16 poster
1440×2160. Screenshots (≥1366×768) come from real app captures, not the logo.

---

## Cleanup / conventions

- Removed orphan `Assets/pgnimbus-icon_opt (1).ico` (was bundled into the app
  via the `Assets\**` glob for no reason).
- Old loose sources (`icon-tile.png`, `logo*.png`, `logo_01.ico`, `simple/`)
  moved to `design/archive/`.
- Per `CLAUDE.md`'s "keep this file current" rule: when the layout or pipeline
  changes, update this file **and** the `## App icon / logo assets` section of
  `CLAUDE.md` in the same PR.
