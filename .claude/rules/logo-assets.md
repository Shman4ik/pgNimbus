---
description: "App icon and logo pipeline: logo.af -> logo.svg -> masters -> app assets and Store logos."
paths:
  - "design/**"
  - "scripts/design/**"
  - "scripts/windows/make-*"
  - "src/PgNimbus.App/Assets/**"
  - "**/LogoMark.axaml"
---

<!-- Moved out of .claude/CLAUDE.md so it loads only when working on these paths. Same rule applies: keep it current in the same PR. -->

## App icon / logo assets

Full reference: [`design/LOGO-ASSETS.md`](design/LOGO-ASSETS.md); the
designer hand-off brief is [`design/DESIGNER-BRIEF.md`](design/DESIGNER-BRIEF.md).
**Keep both current** when assets or the pipeline change.

**One drawing feeds everything** (2026-08). `design/logo.af` is where the mark
is drawn; `design/logo.svg` is generated from it; every raster below is
generated from that. **There is one colourway** (2026-08): the mark is plated,
a dark disc holding a light field, so it carries its own contrast and reads on
white, on a light UI, on GitHub dark and on black alike — a second SVG would be
a second thing to keep in step for no gain. Nothing in `design/masters/**` or
`src/PgNimbus.App/Assets/**` is hand-edited any more — regenerate, don't retouch.
The chain, each step a script:

```
design/logo.af                     Affinity, the editable master
  → scripts/design/dump-af.js      geometry out to JSON (run via the Affinity MCP)
  → scripts/design/af-to-svg.py    design/logo.svg
  → scripts/design/make-masters.ps1        design/masters/**
  → scripts/design/svg-to-axaml.py         src/PgNimbus.App/Styles/LogoMark.axaml
  → scripts/windows/make-app-icons.ps1     src/PgNimbus.App/Assets/**
  → scripts/windows/make-store-logos.ps1   design/store/**
```

What this replaced: masters that were **hand-drawn per size**, because the mark
was a traced raster whose downscale turned to mud below 32px. The modular
vector master rasterises cleanly, so the six icon tiles are now six renders of
one file rather than six drawings that drift apart. If a size ever does stop
reading, the fix is a simplified *mark* fed into `make-masters.ps1` (a
`logo-small.svg`, the way kubeNimbus does it) — never a hand-painted PNG that
nothing can regenerate. Layout:

- `design/masters/icon/icon-{16,24,32,48,256,1024}.png` — the app tile, every
  size rendered from `logo.svg`. All of them keep the plate: these feed
  `app.ico`, which Windows hands the taskbar, Alt+Tab and the title bar
  through one `WM_SETICON` slot, so it cannot be theme-aware, and unplated
  dark line art vanishes on a dark taskbar. The corners outside the plate are
  transparent, which is fine — what must not be transparent is the middle.
- `design/masters/window/window-{light,dark}-256.png` — the same plated mark,
  written out twice (2026-08). It used to be theme-tinted transparent line art
  (the full-bleed plate stripped, `window-light` cut from a palette-inverted
  copy so dark lines would still read on a light Start menu with no plate
  behind them) — two more hand-maintained colourways of a mark that, every
  other place it ships, needs exactly one because the plate already carries
  its own contrast. `make-masters.ps1` now just renders `design/logo.svg` at
  256px for both file names. These feed `window-icon-{light,dark}.ico` (now
  byte-identical, kept as two files only because `ThemedWindowChrome` still
  picks between two names by theme) and the MSIX "unplated" altforms — which,
  since Windows backplates an unplated tile on its own, now show a plate
  inside a plate there. Accepted deliberately: one mark everywhere beat a
  transparent-only cut that only that one Store surface used.
- **`design/logo.svg` — the committed vector master**, generated from the
  `.af` and never hand-edited (`af-to-svg.py` overwrites it). `viewBox="0 0
  1024 1024"`; three modules (`#base`, `#mascot-elephant`, `#brand-broom`) as
  plain `<path>` geometry in the root coordinate system — no `transform`, no
  `mask`, no `<use>`, no CSS variables — which is what makes it survive
  Inkscape / Illustrator / Figma and what lets a module be lifted whole into a
  sibling mark. Colour is two classes, `.ink` and `.paper`, with the value
  repeated as a plain attribute so tools that ignore `<style>` still render, so
  a host page can retheme the mark without touching the geometry. Two rules
  hold it together. **Nothing changes colour where it crosses the field's rim**: the
  broom's handle and the tip of the trunk both carry on past the light field
  onto the plate and stay ink the whole way, carried by a `.paper` clearance
  halo drawn underneath — the raster-era master flipped them to white instead,
  which is the same drawing but a different object every time the rim crosses
  it. And **each module carries its own clearance**, so hiding `#base` leaves a
  whole elephant and a whole broom rather than a heap of fragments. That halo
  is 39.451 in both modules, the width kubeNimbus's broom already used: at the
  trunk it has to *fill* the hollow between the trunk's two walls out on the
  plate, not merely outline them, or the trunk ends with a black wedge inside
  it. What this replaced (the raster-era `logo.svg`, in git history) was one compound
  path with seven subpaths, in which neither the elephant nor the broom was an
  object: both were white showing through a solid ink disc, so hiding the disc
  left nothing.
  **The `.af` mirrors this structure exactly** — same three groups, same
  clearance subgroups, one node per `<path>` — which is what lets `af-to-svg.py`
  be a transcription rather than an interpretation. Keep it that way: a node
  renamed or regrouped in Affinity changes the generated SVG's ids, and those
  ids are load-bearing (`make-masters.ps1` finds the plate by radius, kubeNimbus
  lifts `#brand-broom` by id).
