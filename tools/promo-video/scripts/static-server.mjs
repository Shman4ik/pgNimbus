// Adapted from jamesmontemagno/app-fire-calculator tools/hype-video (MIT, Copyright (c) 2025 James Montemagno).
import { createServer } from 'node:http'
import { readFile, stat } from 'node:fs/promises'
import { extname, join, normalize, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

export const toolDir = resolve(fileURLToPath(new URL('..', import.meta.url)))
export const repoRoot = resolve(toolDir, '..', '..')

const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.wav': 'audio/wav',
}

async function isFile(path) {
  try {
    return (await stat(path)).isFile()
  } catch {
    return false
  }
}

/** Serves `root` on 127.0.0.1 only, on a free port. Nothing outside `root` is reachable. */
export function startStaticServer(root, { port = 0 } = {}) {
  const base = resolve(root)
  const server = createServer(async (req, res) => {
    try {
      const url = new URL(req.url ?? '/', 'http://localhost')
      const relative = normalize(decodeURIComponent(url.pathname)).replace(/^([/\\])+/, '')
      const file = join(base, relative)
      if (file !== base && !file.startsWith(base + sep)) {
        res.writeHead(403).end()
        return
      }
      if (!(await isFile(file))) {
        res.writeHead(404).end('Not found')
        return
      }
      const body = await readFile(file)
      res.writeHead(200, {
        'Content-Type': types[extname(file).toLowerCase()] ?? 'application/octet-stream',
        'Cache-Control': 'no-store',
      })
      res.end(body)
    } catch (error) {
      res.writeHead(500).end(String(error))
    }
  })
  return new Promise((resolvePromise, reject) => {
    server.once('error', reject)
    server.listen(port, '127.0.0.1', () => {
      const { port: actual } = server.address()
      resolvePromise({ server, url: `http://127.0.0.1:${actual}`, close: () => new Promise((r) => server.close(r)) })
    })
  })
}
