#!/usr/bin/env node
/**
 * Cuts the storyboard's clips out of the Store trailer into JPEG frame sequences, one folder
 * per clip, at the video's frame rate. The composition shows frame N of a clip at time N/fps,
 * so a clip plays exactly the same in every render.
 *
 * Output: build/clips/<name>/0001.jpg … and build/clips/manifest.json ({ name: frameCount }).
 * Usage: node scripts/extract-clips.mjs [--only name,name]
 */
import { spawnSync } from 'node:child_process'
import { mkdir, readdir, rm, writeFile } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import { clips, config } from '../storyboard.mjs'
import { repoRoot, toolDir } from './static-server.mjs'

const onlyIndex = process.argv.indexOf('--only')
const only = onlyIndex >= 0 ? process.argv[onlyIndex + 1].split(',') : null

if (spawnSync('ffmpeg', ['-version']).status !== 0) {
  console.error('ffmpeg was not found on PATH (winget install ffmpeg, brew install ffmpeg, apt install ffmpeg).')
  process.exit(1)
}

const source = resolve(repoRoot, clips.source)
const { width, height, x, y } = clips.crop
const outRoot = join(toolDir, 'build', 'clips')
const manifest = {}

for (const [name, clip] of Object.entries(clips.list)) {
  const dir = join(outRoot, name)
  if (!only || only.includes(name)) {
    await rm(dir, { recursive: true, force: true })
    await mkdir(dir, { recursive: true })
    const inputs = []
    let graph = ''
    clip.segments.forEach(([from, to], i) => {
      inputs.push('-ss', String(from), '-t', String(to - from), '-i', source)
      graph += `[${i}:v]crop=${width}:${height}:${x}:${y},setpts=PTS-STARTPTS[s${i}];`
    })
    graph += clip.segments.map((_, i) => `[s${i}]`).join('')
    graph += `concat=n=${clip.segments.length}:v=1:a=0,setpts=PTS/${clip.speed ?? 1},fps=${config.fps}[v]`
    const run = spawnSync(
      'ffmpeg',
      ['-y', '-loglevel', 'error', ...inputs, '-filter_complex', graph, '-map', '[v]', '-q:v', '2', join(dir, '%04d.jpg')],
      { stdio: 'inherit' },
    )
    if (run.status !== 0) process.exit(run.status ?? 1)
  }
  manifest[name] = (await readdir(dir).catch(() => [])).filter((f) => f.endsWith('.jpg')).length
  console.log(`clip ${name}: ${manifest[name]} frames (${(manifest[name] / config.fps).toFixed(2)} s)`)
}

await writeFile(join(outRoot, 'manifest.json'), JSON.stringify(manifest, null, 2))
