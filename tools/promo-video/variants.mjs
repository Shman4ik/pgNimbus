/**
 * Looks and sounds the same storyboard can be rendered with. A variant is a palette (CSS custom
 * properties plus the colours the background canvas paints with) and a music style.
 *
 * Every script takes `--variant <name>` (the preview takes `?variant=name`); the default is
 * the first one. Add a variant here and nothing else has to change.
 *
 * Music: `root` is a MIDI note, `progression` one chord per bar in semitones above it, and
 * `style` one of the arrangements in scripts/generate-music.mjs (edm, synthwave, deephouse).
 */

export const variants = {
  midnight: {
    label: 'Blue and violet on navy, bright EDM in D major',
    theme: {
      css: {
        '--bg-deep': '#0a0f17',
        '--ink': '#eef2f8',
        '--muted': 'rgba(226, 233, 243, 0.7)',
        '--accent': '#7fb2ff',
        '--hot': 'linear-gradient(100deg, #9cc4ff 0%, #4c8dff 48%, #8b6cff 100%)',
        '--card': 'rgba(255, 255, 255, 0.06)',
        '--card-border': 'rgba(160, 190, 240, 0.2)',
        '--check': 'linear-gradient(135deg, #4c8dff, #8b6cff)',
        '--check-ink': '#ffffff',
        '--code-bg': 'rgba(127, 178, 255, 0.14)',
        '--code-ring': 'rgba(127, 178, 255, 0.35)',
        '--code-ink': '#cfe0ff',
        '--panel': 'rgba(10, 15, 23, 0.7)',
        '--flash': 'radial-gradient(circle at 50% 50%, #f2f7ff 0%, #9cc4ff 45%, rgba(47, 111, 235, 0.6) 100%)',
        '--vignette': 'rgba(0, 0, 0, 0.55)',
        '--fade': '#000000',
        '--shadow': 'rgba(0, 0, 0, 0.6)',
        '--window-ring': 'rgba(200, 220, 255, 0.16)',
      },
      /** r,g,b of the glow around windows, the logo and the statement badge. */
      glow: '76,141,255',
      canvas: {
        top: '#111a27',
        bottom: '#080c13',
        blend: 'lighter',
        orbs: [
          { color: '47,111,235', alpha: 0.24, pulse: 0.16 },
          { color: '139,108,255', alpha: 0.2 },
          { color: '34,184,207', alpha: 0.08, pulse: 0.06 },
        ],
        cells: ['127,178,255', '139,108,255'],
        cellAlpha: 1,
        grid: '160,190,240',
        gridAlpha: 0.05,
      },
    },
    music: {
      seed: 11,
      style: 'edm',
      root: 50, // D: D, A, Bm, G
      progression: [
        [0, 4, 7],
        [-5, -1, 2],
        [-3, 0, 4],
        [-7, -3, 0],
      ],
    },
  },

  neon: {
    label: 'Magenta and cyan synthwave with a sunset, A minor',
    theme: {
      css: {
        '--bg-deep': '#07030f',
        '--ink': '#fdf0ff',
        '--muted': 'rgba(240, 220, 255, 0.72)',
        '--accent': '#3ee8ff',
        '--hot': 'linear-gradient(100deg, #ffd36e 0%, #ff5fa2 52%, #b45cff 100%)',
        '--card': 'rgba(255, 255, 255, 0.06)',
        '--card-border': 'rgba(255, 120, 220, 0.3)',
        '--check': 'linear-gradient(135deg, #ff5fa2, #b45cff)',
        '--check-ink': '#ffffff',
        '--code-bg': 'rgba(62, 232, 255, 0.12)',
        '--code-ring': 'rgba(62, 232, 255, 0.45)',
        '--code-ink': '#c8f8ff',
        '--panel': 'rgba(12, 5, 24, 0.75)',
        '--flash': 'radial-gradient(circle at 50% 50%, #fff3fb 0%, #ff9ad5 45%, rgba(180, 92, 255, 0.6) 100%)',
        '--vignette': 'rgba(0, 0, 0, 0.6)',
        '--fade': '#000000',
        '--shadow': 'rgba(0, 0, 0, 0.65)',
        '--window-ring': 'rgba(255, 140, 230, 0.22)',
      },
      glow: '255,95,162',
      canvas: {
        top: '#1a0b2e',
        bottom: '#07030f',
        blend: 'lighter',
        sun: { top: '#ffd36e', bottom: '#ff3c8e', alpha: 0.42 },
        orbs: [
          { color: '255,60,172', alpha: 0.22, pulse: 0.14 },
          { color: '120,75,200', alpha: 0.3 },
          { color: '43,134,197', alpha: 0.14, pulse: 0.06 },
        ],
        cells: ['62,232,255', '255,95,162'],
        cellAlpha: 1,
        grid: '255,95,200',
        gridAlpha: 0.2,
      },
    },
    music: {
      seed: 23,
      style: 'synthwave',
      root: 45, // A minor: Am, F, C, G
      progression: [
        [0, 3, 7],
        [-4, 0, 3],
        [3, 7, 10],
        [-2, 2, 5],
      ],
    },
  },

  daylight: {
    label: 'Light paper with coral and teal, deep house in F with electric piano',
    theme: {
      css: {
        '--bg-deep': '#efe6da',
        '--ink': '#1d2430',
        '--muted': 'rgba(29, 36, 48, 0.66)',
        '--accent': '#e2553a',
        '--hot': 'linear-gradient(100deg, #ff8a3d 0%, #e2553a 50%, #c2367a 100%)',
        '--card': 'rgba(255, 255, 255, 0.72)',
        '--card-border': 'rgba(29, 36, 48, 0.12)',
        '--check': 'linear-gradient(135deg, #ff8a3d, #e2553a)',
        '--check-ink': '#ffffff',
        '--code-bg': 'rgba(226, 85, 58, 0.1)',
        '--code-ring': 'rgba(226, 85, 58, 0.4)',
        '--code-ink': '#9a3412',
        '--panel': 'rgba(255, 255, 255, 0.85)',
        '--flash': 'radial-gradient(circle at 50% 50%, #ffffff 0%, #fff4e6 50%, rgba(255, 200, 150, 0.5) 100%)',
        '--vignette': 'rgba(120, 90, 60, 0.16)',
        '--fade': '#fbf7f1',
        '--shadow': 'rgba(70, 45, 20, 0.28)',
        '--window-ring': 'rgba(29, 36, 48, 0.14)',
      },
      glow: '255,138,61',
      canvas: {
        top: '#fbf7f1',
        bottom: '#eee4d6',
        blend: 'source-over',
        orbs: [
          { color: '255,150,100', alpha: 0.22, pulse: 0.08 },
          { color: '40,170,160', alpha: 0.12 },
          { color: '255,205,130', alpha: 0.28, pulse: 0.06 },
        ],
        cells: ['226,85,58', '40,150,140'],
        cellAlpha: 0.45,
        grid: '29,36,48',
        gridAlpha: 0.05,
      },
    },
    music: {
      seed: 5,
      style: 'deephouse',
      root: 53, // F: Fmaj7, Dm7, Bbmaj7, C(add9)
      progression: [
        [0, 4, 7, 11],
        [-3, 0, 4, 7],
        [-7, -3, 0, 4],
        [-5, -1, 2, 9],
      ],
    },
  },
}

export const variantNames = Object.keys(variants)

/** The variant `name` names, or the default; throws on a name that isn't defined. */
export function variant(name) {
  const key = name ?? variantNames[0]
  const found = variants[key]
  if (!found) throw new Error(`Unknown variant "${key}". Known: ${variantNames.join(', ')}.`)
  return { name: key, ...found }
}

/** Reads `--variant <name>` from a script's arguments. */
export function variantFromArgs(argv = process.argv) {
  const index = argv.indexOf('--variant')
  return variant(index >= 0 ? argv[index + 1] : undefined)
}
