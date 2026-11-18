// Headless smoke test of the web build: serves <dist>, opens it in a headless Chromium (never a visible window),
// feeds it the local game install through the page's folder picker, and screenshots each scenario.
//   node smoke.mjs <dist> --install "<game folder>" [--out DIR] [--only menu,duel,adventure,endturn,determinism,remember,persist,decoded,audio,lobby,lobby-wss] [--server H5.Server.dll] [--timeout s]
//                         [--map-arg --adventure=<map>] [--duel-arg --combat-vs-ai=1,2] [--det-arg --determinism-check=trace] [--gpu] [--coi] [--init extra-init-script.js]
//                         [--record-shaders web/game/H5Reborn.web/wwwroot/h5shaders.json]
// --pick-delay <s>: seconds between the page being ready and the folder being picked.
// --record-shaders: records every WebGL program the game links in the scenarios run (menu,duel,adventure make a good
// list) and writes the page's shader warm-up list (wwwroot/h5shaders.js; docs/WEB_BUILD.md, Shaders).
// --browser firefox: Playwright's Firefox ($FIREFOX, else `npx playwright-core install firefox`); default Chromium
// ($CHROMIUM, else the playwright cache: npx playwright-core install chromium).
// Exit code 0 only if every scenario reached its marker without a page error; 3 = environment failure (the headless
// browser has no WebGL 2 with any flag set, browsers.mjs; results.json `environment`); 1 = anything else.
import { browserType, launchOptions, launchWithWebGL2, probeWebGL2, WEBGL2_MISSING } from './browsers.mjs';
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
  // Engine results must be bit-identical to the desktop build (network games mix both): the seeded AI duels of
  // H5.Combat.DeterminismSuite against the hashes recorded on desktop. --det-arg '--determinism-check=trace' logs every
  // command's state hash (diff against a desktop log of the same run).
  determinism: { query: '?args=' + encodeURIComponent(opt('--det-arg', '--determinism-check')), marker: /\[H5\] determinism: done/, settle: 0,
    after: async (page, log) => {
      const done = log.map((l) => /\[H5\] determinism: done (\d+)\/(\d+) match/.exec(l)).find(Boolean);
      return { result: done && done[2] !== '0' && done[1] === done[2] ? 'ok' : 'mismatch', determinism: log.filter((l) => /determinism: /.test(l)).map((l) => l.replace(/^\[[^\]]*\] /, '')) };
    } },
};
const only = opt('--only', 'menu,duel,adventure').split(',');

// Browser: Chromium (default) or --browser firefox (Playwright's Firefox build). Default renderer: SwiftShader (software
// WebGL 2; works on any machine and server, slow for 3D scenes). --gpu: the machine's GPU through ANGLE (still headless,
// no window), falling back to SwiftShader when that browser has no WebGL 2 (browsers.mjs).
const GPU = argv.includes('--gpu');
const KIND = opt('--browser', 'chromium');
const BT = browserType(KIND);
let env = await launchWithWebGL2(KIND, { gpu: GPU });
const environment = { browser: KIND, attempts: [...env.attempts] };
if (!env.browser) envFailure('no flag set gave this headless browser WebGL 2');
let LAUNCH = launchOptions(KIND, env.set);
environment.flags = env.set.name; environment.renderer = env.renderer;

// Every further browser (persistent profiles, the audio and lobby browsers) is opened through these: they wait until its
// GPU process gives WebGL 2 (a fresh browser's first context often fails, browsers.mjs), else it is an environment failure.
async function persistent(profile) {
  const ctx = await BT.launchPersistentContext(profile, { ...LAUNCH, viewport: { width: 1280, height: 720 } });
  if (!(await probeWebGL2(ctx)).ok) { await ctx.close(); envFailure('a new browser profile got no WebGL 2'); }
  return ctx;
}
// One visit in a persistent profile: a page that finds no WebGL 2 is an environment failure, so the visit is made again
// in a newly opened browser on the same profile (twice at most). Returns the context (the caller closes it) and page.
async function visit(profile, name, scenario, before) {
  for (let attempt = 0; ; attempt++) {
    const ctx = await persistent(profile);
    const page = ctx.pages()[0] || await ctx.newPage();
    await before?.(page);
    await run(name, page, scenario);
    if (results[name].result !== 'env:webgl2-missing' || attempt >= 2) return { ctx, page };
    console.log(`${name}: the page found no WebGL 2 (environment) - reopening the browser and visiting again`);
    (environment.retries ??= []).push(name);
    await ctx.close();
  }
}
async function launch(opts) {
  const b = await BT.launch(opts);
  if (!(await probeWebGL2(b)).ok) { await b.close(); envFailure('a new browser got no WebGL 2'); }
  return b;
}