- `design/masters/logo/` — README/website assets: `logo.png`
  (the mark at 1024 on transparency, one file), `wordmark-{light,dark}.{svg,png}` (the
  mark at 240px beside "pgNimbus" in Segoe UI Bold, text baked to paths by
  Inkscape so it renders on a machine without that font), and
  `social-preview.png` (1280×640, the dark navy card the raster-era mark
  used: the "pgNimbus" wordmark plus the one-line tagline, not the bare mark
  — a bare-mark version shipped briefly in 2026-08 on the theory that link
  unfurlers crop this to wildly different aspect ratios and a square survives
  that better than a lockup, but GitHub itself renders the card uncropped at
  its native 2:1, so the crop-safety argument gave up a legible product name
  for a benefit that mostly wasn't there; reuses the generated
  `wordmark-dark.svg` rather than re-deriving the mark+text lockup a third
  time). The wordmark is the one
  asset that still ships in two colourways, and only because of the type:
  "pgNimbus" set in ink is unreadable on a dark README. Both lockups carry the
  same mark. All of these come out of `make-masters.ps1`.
- `design/store/` — **generated**, not hand-edited: Microsoft Partner Center
  listing images from `icon-1024.png`, via
  `scripts/windows/make-store-logos.ps1`. Checked into git so a Partner
  Center re-upload doesn't depend on someone remembering to run the script.

Everything in `src/PgNimbus.App/Assets/` is **generated** by
`scripts/windows/make-app-icons.ps1` (Windows-only, System.Drawing) —
regenerate via that script, don't hand-edit. Output filenames are stable so
csproj / MSIX manifest reference them unchanged:

- `app.ico` — 16–256px multi-size tile; the exe (`ApplicationIcon`)
  only. Windows don't set `Icon` in XAML; the runtime window icon is
  the next bullet, not this file.
- `window-icon-light.ico` / `window-icon-dark.ico` — what
  `ThemedWindowChrome.Attach(this)` (called from every window's constructor)
  actually sets at runtime: `Window.Icon` (always from the `-dark` file — a
  quirk that stopped mattering once both files became the same plated mark,
  2026-08) and, via a direct `WM_SETICON` P/Invoke built from the same `.ico`
  bytes, the small/big taskbar HICONs (still picked by theme, `-light` or
  `-dark`, though now visually identical too — that branch is harmless,
  redundant, and hasn't been collapsed). The P/Invoke exists because
  Avalonia's `Window.Icon` reliably updates the title bar but not the
  Windows 11 taskbar button (a known Avalonia/Win32 gap). One plated icon
  everywhere is the same reasoning `app.ico` already applied: the title bar,
  taskbar and Alt+Tab all read the same `WM_SETICON` slots (they cannot
  diverge), and theme-swapped transparent line art was unreadable on the
  (almost always dark) taskbar whenever the app ran the light theme — which
  is also why these two files are no longer transparent line art themselves
  (see `design/masters/window/` above).
- `Assets/Msix/*` — MSIX tiles, packaging-time-only. Each of
  `Square44x44Logo`/`Square150x150Logo`/`StoreLogo` ships as
  `.scale-{100,125,150,200,400}.png` (not one flat file — Windows will
  backplate/blur a lone unqualified asset when a surface asks for a size it
  doesn't have), plus `Square44x44Logo.targetsize-{16,24,32,48,256}_altform-
  {unplated,lightunplated}.png` (reused from the `window/` masters, which are
  the plated mark rather than transparent line art as of 2026-08 — see
  `design/masters/window/` above for why that's a deliberate plate-inside-a-
  plate on this one surface) for the taskbar/Start/Alt+Tab/install-dialog
  surfaces that expect an unplated icon. `build-msix.ps1` compiles these into
  `resources.pri` via
  `makepri` — see "Microsoft Store (MSIX)" in `release-ci.md`; the qualified filenames do
  nothing on their own without that resource index.
