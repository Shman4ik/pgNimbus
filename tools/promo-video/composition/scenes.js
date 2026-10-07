// Scene builders. Each builder creates its DOM once and returns `update(local, g)`, which sets
// every animated style from `local` (seconds since the scene started) alone, so any frame can
// be rendered in any order.
//
// Adapted from jamesmontemagno/app-fire-calculator tools/hype-video (MIT, Copyright (c) 2025
// James Montemagno): the helpers, the pillars scene and the copy block are close to the
// original; the intro, the clip window and the outro are pgNimbus's own.
import { icon } from './icons.js'

// ---------- Animation helpers ----------

export const clamp = (x, min = 0, max = 1) => Math.min(max, Math.max(min, x))
export const easeOutCubic = (x) => 1 - (1 - x) ** 3
export const easeInCubic = (x) => x ** 3
export const easeInOutCubic = (x) => (x < 0.5 ? 4 * x ** 3 : 1 - (-2 * x + 2) ** 3 / 2)
export const easeOutBack = (x) => {
  const c1 = 1.70158
  const c3 = c1 + 1
  return 1 + c3 * (x - 1) ** 3 + c1 * (x - 1) ** 2
}
/** Eased 0..1 progress of a `length`-second tween beginning at `start`. */
export const tween = (local, start, length, ease = easeOutCubic) => ease(clamp((local - start) / length))

/** Standard entrance: fade, rise, optional scale and blur, driven by eased progress `p`. */
function reveal(el, p, { x = 0, y = 50, scale = 1, blur = 10, rotate = 0 } = {}) {
  const inv = 1 - p
  el.style.opacity = clamp(p * 1.4)
  el.style.transform = `translate(${x * inv}px, ${y * inv}px) scale(${1 + (scale - 1) * inv}) rotate(${rotate * inv}deg)`
  el.style.filter = blur ? `blur(${blur * inv}px)` : ''
}

// ---------- Markup helpers ----------

const escapeHtml = (text) =>
  String(text).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c])

/** Escapes text, then turns `*word*` into the brand gradient and `` `x` `` into code. */
function rich(text) {
  return escapeHtml(text)
    .replace(/`(.+?)`/g, '<code>$1</code>')
    .replace(/\*(.+?)\*/g, '<span class="hot">$1</span>')
}

function html(markup) {
  const template = document.createElement('template')
  template.innerHTML = markup.trim()
  return template.content.firstElementChild
}

/** Wraps each headline line so the lines can enter one after another. */
function headlineLines(text) {
  return String(text)
    .split('\n')
    .map((line) => `<span class="anim line" style="display:block">${rich(line)}</span>`)
    .join('')
}

// ---------- The app window ----------

const WINDOW = { width: 1160, height: 652, cx: 1290, cy: 548 }

/**
 * A window showing a clip (`clip`) or a still (`src`). Returns its element and `show(local)`,
 * which picks the clip frame and applies the focus camera for `local` seconds into the clip.
 */
function createWindow(ctx, { clip, src, focus: still }, size = WINDOW) {
  const el = html(`<div class="window anim"><img alt=""></div>`)
  el.style.width = `${size.width}px`
  el.style.height = `${size.height}px`
  const img = el.querySelector('img')
  let frames = 1
  let source
  let focus
  if (clip) {
    ;({ frames, source, focus } = ctx.clip(clip))
  } else {
    const image = ctx.image(src)
    img.src = image.src
    source = { width: image.width, height: image.height }
    focus = [{ at: 0, x: source.width / 2, y: source.height / 2, zoom: 1, ...still }]
  }
  img.style.width = `${source.width}px`
  img.style.height = `${source.height}px`
  let shown = -1
  return {
    el,
    show(local) {
      const index = clamp(Math.floor(local * ctx.config.fps), 0, frames - 1)
      if (clip && index !== shown) {
        shown = index
        ctx.setFrame(img, clip, index)
      }
      const cam = camera(focus, local)
      const fit = size.width / source.width
      const scale = fit * cam.zoom
      const tx = clamp(size.width / 2 - cam.x * scale, size.width - source.width * scale, 0)
      const ty = clamp(size.height / 2 - cam.y * scale, size.height - source.height * scale, 0)
      img.style.transform = `translate(${tx}px, ${ty}px) scale(${scale})`
    },
  }
}

