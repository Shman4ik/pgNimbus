#!/usr/bin/env node
/**
 * Renders the storyboard's `shot:` stills with the screenshot harness (tools/Screenshot), which
 * draws the real windows from fixture data with no display and no database. The PNGs land in
 * build/shots/<scenario>.<theme>.png, the name the harness gives them.
 *
 * Needs the .NET SDK the repository builds with. Usage: node scripts/render-shots.mjs
 */
import { spawnSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import { join } from 'node:path'
import { scenes } from '../storyboard.mjs'
import { repoRoot, toolDir } from './static-server.mjs'

const wanted = [...new Set(scenes.flatMap((scene) => scene.shots ?? []).map((shot) => shot.src).filter((src) => src.startsWith('shot:')))]
const outDir = join(toolDir, 'build', 'shots')

// The harness takes a scenario-name substring and renders both themes of every match.
const scenarios = [...new Set(wanted.map((src) => src.slice(5).replace(/\.(light|dark)$/, '')))]
for (const scenario of scenarios) {
  console.log(`rendering ${scenario}`)
  const run = spawnSync('dotnet', ['run', '--project', 'tools/Screenshot', '--', outDir, scenario], { cwd: repoRoot, stdio: ['ignore', 'ignore', 'inherit'] })
  if (run.status !== 0) process.exit(run.status ?? 1)
}

const missing = wanted.filter((src) => !existsSync(join(outDir, `${src.slice(5)}.png`)))
if (missing.length) {
  console.error(`The harness rendered no ${missing.join(', ')}. Check the scenario names in tools/Screenshot/Scenarios.cs.`)
  process.exit(1)
}
console.log(`shots: ${wanted.length} -> ${outDir}`)
