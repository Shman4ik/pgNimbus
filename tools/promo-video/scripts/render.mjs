#!/usr/bin/env node
/**
 * Renders the composition frame by frame with headless Chromium and encodes it with ffmpeg.
 * Adapted from jamesmontemagno/app-fire-calculator tools/hype-video (MIT, Copyright (c) 2025
 * James Montemagno).
 *
 * Usage:
 *   node scripts/render.mjs                      # full video -> config.output
 *   node scripts/render.mjs --variant neon       # another look and soundtrack (variants.mjs)
 *   node scripts/render.mjs --out out/test.mp4   # custom output path (relative to the current directory)
 *   node scripts/render.mjs --from 10 --to 20    # render a section (seconds)
 *   node scripts/render.mjs --stills 2,9,30      # PNG stills into build/stills/ for quick review
 *   node scripts/render.mjs --fps 60 --crf 18    # override frame rate / quality
 *   node scripts/render.mjs --no-audio           # skip the soundtrack
 */
import { spawn, spawnSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import { mkdir } from 'node:fs/promises'
import { dirname, isAbsolute, join, resolve } from 'node:path'
import { chromium } from 'playwright'
import { config, totalSeconds } from '../storyboard.mjs'
import { variantFromArgs } from '../variants.mjs'
import { repoRoot, startStaticServer, toolDir } from './static-server.mjs'

function option(name, fallback) {
  const index = process.argv.indexOf(`--${name}`)
  return index >= 0 ? process.argv[index + 1] : fallback
}
const flag = (name) => process.argv.includes(`--${name}`)

const look = variantFromArgs()
const fps = Number(option('fps', config.fps))
const crf = String(option('crf', 24))
const duration = totalSeconds()
const from = Math.max(0, Number(option('from', 0)))
const to = Math.min(duration, Number(option('to', duration)))
const stills = option('stills', null)
// --out is relative to the current directory; the storyboard default is relative to the repo root.
const outArg = option('out', null)
const output = config.output.replace('{variant}', look.name)
const outFile = outArg ? resolve(outArg) : isAbsolute(output) ? output : resolve(repoRoot, output)
const musicFile = join(toolDir, 'build', `music-${look.name}.wav`)
const withAudio = !flag('no-audio') && !stills

if (!stills && spawnSync('ffmpeg', ['-version']).status !== 0) {
  console.error('ffmpeg was not found on PATH. Install it (winget install ffmpeg, brew install ffmpeg, apt install ffmpeg).')
  process.exit(1)
}
if (withAudio && !existsSync(musicFile)) {
  console.error(`build/music-${look.name}.wav is missing. Run \`npm run music -- --variant ${look.name}\` first (or pass --no-audio).`)
  process.exit(1)
}

const server = await startStaticServer(repoRoot)
const browser = await chromium.launch()
const page = await browser.newPage({ viewport: { width: config.width, height: config.height }, deviceScaleFactor: 1 })
page.on('console', (message) => {
  if (message.type() === 'error') console.error(`[page] ${message.text()}`)
})
page.on('pageerror', (error) => console.error(`[page] ${error.message}`))

try {
  await page.goto(`${server.url}/tools/promo-video/composition/index.html?variant=${look.name}`, { waitUntil: 'load' })
  await page.evaluate(() => window.__video.ready)

  if (stills) {
    const dir = join(toolDir, 'build', 'stills', look.name)
    await mkdir(dir, { recursive: true })
    for (const t of stills.split(',').map(Number)) {
      await page.evaluate((time) => window.__video.seek(time), t)
      const file = join(dir, `still-${t.toFixed(2)}s.png`)
      await page.screenshot({ path: file })
      console.log(`still ${t}s -> ${file}`)
    }
  } else {
    await mkdir(dirname(outFile), { recursive: true })
    const length = to - from
    const ffmpegArgs = ['-y', '-loglevel', 'error', '-f', 'image2pipe', '-framerate', String(fps), '-c:v', 'mjpeg', '-i', '-']
    if (withAudio) ffmpegArgs.push('-ss', String(from), '-t', String(length), '-i', musicFile)
    ffmpegArgs.push('-c:v', 'libx264', '-preset', 'slow', '-crf', crf, '-pix_fmt', 'yuv420p', '-r', String(fps))
    // -14 LUFS, the loudness YouTube plays at; the synth's own mix comes out near -11.
    if (withAudio) ffmpegArgs.push('-af', 'loudnorm=I=-14:TP=-1.5:LRA=11', '-ar', '48000', '-c:a', 'aac', '-b:a', '192k')
    ffmpegArgs.push('-t', String(length), '-movflags', '+faststart', outFile)
    const ffmpeg = spawn('ffmpeg', ffmpegArgs, { stdio: ['pipe', 'inherit', 'inherit'] })
    const finished = new Promise((resolvePromise, reject) => {
      ffmpeg.on('error', reject)
      ffmpeg.on('close', (code) => (code === 0 ? resolvePromise() : reject(new Error(`ffmpeg exited with ${code}`))))
    })

    const frames = Math.round(length * fps)
    const startedAt = Date.now()
    for (let frame = 0; frame < frames; frame++) {
      const t = from + frame / fps
      await page.evaluate((time) => window.__video.seek(time), t)
      const jpeg = await page.screenshot({ type: 'jpeg', quality: 95 })
      if (!ffmpeg.stdin.write(jpeg)) await new Promise((r) => ffmpeg.stdin.once('drain', r))
      if (frame % fps === 0 || frame === frames - 1) {
        const elapsed = (Date.now() - startedAt) / 1000
        process.stdout.write(`\rframe ${frame + 1}/${frames}  (${t.toFixed(1)}s)  ${elapsed.toFixed(0)}s elapsed   `)
      }
    }
    ffmpeg.stdin.end()
    await finished
    console.log(`\nrendered ${length.toFixed(1)}s at ${fps} fps -> ${outFile}`)
  }
} finally {
  await browser.close()
  await server.close()
}
