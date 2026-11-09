// Headless browser CPU profile of the web build (docs/WEB_BUILD.md, Frame rate). Build with
// H5_WEB_PUBLISH_ARGS=-p:WasmNativeStrip=false tools/web/build.sh so the wasm keeps its function names.
//   node prof.mjs <dist> <install> <query> <start marker regex> <settle s> <profile s> <out.txt>
//   e.g. node prof.mjs ../../../web/dist "/path/to/TotE" "?args=--adventure=A2M1" "adventure on screen" 15 10 adv.txt
// STOP=<regex>: profile from the start marker until a log line matching it instead of <profile s> seconds
//   (e.g. "load: decoded prefetch" ... STOP="load: objects" profiles the map's object pass).
// Writes <out.txt> (top self / inclusive functions), <out.txt>.log (console) and <out.txt>.cpuprofile (Chrome DevTools).
// Uses the GPU (still headless) like smoke.mjs --gpu; Chromium: $CHROMIUM, else the playwright cache.
import { chromium } from 'playwright-core';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { serve } from './serve.mjs';

const [dist, install, query, marker, settle, secs, out] = process.argv.slice(2);
if (!out) { console.error('usage: node prof.mjs <dist> <install> <query> <marker> <settle s> <profile s> <out.txt>'); process.exit(2); }

function findChromium() {
  if (process.env.CHROMIUM) return process.env.CHROMIUM;
  const base = process.env.PLAYWRIGHT_BROWSERS_PATH || (process.platform === 'win32'
    ? path.join(os.homedir(), 'AppData/Local/ms-playwright') : path.join(os.homedir(), '.cache/ms-playwright'));
  const dirs = fs.existsSync(base) ? fs.readdirSync(base).filter((d) => /^chromium-\d+$/.test(d)).sort().reverse() : [];
  for (const d of dirs)
    for (const rel of ['chrome-win64/chrome.exe', 'chrome-win/chrome.exe', 'chrome-linux64/chrome', 'chrome-linux/chrome']) {
      const p = path.join(base, d, rel);
      if (fs.existsSync(p)) return p;
    }
  throw new Error('no Chromium found: set CHROMIUM or run `npx playwright-core install chromium`');
}

const server = await serve(path.resolve(dist), 0, {});
const base = `http://127.0.0.1:${server.address().port}/`;
const browser = await chromium.launch({ executablePath: findChromium(), headless: true,
  args: ['--enable-gpu', '--ignore-gpu-blocklist', process.platform === 'win32' ? '--use-angle=d3d11' : '--use-angle=vulkan', '--autoplay-policy=no-user-gesture-required'] });
const page = await (await browser.newContext({ viewport: { width: 1280, height: 720 } })).newPage();
const log = []; const t0 = Date.now();
const stopRe = process.env.STOP && new RegExp(process.env.STOP); let stopHit; const stopped = new Promise((r) => (stopHit = r));
const started = new Promise((r) => page.on('console', (m) => {
  const t = m.text(); log.push(`[${((Date.now() - t0) / 1000).toFixed(1)}] ${t}`);
  if (/engine started/.test(t)) page.mouse.move(640, 360).catch(() => {});
  if (new RegExp(marker).test(t)) r();
  if (stopRe && stopRe.test(t)) stopHit();
}));
await page.goto(base + query);
await page.waitForSelector('#h5-dir', { state: 'attached', timeout: 60000 });
await page.setInputFiles('#h5-dir', install);
await started;
await page.waitForTimeout(+settle * 1000);
const cdp = await page.context().newCDPSession(page);
await cdp.send('Profiler.enable');
await cdp.send('Profiler.setSamplingInterval', { interval: 200 });
await cdp.send('Profiler.start');
if (stopRe) await stopped; else await page.waitForTimeout(+secs * 1000);
const { profile } = await cdp.send('Profiler.stop');

const byId = new Map(profile.nodes.map((n) => [n.id, n]));
const parent = new Map(); for (const n of profile.nodes) for (const c of n.children || []) parent.set(c, n.id);
const cnt = new Map(); let total = 0;
profile.samples.forEach((id, i) => { const dt = profile.timeDeltas[i] || 0; cnt.set(id, (cnt.get(id) || 0) + dt); total += dt; });
const self = new Map(), incl = new Map();
for (const [id, t] of cnt) {
  const n = byId.get(id); const k = n.callFrame.functionName || `(${n.callFrame.url.split('/').pop()})`;
  self.set(k, (self.get(k) || 0) + t);
  const seen = new Set();
  for (let x = id; x !== undefined; x = parent.get(x)) { const f = byId.get(x).callFrame.functionName || '?'; if (!seen.has(f)) { seen.add(f); incl.set(f, (incl.get(f) || 0) + t); } }
}
const fmt = (m) => [...m].sort((a, b) => b[1] - a[1]).slice(0, 80).map(([k, t]) => `${(100 * t / total).toFixed(1).padStart(5)}% ${k}`).join('\n');
fs.writeFileSync(out, `SELF\n${fmt(self)}\n\nINCLUSIVE\n${fmt(incl)}\n`);
fs.writeFileSync(out + '.log', log.join('\n'));
fs.writeFileSync(out + '.cpuprofile', JSON.stringify(profile));
console.log(`profiled ${(total / 1e6).toFixed(1)} s -> ${out}`);
await browser.close(); server.close(); process.exit(0);
