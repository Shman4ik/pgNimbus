# pgNimbus — logo redraw brief

> **Revised 2026-08.** This brief used to ask for a folder of hand-drawn PNGs,
> one per size. That is no longer how the logo works: there is a single vector
> master, and every PNG in the repo is generated from it. The sizes and
> surfaces below are still right, and so are the two golden rules — what
> changed is the deliverable. See **How delivery works** immediately below, and
> [`LOGO-ASSETS.md`](LOGO-ASSETS.md) for the full pipeline.

Hi, and thanks for taking this on! This is everything you need to redraw the
pgNimbus logo and icon set.

**What pgNimbus is:** a fast, modern, open-source PostgreSQL desktop app for
Windows and macOS. The current mascot is *an elephant riding a broom* (Postgres
is traditionally an elephant). You're free to evolve or redraw the mark — just
keep it recognizable at tiny sizes.

---

## How delivery works (please read first)

**One file: `design/logo.af`.** Draw the mark there, at 1024 × 1024, and
everything else in the repo is regenerated from it by script. Do not hand-paint
anything under `design/masters/` — those files are output, and the next person
to run the pipeline will overwrite them.

Three things about the `.af` are load-bearing, because a script reads them:

1. **The layer names.** They become the ids in the generated SVG, and other
   tools find geometry by id — the transparent title-bar icon is built by
   stripping the layer named for the full-bleed plate, and the sibling project
   kubeNimbus lifts the broom group out by name. Rename a layer and something
   downstream stops finding it. The current tree is listed in Part 0 of
   [`LOGO-ASSETS.md`](LOGO-ASSETS.md).
2. **Each module carries its own light halo.** Hiding the plate has to leave a
   whole elephant and a whole broom, not fragments, because the mark gets used
   without its plate.
3. **Nothing changes colour where it crosses the plate's rim.** The broom
   handle and the tip of the trunk run out past the light field onto the dark
   plate and stay dark the whole way; the halo underneath is what keeps them
   readable there.

If you would rather work in another tool, hand back an SVG at
`viewBox="0 0 1024 1024"` with the same group structure and no transforms,
masks, or `<use>` — that is the shape the pipeline expects.

```
design/logo.af        ← you draw here
  ↓ generated
design/logo.svg
  ↓ generated
design/masters/
├── icon/      ← the app icon tile, six sizes
├── window/    ← the in-app title-bar icon (transparent line art)
└── logo/      ← website/README logo + wordmark + share card
```

---

## Two golden rules

**1. Transparent vs. solid background — this matters, please don't mix them up.**

| Type | Background | Used as… |
|---|---|---|
| **App icon "tile"** (`icon/`) | **Solid color, fills the whole square** | The app's icon on the desktop, taskbar, Start menu, Dock, and in the app stores. It's a little colored square. |
| **Window / line-art** (`window/`, `logo/`) | **Transparent** | Drawn *on top of* the app's UI and the website. No background — it must look right on both white and near-black. |

**2. Small icons need a simpler drawing, not just a smaller one.**
The small icons are the big mark shrunk down, so 16 and 24 px look muddy. The
fix is a second *vector* of the mark drawn for those sizes — thicker strokes,
fewer details, anything that turns to mush left out. The 16 px version can be
almost a symbol. Think "readable first, pretty second."

---

## Checklist — what to deliver

### 📦 App icon tile — `design/masters/icon/`
**Generated, not delivered.** All six sizes (1024, 256, 48, 32, 24, 16) are
rendered from the vector master, along with the in-between sizes (64, 128, 44,
50, 150). You do not draw any of them.

What still needs your attention is rule 2 above: the mark has to survive being
shrunk to 16 px. It currently does not — at that size it is a dark disc with a
smudge in it. If you want to fix that, the answer is a **second, simpler
drawing of the mark** (fewer strokes, heavier weight, no fine detail), which we
wire in as the source for the 16 and 24 px renders. It is not a hand-painted
PNG: it is another vector, drawn once.

### 🪟 Window icon — `design/masters/window/`
The little icon in the app's own title bar and taskbar. **Transparent line
art** (no background). Two color versions so it reads on either theme:

| File | Size (px) | Notes |
|---|---|---|
| `window-light-256.png` | 256 × 256 | For the **light** theme → **dark** ink. |
| `window-dark-256.png` | 256 × 256 | For the **dark** theme → **light** ink. |

