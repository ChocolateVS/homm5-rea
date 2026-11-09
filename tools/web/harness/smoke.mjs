// Headless smoke test of the web build: serves <dist>, opens it in a headless Chromium (never a visible window),
// feeds it the local game install through the page's folder picker, and screenshots each scenario.
//   node smoke.mjs <dist> --install "<game folder>" [--out DIR] [--only menu,duel,adventure,remember] [--timeout s]
//                         [--map-arg --adventure=<map>] [--duel-arg --combat-vs-ai=1,2] [--gpu] [--coi]
// Chromium: $CHROMIUM, else the playwright cache (npx playwright-core install chromium).
// Exit code 0 only if every scenario reached its marker without a page error.
import { chromium } from 'playwright-core';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { serve } from './serve.mjs';

const argv = process.argv.slice(2);
const opt = (k, d) => { const i = argv.indexOf(k); return i >= 0 ? argv[i + 1] : d; };
const dist = path.resolve(argv[0] && !argv[0].startsWith('--') ? argv[0] : '../../../web/dist');
const install = opt('--install', process.env.H5_GAME_DIR);
const outDir = path.resolve(opt('--out', path.join(dist, '..', 'smoke')));
const timeoutS = Number(opt('--timeout', '600'));
if (!install) { console.error('--install <game folder> (or H5_GAME_DIR) is required'); process.exit(2); }

// Scenario = page query (the game's dev args) + a log line that means "on screen" + seconds to render before the shot.
const SCENARIOS = {
  menu: { query: '?args=--skip-intro', marker: /\[H5\] web: main menu/, settle: 8 },
  duel: { query: '?args=' + encodeURIComponent(opt('--duel-arg', '--combat-vs-ai=1,2')), marker: /\[H5\] combat arena:/, settle: 30 },
  adventure: { query: '?args=' + encodeURIComponent(opt('--map-arg', '--adventure=A2M1')), marker: /\[H5\] web: adventure on screen/, settle: 15 },
};
const only = opt('--only', 'menu,duel,adventure').split(',');

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

// Default: SwiftShader (software WebGL 2; works on any machine and server, slow for 3D scenes).
// --gpu: the machine's GPU through ANGLE (still headless, no window).
const GPU = argv.includes('--gpu');
const LAUNCH = { executablePath: findChromium(), headless: true,
  args: [...(GPU ? ['--enable-gpu', '--ignore-gpu-blocklist', process.platform === 'win32' ? '--use-angle=d3d11' : '--use-angle=vulkan']
                 : ['--enable-unsafe-swiftshader', '--use-angle=swiftshader']),
    '--enable-precise-memory-info', '--autoplay-policy=no-user-gesture-required'] };

fs.mkdirSync(outDir, { recursive: true });
// --coi: serve with COOP/COEP like the deployment (Caddy on the LAN server); the build must work with and without.
const server = await serve(dist, 0, { coi: argv.includes('--coi') });
const base = `http://127.0.0.1:${server.address().port}/`;
const results = {};

async function run(name, page, { pick = true, query, marker, settle }) {
  const t0 = Date.now();
  const log = [];
  const stamp = () => ((Date.now() - t0) / 1000).toFixed(1).padStart(6);
  let hit; const reached = new Promise((r) => (hit = r));
  const errors = [];
  page.on('console', (m) => {
    const t = m.text(); log.push(`[${stamp()}] ${t}`); if (marker.test(t)) hit(t);
    // A headless page's pointer sits at (0,0) - the screen corner, where the adventure map edge-scrolls. Park it in
    // the middle of the canvas as soon as the engine runs (a real player's mouse is wherever they put it).
    if (/\[H5\] web: engine started/.test(t)) page.mouse.move(640, 360).catch(() => {});
  });
  page.on('pageerror', (e) => { errors.push(e.message); log.push(`[${stamp()}] PAGEERROR ${e.message}`); });
  await page.goto(base + query);
  if (pick) {
    await page.waitForSelector('#h5-dir', { state: 'attached', timeout: 60000 });
    await page.setInputFiles('#h5-dir', install);
    log.push(`[${stamp()}] picked ${install}`);
  }
  const res = await Promise.race([reached.then(() => 'ok'), new Promise((r) => setTimeout(() => r('timeout'), timeoutS * 1000))]);
  if (res === 'ok') await page.waitForTimeout(settle * 1000);
  const shot = path.join(outDir, `${name}.png`);
  await page.screenshot({ path: shot });
  const fps = await page.evaluate(() => new Promise((r) => { let n = 0; const s = performance.now();
    const f = () => { n++; if (performance.now() - s < 3000) requestAnimationFrame(f); else r(n / ((performance.now() - s) / 1000)); };
    requestAnimationFrame(f); }));
  const mem = await page.evaluate(() => ({ js: performance.memory?.usedJSHeapSize, wasm: globalThis.h5WasmHeapBytes?.() }));
  results[name] = { result: res, seconds: +((Date.now() - t0) / 1000).toFixed(1), screenshot: shot, pageErrors: errors.length,
    rafFps: Math.round(fps * 10) / 10, wasmHeapMB: mem.wasm && Math.round(mem.wasm / 2 ** 20), jsHeapMB: mem.js && Math.round(mem.js / 2 ** 20) };
  fs.writeFileSync(path.join(outDir, `${name}.log`), log.join('\n') + '\n');
  console.log(`${name}: ${res} in ${results[name].seconds}s -> ${shot}`);
}

const browser = await chromium.launch(LAUNCH);
for (const name of only.filter((n) => SCENARIOS[n])) {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 720 } });
  const page = await ctx.newPage();
  try { await run(name, page, SCENARIOS[name]); } catch (e) { results[name] = { result: 'error', error: String(e) }; console.log(name, e); }
  await ctx.close();
}
await browser.close();

if (only.includes('remember')) {
  // "Remember" flow: the first visit picks and copies the files to OPFS (persistent profile); the second visit must
  // start the game without any picking.
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'h5web-'));
  try {
    let ctx = await chromium.launchPersistentContext(profile, { ...LAUNCH, viewport: { width: 1280, height: 720 } });
    let page = ctx.pages()[0] || await ctx.newPage();
    const copied = new Promise((r) => page.on('console', (m) => { if (/H5DATA_OPFS_CACHED/.test(m.text())) r(); }));
    await run('remember-first', page, SCENARIOS.menu);
    await Promise.race([copied, new Promise((r) => setTimeout(r, timeoutS * 1000))]);
    await ctx.close();
    ctx = await chromium.launchPersistentContext(profile, { ...LAUNCH, viewport: { width: 1280, height: 720 } });
    page = ctx.pages()[0] || await ctx.newPage();
    await run('remember-second', page, { ...SCENARIOS.menu, pick: false });
    await ctx.close();
  } finally { fs.rmSync(profile, { recursive: true, force: true }); }
}
server.close();
fs.writeFileSync(path.join(outDir, 'results.json'), JSON.stringify(results, null, 2));
console.log(JSON.stringify(results, null, 2));
process.exit(Object.values(results).every((r) => r.result === 'ok' && !r.pageErrors) ? 0 : 1);
