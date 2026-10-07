/**
 * The promo video's storyboard: the one file to edit when the story changes.
 *
 * Scenes are measured in bars, so every cut lands on a downbeat. The music generator
 * (scripts/generate-music.mjs) reads the same list, so a longer scene is a longer soundtrack.
 *
 * Text: `*word*` is the brand gradient, `\n` breaks a headline, `` `code` `` sets a run in
 * the code face.
 *
 * Images are paths from the repository root, or `shot:<scenario>.<theme>`: a window the
 * screenshot harness (tools/Screenshot) renders from its fixtures, through
 * scripts/render-shots.mjs, so a still always shows the current UI. Clip scenes play footage
 * that scripts/extract-clips.mjs cuts out of the Store trailer (see `clips` below).
 *
 * This structure, and the code around it, is adapted from the hype video in
 * jamesmontemagno/app-fire-calculator (tools/hype-video, MIT).
 */

export const config = {
  title: 'pgNimbus promo video',
  width: 1920,
  height: 1080,
  fps: 30,
  bpm: 120,
  beatsPerBar: 4,
  url: 'shman4ik.github.io/pgNimbus',
  repoUrl: 'github.com/Shman4ik/pgNimbus',
  install: 'winget install pgNimbus --source msstore',
  /**
   * Git-ignored: the video is rebuilt from this storyboard, not kept in the repository.
   * `{variant}` is the look and sound it was rendered with (variants.mjs).
   */
  output: 'tools/promo-video/build/pgnimbus-promo-{variant}.mp4',
  /** The startup figure the README, the docs and the landing page quote (the CI number). */
  startupSeconds: 0.2,
  /** Seeds the background's particles; each variant's music has its own seed. */
  seed: 11,
  /**
   * How much the picture throbs on each kick (the frame's zoom and the background's glow,
   * grid and particles): 0 is still, 1 is the full pulse. Off: it read as a heartbeat.
   */
  beatPulse: 0,
}

/**
 * Footage, cut from the Microsoft Store trailer (design/store/trailer/pgnimbus-trailer.mp4,
 * recorded by scripts/demo/record/scenes/store-trailer.ps1). Each take sits 1:1 in that
 * video at 1600x900, offset (160, 36), so the cut is lossless in size.
 *
 * `segments` are [from, to] seconds of the trailer, joined end to end; `speed` plays the
 * result faster. `focus` keyframes pan and zoom inside the window: `at` is seconds into the
 * extracted clip (after `speed`), `x`/`y` the point of the 1600x900 take to centre on, and
 * `zoom` the magnification. Between keyframes the camera eases; before the first and after
 * the last it holds.
 */
export const clips = {
  source: 'design/store/trailer/pgnimbus-trailer.mp4',
  crop: { width: 1600, height: 900, x: 160, y: 36 },
  list: {
    palette: {
      segments: [[3.0, 8.1]],
      speed: 1.15,
      focus: [
        { at: 0, x: 800, y: 300, zoom: 1.55 },
        { at: 2.9, x: 800, y: 300, zoom: 1.55 },
        { at: 3.7, x: 800, y: 450, zoom: 1 },
      ],
    },
    completion: {
      segments: [[9.5, 19.6]],
      speed: 1.4,
      focus: [
        { at: 0, x: 620, y: 230, zoom: 2.0 },
        { at: 5.9, x: 700, y: 230, zoom: 2.0 },
        { at: 6.7, x: 800, y: 450, zoom: 1 },
      ],
    },
    explain: {
      segments: [[45.0, 58.6]],
      speed: 1.9,
      focus: [
        { at: 0, x: 760, y: 560, zoom: 1.45 },
        { at: 7.2, x: 760, y: 560, zoom: 1.45 },
      ],
    },
    safeMode: {
      segments: [
        [26.0, 31.4],
        [37.0, 41.0],
      ],
      speed: 1.35,
      focus: [
        { at: 0, x: 760, y: 600, zoom: 1.5 },
        { at: 3.5, x: 760, y: 600, zoom: 1.5 },
        { at: 4.1, x: 800, y: 450, zoom: 1.5 },
        { at: 5.7, x: 800, y: 450, zoom: 1.5 },
        { at: 6.4, x: 800, y: 600, zoom: 1 },
      ],
    },
  },
}

