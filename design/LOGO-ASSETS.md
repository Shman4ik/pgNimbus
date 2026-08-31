# pgNimbus — logo & icon assets (engineering reference)

The technical source of truth for every logo/icon: where each file lives, what
generates it, and where it's consumed. **For the designer hand-off, use
[`DESIGNER-BRIEF.md`](DESIGNER-BRIEF.md)** — this file is the plumbing behind it.

Pipeline model (changed 2026-08): **one drawing feeds everything.** The mark is
drawn once in `design/logo.af`, exported to `design/logo.svg`, and every raster
in the repo is rendered from that. Nothing under `design/masters/` or
`PgNimbus.App/Assets/` is hand-edited any more.

```
design/logo.af                     Affinity, the editable master
  → scripts/design/dump-af.js      geometry out to JSON (run via the Affinity MCP)
  → scripts/design/af-to-svg.py    design/logo.svg
  → scripts/design/make-masters.ps1        design/masters/**
  → scripts/windows/make-app-icons.ps1     PgNimbus.App/Assets/**
  → scripts/windows/make-store-logos.ps1   design/store/**
```

What this replaced: from 2026-07 to 2026-08 the masters were **hand-drawn per
size**, because the mark was then a traced raster whose downscale turned to mud
below 32px. The modular vector master rasterises cleanly, so the six icon tiles
became six renders of one file rather than six drawings that drift apart. If a
size ever does stop reading, the fix is a simplified **mark** fed into
`make-masters.ps1` (a `logo-small.svg`, the way kubeNimbus does it), never a
hand-painted PNG that nothing can regenerate.

- **Part 0 — The vector master** (`design/logo.af`, `design/logo.svg`)
- **Part 1 — Sources** (`design/masters/`, now all generated)
- **Part 2 — Shipped outputs** (`PgNimbus.App/Assets/`, generated)
- **Part 3 — The scripts** (source → output mapping)
- **Part 4 — GitHub surfaces**
- **Part 5 — Full store/platform resolution reference**

---

## Part 0 — The vector master

`design/logo.af` is the only file anyone *draws* in. Its layer tree mirrors the
generated SVG one-for-one, and that correspondence is what lets
`af-to-svg.py` be a transcription rather than an interpretation:

```
base                 base-plate                 ink disc, r 512, full bleed
                     base-field                 paper disc, r 360, concentric
mascot-elephant      elephant-clearance/        one paper halo per ink path
                     elephant-head              \
                     elephant-haunch             |  ink line art,
                     elephant-foreleg            |  21 units wide
                     elephant-eye               /
brand-broom          broom-clearance/           bristles + handle + ferrule halos
                     broom-bristles             \
                     broom-handle                |  ink, the kubeNimbus broom verbatim
                     broom-ferrule              /
                     broom-grip-slot            paper, cut out of the handle
```

The numbers that are not free choices:

| | |
|---|---|
| plate / field | 512 / 360, both centred on (512, 512) — ratio 0.703125, measured off the raster-era master (plate 460.68, field 323.84 at 1024) |
| ink line width | 21 units, held by every elephant stroke |
| clearance halo | 39.451, the width kubeNimbus's broom already used |
| broom offset | (−1, −10) applied to every broom part, so the module stays rigid |

**Rename a node and you change the generated SVG's ids**, which are
load-bearing: `make-masters.ps1` finds the plate by `r="512"` to build the
transparent glyph, and kubeNimbus lifts `#brand-broom` by id. The elephant's
four ink paths deliberately carry *different* offsets — they are independent
strokes with gaps between them, not a jointed assembly — so moving the mascot
means moving eight objects (four ink, four halo), not one group.

---

## Part 1 — Sources: `design/masters/`

**Generated, not edited** (changed 2026-08 — see the pipeline at the top).
`scripts/design/make-masters.ps1` rewrites every file here from
`design/logo.svg`; a hand edit survives exactly until the next person runs it.

### `icon/` — app-icon tiles (plated, transparent only outside the disc)

Every size is a render of `logo.svg`. They all keep the plate because they feed
`app.ico`, which Windows hands the taskbar, Alt+Tab and the title bar through
one `WM_SETICON` slot — it cannot be theme-aware, and unplated dark line art
disappears on a dark taskbar.

