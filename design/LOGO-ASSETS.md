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
| `icon-1024.png` | 1024² | rendered from `icon-badge.svg` | macOS 512/1024, Store listing images |
| `icon-256.png` | 256² | rendered from `icon-badge.svg` | `app.ico` 64/128/256, MSIX 150, macOS 64–256 |
| `icon-48.png` | 48² | yes | `app.ico` 48, MSIX 44 & 50 |
| `icon-32.png` | 32² | yes (**simplified**) | `app.ico` 32, macOS 32 |
| `icon-24.png` | 24² | yes (**simplified**) | `app.ico` 24 |
| `icon-16.png` | 16² | yes (**simplified**) | `app.ico` 16, macOS 16 |

`icon-badge.svg` (2026-07) is the vector source for the circular badge —
a single traced path (Inkscape Path > Trace Bitmap off the previous
`icon-1024.png`), scaled up so the artwork fills the 1024×1024 canvas edge
to edge instead of sitting in a wide transparent margin. `icon-1024.png` and
`icon-256.png` are rendered from it (`inkscape icon-badge.svg
--export-type=png --export-filename=<out>.png -w <N> -h <N>`); the smaller
full-bleed sizes (16–48) stay hand-drawn PNGs, untouched by this file.

### `window/` — in-app title-bar icons (**transparent** line art)

| File | Size | Feeds |
|---|---|---|
| `window-light-256.png` | 256² | `Assets/window-icon-light.ico` (light theme) |
| `window-dark-256.png` | 256² | `Assets/window-icon-dark.ico` (dark theme) |

### `logo/` — website / marketing (**transparent**, except the social card)

| File | Size | Feeds / used by |
|---|---|---|
| `logo.svg` | vector | archival master |
| `logo-light.png` / `logo-dark.png` | 1024² | README header `<picture>` (light/dark) |
| `wordmark-{light,dark}.svg` (+ `.png` @2×) | ≈3.5:1 | *planned* README wordmark (see Part 4) |
| `social-preview.png` | 1280×640 | *planned* GitHub repo social preview (has a bg) |

> Superseded/old concepts live in `design/archive/`.

### `design/store/` — Microsoft Partner Center listing images (**generated**)

Not a source — **generated** by `scripts/windows/make-store-logos.ps1` from
`icon/icon-1024.png` and checked in so a Partner Center re-upload is a
copy-paste, not a script run someone forgot about. Regenerate and commit
whenever `icon-1024.png` changes: `BoxArt-1x1-2160x2160.png`,
`AppTileIcon-1x1-300x300.png`, `Square-1x1-{150,71}x{150,71}.png`,
`Poster-9x16-1440x2160.png` — see Part 3.

---

## Part 2 — Shipped outputs: `PgNimbus.App/Assets/`

**Generated — do not hand-edit.** Filenames are stable, so csproj / WiX /
MSIX manifest / CI reference them unchanged.

| File | Size(s) | Bg | Consumed by |
|---|---|---|---|
| `app.ico` | 16,24,32,48,64,128,256 | solid tile | exe icon (`ApplicationIcon` in csproj) + MSI (`Product.wxs` → `ARPPRODUCTICON`, shortcut) + runtime window icon, title bar + taskbar (`ThemedWindowChrome.cs`) |
| `window-icon-light.ico` | 16,24,32,48,256 | transparent | **nothing right now** — was the light-theme window icon until 2026-07, superseded by the plated `app.ico` (title bar/taskbar/Alt+Tab share one `WM_SETICON` slot, and theme-swapped line art was unreadable on the dark taskbar in light theme); still generated |
| `window-icon-dark.ico` | 16,24,32,48,256 | transparent | **nothing right now** — same as above (was the dark-theme window icon) |
| `Msix/Square44x44Logo.scale-{100,125,150,200,400}.png` | 44,55,66,88,176 | solid tile | MSIX small tile (`Package.appxmanifest`) |
| `Msix/Square150x150Logo.scale-{100,125,150,200,400}.png` | 150,188,225,300,600 | solid tile | MSIX medium tile |
| `Msix/StoreLogo.scale-{100,125,150,200,400}.png` | 50,63,75,100,200 | solid tile | MSIX `Properties/Logo` |
| `Msix/Square44x44Logo.targetsize-{16,24,32,48,256}_altform-unplated.png` | 16,24,32,48,256 | transparent | taskbar/Alt+Tab/Start/install-dialog icon on dark surfaces |
| `Msix/Square44x44Logo.targetsize-{16,24,32,48,256}_altform-lightunplated.png` | 16,24,32,48,256 | transparent | same, on light surfaces |
| `PostgreSql.xshd` | — | n/a | *(not a logo — syntax highlighting; listed to avoid confusion)* |

The scale/targetsize sets replaced a single flat file per logo (fixed
2026-07): without a qualifier-matched size, Windows shrinks the one file it
has and adds its own backplate around it — visible as an undersized icon on
a big dark square in the taskbar, Start, and the sideload "Install app?"
dialog. The qualified filenames alone don't do anything, though —
`scripts/windows/build-msix.ps1` has to compile them into `resources.pri`
via `makepri` at pack time for Windows to actually resolve them (see Part 3).

