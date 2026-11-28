// Serves the built web client (src/Stowaway.Service/wwwroot) with made-up data (fixtures.mjs) instead of the real
// service, for the help screenshots. Also answers SignalR over long polling, so the app shows "Live" and a backup in
// progress. Dev tool only: it accepts anything and stores nothing.
//
//   node tools/screenshots/server.mjs [port]     (then open http://localhost:5199)
//
// GET /__scenario/windows switches to the Windows-engine data (a Business Central container); /__scenario/linux back.

import { createServer } from 'node:http'
import { readFile } from 'node:fs/promises'
import { extname, join, normalize, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import * as f from './fixtures.mjs'

const root = resolve(fileURLToPath(new URL('../../src/Stowaway.Service/wwwroot/', import.meta.url)))
const port = Number(process.argv[2] ?? 5199)
let scenario = 'linux'

const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png', '.webp': 'image/webp', '.ico': 'image/x-icon', '.woff2': 'font/woff2', '.json': 'application/json' }

const json = (res, body, status = 200) => {
  res.writeHead(status, { 'Content-Type': 'application/json' })
  res.end(body === undefined ? '' : JSON.stringify(body))
}

async function body(req) {
  let text = ''
  for await (const chunk of req) text += chunk
  return text
}

// ---- API ------------------------------------------------------------------------------------------------------

const routes = [
  ['GET', /^\/api\/system$/, () => f.system(scenario)],
  ['GET', /^\/api\/inventory$/, () => f.inventory(scenario)],
  ['GET', /^\/api\/destinations$/, () => f.destinations()],
  ['GET', /^\/api\/destination-types$/, () => f.destinationTypes],
  ['GET', /^\/api\/plans$/, () => f.plans()],
  ['GET', /^\/api\/backups/, () => f.runs()],
  ['GET', /^\/api\/snapshots$/, () => f.snapshots()],
  ['GET', /^\/api\/destinations\/[^/]+\/snapshots\/([^/]+)\/contents$/, (m) => f.contents(m[1])],
  ['GET', /^\/api\/destinations\/[^/]+\/snapshots\/[^/]+\/browse/, () => []],
  ['GET', /^\/api\/notifications$/, () => f.channels()],
  ['GET', /^\/api\/databases\/credentials$/, () => []],
  ['GET', /^\/api\/remote$/, () => f.remote()],
  ['GET', /^\/api\/remote\/accounts$/, () => []],
  ['GET', /^\/api\/audit/, () => []],
  ['GET', /^\/api\/me$/, () => ({ name: `${f.machine}\\alex`, signInRequired: true, method: 'windows', remote: false })],
  ['GET', /^\/api\/auth\/mode$/, () => ({ remote: false, signIn: 'windows' })],
  ['POST', /^\/api\/destinations\/([^/]+)\/recovery-key$/, (m) => {
    const d = f.destinations().find((x) => x.id === m[1]) ?? f.destinations()[0]
    return { recoveryKey: f.recoveryKey, kit: f.kit(d), kitFileName: `stowaway-recovery-kit-${d.name.replace(/\W+/g, '-')}.txt` }
  }],
  ['POST', /^\/api\/restores\/preview$/, () => f.restorePreview()],
  ['POST', /^\/api\/plans\/preview$/, () => ({ valid: true, error: null, nextRuns: [], intervalHours: 24 })],
]

// ---- SignalR (JSON protocol over long polling) ----------------------------------------------------------------

const RS = '\x1e'
const connections = new Map()
let tick = 0
setInterval(() => {
  tick++
  for (const c of connections.values()) if (c.handshaken) c.queue.push(JSON.stringify({ type: 1, target: 'backupProgress', arguments: [f.progress(tick)] }) + RS)
}, 1000)

async function hub(req, res, url) {
  if (url.pathname.endsWith('/negotiate')) {
    const token = Math.random().toString(36).slice(2)
    connections.set(token, { queue: [], handshaken: false, polled: false })
    return json(res, { negotiateVersion: 1, connectionId: token, connectionToken: token, availableTransports: [{ transport: 'LongPolling', transferFormats: ['Text', 'Binary'] }] })
  }

  const c = connections.get(url.searchParams.get('id'))
  if (!c) return json(res, undefined, 404)
  if (req.method === 'POST') {
    const text = await body(req)
    if (text.includes('"protocol"')) {
      c.handshaken = true
      c.queue.push('{}' + RS)
    }
    res.writeHead(200)
    return res.end()
  }
  if (req.method === 'DELETE') {
    connections.delete(url.searchParams.get('id'))
    res.writeHead(202)
    return res.end()
  }

  // GET: the first poll only opens the connection; later ones return queued messages, or a ping after a while.
  if (!c.polled) {
    c.polled = true
    res.writeHead(200)
    return res.end()
  }
  for (let waited = 0; c.queue.length === 0 && waited < 5000; waited += 100) await new Promise((r) => setTimeout(r, 100))
  const out = c.queue.length > 0 ? c.queue.splice(0).join('') : JSON.stringify({ type: 6 }) + RS
  res.writeHead(200, { 'Content-Type': 'text/plain' })
  res.end(out)
}

// ---- Static files ---------------------------------------------------------------------------------------------

async function file(res, path) {
  const full = normalize(join(root, path))
  if (!full.startsWith(root + sep)) return json(res, undefined, 404)
  try {
    const content = await readFile(full)
    res.writeHead(200, { 'Content-Type': types[extname(full)] ?? 'application/octet-stream' })
    res.end(content)
  } catch {
    // Client-side routes (/inventory, /help/...) get the app.
    const index = await readFile(join(root, 'index.html'))
    res.writeHead(200, { 'Content-Type': 'text/html' })
    res.end(index)
  }
}

createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost')
  if (url.pathname.startsWith('/__scenario/')) {
    scenario = url.pathname.split('/')[2]
    return json(res, { scenario })
  }
  if (url.pathname.startsWith('/hubs/events')) return hub(req, res, url)
  if (url.pathname.startsWith('/api/')) {
    for (const [method, pattern, handler] of routes) {
      const m = req.method === method && url.pathname.match(pattern)
      if (m) return json(res, handler(m))
    }
    if (req.method !== 'GET') {
      await body(req)
      return json(res, undefined, 204)
    }
    return json(res, { title: 'Not in the screenshot fixtures' }, 404)
  }
  return file(res, url.pathname === '/' ? 'index.html' : url.pathname.slice(1))
}).listen(port, () => console.log(`Screenshot fixtures on http://localhost:${port}`))