// The machine, not the build: results.json says so and the exit code is 3 (1 = a real page failure).
function envFailure(why) {
  fs.mkdirSync(outDir, { recursive: true });
  const r = { environment: { ...environment, failure: 'WebGL2 missing', detail: why } };
  fs.writeFileSync(path.join(outDir, 'results.json'), JSON.stringify(r, null, 2));
  console.log(`ENVIRONMENT FAILURE (not a page failure): ${why}. Tried: ` +
    environment.attempts.map((a) => `${a.flags}=${a.webgl2 ? 'webgl2' : a.error ? 'error' : 'no webgl2'}`).join(', ') +
    '. The GPU is not available to headless browsers on this machine right now; rerun later or on another machine.');
  process.exit(3);
}

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
  // The browser lost WebGL 2 (headless GPU process unavailable): an environment failure, reported at once.
  let envHit; const envMissing = new Promise((r) => (envHit = r));
  page.on('console', (m) => { if (WEBGL2_MISSING.test(m.text())) envHit('env:webgl2-missing'); });
  // Longest gap between two animation frames: how long the page was frozen by one synchronous piece of work.
  // Every gap over 400 ms is kept (page time it ended, length) for results.json `longFrames`.
  await page.addInitScript(() => { let last = 0; globalThis.h5MaxFrameGapMs = 0; globalThis.h5LongFrames = [];
    const f = (t) => { if (last) { globalThis.h5MaxFrameGapMs = Math.max(globalThis.h5MaxFrameGapMs, t - last); if (t - last > 400) globalThis.h5LongFrames.push([Math.round(t / 100) / 10, Math.round(t - last)]); } last = t; requestAnimationFrame(f); };
    requestAnimationFrame(f); globalThis.h5ResetFrameGap = () => { globalThis.h5MaxFrameGapMs = 0; }; });
  // --init <file.js>: an extra script that runs before the page's own (instrumentation for experiments).
  if (opt('--init')) await page.addInitScript({ path: path.resolve(opt('--init')) });
  if (opt('--record-shaders')) await page.addInitScript(() => { globalThis.H5_RECORD_SHADERS = true; });
  // --query <a&b>: extra page flags for every scenario (e.g. noshaderwarm, nodecoded).
  const extraQ = opt('--query');
  await page.goto(pageBase + (extraQ ? query.replace('?', '?' + extraQ + '&') : query));
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
  const res = await Promise.race([reached.then(() => 'ok'), envMissing, new Promise((r) => setTimeout(() => r('timeout'), timeoutS * 1000))]);
  if (res === 'ok') await page.waitForTimeout(settle * 1000);
  const extra = res === 'ok' && after ? await after(page, log, stamp) : undefined;
  if (opt('--record-shaders')) recordShaders(await page.evaluate(() => globalThis.h5shaders?.recorded()).catch(() => null), name);
  clearInterval(memTimer);
  const shot = path.join(outDir, `${name}.png`);
  await page.screenshot({ path: shot });
  const fps = await page.evaluate(() => new Promise((r) => { let n = 0; const s = performance.now();
    const f = () => { n++; if (performance.now() - s < 3000) requestAnimationFrame(f); else r(n / ((performance.now() - s) / 1000)); };
    requestAnimationFrame(f); }));
  const mem = await page.evaluate(() => ({ js: performance.memory?.usedJSHeapSize, wasm: globalThis.h5WasmHeapBytes?.(), gap: globalThis.h5MaxFrameGapMs, long: globalThis.h5LongFrames }));
  const shaderWarm = await page.evaluate(() => globalThis.h5shaders?.status()).catch(() => null);
  results[name] = { result: res, seconds: +((Date.now() - t0) / 1000).toFixed(1), screenshot: shot, pageErrors: errors.length,
    rafFps: Math.round(fps * 10) / 10, maxFrameGapMs: Math.round(mem.gap), longFrames: (mem.long || []).slice(0, 40), ...(extra || {}), wasmHeapMB: mem.wasm && Math.round(mem.wasm / 2 ** 20), jsHeapMB: mem.js && Math.round(mem.js / 2 ** 20), ...(shaderWarm ? { shaderWarm } : {}) };
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

