// The composition engine: builds every scene from the storyboard and renders any time `t` on
// demand. A frame is a function of time alone, so scripts/render.mjs can step frame by frame.
//
// Adapted from jamesmontemagno/app-fire-calculator tools/hype-video (MIT, Copyright (c) 2025
// James Montemagno). pgNimbus's additions: clip frames, and `seek` resolving only once every
// image the frame shows has decoded.
import { clips, config, secondsPerBar, timeline, totalSeconds } from '../storyboard.mjs'
import { variant } from '../variants.mjs'
import { builders, clamp } from './scenes.js'

const params = new URLSearchParams(location.search)
const preview = params.has('preview')
const look = variant(params.get('variant') ?? undefined)
const theme = look.theme
for (const [name, value] of Object.entries(theme.css)) document.documentElement.style.setProperty(name, value)
document.documentElement.style.setProperty('--glow', theme.glow)
const stage = document.getElementById('stage')
const scenesRoot = document.getElementById('scenes')
const canvas = document.getElementById('background')
const flash = document.getElementById('flash')
const fade = document.getElementById('fade')
const paint = canvas.getContext('2d')

const beat = 60 / config.bpm
const barSeconds = secondsPerBar()
const duration = totalSeconds()
const scenes = timeline()

// ---------- Assets ----------

/** Storyboard paths are relative to the repository root; `shot:` ones are harness renders. */
const fromRepo = (path) =>
  path.startsWith('shot:')
    ? new URL(`../build/shots/${path.slice(5)}.png`, import.meta.url).href
    : new URL(`../../../${path}`, import.meta.url).href
const clipFrameUrl = (name, index) => new URL(`../build/clips/${name}/${String(index + 1).padStart(4, '0')}.jpg`, import.meta.url).href

const images = new Map()
async function loadImages() {
  const sources = new Set()
  for (const scene of scenes) {
    if (scene.logo) sources.add(scene.logo)
    for (const shot of scene.shots ?? []) sources.add(shot.src)
  }
  await Promise.all(
    [...sources].map(async (src) => {
      const image = new Image()
      image.src = fromRepo(src)
      try {
        await image.decode()
      } catch {
        const hint = src.startsWith('shot:') ? ' Run `npm run shots` first.' : ''
        throw new Error(`Could not load image "${src}" (${image.src}).${hint}`)
      }
      images.set(src, { src: image.src, width: image.naturalWidth, height: image.naturalHeight })
    }),
  )
}

let clipFrames = {}
async function loadClipManifest() {
  const used = scenes.filter((scene) => scene.clip).map((scene) => scene.clip)
  if (!used.length) return
  const response = await fetch(new URL('../build/clips/manifest.json', import.meta.url))
  if (!response.ok) throw new Error('build/clips/manifest.json is missing. Run `npm run clips` first.')
  clipFrames = await response.json()
  for (const name of used) {
    if (!clips.list[name]) throw new Error(`Scene uses clip "${name}", which storyboard.mjs does not define.`)
    if (!clipFrames[name]) throw new Error(`Clip "${name}" has no frames. Run \`npm run clips\`.`)
  }
}

/** Decodes still pending for the frame being drawn; `seek` waits for them. */
let pending = []

// ---------- Background ----------

function mulberry32(seed) {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) >>> 0
    let t = a
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}
const random = mulberry32(config.seed)
// Rising "cells": small rounded squares, like rows streaming past.
const cells = Array.from({ length: 80 }, () => ({
  x: random() * config.width,
  phase: random(),
  speed: 0.04 + random() * 0.08,
  size: 2 + random() ** 2 * 5,
  sway: 10 + random() * 30,
  flicker: random() * Math.PI * 2,
  hue: theme.canvas.cells[random() < 0.65 ? 0 : 1],
}))

// Where the three glows drift; their colours come from the variant.
const orbPaths = [
  (t) => ({ x: 0.22 + 0.08 * Math.sin(t * 0.31), y: 0.3 + 0.1 * Math.cos(t * 0.23), r: 0.55 }),
  (t) => ({ x: 0.8 + 0.07 * Math.cos(t * 0.27), y: 0.72 + 0.08 * Math.sin(t * 0.35), r: 0.6 }),
  (t) => ({ x: 0.6 + 0.1 * Math.sin(t * 0.19 + 1), y: 0.12 + 0.06 * Math.sin(t * 0.41), r: 0.42 }),
]