---

## Part 3 — The scripts (source → output)

### `scripts/windows/make-app-icons.ps1` (Windows, System.Drawing)
Run after the designer updates `masters/`. Copies exact-size masters verbatim,
derives only larger sizes:

```
window/window-light-256.png ── resize to 16/24/32/48/256 ──► Assets/window-icon-light.ico
window/window-dark-256.png  ── resize to 16/24/32/48/256 ──► Assets/window-icon-dark.ico
icon/icon-{16,24,32,48}.png ── copy (as-is) ─┐
icon/icon-256.png ── downscale → 64,128 ─────┼─► Assets/app.ico  (7 entries)
icon/icon-48.png   ── → 44,55,66 ────────────► Assets/Msix/Square44x44Logo.scale-{100,125,150}.png
icon/icon-1024.png ── → 88,176 ──────────────► Assets/Msix/Square44x44Logo.scale-{200,400}.png
icon/icon-48.png   ── → 50,63,75 ────────────► Assets/Msix/StoreLogo.scale-{100,125,150}.png
icon/icon-1024.png ── → 100,200 ─────────────► Assets/Msix/StoreLogo.scale-{200,400}.png
icon/icon-256.png  ── → 150,188,225 ─────────► Assets/Msix/Square150x150Logo.scale-{100,125,150}.png
icon/icon-1024.png ── → 300,600 ─────────────► Assets/Msix/Square150x150Logo.scale-{200,400}.png
window/window-dark-256.png  ── → 16,24,32,48,256 ► Assets/Msix/Square44x44Logo.targetsize-*_altform-unplated.png
window/window-light-256.png ── → 16,24,32,48,256 ► Assets/Msix/Square44x44Logo.targetsize-*_altform-lightunplated.png
```

(scale-200/400 fall back to the 1024 master instead of upscaling the small
48/256 master, which would blur; sizes ≤ the small master still use it, same
as before — see the script's `SmallFrom` per-logo mapping.)

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
BoxArt 2160, tile 300/150/71, 9:16 poster 1440×2160. Writes to `design/store/`
by default (checked into the repo — re-run and commit after `icon-1024.png`
changes) or `-OutDir` for a one-off elsewhere. Not wired into any build;
uploading the files to Partner Center is still a manual step.

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

**MSIX / Microsoft Store tiles** — shipped: 44, 150, 50 (required), each at
scale 100/125/150/200/400%, plus Square44x44Logo's unplated
targetsize-{16,24,32,48,256} pair (dark/light) for taskbar/Start/Alt+Tab.
Optional for a richer Store tile set: Square71x71, Square310x310,
Wide310x150, SplashScreen 620×300 (same per-scale set each).

### Microsoft guideline compliance (checked 2026-07)

Validated against Microsoft's official
[app-icon-design](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design)
and [app-icon-construction](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction)
pages. Where we stand:

- ✅ **Bare-minimum size set** (16/24/32/48/256) — met by `app.ico` and the
  targetsize pair. Microsoft: with a 256px present, Windows only ever scales
  *down*, never up.
- ✅ **Unplated + lightunplated variants** — shipped; these are what keep the
  taskbar/Start icon from getting Windows's auto-backplate.
- ✅ **Per-size hand-drawn small masters** — matches Microsoft's "maintain
  legibility at small sizes" guidance.
- ⚠️ **Partial targetsize coverage.** Microsoft's *required* AppList list is
  14 sizes: 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256 — we ship
  5 (16/24/32/48/256). The gap bites at fractional display scales: at 125% /
  150% the taskbar wants **30 / 36 px** and Windows scales our 48 down.
  Windows picks the next size *above* and downscales, so the result is decent
  but not pixel-perfect. Fix = extend `make-app-icons.ps1` to emit the
  intermediate targetsizes (derive 20/30/36/40 from `icon-24/32/48`-class
  masters, 60–96 from `icon-256`); no new masters strictly needed.
- ⚠️ **No plain (plated) `targetsize-N.png` files.** Microsoft's list has
  three variants per size (plain, unplated, lightunplated); we ship only the
  two unplated ones. In practice Windows falls back to the
  `Square44x44Logo.scale-*` assets for plated contexts, so nothing visibly
  breaks — but adding the plain set (solid-tile artwork) would match the
  letter of the spec.
- ℹ️ **Solid tile vs transparent.** Microsoft prefers transparent-background
  icons, but explicitly allows a branded plate ("that's okay too") as long as
  theme-aware unplated assets exist — which is exactly our split: solid
  `icon/` tiles for plated surfaces, transparent `window/` masters for the
  unplated ones.
- ℹ️ **Win10 tile/splash extras** (SmallTile/WideTile/LargeTile,
  SplashScreen, badge) — optional; Windows 11 ignores tiles, and only the
  medium tile (our Square150x150Logo, shipped) is Store-required.

The *design*-side rules (≤2 metaphors, no typography, 48px grid, 2px/1px
corner radii, flat straight-on layers, 120° gradients, 3.0:1 contrast on half
the icon) live in [`DESIGNER-BRIEF.md`](DESIGNER-BRIEF.md) → "Windows icon
rules", so the designer sees them without reading this file.

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