let browser = env.browser;
for (const name of only.filter((n) => SCENARIOS[n])) {
  // A scenario whose page found no WebGL 2 runs again in a freshly probed browser (the next flag set when this one
  // no longer has it); twice at most.
  for (let attempt = 0; ; attempt++) {
    const ctx = await browser.newContext({ viewport: { width: 1280, height: 720 } });
    const page = await ctx.newPage();
    try { await run(name, page, SCENARIOS[name]); } catch (e) { results[name] = { result: 'error', error: String(e) }; console.log(name, e); }
    await ctx.close();
    if (results[name].result !== 'env:webgl2-missing' || attempt >= 2) break;
    console.log(`${name}: the page found no WebGL 2 (environment) - relaunching the browser and running it again`);
    await browser.close();
    env = await launchWithWebGL2(KIND, { gpu: GPU, from: env.index });
    environment.attempts.push(...env.attempts);
    if (!env.browser) envFailure(`the page lost WebGL 2 during "${name}" and no flag set got it back`);
    browser = env.browser; LAUNCH = launchOptions(KIND, env.set); environment.flags = env.set.name; environment.renderer = env.renderer;
    (environment.retries ??= []).push(name);
  }
}
await browser.close();

if (only.includes('remember')) {
  // "Remember" flow: the first visit picks and copies the files to OPFS (persistent profile); the second visit must
  // start the game without any picking.
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'h5web-'));
  try {
    let copied;
    let { ctx } = await visit(profile, 'remember-first', SCENARIOS.menu,
      (page) => { copied = new Promise((r) => page.on('console', (m) => { if (/H5DATA_OPFS_CACHED/.test(m.text())) r(); })); });
    await Promise.race([copied, new Promise((r) => setTimeout(r, timeoutS * 1000))]);
    await ctx.close();
    ({ ctx } = await visit(profile, 'remember-second', { ...SCENARIOS.menu, pick: false }));
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
    for (const name of ['decoded-first', 'decoded-second']) {
      const { ctx, page } = await visit(profile, name, q);
      // Let the background IndexedDB writes finish before the browser closes.
      if (name === 'decoded-first') await page.waitForTimeout(5000);
      await ctx.close();
      Object.assign(results[name], loadTimes(name));
    }
    const second = results['decoded-second'];
    if (second.result === 'ok' && !(second.cacheHits && !second.cacheHits.startsWith('0/'))) second.result = 'no cache hits';
  } finally { fs.rmSync(profile, { recursive: true, force: true }); }
}
if (only.includes('audio')) {
  // Sound: a browser with the normal autoplay policy (sound only after a user gesture). The menu music must be
  // silent before the first key press and audible after it; the intro movies (played without --skip-intro) must run.
  const b = await launch(KIND === 'firefox' ? { ...LAUNCH, firefoxUserPrefs: { ...LAUNCH.firefoxUserPrefs, 'media.autoplay.default': 1 } }
    : { ...LAUNCH, args: LAUNCH.args.filter((a) => !a.startsWith('--autoplay-policy')) });
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
    const b = await launch(LAUNCH);
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
    const b = await launch(KIND === 'firefox' ? LAUNCH : { ...LAUNCH, args: [...LAUNCH.args, '--ignore-certificate-errors'] });
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
    let { ctx, page } = await visit(profile, 'persist-save', { query: '?nocache&args=' + encodeURIComponent(opt('--map-arg', '--adventure=A2M1') + ' --save-dev=quicksave'),
      marker: /\[H5\] web: adventure on screen/, then: [/\[H5\] saved "/, /user data changed/], settle: 2 });
    await ctx.close();
    ({ ctx, page } = await visit(profile, 'persist-reload', { query: '?nocache&args=--skip-intro', marker: /\[H5\] web: user data .*saves\/[^,]+\.sav/, then: [/\[H5\] web: main menu on screen/], settle: 2,
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
      } }));
    page = ctx.pages()[0];
    await run('persist-import', page, { pick: true, query: '?nocache&args=--skip-intro', marker: /H5SAVES_IMPORTED 1/, then: [/\[H5\] web: user data .*imported-copy\.sav/], settle: 1 });
    await ctx.close();
  } finally { fs.rmSync(profile, { recursive: true, force: true }); }
}
server.close();
fs.writeFileSync(path.join(outDir, 'results.json'), JSON.stringify({ ...results, environment }, null, 2));
if (opt('--record-shaders')) await writeShaderList(path.resolve(opt('--record-shaders')));
console.log(JSON.stringify({ ...results, environment }, null, 2));
// 0 = all ok; 3 = only environment failures (no WebGL 2 in this browser); 1 = a real failure.
const all = Object.values(results);
const real = all.filter((r) => !(r.result === 'ok' && !r.pageErrors) && !String(r.result).startsWith('env:'));
if (!real.length && all.some((r) => String(r.result).startsWith('env:'))) console.log('ENVIRONMENT FAILURE (not a page failure): the page found no WebGL 2 in this headless browser.');
process.exit(real.length ? 1 : all.some((r) => String(r.result).startsWith('env:')) ? 3 : 0);