| File | Size | Source | Feeds |
|---|---|---|---|
| `icon-1024.png` | 1024² | `logo.svg` | macOS 512/1024, Store listing images |
| `icon-256.png` | 256² | `logo.svg` | `app.ico` 64/128/256, MSIX 150, macOS 64–256 |
| `icon-48.png` | 48² | `logo.svg` | `app.ico` 48, MSIX 44 & 50 |
| `icon-32.png` | 32² | `logo.svg` | `app.ico` 32, macOS 32 |
| `icon-24.png` | 24² | `logo.svg` | `app.ico` 24 |
| `icon-16.png` | 16² | `logo.svg` | `app.ico` 16, macOS 16 |

### `window/` — in-app title-bar icons (**transparent** line art)

| File | Size | Feeds |
|---|---|---|
| `window-light-256.png` | 256² | `Assets/window-icon-light.ico` (light theme) |
| `window-dark-256.png` | 256² | `Assets/window-icon-dark.ico` (dark theme) |

### `logo/` — website / marketing (**transparent**, except the social card)

| File | Size | Feeds / used by |
|---|---|---|
| `logo.png` | 1024² | the bare mark, for a README or site header |
| `wordmark-{light,dark}.svg` (+ `.png` @2×) | ≈3.7:1 | README wordmark (see Part 4) |
| `social-preview.png` | 1280×640 | GitHub repo social preview (has a bg) |

The wordmark is the mark at 240px beside "pgNimbus" in Segoe UI Bold, with the
text baked to paths by Inkscape so the committed SVG renders identically on a
machine without that font — GitHub's, for one. **Both lockups carry the same
mark**; only the type changes colour, because "pgNimbus" set in ink is
unreadable on a dark README. That is also why there is one `logo.png` and not a
light/dark pair — the plate carries the mark's own contrast.

The social card is the bare mark on a `.paper` background. It is the mark
rather than the lockup because link unfurlers crop this aggressively and to
wildly different aspect ratios: a square survives that, a 3.7:1 lockup gets its
ends eaten. It is opaque because a transparent card renders white in some
clients and black in others.

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

### `scripts/design/af-to-svg.py` (any OS, stdlib only)
Run after `scripts/design/dump-af.js` in Affinity. Bakes every node transform
into the path data and writes `design/logo.svg`.

### `scripts/design/make-masters.ps1` (Windows, Inkscape + System.Drawing)
Run after any change to `design/logo.svg`. Renders every file under
`design/masters/`. The palette is inverted in exactly one place — the
light-surface window master — and that inversion lives here rather than in a
second committed SVG:

```
logo.svg ─── render at 16/24/32/48/256/1024 ──────────► masters/icon/icon-*.png
logo.svg ─── render at 1024 ──────────────────────────► masters/logo/logo.png
logo.svg ─── invert palette, strip <circle r="512"> ─► masters/window/window-light-256.png
logo.svg ─── strip <circle r="512"> ─────────────────► masters/window/window-dark-256.png
logo.svg ─── + "pgNimbus" text, baked to paths ──────► masters/logo/wordmark-*.{svg,png}
logo.png ─── centred on a 1280×640 paper card ───────► masters/logo/social-preview.png
```

### `scripts/windows/make-app-icons.ps1` (Windows, System.Drawing)
Run after `make-masters.ps1`. Copies exact-size masters verbatim, derives only
the sizes with no master of their own:

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

**1. README header** (`README.md`, top). The **wordmark** lockup at
`width="300"`, theme-switched — the mark is the same in both, only the type
changes colour:

```html
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="design/masters/logo/wordmark-dark.png">
  <img src="design/masters/logo/wordmark-light.png" alt="pgNimbus logo …" width="300">
</picture>
```

For the bare mark with no type, use `masters/logo/logo.png` — one file, no
theme switch needed. How GitHub renders either: README column ≈980px on
desktop, device-width on mobile, with `max-width:100%`, so one image scales to
both **if it stays legible small**. The committed wordmark SVGs are crisper
still if you would rather link those; the PNGs are 2× (~2150px wide).

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
- ⚠️ **Small-size legibility.** Every tile is now a render of one vector
  master rather than a per-size drawing, so 16px is the whole mark shrunk to
  16px — a dark disc with a smudge in it. Microsoft's "maintain legibility at
  small sizes" guidance is not met at 16 and 24. The fix, when it matters, is a
  simplified **mark** (`logo-small.svg` / `logo-micro.svg`, as in kubeNimbus)
  fed into `make-masters.ps1`, not a hand-painted PNG.
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
