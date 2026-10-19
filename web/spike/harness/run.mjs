// Headless Chromium run of a web bundle: no window on screen. Collects console output, timings,
// memory and fps, picks pak files into the page's file input, saves a screenshot.
// Usage: node run.mjs <bundleDir> [--paks a.pak,b.pak] [--wait REGEX] [--timeout ms] [--shot out.png] [--coi]
import { chromium } from 'playwright-core';
import os from 'node:os';
import path from 'node:path';
import { serve } from './serve.mjs';

const args = process.argv.slice(2);
const opt = (k, d) => { const i = args.indexOf(k); return i >= 0 ? args[i + 1] : d; };
const dir = args[0];
const paks = opt('--paks', '') ? opt('--paks').split(',') : [];
const waitRe = new RegExp(opt('--wait', 'SPIKE_DONE|SPIKE_FAIL'));
const timeout = Number(opt('--timeout', '300000'));
const exe = process.env.CHROMIUM || path.join(os.homedir(), 'AppData/Local/ms-playwright/chromium-1234/chrome-win64/chrome.exe');

const server = await serve(dir, 0, args.includes('--coi'));
const url = `http://127.0.0.1:${server.address().port}/` + opt('--page', '');
const browser = await chromium.launch({ executablePath: exe, headless: true,
  args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader', '--enable-precise-memory-info', '--js-flags=--expose-gc'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
const t0 = Date.now();
let done; let finished = new Promise(r => (done = r));
page.on('console', m => { const t = m.text(); console.log(`[${((Date.now() - t0) / 1000).toFixed(2)}s] ${t}`); if (waitRe.test(t)) done(t); });
page.on('pageerror', e => console.log('PAGEERROR', e.message));
await page.goto(url);
if (opt('--dir')) {
  await page.waitForSelector('#h5-dir', { state: 'attached', timeout: 60000 });
  await page.setInputFiles('#h5-dir', opt('--dir'));
  console.log(`[${((Date.now() - t0) / 1000).toFixed(2)}s] picked folder ${opt('--dir')}`);
}
if (paks.length) {
  await page.waitForSelector('#h5-paks', { state: 'attached', timeout: 60000 });
  await page.setInputFiles('#h5-paks', paks);
  console.log(`[${((Date.now() - t0) / 1000).toFixed(2)}s] picked ${paks.length} pak(s)`);
}
let res = await Promise.race([finished, new Promise(r => setTimeout(() => r('TIMEOUT'), timeout))]);
if (args.includes('--opfs-reload')) {
  // "remember" flow: wait for the OPFS copy (page opened with ?opfs), reload WITHOUT picking, run again.
  let cached; const copied = new Promise(r => (cached = r));
  page.on('console', m => { if (/H5PAK_OPFS_CACHED/.test(m.text())) cached(); });
  const tc = Date.now();
  await Promise.race([copied, new Promise(r => setTimeout(r, timeout))]);
  console.log(`[opfs] copy finished ${((Date.now() - tc) / 1000).toFixed(1)}s after first SPIKE_DONE; reloading without picking`);
  finished = new Promise(r => (done = r));
  await page.goto(url.replace('opfs', 'noop'));
  res = await Promise.race([finished, new Promise(r => setTimeout(() => r('TIMEOUT'), timeout))]);
}
const fps = await page.evaluate(() => new Promise(r => { let n = 0; const s = performance.now();
  const f = () => { n++; if (performance.now() - s < 3000) requestAnimationFrame(f); else r(n / ((performance.now() - s) / 1000)); };
  requestAnimationFrame(f); }));
const mem = await page.evaluate(() => ({ jsHeap: performance.memory?.usedJSHeapSize,
  wasmHeap: globalThis.h5WasmHeapBytes?.() }));
const cdp = await page.context().newCDPSession(page);
const metrics = Object.fromEntries((await cdp.send('Performance.enable').then(() => cdp.send('Performance.getMetrics'))).metrics.map(m => [m.name, m.value]));
console.log(JSON.stringify({ result: res, wallSeconds: (Date.now() - t0) / 1000, rafFps: Math.round(fps * 10) / 10,
  jsHeapMB: mem.jsHeap && +(mem.jsHeap / 2 ** 20).toFixed(1), wasmHeapMB: mem.wasmHeap && +(mem.wasmHeap / 2 ** 20).toFixed(1),
  cdpJSHeapTotalMB: +(metrics.JSHeapTotalSize / 2 ** 20).toFixed(1) }));
const shot = opt('--shot');
if (shot) await page.screenshot({ path: shot });
await browser.close();
server.close();
