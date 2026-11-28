// Takes the help screenshots (docs/help/images/*.png) from the web client running on made-up data.
//
//   cd web && npm run build && cd ..
//   node tools/screenshots/capture.mjs            (starts server.mjs itself; needs Chrome or Edge)
//   node tools/screenshots/capture.mjs --page /help/destinations out.png    (one page, e.g. to check the help)
//
// Drives headless Chrome over the DevTools protocol with Node's own WebSocket: no extra dependencies.

import { spawn } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const port = 5199
const debugPort = 9333
const out = fileURLToPath(new URL('../../docs/help/images/', import.meta.url))
const base = `http://localhost:${port}`
const width = 1440
const height = 900

// What to capture: a page, the clicks that get there, and what to crop to (default: the whole window).
const one = process.argv[2] === '--page' ? { path: process.argv[3], file: process.argv[4] } : null

const shots = [
  { name: 'dashboard', path: '/', waitFor: 'Needs attention' },
  { name: 'inventory', path: '/inventory', waitFor: 'nextcloud' },
  {
    name: 'inventory-business-central',
    scenario: 'windows',
    path: '/inventory',
    waitFor: 'need no backup',
    clip: `[...document.querySelectorAll('h3')].find((h) => h.textContent === 'bc-demo').closest('section')`,
  },
  { name: 'destinations', path: '/destinations', waitFor: 'Recovery key not saved' },
  { name: 'add-destination', path: '/destinations', waitFor: 'Add destination', clicks: ['Add destination'], clip: `document.querySelector('dialog[open]')` },
  {
    name: 'recovery-key',
    path: '/destinations',
    waitFor: 'Recovery key not saved',
    clicks: ['Recovery key not saved', 'Show key', "I've stored this key"],
    clip: `document.querySelector('dialog[open]')`,
  },
  { name: 'plans', path: '/plans', waitFor: 'Weekly offsite' },
  { name: 'activity', path: '/activity', waitFor: 'homeassistant', settle: 2500 },
  { name: 'snapshots', path: '/snapshots', waitFor: 'Restore everything', clicks: ['nextcloud'] },
  {
    name: 'restore',
    path: '/snapshots',
    waitFor: 'Restore everything',
    clicks: ['nextcloud', 'Restore everything'],
    clip: `document.querySelector('dialog[open]')`,
  },
  { name: 'notifications', path: '/settings', waitFor: 'Notifications' },
]

function browser() {
  const candidates = [
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    '/usr/bin/google-chrome',
    '/usr/bin/chromium',
  ]
  const found = candidates.find(existsSync)
  if (!found) throw new Error('Chrome or Edge not found')
  return found
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

async function devtools(profile) {
  const chrome = spawn(browser(), [
    '--headless=new',
    `--remote-debugging-port=${debugPort}`,
    `--user-data-dir=${profile}`,
    `--window-size=${width},${height}`,
    '--hide-scrollbars',
    '--force-device-scale-factor=1',
    '--no-first-run',
    'about:blank',
  ])
  for (let i = 0; i < 50; i++) {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${debugPort}/json/list`)).json()
      const page = targets.find((t) => t.type === 'page')
      if (page) return { chrome, url: page.webSocketDebuggerUrl }
    } catch {
      // not up yet
    }
    await sleep(200)
  }
  throw new Error('Chrome did not start')
}

function session(url) {
  const ws = new WebSocket(url)
  let next = 0
  const pending = new Map()
  ws.addEventListener('message', (e) => {
    const msg = JSON.parse(e.data)
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id)
      pending.delete(msg.id)
      msg.error ? reject(new Error(msg.error.message)) : resolve(msg.result)
    }
  })
  const send = (method, params = {}) =>
    new Promise((resolve, reject) => {
      const id = ++next
      pending.set(id, { resolve, reject })
      ws.send(JSON.stringify({ id, method, params }))
    })
  return new Promise((resolve) => ws.addEventListener('open', () => resolve({ send, close: () => ws.close() })))
}

async function evaluate(cdp, expression) {
  const { result, exceptionDetails } = await cdp.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })
  if (exceptionDetails) throw new Error(exceptionDetails.exception?.description ?? exceptionDetails.text)
  return result.value
}

async function waitForText(cdp, text) {
  for (let i = 0; i < 100; i++) {
    if (await evaluate(cdp, `document.body.innerText.includes(${JSON.stringify(text)})`)) return
    await sleep(100)
  }
  throw new Error(`"${text}" never appeared`)
}

// Clicks the innermost button, link or label whose text contains the given text.
async function click(cdp, text) {
  await waitForText(cdp, text)
  const clicked = await evaluate(
    cdp,
    `(() => {
      const els = [...document.querySelectorAll('button, a, label, [role=radio]')].filter((e) => e.innerText.includes(${JSON.stringify(text)}))
      const el = els.at(-1)
      if (!el) return false
      el.click()
      return true
    })()`,
  )
  if (!clicked) throw new Error(`Nothing to click for "${text}"`)
  await sleep(400)
}

async function main() {
  mkdirSync(out, { recursive: true })
  const server = spawn(process.execPath, [fileURLToPath(new URL('./server.mjs', import.meta.url)), String(port)], { stdio: 'inherit' })
  const profile = mkdtempSync(join(tmpdir(), 'stowaway-shots-'))
  const { chrome, url } = await devtools(profile)
  const cdp = await session(url)
  try {
    await cdp.send('Page.enable')
    await cdp.send('Runtime.enable')
    await cdp.send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false })
    if (one) {
      await cdp.send('Page.navigate', { url: base + one.path })
      await sleep(2500)
      const { data } = await cdp.send('Page.captureScreenshot', { format: 'png' })
      writeFileSync(one.file, Buffer.from(data, 'base64'))
      console.log(`  ${one.file}`)
      return
    }

    for (const shot of shots) {
      await fetch(`${base}/__scenario/${shot.scenario ?? 'linux'}`)
      await cdp.send('Page.navigate', { url: base + shot.path })
      await waitForText(cdp, shot.waitFor)
      for (const text of shot.clicks ?? []) await click(cdp, text)
      await sleep(shot.settle ?? 1200) // animations and the progress bar
      const params = { format: 'png' }
      if (shot.clip) {
        const pad = 12
        const rect = await evaluate(cdp, `(() => { const r = (${shot.clip}).getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width, height: r.height } })()`)
        params.clip = { x: Math.max(0, rect.x - pad), y: Math.max(0, rect.y - pad), width: rect.width + 2 * pad, height: rect.height + 2 * pad, scale: 1 }
        params.captureBeyondViewport = true
      }
      const { data } = await cdp.send('Page.captureScreenshot', params)
      writeFileSync(join(out, `${shot.name}.png`), Buffer.from(data, 'base64'))
      console.log(`  ${shot.name}.png`)
    }
  } finally {
    cdp.close()
    chrome.kill()
    server.kill()
    await sleep(500)
    rmSync(profile, { recursive: true, force: true })
  }
}

main().catch((e) => {
  console.error(e)
  process.exit(1)
})