/** A synthwave sun sinking into the horizon, cut by bands that drift down. */
function drawSun(t, pulse, horizon, sun, dim) {
  const { width } = config
  const radius = 260
  const cx = width / 2
  const cy = horizon + 100
  paint.save()
  paint.beginPath()
  paint.rect(0, 0, width, horizon)
  paint.clip()
  const fill = paint.createLinearGradient(0, cy - radius, 0, horizon)
  fill.addColorStop(0, sun.top)
  fill.addColorStop(1, sun.bottom)
  paint.globalAlpha = sun.alpha * dim * (0.9 + 0.1 * pulse)
  paint.fillStyle = fill
  paint.beginPath()
  paint.arc(cx, cy, radius, 0, Math.PI * 2)
  paint.fill()
  // Bands: gaps that grow toward the horizon, scrolling down.
  paint.globalCompositeOperation = 'destination-out'
  paint.globalAlpha = 1
  for (let i = 0; i < 7; i++) {
    const p = (i + ((t * 0.25) % 1)) / 7
    const top = cy - radius
    const y = top + (horizon - top) * (0.3 + 0.7 * p)
    paint.fillRect(cx - radius, y, radius * 2, 2 + 10 * p)
  }
  paint.restore()
}

function drawBackground(t, pulse, energy, centered) {
  const { width, height } = config
  const look = theme.canvas
  const base = paint.createLinearGradient(0, 0, 0, height)
  base.addColorStop(0, look.top)
  base.addColorStop(1, look.bottom)
  paint.globalCompositeOperation = 'source-over'
  paint.fillStyle = base
  paint.fillRect(0, 0, width, height)

  const horizon = height * 0.62
  // Behind centred text the sun would sit under the words, so it fades there.
  if (look.sun) drawSun(t, pulse, horizon, look.sun, centered ? 0.3 : 1)

  const calm = energy === 'break' ? 0.6 : 1
  const orbs = look.orbs.map((orb, i) => ({
    ...orbPaths[i % orbPaths.length](t),
    color: orb.color,
    a: (orb.alpha + (orb.pulse ?? 0) * pulse) * (orb.pulse ? calm : 1),
  }))
  paint.globalCompositeOperation = look.blend
  for (const orb of orbs) {
    const gradient = paint.createRadialGradient(orb.x * width, orb.y * height, 0, orb.x * width, orb.y * height, orb.r * width)
    gradient.addColorStop(0, `rgba(${orb.color},${orb.a})`)
    gradient.addColorStop(1, `rgba(${orb.color},0)`)
    paint.fillStyle = gradient
    paint.fillRect(0, 0, width, height)
  }

  // A perspective grid on the floor: a result grid receding into the distance.
  paint.strokeStyle = `rgba(${look.grid},${look.gridAlpha * (1 + 0.6 * pulse)})`
  paint.lineWidth = look.gridAlpha > 0.1 ? 1.5 : 1
  for (let i = -12; i <= 12; i++) {
    paint.beginPath()
    paint.moveTo(width / 2 + i * 40, horizon)
    paint.lineTo(width / 2 + i * 260, height)
    paint.stroke()
  }
  for (let i = 0; i < 8; i++) {
    const p = ((i + ((t * 0.35) % 1)) / 8) ** 2
    const y = horizon + (height - horizon) * p
    paint.beginPath()
    paint.moveTo(0, y)
    paint.lineTo(width, y)
    paint.stroke()
  }

  for (const c of cells) {
    const travel = (c.phase + t * c.speed) % 1
    const y = height + 40 - travel * (height + 80)
    const x = c.x + Math.sin(t * 0.7 + c.flicker) * c.sway
    const alpha = (0.25 + 0.4 * Math.sin(t * 4 + c.flicker) ** 2) * Math.sin(travel * Math.PI) * (0.7 + 0.5 * pulse) * look.cellAlpha
    const s = c.size * (1 + 0.3 * pulse)
    paint.fillStyle = `rgba(${c.hue},${alpha})`
    paint.beginPath()
    paint.roundRect(x - s, y - s, s * 2, s * 2, s * 0.4)
    paint.fill()
  }
  paint.globalCompositeOperation = 'source-over'
}

// ---------- Scenes ----------

let built = []
function build() {
  const ctx = {
    config,
    theme,
    beat,
    barSeconds,
    image(src) {
      const image = images.get(src)
      if (!image) throw new Error(`Image "${src}" was not preloaded.`)
      return image
    },
    clip(name) {
      return { frames: clipFrames[name], source: clips.crop, focus: clips.list[name].focus }
    },
    setFrame(img, name, index) {
      img.src = clipFrameUrl(name, index)
      pending.push(img.decode().catch(() => console.error(`Clip frame ${name} #${index + 1} failed to load.`)))
    },
  }
  built = scenes.map((scene) => {
    const builder = builders[scene.type]
    if (!builder) throw new Error(`Unknown scene type "${scene.type}" (scene ${scene.index + 1}).`)
    const el = document.createElement('section')
    el.className = `scene scene-${scene.type}`
    scenesRoot.append(el)
    const update = builder(el, scene, ctx, scene.duration)
    return { scene, el, update }
  })
}