/** Interpolates the storyboard's focus keyframes, easing in and out between each pair. */
function camera(keys, t) {
  if (!keys?.length) return { x: 800, y: 450, zoom: 1 }
  if (t <= keys[0].at) return keys[0]
  for (let i = 1; i < keys.length; i++) {
    const a = keys[i - 1]
    const b = keys[i]
    if (t <= b.at) {
      const p = easeInOutCubic((t - a.at) / (b.at - a.at || 1))
      return { x: a.x + (b.x - a.x) * p, y: a.y + (b.y - a.y) * p, zoom: a.zoom + (b.zoom - a.zoom) * p }
    }
  }
  return keys[keys.length - 1]
}

/** Slides a window in from the right with a slight turn that settles flat, then a slow push. */
function placeWindow(el, local, enterAt = 0.05, size = WINDOW, leave = 0) {
  const p = tween(local, enterAt, 0.8)
  const x = size.cx - size.width / 2 + 160 * (1 - p) - 200 * leave
  const y = size.cy - size.height / 2 + 30 * (1 - p)
  el.style.opacity = Math.min(clamp(p * 1.6), 1 - leave)
  el.style.transform = `translate(${x}px, ${y}px) perspective(2400px) rotateY(${-10 * (1 - p) + 6 * leave}deg) scale(${(0.92 + 0.08 * p) * (1 + local * 0.004)})`
}

// ---------- The copy block (kicker, headline, bullets) ----------

function createCopy(scene, ctx) {
  const copy = html(`<div class="copy">
    ${scene.kicker ? `<div><div class="kicker anim">${rich(scene.kicker)}</div></div>` : ''}
    <h2 class="headline">${headlineLines(scene.headline)}</h2>
    ${
      scene.bullets?.length
        ? `<ul class="bullets">${scene.bullets
            .map((b) => `<li class="anim"><span class="check">${icon('check')}</span><span>${rich(b)}</span></li>`)
            .join('')}</ul>`
        : ''
    }</div>`)
  const kicker = copy.querySelector('.kicker')
  const lines = [...copy.querySelectorAll('.line')]
  const bullets = [...copy.querySelectorAll('.bullets li')]
  return {
    el: copy,
    update(local) {
      if (kicker) reveal(kicker, tween(local, 0, 0.45), { x: -40, y: 0, blur: 0 })
      lines.forEach((line, i) => reveal(line, tween(local, 0.08 + i * 0.16, 0.6), { y: 70, blur: 14 }))
      bullets.forEach((li, i) => reveal(li, tween(local, 0.9 + i * ctx.beat, 0.45, easeOutBack), { x: -50, y: 0, blur: 0 }))
    },
  }
}

// ---------- Scene types ----------

function intro(root, scene, ctx) {
  const lines = scene.hook.split('\n')
  const el = html(`<div>
    <div class="center hook-block">
      <div class="intro-hook">${lines
        .map((line) => `<span class="line">${line.split(' ').map((w) => `<span class="word anim">${rich(w)}</span>`).join(' ')}</span>`)
        .join('')}</div>
      <div class="stopwatch anim">${icon('timer')}<span class="readout">0.00 s</span></div>
      <div class="intro-caption anim">${rich(scene.caption)}</div>
    </div>
    <div class="center brand">
      <img class="logo anim" alt="" src="${ctx.image(scene.logo).src}">
      <div class="brand-name anim">${rich(scene.name)}</div>
      <div class="brand-tagline anim">${rich(scene.tagline)}</div>
    </div></div>`)
  root.append(el)
  const hookBlock = el.querySelector('.hook-block')
  const words = [...el.querySelectorAll('.intro-hook .word')]
  const watch = el.querySelector('.stopwatch')
  const readout = el.querySelector('.readout')
  const caption = el.querySelector('.intro-caption')
  const brand = el.querySelector('.brand')
  const logo = el.querySelector('.logo')
  const name = el.querySelector('.brand-name')
  const tagline = el.querySelector('.brand-tagline')
  const bar = ctx.barSeconds
  const target = ctx.config.startupSeconds

  return (local) => {
    words.forEach((w, i) => reveal(w, tween(local, 0.25 + i * 0.2, 0.55), { y: 80, blur: 18 }))
    // The stopwatch runs for exactly the startup time, in real time, then stamps.
    const runFrom = bar * 1.25
    reveal(watch, tween(local, runFrom - 0.45, 0.4), { y: 40, scale: 0.85, blur: 8 })
    const elapsed = clamp(local - runFrom, 0, target)
    const done = local >= runFrom + target
    readout.textContent = done ? `~${target} s` : `${elapsed.toFixed(2)} s`
    readout.classList.toggle('hot', done)
    const stamp = local >= runFrom + target ? Math.exp(-(local - runFrom - target) * 10) : 0
    watch.style.transform += ` scale(${1 + 0.12 * stamp})`
    reveal(caption, tween(local, runFrom + target + 0.15, 0.5), { y: 20, blur: 6 })
    const out = tween(local, bar * 2 - 0.35, 0.35, easeInCubic)
    hookBlock.style.opacity = 1 - out
    hookBlock.style.transform = `scale(${1 - 0.15 * out})`
    hookBlock.style.filter = `blur(${out * 16}px)`

    const brandIn = bar * 2
    brand.style.visibility = local >= brandIn - 0.01 ? 'visible' : 'hidden'
    reveal(logo, tween(local, brandIn, 0.7, easeOutBack), { y: 0, scale: 0.3, blur: 0, rotate: -40 })
    reveal(name, tween(local, brandIn + 0.3, 0.6), { y: 60, blur: 14 })
    reveal(tagline, tween(local, brandIn + 0.6, 0.6), { y: 30, blur: 8 })
    // The riser bar: push in, then blow out into the drop.
    const push = tween(local, bar * 3, bar, easeInCubic)
    const blow = tween(local, bar * 4 - 0.3, 0.3, easeInCubic)
    brand.style.transform = `scale(${1 + 0.12 * push + 0.8 * blow})`
    brand.style.opacity = 1 - blow
    brand.style.filter = `brightness(${1 + 0.5 * push})`
  }
}

