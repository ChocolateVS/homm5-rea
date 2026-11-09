// Headless smoke test of the web build: serves <dist>, opens it in a headless Chromium (never a visible window),
// feeds it the local game install through the page's folder picker, and screenshots each scenario.
//   node smoke.mjs <dist> --install "<game folder>" [--out DIR] [--only menu,duel,adventure,endturn,remember,persist,decoded,audio,lobby,lobby-wss] [--server H5.Server.dll] [--timeout s]
//                         [--map-arg --adventure=<map>] [--duel-arg --combat-vs-ai=1,2] [--gpu] [--coi] [--init extra-init-script.js]
//                         [--record-shaders web/game/H5Reborn.web/wwwroot/h5shaders.json]
// --pick-delay <s>: seconds between the page being ready and the folder being picked.
// --record-shaders: records every WebGL program the game links in the scenarios run (menu,duel,adventure make a good
// list) and writes the page's shader warm-up list (wwwroot/h5shaders.js; docs/WEB_BUILD.md, Shaders).
// Chromium: $CHROMIUM, else the playwright cache (npx playwright-core install chromium).
// Exit code 0 only if every scenario reached its marker without a page error.
import { chromium } from 'playwright-core';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { serve, selfSignedCert } from './serve.mjs';

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
  // End the first turn on a map with computer players: every computer turn must run and the human get the next day.
  endturn: { query: '?args=' + encodeURIComponent(opt('--map-arg', '--adventure=A2M1') + ' --adventure-close-boxes --save-dev=endturn,endturn,endturn'),
    marker: /\[H5\] web: adventure on screen/, then: [/computer turn/, /adventure day [2-9]\d* player \d+ human turn/], settle: 5 },
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

async function run(name, page, { pick = true, query, marker, then = [], settle, after, base: pageBase = base }) {
  const t0 = Date.now();
  const log = [];
  const stamp = () => ((Date.now() - t0) / 1000).toFixed(1).padStart(6);
  // marker, then each `then` pattern in order (a log line after the previous one).
  const seq = [marker, ...then];
  let step = 0, hit; const reached = new Promise((r) => (hit = r));
  const errors = [];
  page.on('console', (m) => {
    const t = m.text(); log.push(`[${stamp()}] ${t}`);
    if (step < seq.length && seq[step].test(t)) { log.push(`[${stamp()}] -- step ${step + 1}/${seq.length} reached`); if (step === 0 && seq.length > 1) page.evaluate(() => globalThis.h5ResetFrameGap?.()).catch(() => {}); if (++step === seq.length) hit(t); }
    // A headless page's pointer sits at (0,0) - the screen corner, where the adventure map edge-scrolls. Park it in
    // the middle of the canvas as soon as the engine runs (a real player's mouse is wherever they put it).
    if (/\[H5\] web: engine started/.test(t)) page.mouse.move(640, 360).catch(() => {});
  });
  page.on('pageerror', (e) => { errors.push(e.message); log.push(`[${stamp()}] PAGEERROR ${e.message}`); });
  // Longest gap between two animation frames: how long the page was frozen by one synchronous piece of work.
  await page.addInitScript(() => { let last = 0; globalThis.h5MaxFrameGapMs = 0;
    const f = (t) => { if (last) globalThis.h5MaxFrameGapMs = Math.max(globalThis.h5MaxFrameGapMs, t - last); last = t; requestAnimationFrame(f); };
    requestAnimationFrame(f); globalThis.h5ResetFrameGap = () => { globalThis.h5MaxFrameGapMs = 0; }; });
  // --init <file.js>: an extra script that runs before the page's own (instrumentation for experiments).
  if (opt('--init')) await page.addInitScript({ path: path.resolve(opt('--init')) });
  if (opt('--record-shaders')) await page.addInitScript(() => { globalThis.H5_RECORD_SHADERS = true; });
  await page.goto(pageBase + query);
  // Memory timeline: the wasm heap (linear memory, never shrinks) every 2 s, next to the game's log lines.
  let peakMB = 0;
  const memTimer = setInterval(() => page.evaluate(() => globalThis.h5WasmHeapBytes?.()).then((b) => {
    const mb = b && Math.round(b / 2 ** 20);
    if (mb && mb !== peakMB) { peakMB = Math.max(peakMB, mb); log.push(`[${stamp()}] [mem] wasm heap ${mb} MB`); }
  }).catch(() => {}), 2000);
  if (pick) {
    await page.waitForSelector('#h5-dir', { state: 'attached', timeout: 60000 });
    // --pick-delay <s>: wait like a player choosing the folder (the page warms shaders meanwhile, h5shaders.js).
    if (opt('--pick-delay')) await page.waitForTimeout(Number(opt('--pick-delay')) * 1000);
    await page.setInputFiles('#h5-dir', install);
    log.push(`[${stamp()}] picked ${install}`);
  }
  const res = await Promise.race([reached.then(() => 'ok'), new Promise((r) => setTimeout(() => r('timeout'), timeoutS * 1000))]);
  if (res === 'ok') await page.waitForTimeout(settle * 1000);
  const extra = res === 'ok' && after ? await after(page, log, stamp) : undefined;
  if (opt('--record-shaders')) recordShaders(await page.evaluate(() => globalThis.h5shaders?.recorded()).catch(() => null), name);
  clearInterval(memTimer);
  const shot = path.join(outDir, `${name}.png`);
  await page.screenshot({ path: shot });
  const fps = await page.evaluate(() => new Promise((r) => { let n = 0; const s = performance.now();
    const f = () => { n++; if (performance.now() - s < 3000) requestAnimationFrame(f); else r(n / ((performance.now() - s) / 1000)); };
    requestAnimationFrame(f); }));
  const mem = await page.evaluate(() => ({ js: performance.memory?.usedJSHeapSize, wasm: globalThis.h5WasmHeapBytes?.(), gap: globalThis.h5MaxFrameGapMs }));
  const shaderWarm = await page.evaluate(() => globalThis.h5shaders?.status()).catch(() => null);
  results[name] = { result: res, seconds: +((Date.now() - t0) / 1000).toFixed(1), screenshot: shot, pageErrors: errors.length,
    rafFps: Math.round(fps * 10) / 10, maxFrameGapMs: Math.round(mem.gap), ...(extra || {}), wasmHeapMB: mem.wasm && Math.round(mem.wasm / 2 ** 20), jsHeapMB: mem.js && Math.round(mem.js / 2 ** 20), ...(shaderWarm ? { shaderWarm } : {}) };
  fs.writeFileSync(path.join(outDir, `${name}.log`), log.join('\n') + '\n');
  if (extra?.result && extra.result !== 'ok') results[name].result = extra.result;
  console.log(`${name}: ${results[name].result} in ${results[name].seconds}s -> ${shot}`);
}