const drumsOn = (scene, local) =>
  scene.energy === 'drop' || scene.energy === 'groove' || (scene.energy === 'outro' && local < scene.duration - barSeconds)

function render(t) {
  const time = clamp(t, 0, duration - 1e-6)
  const current = built.find(({ scene }) => time >= scene.start && time < scene.end) ?? built[built.length - 1]
  const local = time - current.scene.start
  const pulse = drumsOn(current.scene, local) ? config.beatPulse * Math.exp(-(time % beat) * 9) : 0
  const g = { t: time, pulse }

  drawBackground(time, pulse, current.scene.energy, !['clip', 'showcase'].includes(current.scene.type))
  for (const item of built) {
    const active = item === current
    item.el.classList.toggle('active', active)
    if (!active) continue
    item.update(local, g)
    // A quick whip between scenes: slide out at the end, slide in at the start.
    const out = clamp((local - (item.scene.duration - 0.15)) / 0.15) ** 2
    const inn = 1 - clamp(local / 0.18)
    const shift = -90 * out + 90 * inn ** 2
    const blur = 8 * out + 8 * inn ** 2
    item.el.style.transform = `translateX(${shift}px)`
    item.el.style.filter = blur > 0.05 ? `blur(${blur}px)` : ''
  }

  const big = current.scene.energy === 'drop' || current.scene.energy === 'outro'
  flash.style.opacity = big && current.scene.index > 0 ? 0.85 * Math.exp(-local * 7) : 0.1 * Math.exp(-local * 12)
  stage.style.transform = `${stageScale ? `scale(${stageScale})` : ''} scale(${1 + 0.006 * pulse})`
  fade.style.opacity = String(Math.max(clamp(1 - time / 0.4), clamp((time - (duration - 0.7)) / 0.7)))
}

async function seek(t) {
  pending = []
  render(t)
  await Promise.all(pending)
}

// ---------- Preview UI ----------

let stageScale = 0
function fitStage() {
  if (!preview) return
  stageScale = Math.min(innerWidth / config.width, (innerHeight - 70) / config.height)
}

function setupPreview() {
  const controls = document.getElementById('controls')
  const play = document.getElementById('play')
  const scrub = document.getElementById('scrub')
  const output = document.getElementById('time')
  const audio = document.getElementById('music')
  controls.hidden = false
  scrub.max = String(duration)
  audio.src = new URL(`../build/music-${look.name}.wav`, import.meta.url).href
  fitStage()
  addEventListener('resize', () => {
    fitStage()
    render(Number(scrub.value))
  })
  let playing = false
  let startedAt = 0
  let offset = Number(params.get('t') ?? 0)
  const show = (t) => {
    scrub.value = String(t)
    output.textContent = `${t.toFixed(2)}s`
    render(t)
  }
  const tick = () => {
    if (!playing) return
    const t = audio.duration && !audio.paused ? audio.currentTime : offset + (performance.now() - startedAt) / 1000
    if (t >= duration) {
      playing = false
      play.textContent = 'Play'
      show(duration)
      return
    }
    show(t)
    requestAnimationFrame(tick)
  }
  const toggle = () => {
    playing = !playing
    play.textContent = playing ? 'Pause' : 'Play'
    if (playing) {
      offset = Number(scrub.value) >= duration ? 0 : Number(scrub.value)
      startedAt = performance.now()
      audio.currentTime = offset
      audio.play().catch(() => {
        // No music yet (npm run music): play silently.
      })
      requestAnimationFrame(tick)
    } else {
      audio.pause()
    }
  }
  play.addEventListener('click', toggle)
  addEventListener('keydown', (event) => {
    if (event.code === 'Space') {
      event.preventDefault()
      toggle()
    }
  })
  scrub.addEventListener('input', () => {
    audio.currentTime = Number(scrub.value)
    offset = Number(scrub.value)
    startedAt = performance.now()
    show(Number(scrub.value))
  })
  show(offset)
}

// ---------- Boot ----------

const ready = (async () => {
  await document.fonts.ready
  await Promise.all([
    ...['500', '600', '800', '900'].map((weight) => document.fonts.load(`${weight} 64px Inter`)),
    document.fonts.load('600 64px "JetBrains Mono"'),
  ])
  await Promise.all([loadImages(), loadClipManifest()])
  build()
  await seek(Number(params.get('t') ?? 0))
  if (preview) setupPreview()
})()

ready.catch((error) => {
  console.error(error)
  const message = document.createElement('pre')
  message.style.cssText = 'position:fixed;inset:auto 16px 80px;padding:16px;background:#400;color:#fff;white-space:pre-wrap;z-index:10'
  message.textContent = String(error?.message ?? error)
  document.body.append(message)
})

window.__video = { config, variant: look.name, duration, ready, seek }