/**
 * Scene types (composition/scenes.js):
 *  - intro:     hook, the startup stopwatch, then the logo and name.
 *  - pillars:   words slam in one per `beatsPerWord` beats, then all of them as chips.
 *  - clip:      kicker, headline and bullets beside a window playing a clip.
 *  - showcase:  the same layout with still screenshots taking turns; a shot's optional
 *               `focus` ({ x, y, zoom } in the image's pixels) frames part of it.
 *  - statement: one big centred line with chips under it.
 *  - outro:     platforms, the install command, the link and the repository.
 *
 * Icons are names from composition/icons.js. `energy` arranges the music for the scene's
 * bars: intro | build | drop | groove | break | outro.
 */
export const scenes = [
  {
    type: 'intro',
    bars: 4,
    energy: 'intro',
    hook: 'Your database client\nshould *already* *be* *open*.',
    caption: 'From launch to the first frame. NativeAOT, measured by CI on every release.',
    logo: 'design/logo.svg',
    name: 'pgNimbus',
    tagline: 'A fast PostgreSQL client that talks to your database and nothing else',
  },
  {
    type: 'pillars',
    bars: 4,
    energy: 'drop',
    beatsPerWord: 2,
    summary: 'MIT licensed. Built by someone who lives in Postgres.',
    words: [
      { text: 'Native', icon: 'zap' },
      { text: 'Free', icon: 'gift' },
      { text: 'Open source', icon: 'code' },
      { text: 'No telemetry', icon: 'eyeOff' },
      { text: 'No account', icon: 'userX' },
    ],
  },
  {
    type: 'clip',
    bars: 3,
    energy: 'groove',
    kicker: 'Keyboard first',
    headline: '`Ctrl+K`.\n*Anything*, instantly.',
    bullets: ['Jump to any table or view', 'Every command, with its shortcut'],
    clip: 'palette',
  },
  {
    type: 'clip',
    bars: 4,
    energy: 'groove',
    kicker: 'Autocomplete',
    headline: 'It writes the *JOIN*\nfrom your foreign keys.',
    bullets: ['Reads SQL the way the server does', 'Knows every schema, table and alias', 'Ranks what can legally come next'],
    clip: 'completion',
  },
  {
    type: 'clip',
    bars: 4,
    energy: 'drop',
    kicker: 'Query plans',
    headline: 'EXPLAIN ANALYZE,\n*heat-mapped*.',
    bullets: ['A tree colored by time, rows, cost or buffers', 'Flags bad estimates and disk spills', 'Writes are always rolled back'],
    clip: 'explain',
  },
  {
    type: 'clip',
    bars: 4,
    energy: 'groove',
    kicker: 'Safe mode',
    headline: 'Edit the rows.\nReview the SQL.\nCommit *once*.',
    bullets: ['Every change in one transaction', 'Conflicts checked before commit'],
    clip: 'safeMode',
  },
  {
    type: 'showcase',
    bars: 3,
    energy: 'groove',
    kicker: 'When production is slow',
    headline: 'Who holds *the lock*?\nAnswered.',
    bullets: ['Who blocks whom, as a tree', 'Slow queries from pg_stat_statements', 'Cancel a query or end a session'],
    shots: [
      { src: 'shot:activity-window-blocking.dark', focus: { x: 440, y: 150, zoom: 1.25 } },
      { src: 'shot:slow-queries-window.light', focus: { x: 454, y: 160, zoom: 1.3 } },
    ],
  },
  {
    type: 'statement',
    bars: 2,
    energy: 'break',
    icon: 'shield',
    headline: 'Talks to your database.\n*Nothing else.*',
    chips: ['No telemetry', 'No update checks', 'No crash uploads', 'No AI features', 'Passwords in the OS keychain'],
  },
  {
    type: 'outro',
    bars: 4,
    energy: 'outro',
    headline: '*Free* on Windows, macOS and Linux.',
    platforms: [
      { name: 'Windows', icon: 'monitor', note: 'Microsoft Store' },
      { name: 'macOS', icon: 'laptop', note: 'beta' },
      { name: 'Linux', icon: 'terminal', note: 'beta' },
    ],
    logo: 'design/logo.svg',
  },
]

export function secondsPerBar(cfg = config) {
  return (60 / cfg.bpm) * cfg.beatsPerBar
}

/** Scenes with absolute start and end times in seconds. */
export function timeline(cfg = config, list = scenes) {
  const barSeconds = secondsPerBar(cfg)
  let start = 0
  let startBar = 0
  return list.map((scene, index) => {
    const duration = scene.bars * barSeconds
    const entry = { ...scene, index, start, duration, end: start + duration, startBar }
    start += duration
    startBar += scene.bars
    return entry
  })
}

export function totalSeconds(cfg = config, list = scenes) {
  return list.reduce((sum, scene) => sum + scene.bars, 0) * secondsPerBar(cfg)
}