function pillars(root, scene, ctx) {
  const wordTime = (scene.beatsPerWord ?? 2) * ctx.beat
  const el = html(`<div>
    ${scene.words
      .map((w) => `<div class="pillar-word"><span class="anim pop">${icon(w.icon)}</span><span class="text anim">${rich(w.text)}</span></div>`)
      .join('')}
    <div class="pillar-grid">
      <div class="kicker anim">${rich(scene.summary ?? '')}</div>
      <div class="row">${scene.words.map((w) => `<span class="chip anim">${icon(w.icon)}${rich(w.text)}</span>`).join('')}</div>
    </div></div>`)
  root.append(el)
  const words = [...el.querySelectorAll('.pillar-word')]
  const grid = el.querySelector('.pillar-grid')
  const summary = grid.querySelector('.kicker')
  const chips = [...grid.querySelectorAll('.chip')]
  const gridStart = words.length * wordTime

  return (local) => {
    words.forEach((word, i) => {
      const start = i * wordTime
      const visible = local >= start && local < start + wordTime
      word.style.visibility = visible ? 'visible' : 'hidden'
      if (!visible) return
      const d = local - start
      const slam = tween(d, 0, 0.18)
      const shake = Math.exp(-d * 14) * Math.sin(d * 90) * 14
      const text = word.querySelector('.text')
      text.style.opacity = 1
      text.style.transform = `translate(${shake}px, 0) scale(${1.6 - 0.6 * slam + 0.04 * (d / wordTime)})`
      const pop = word.querySelector('.pop')
      reveal(pop, tween(d, 0.05, 0.4, easeOutBack), { y: 0, scale: 0, blur: 0, rotate: -25 })
      text.classList.toggle('hot', i % 2 === 1)
    })
    grid.style.visibility = local >= gridStart ? 'visible' : 'hidden'
    reveal(summary, tween(local, gridStart, 0.4), { y: -30, blur: 0 })
    chips.forEach((chip, i) => reveal(chip, tween(local, gridStart + 0.05 + i * 0.08, 0.4, easeOutBack), { y: 60, scale: 0.6, blur: 0 }))
  }
}

/** Copy on the left, the app window playing a clip on the right. */
function clip(root, scene, ctx) {
  const el = html(`<div class="split"><div class="shots"></div></div>`)
  const copy = createCopy(scene, ctx)
  el.prepend(copy.el)
  root.append(el)
  const win = createWindow(ctx, { clip: scene.clip })
  el.querySelector('.shots').append(win.el)
  const playFrom = 0.45
  return (local) => {
    copy.update(local)
    placeWindow(win.el, local)
    win.show(Math.max(0, local - playFrom))
  }
}

/** Copy on the left, still screenshots taking turns in the window. */
function showcase(root, scene, ctx, duration) {
  const el = html(`<div class="split"><div class="shots"></div></div>`)
  const copy = createCopy(scene, ctx)
  el.prepend(copy.el)
  root.append(el)
  const container = el.querySelector('.shots')
  const windows = scene.shots.map((shot) => {
    const image = ctx.image(shot.src)
    const height = WINDOW.width * (image.height / image.width)
    const size = { ...WINDOW, height }
    const win = createWindow(ctx, shot, size)
    container.append(win.el)
    return { ...win, size }
  })
  const slice = duration / windows.length
  return (local) => {
    copy.update(local)
    windows.forEach((win, i) => {
      const start = i * slice
      const leave = i === windows.length - 1 ? 0 : tween(local, start + slice - 0.25, 0.5, easeInCubic)
      const visible = local >= start - 0.25 && leave < 1
      win.el.style.visibility = visible ? 'visible' : 'hidden'
      win.el.style.zIndex = String(10 + i)
      if (!visible) return
      placeWindow(win.el, local - start + (i === 0 ? 0 : 0.25), i === 0 ? 0.05 : 0, win.size, leave)
      win.show(0)
    })
  }
}

