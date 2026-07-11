# pgNimbus — logo redraw brief

Hi, and thanks for taking this on! This is everything you need to redraw the
pgNimbus logo and icon set. It's written so you can work from **this page
alone** — no need to read any code.

**What pgNimbus is:** a fast, modern, open-source PostgreSQL desktop app for
Windows and macOS. The current mascot is *an elephant riding a broom* (Postgres
is traditionally an elephant). You're free to evolve or redraw the mark — just
keep it recognizable at tiny sizes.

---

## How delivery works (please read first)

Every file you produce has a **fixed name and a fixed folder**. To hand off,
just **replace the placeholder file of the same name** in the `design/masters/`
folders below. The build tooling picks them up automatically — nothing else
needs to change. Please keep the exact filenames, sizes, and PNG format.

```
design/masters/
├── icon/      ← the app icon (a colored square "tile") — hand-drawn per size
├── window/    ← the in-app title-bar icon (transparent line art)
└── logo/      ← website/README logo + wordmark (transparent + a share card)
```

---

## Two golden rules

**1. Transparent vs. solid background — this matters, please don't mix them up.**

| Type | Background | Used as… |
|---|---|---|
| **App icon "tile"** (`icon/`) | **Solid color, fills the whole square** | The app's icon on the desktop, taskbar, Start menu, Dock, and in the app stores. It's a little colored square. |
| **Window / line-art** (`window/`, `logo/`) | **Transparent** | Drawn *on top of* the app's UI and the website. No background — it must look right on both white and near-black. |

**2. Small icons must be redrawn simpler, not just shrunk.**
The whole reason for this brief: today the small icons are just the big one
shrunk down, so they look muddy and unreadable. At **16 / 24 / 32 px** please
**simplify** — thicker lines, fewer details, drop anything that turns to mush.
The 16px version can be almost a symbol. Think "readable first, pretty second."

---

## Checklist — what to deliver

### 📦 App icon tile — `design/masters/icon/`
Square, **solid-color background filling the entire canvas** (no rounded corners
— the OS rounds them itself; no transparency). Same artwork, but **hand-tuned
per size**: full detail on the big ones, simplified on the small ones.

| File | Size (px) | Detail level |
|---|---|---|
| `icon-1024.png` | 1024 × 1024 | ⭐ Master — full detail. Everything large derives from this. |
| `icon-256.png` | 256 × 256 | Full detail. |
| `icon-48.png` | 48 × 48 | Slightly simplified. |
| `icon-32.png` | 32 × 32 | **Simplified.** |
| `icon-24.png` | 24 × 24 | **Simplified.** |
| `icon-16.png` | 16 × 16 | **Most simplified** — legibility over detail. |

*(We generate the in-between sizes — 64, 128, 44, 50, 150 — automatically from
these, so you don't need to draw them.)*

### 🪟 Window icon — `design/masters/window/`
The little icon in the app's own title bar and taskbar. **Transparent line
art** (no background). Two color versions so it reads on either theme:

| File | Size (px) | Notes |
|---|---|---|
| `window-light-256.png` | 256 × 256 | For the **light** theme → **dark** ink. |
| `window-dark-256.png` | 256 × 256 | For the **dark** theme → **light** ink. |

### 🌐 Website / README logo — `design/masters/logo/`
Shown at the top of the project's web page (GitHub). **Transparent.**

| File | Size / format | Notes |
|---|---|---|
| `logo.svg` | vector | ⭐ Master vector of the full logo. |
| `logo-light.png` | 1024 × 1024 | Transparent, dark ink (for light backgrounds). |
| `logo-dark.png` | 1024 × 1024 | Transparent, light ink (for dark backgrounds). |
| `wordmark-light.svg` + `wordmark-light.png` | vector + PNG ~880 wide | **Horizontal lockup**: the mark **+ the word “pgNimbus”**. Dark ink. See note below. |
| `wordmark-dark.svg` + `wordmark-dark.png` | vector + PNG ~880 wide | Same, light ink. |
| `social-preview.png` | **1280 × 640** | A share/link-preview **card** — mark + “pgNimbus” + short tagline, **on a solid branded background** (this one is NOT transparent). |

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

---

## Quick reference — where each thing shows up

| Your file(s) | Appears as |
|---|---|
| `icon/*` | Desktop / taskbar / Dock icon, and the Microsoft Store & (later) Mac App Store icon. |
| `window/*` | The icon in the app's own window title bar. |
| `logo/logo-*` + `wordmark-*` | Top of the GitHub project page. |
| `logo/social-preview.png` | The preview card when someone shares the project link. |

Questions about sizes, formats, or where something is used? The engineering-side
detail lives in [`LOGO-ASSETS.md`](LOGO-ASSETS.md) — but you shouldn't need it.
Thank you! 🐘🧹
