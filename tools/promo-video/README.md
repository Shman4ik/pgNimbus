# Promo video

A 64-second promo video for pgNimbus, built from code: a storyboard, generated
music, and a renderer that draws every frame in headless Chromium and encodes it
with ffmpeg. Nothing is edited by hand, so the video can be rebuilt whenever the
app or the story changes.

The approach, and much of the code, comes from the hype video in
[jamesmontemagno/app-fire-calculator](https://github.com/jamesmontemagno/app-fire-calculator)
(`tools/hype-video`, MIT, Copyright (c) 2025 James Montemagno). The files taken
from it say so at the top.

## Build it

You need Node 20 or later, ffmpeg on `PATH`, and the .NET SDK the repository
builds with (for the screenshots).

```bash
cd tools/promo-video
npm install
npm run setup     # once: Playwright's headless Chromium
npm run build     # clips, shots, music, render
```

The video lands in `build/pgnimbus-promo-midnight.mp4` (1920x1080, 30 fps, H.264,
and AAC normalised to about -14 LUFS). `build/` is git-ignored: the video is rebuilt from the storyboard,
not kept in the repository. A full render takes about 5 minutes.

| Script | What it does |
|---|---|
| `npm run clips` | Cuts the storyboard's clips out of the Store trailer into JPEG frames in `build/clips/` |
| `npm run shots` | Renders the `shot:` stills with the screenshot harness into `build/shots/` |
| `npm run music` | Writes `build/music-<variant>.wav` from the storyboard |
| `npm run render` | Renders the video. `--stills 3,17.5` writes PNGs to `build/stills/` instead; `--from`/`--to`, `--fps`, `--crf`, `--out` and `--no-audio` also work |
| `npm run preview` | Serves the composition with the music, a scrubber and Space to play |

## Variants

The same storyboard renders in several looks, each with its own soundtrack. They
are defined in [`variants.mjs`](variants.mjs): a palette (CSS custom properties
and the background's colours) and a music style.

| Variant | Look | Music |
|---|---|---|
| `midnight` (default) | Blue and violet on navy | Bright EDM in D major |
| `neon` | Magenta and cyan on deep purple, a synthwave sun on the horizon | Synthwave in A minor: driving 8th-note bass, gated snare |
| `daylight` | Light paper with coral and teal | Deep house in F: electric piano, swing, a little vinyl crackle |

Pass `--variant <name>` to `music` and `render` (after `--` with npm, as in
`npm run render -- --variant neon`); the preview takes `&variant=neon`. Each
renders to `build/pgnimbus-promo-<variant>.mp4`. A new variant is one entry in
`variants.mjs`; a new music style is one arrangement function in
`scripts/generate-music.mjs`.

## Change it

Everything about the story is in [`storyboard.mjs`](storyboard.mjs): the scenes,
their copy, the clips and where the camera looks.

- **Scenes are measured in bars** (120 BPM, so a bar is 2 s). The music is
  generated from the same list, so a scene's `energy` (`intro`, `drop`, `groove`,
  `break`, `outro`) arranges its bars and every cut lands on a downbeat.
- **Copy**: `*word*` is the brand gradient, `` `x` `` is set in the code face,
  `\n` breaks a headline line. User-facing copy goes through the `humanizer`
  skill like the README and the website, and every claim has to be true of the
  current release (the startup figure is the CI number the README quotes).
- **Clips** are footage from the Microsoft Store trailer
  (`design/store/trailer/pgnimbus-trailer.mp4`, recorded by
  `scripts/demo/record/scenes/store-trailer.ps1`). A clip names trailer
  `segments`, a `speed`, and `focus` keyframes that pan and zoom inside the
  window. When the trailer is re-recorded, the segment times move: find the new
  ones with `scripts/demo/record/sheet.sh` and check the framing with
  `npm run render -- --stills …` before a full render.
- **Stills** are `shot:<scenario>.<theme>`, rendered by `tools/Screenshot` from
  its fixtures, so they always show the current UI. A shot's `focus` frames part
  of it.

Review stills before rendering the whole video, and look at a contact sheet of
the result (`scripts/demo/record/sheet.sh build/pgnimbus-promo-midnight.mp4 sheet.png 6 6`).
Listen to it once too: only its loudness is checked by the build.

## How it works

- `composition/` is a page whose every frame is a function of time alone: no CSS
  transitions, no timers. `window.__video.seek(t)` draws time `t` and resolves once
  every image on screen has decoded, which is what makes a render exact and
  repeatable.
- `composition/scenes.js` has one builder per scene type (`intro`, `pillars`,
  `clip`, `showcase`, `statement`, `outro`); each builds its DOM once and returns
  an `update(local)` that sets every animated style from the scene's local time.
- `scripts/static-server.mjs` serves the repository on `127.0.0.1` only, so the
  page can load the logo, the clips and the shots. Nothing leaves the machine.