Heads-up: these two also become the **Windows taskbar / Start / Alt+Tab icon**
for the Store version of the app (Windows calls them "unplated" icons), shown
as small as 16–24 px. Today we auto-shrink the 256 px file to those sizes — so
keep the line art **bold and simple** (thick strokes, minimal detail), or it
will turn to mush at taskbar size. If you'd like, you may optionally also
deliver hand-simplified `window-{light,dark}-{16,24,32,48}.png` versions and
we'll wire them in.

### 🌐 Website / README logo — `design/masters/logo/`
Shown at the top of the project's web page (GitHub). **Transparent.**

| File | Size / format | Notes |
|---|---|---|
| `logo.png` | 1024 × 1024 | Transparent. One file, no light/dark pair: the plate gives the mark its own contrast, so it reads on both. |
| `wordmark-light.svg` + `wordmark-light.png` | vector + PNG @2× | **Horizontal lockup**: the mark **+ the word “pgNimbus”**, dark type. Generated. |
| `wordmark-dark.svg` + `wordmark-dark.png` | vector + PNG @2× | Same mark, light type. Two versions exist only because the *type* has to change colour; the mark does not. |
| `social-preview.png` | **1280 × 640** | Share/link-preview card: the bare mark on a solid background (this one is NOT transparent). Generated. |

**Wordmark note (important for how it looks on phones):** please deliver it as
**SVG** — that stays razor-sharp on any screen, phone or 4K monitor. Keep the
shape roughly **3.5 : 1** (about 3.5× wider than tall, e.g. 1000 × 280). Don't
go wider than ~4:1, or it becomes an unreadable thin strip on a phone.

---

## Style notes

- **One coherent family** — the tile, the window icon, and the wordmark should
  clearly be the same brand.
- **Colors:** we don't have a locked brand palette yet — propose one. It should
  work on both a light and a dark app. If you give us the SVGs, we can retint.
- **Safe margin:** keep the glyph within ~85% of the tile so nothing gets
  clipped when the OS rounds the corners.
- **Formats:** PNG (with transparency where noted) for rasters, plain SVG for
  vectors. Please also send your **source file** (AI/Figma/Sketch/SVG) so we can
  make small tweaks later.

## Windows icon rules (please follow — they're Microsoft's official guidelines)

Windows is our primary platform, and Microsoft publishes concrete design rules
for app icons ([design](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-design),
[construction](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction)).
The short version that applies to this job:

- **At most two metaphors.** Elephant + broom is already exactly two — please
  don't add a third element (no database cylinders, clouds, lightning bolts,
  sparkles). One focal concept, simple forms.
- **No letters or words inside the icon.** The app name is always shown next
  to the icon by the OS. (The *wordmark* is the place for typography — not the
  tile or window icon.)
- **Flat and straight-on.** No 3/4 perspective, no isometric views, no 3D
  bevels. Icons are flat shapes layered on top of each other; depth comes only
  from **subtle drop shadows between layers**. Design shadow values at 48×48 px
  and scale from there.
- **Design on a 48×48 grid.** Align the silhouette's key features to the grid.
  Rounded corners on the shapes themselves: **2 px radius on exterior curves,
  1 px on interior curves, at 48 px** (scale proportionally at other sizes).
- **Gradients: subtle or none.** If used, limit to one–two steps, default angle
  **120°**, lighter hue toward the top-left. No tight transitions that read as
  reflections or shininess.
- **Contrast:** at least **half of the icon must pass a 3.0:1 contrast ratio on
  both light and dark backgrounds**; use color values across the dark, medium,
  and light ranges. Beware: pure yellow never passes on light theme, saturated
  reds struggle on dark theme.

Our "simplify per size" rule above is Microsoft's own recommendation too —
Windows renders the taskbar icon at 24–36 px on typical displays, so the small
masters are what most users see most of the time.

---

## Quick reference — where each thing shows up

| Your file(s) | Appears as |
|---|---|
| `icon/*` | Desktop / taskbar / Dock icon, and the Microsoft Store & (later) Mac App Store icon. |
| `window/*` | The icon in the app's own window title bar — and the taskbar/Start/Alt+Tab icon of the Store build. |
| `logo/logo-*` + `wordmark-*` | Top of the GitHub project page. |
| `logo/social-preview.png` | The preview card when someone shares the project link. |

Questions about sizes, formats, or where something is used? The engineering-side
detail lives in [`LOGO-ASSETS.md`](LOGO-ASSETS.md) — but you shouldn't need it.
Thank you! 🐘🧹