function statement(root, scene, ctx) {
  const el = html(`<div class="center statement">
    <div class="badge anim">${icon(scene.icon ?? 'shield')}</div>
    <h2 class="headline">${headlineLines(scene.headline)}</h2>
    <div class="chip-row">${(scene.chips ?? []).map((c) => `<span class="chip anim">${icon('check')}${rich(c)}</span>`).join('')}</div>
  </div>`)
  root.append(el)
  const badge = el.querySelector('.badge')
  const lines = [...el.querySelectorAll('.line')]
  const chips = [...el.querySelectorAll('.chip')]
  return (local) => {
    reveal(badge, tween(local, 0, 0.5, easeOutBack), { y: -40, scale: 0.5, blur: 0 })
    const glow = local > 0.5 ? 34 * Math.exp(-(local - 0.5) * 3) : 0
    badge.style.filter = `drop-shadow(0 0 ${glow}px rgba(${ctx.theme.glow},0.9))`
    lines.forEach((line, i) => reveal(line, tween(local, 0.2 + i * 0.2, 0.7), { y: 60, blur: 16 }))
    chips.forEach((chip, i) => reveal(chip, tween(local, 0.9 + i * 0.18, 0.45, easeOutBack), { y: 40, scale: 0.7, blur: 0 }))
  }
}

function outro(root, scene, ctx) {
  const command = ctx.config.install
  const el = html(`<div class="center outro">
    <div class="lockup">
      <img class="logo anim" alt="" src="${ctx.image(scene.logo).src}">
      <span class="name anim">pgNimbus</span>
    </div>
    <h2 class="headline anim">${rich(scene.headline)}</h2>
    <div class="platforms">${scene.platforms
      .map((p) => `<span class="chip anim">${icon(p.icon)}${rich(p.name)}${p.note ? `<span class="note">${rich(p.note)}</span>` : ''}</span>`)
      .join('')}</div>
    <div class="install anim"><span class="prompt">&gt; </span><span class="typed"></span><span class="caret"></span></div>
    <div class="links">
      <span class="anim">${icon('globe')}${escapeHtml(ctx.config.url)}</span>
      <span class="anim">${icon('github')}${escapeHtml(ctx.config.repoUrl)}</span>
    </div>
  </div>`)
  root.append(el)
  const logo = el.querySelector('.logo')
  const name = el.querySelector('.lockup .name')
  const headline = el.querySelector('.headline')
  const chips = [...el.querySelectorAll('.platforms .chip')]
  const install = el.querySelector('.install')
  const typed = el.querySelector('.typed')
  const caret = el.querySelector('.caret')
  const links = [...el.querySelectorAll('.links span')]
  const beat = ctx.beat
  return (local, g) => {
    reveal(logo, tween(local, 0, 0.7, easeOutBack), { y: 0, scale: 0.2, blur: 0, rotate: -40 })
    reveal(name, tween(local, 0.15, 0.6), { x: -60, y: 0, blur: 14 })
    reveal(headline, tween(local, 0.45, 0.6), { y: 60, scale: 1.1, blur: 16 })
    chips.forEach((chip, i) => reveal(chip, tween(local, 0.9 + i * beat * 0.5, 0.45, easeOutBack), { y: 50, scale: 0.6, blur: 0 }))
    const typeFrom = 0.9 + chips.length * beat * 0.5 + 0.2
    reveal(install, tween(local, typeFrom - 0.3, 0.4), { y: 30, blur: 0 })
    const chars = Math.round(command.length * tween(local, typeFrom, 1.3, (x) => x))
    typed.textContent = command.slice(0, chars)
    caret.style.opacity = chars < command.length || Math.floor(local / beat) % 2 === 0 ? 1 : 0
    links.forEach((link, i) => reveal(link, tween(local, typeFrom + 1.4 + i * 0.25, 0.6), { y: 24, blur: 6 }))
    logo.style.boxShadow = `0 30px 90px var(--shadow), 0 0 ${80 + 60 * g.pulse}px rgba(${ctx.theme.glow},${0.35 + 0.25 * g.pulse})`
  }
}

export const builders = { intro, pillars, clip, showcase, statement, outro }