// --record-shaders: programs in first-linked order over all scenarios, then encoded with a shared line table.
const shaderRec = { exts: new Set(), programs: [], seen: new Set() };
function recordShaders(r, name) {
  if (!r) return;
  r.exts.forEach((e) => shaderRec.exts.add(e));
  let added = 0;
  for (const p of r.programs) { const k = JSON.stringify(p); if (!shaderRec.seen.has(k)) { shaderRec.seen.add(k); shaderRec.programs.push(p); added++; } }
  console.log(`shaders: ${name}: ${r.programs.length} programs, ${added} new (${shaderRec.programs.length} in all)`);
}
async function writeShaderList(file) {
  const lines = [], lineIdx = new Map(), shaders = [], shaderIdx = new Map();
  const runs = (text) => {
    const out = [];
    for (const l of text.split(String.fromCharCode(10))) {
      let i = lineIdx.get(l);
      if (i === undefined) { i = lines.length; lines.push(l); lineIdx.set(l, i); }
      if (out.length && out[out.length - 2] + out[out.length - 1] === i) out[out.length - 1]++; else out.push(i, 1);
    }
    return out;
  };
  const programs = shaderRec.programs.map((p) => {
    const s = p.shaders.map(([t, text]) => { let i = shaderIdx.get(text); if (i === undefined) { i = shaders.length; shaders.push(runs(text)); shaderIdx.set(text, i); } return [t, i]; });
    return p.tf ? { s, tf: p.tf } : { s };
  });
  const body = { exts: [...shaderRec.exts].sort(), lines, shaders, programs };
  const id = (await import('node:crypto')).createHash('sha256').update(JSON.stringify(body)).digest('hex').slice(0, 16);
  fs.writeFileSync(file, JSON.stringify({ v: 1, id, ...body }));
  console.log(`shaders: wrote ${programs.length} programs, ${shaders.length} shaders, ${lines.length} lines -> ${file}`);
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
if (only.includes('decoded')) {
  // Decoded-asset cache (IndexedDB, h5cache.js): the same map twice in one browser profile. The second visit must find
  // the characters' skeletons and clips stored by the first and build the objects faster; results.json gets both visits'
  // object-pass and character-template times.
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'h5web-'));
  const q = { ...SCENARIOS.adventure, query: SCENARIOS.adventure.query.replace('?', '?nocache&'), settle: 5 };
  const loadTimes = (name) => {
    const log = fs.readFileSync(path.join(outDir, `${name}.log`), 'utf8');
    const ms = (re) => { const m = log.match(re); return m ? Number(m[1]) : null; };
    const hit = log.match(/decoded cache: (\d+) of (\d+) entries/);
    return { objectsMs: ms(/load: objects (\d+) ms/), chTemplateMs: ms(/ch template=(\d+)ms/), cacheHits: hit ? `${hit[1]}/${hit[2]}` : null };
  };
  try {
    for (const visit of ['decoded-first', 'decoded-second']) {
      const ctx = await chromium.launchPersistentContext(profile, { ...LAUNCH, viewport: { width: 1280, height: 720 } });
      const page = ctx.pages()[0] || await ctx.newPage();
      await run(visit, page, q);
      // Let the background IndexedDB writes finish before the browser closes.
      if (visit === 'decoded-first') await page.waitForTimeout(5000);
      await ctx.close();
      Object.assign(results[visit], loadTimes(visit));
    }
    if (!(results['decoded-second'].cacheHits && !results['decoded-second'].cacheHits.startsWith('0/'))) results['decoded-second'].result = 'no cache hits';
  } finally { fs.rmSync(profile, { recursive: true, force: true }); }
}
if (only.includes('audio')) {
  // Sound: a browser with the normal autoplay policy (sound only after a user gesture). The menu music must be
  // silent before the first key press and audible after it; the intro movies (played without --skip-intro) must run.
  const b = await chromium.launch({ ...LAUNCH, args: LAUNCH.args.filter((a) => !a.startsWith('--autoplay-policy')) });
  const sample = (pg) => pg.evaluate(async () => { let peak = 0;
    for (let i = 0; i < 30; i++) { peak = Math.max(peak, globalThis.h5audio?.level() ?? 0); await new Promise((r) => setTimeout(r, 100)); }
    return { peak, states: globalThis.h5audio?.states() }; });
  try {
    let ctx = await b.newContext({ viewport: { width: 1280, height: 720 } });
    let page = await ctx.newPage();
    await run('audio', page, { query: '?nocache&audiotest&args=' + encodeURIComponent(opt('--audio-arg', '--skip-intro')), marker: /\[H5\] web: main menu on screen/, settle: 4,
      after: async (pg, log, stamp) => {
        const before = await sample(pg);
        await pg.keyboard.press('Shift');
        log.push(`[${stamp()}] pressed Shift`);
        await pg.waitForTimeout(1500);
        const after = await sample(pg);
        const ok = after.states?.includes('running') && after.peak > 0.001;
        return { result: ok ? 'ok' : 'silent', beforeGesture: before, afterGesture: after };
      } });
    await ctx.close();
    ctx = await b.newContext({ viewport: { width: 1280, height: 720 } });
    page = await ctx.newPage();
    await run('intro', page, { query: '?nocache&audiotest', marker: /\[H5\] web: video playing .*logo/, settle: 1,
      after: async (pg) => { await pg.keyboard.press('Shift'); await pg.waitForTimeout(1500); return { afterGesture: await sample(pg) }; } });
    await ctx.close();
  } finally { await b.close(); }
}
// Multiplayer: a desktop dedicated server (H5.Server, --server <H5.Server.dll>) on this machine; the browser joins its
// WebSocket port with the game's own --connect (same as typing the address in the lobby). "Connected to" means the server
// accepted the browser's hello, including the game-data fingerprint check.
//   lobby:     the page over http, ws://127.0.0.1:<port>/ straight to the server.
//   lobby-wss: the page over https (self-signed, like the LAN server's Caddy `tls internal`) and wss:// through that web
//              server's WebSocket proxy (/h5host/ -> the server, prefix stripped): the setup docs/WEB_BUILD.md describes.
async function withServer(name, fn) {
  const dll = opt('--server');
  if (!dll) { results[name] = { result: 'error', error: '--server <path to H5.Server.dll> is required for the ' + name + ' scenario' }; return; }
  const { spawn } = await import('node:child_process');
  const port = 20000 + Math.floor(Math.random() * 20000);
  const srv = spawn('dotnet', [dll, '--port', String(port), '--bind', '127.0.0.1', '--no-lan', '--game-dir', install, '--name', 'smoke server']);
  const srvLog = [];
  const ready = new Promise((r) => { const on = (d) => { srvLog.push(String(d)); if (/fingerprint/.test(String(d))) r(); }; srv.stdout.on('data', on); srv.stderr.on('data', on); });
  try {
    await Promise.race([ready, new Promise((r) => setTimeout(r, 60000))]);
    await fn(port);
  } finally { srv.kill(); fs.writeFileSync(path.join(outDir, `${name}-server.log`), srvLog.join('')); }
}
const LOBBY_MARKER = /\[H5\] web: lobby: Connected to smoke server/;
if (only.includes('lobby')) {
  await withServer('lobby', async (port) => {
    const b = await chromium.launch(LAUNCH);
    const ctx = await b.newContext({ viewport: { width: 1280, height: 720 } });
    const page = await ctx.newPage();
    await run('lobby', page, { query: '?nocache&args=' + encodeURIComponent(`--goto=create --connect=ws://127.0.0.1:${port}/`), marker: LOBBY_MARKER, settle: 3 });
    await ctx.close(); await b.close();
  });
}
if (only.includes('lobby-wss')) {
  await withServer('lobby-wss', async (port) => {
    const tlsServer = await serve(dist, 0, { coi: argv.includes('--coi'), tls: selfSignedCert(), proxy: { '/h5host/': `127.0.0.1:${port}` } });
    const tlsBase = `https://127.0.0.1:${tlsServer.address().port}/`;
    const b = await chromium.launch({ ...LAUNCH, args: [...LAUNCH.args, '--ignore-certificate-errors'] });
    try {
      const ctx = await b.newContext({ viewport: { width: 1280, height: 720 }, ignoreHTTPSErrors: true });
      const page = await ctx.newPage();
      await run('lobby-wss', page, { base: tlsBase, marker: LOBBY_MARKER, settle: 3,
        query: '?nocache&args=' + encodeURIComponent(`--goto=create --connect=wss://127.0.0.1:${tlsServer.address().port}/h5host/`) });
      await ctx.close();
    } finally { await b.close(); tlsServer.close(); }
  });
}
if (only.includes('persist')) {
  // user:// in IndexedDB: visit 1 quicksaves on an adventure map; visit 2 (same browser profile) must list the save,
  // export it as a zip and import it back under a new name (applied on the reload of visit 3).
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'h5web-'));
  try {
    let ctx = await chromium.launchPersistentContext(profile, { ...LAUNCH, viewport: { width: 1280, height: 720 } });
    let page = ctx.pages()[0] || await ctx.newPage();
    await run('persist-save', page, { query: '?nocache&args=' + encodeURIComponent(opt('--map-arg', '--adventure=A2M1') + ' --save-dev=quicksave'),
      marker: /\[H5\] web: adventure on screen/, then: [/\[H5\] saved "/, /user data changed/], settle: 2 });
    await ctx.close();
    ctx = await chromium.launchPersistentContext(profile, { ...LAUNCH, viewport: { width: 1280, height: 720 } });
    page = ctx.pages()[0] || await ctx.newPage();
    await run('persist-reload', page, { query: '?nocache&args=--skip-intro', marker: /\[H5\] web: user data .*saves\/[^,]+\.sav/, then: [/\[H5\] web: main menu on screen/], settle: 2,
      after: async (pg) => {
        const r = await pg.evaluate(async () => {
          const files = await h5saves.listUserFiles();
          const sav = files.find((f) => /saves\/[^/]+\.sav$/.test(f.name));
          if (!sav) return { result: 'no-save-in-idb', files: files.map((f) => f.name) };
          const zipped = h5saves.zip([{ name: sav.name.replace(/[^/]+\.sav$/, 'imported-copy.sav'), data: sav.data }]);
          const back = await h5saves.unzip(await zipped.arrayBuffer());
          await h5saves.stageImport([new File([zipped], 'export.zip')]);
          return { result: back.length === 1 && back[0].data.length === sav.data.length ? 'ok' : 'zip-roundtrip-failed', idbFiles: files.length, saveBytes: sav.data.length };
        });
        return r;
      } });
    page = ctx.pages()[0];
    await run('persist-import', page, { pick: true, query: '?nocache&args=--skip-intro', marker: /H5SAVES_IMPORTED 1/, then: [/\[H5\] web: user data .*imported-copy\.sav/], settle: 1 });
    await ctx.close();
  } finally { fs.rmSync(profile, { recursive: true, force: true }); }
}
server.close();
fs.writeFileSync(path.join(outDir, 'results.json'), JSON.stringify(results, null, 2));
if (opt('--record-shaders')) await writeShaderList(path.resolve(opt('--record-shaders')));
console.log(JSON.stringify(results, null, 2));
process.exit(Object.values(results).every((r) => r.result === 'ok' && !r.pageErrors) ? 0 : 1);
