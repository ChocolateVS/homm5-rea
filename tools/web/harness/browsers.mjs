// Headless browsers for the web-build harness (smoke.mjs, prof.mjs): finding them, launch flags, and a WebGL 2 probe.
//
// Headless Chromium sometimes has no WebGL 2 at all (no GPU process for it: driver, remote session, GPU busy or
// blocklisted). The page then stops with "features required to run Godot projects on the Web are missing: WebGL2".
// That is a property of the machine, not of the build, so launchWithWebGL2() probes WebGL 2 in a blank page first and
// walks a list of flag sets (GPU first when asked, then the SwiftShader software renderers) until one has it; when none
// has it the caller reports an ENVIRONMENT failure (exit code 3), never a page failure.
import { chromium, firefox } from 'playwright-core';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const cacheDir = () => process.env.PLAYWRIGHT_BROWSERS_PATH || (process.platform === 'win32'
  ? path.join(os.homedir(), 'AppData/Local/ms-playwright') : path.join(os.homedir(), '.cache/ms-playwright'));

export function findChromium() {
  if (process.env.CHROMIUM) return process.env.CHROMIUM;
  const base = cacheDir();
  const dirs = fs.existsSync(base) ? fs.readdirSync(base).filter((d) => /^chromium-\d+$/.test(d)).sort().reverse() : [];
  for (const d of dirs)
    for (const rel of ['chrome-win64/chrome.exe', 'chrome-win/chrome.exe', 'chrome-linux64/chrome', 'chrome-linux/chrome']) {
      const p = path.join(base, d, rel);
      if (fs.existsSync(p)) return p;
    }
  throw new Error('no Chromium found: set CHROMIUM or run `npx playwright-core install chromium`');
}

// Playwright's own Firefox build (`npx playwright-core install firefox`); $FIREFOX overrides. Undefined = let
// playwright-core use the revision it expects (it names the install command if that is missing).
export function findFirefox() {
  if (process.env.FIREFOX) return process.env.FIREFOX;
  const base = cacheDir();
  const dirs = fs.existsSync(base) ? fs.readdirSync(base).filter((d) => /^firefox-\d+$/.test(d)).sort().reverse() : [];
  for (const d of dirs)
    for (const rel of ['firefox/firefox.exe', 'firefox/firefox', 'firefox/Nightly.app/Contents/MacOS/firefox']) {
      const p = path.join(base, d, rel);
      if (fs.existsSync(p)) return p;
    }
  return undefined;
}

// --disable-domain-blocking-for-3d-apis: after a GPU process failure Chromium blocks WebGL for the page's site (here
// 127.0.0.1) for a while, so every following page of that browser reported "WebGL2 missing" although a blank page had it.
const COMMON = ['--enable-precise-memory-info', '--autoplay-policy=no-user-gesture-required', '--disable-domain-blocking-for-3d-apis'];
const GPU_SET = { name: 'gpu', args: ['--enable-gpu', '--ignore-gpu-blocklist', process.platform === 'win32' ? '--use-angle=d3d11' : '--use-angle=vulkan'] };
// Software renderers, in the order tried. All are headless and work without a GPU; they differ in which GL backend
// Chromium's GPU process gets (and whether there is a separate GPU process at all).
const SOFTWARE_SETS = [
  { name: 'swiftshader', args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader'] },
  { name: 'swiftshader-webgl', args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader-webgl', '--ignore-gpu-blocklist'] },
  { name: 'swiftshader-in-process', args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader', '--in-process-gpu', '--disable-gpu-sandbox', '--ignore-gpu-blocklist'] },
];

/** The flag sets to try: --gpu starts with the GPU and falls back to software. */
export function flagSets(gpu) { return gpu ? [GPU_SET, ...SOFTWARE_SETS] : SOFTWARE_SETS; }

/** Launch options for one flag set (Chromium) or for Firefox. */
export function launchOptions(kind, set, extraArgs = []) {
  if (kind === 'firefox') {
    return { executablePath: findFirefox(), headless: true,
      firefoxUserPrefs: { 'webgl.force-enabled': true, 'webgl.disabled': false, 'media.autoplay.default': 0,
        'media.autoplay.blocking_policy': 0 } };
  }
  return { executablePath: findChromium(), headless: true, args: [...set.args, ...COMMON, ...extraArgs] };
}

export const browserType = (kind) => (kind === 'firefox' ? firefox : chromium);

/**
 * WebGL 2 in a blank page of `browser`: { ok, renderer, tries } (renderer = the unmasked GL renderer string when exposed).
 * Asked up to `tries` times a second apart: right after launch Chromium's GPU process is often not up yet and the first
 * context fails (measured on Windows / D3D11: the first probe fails about 3 times in 4, the next ones succeed). Without
 * this wait the game page, the first page of a fresh browser, found no WebGL 2 "sometimes".
 */
export async function probeWebGL2(browser, tries = 6) {
  let r = { ok: false };
  for (let i = 1; i <= tries; i++) {
    r = await probeOnce(browser);
    if (r.ok) return { ...r, tries: i };
    if (i < tries) await new Promise((res) => setTimeout(res, 1000));
  }
  return { ...r, tries };
}

async function probeOnce(target) {
  // A Browser gets a throw-away context; a (persistent) BrowserContext a throw-away page.
  const ctx = target.newContext ? await target.newContext() : null;
  const page = await (ctx ?? target).newPage();
  try {
    await page.setContent('<canvas id=c></canvas>');
    return await page.evaluate(() => {
      const gl = document.getElementById('c').getContext('webgl2');
      if (!gl) return { ok: false };
      const dbg = gl.getExtension('WEBGL_debug_renderer_info');
      return { ok: true, renderer: String(dbg ? gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER)) };
    });
  } finally { await (ctx ? ctx.close() : page.close()); }
}

/** The page's own words when the browser lacks WebGL 2 (Godot's Engine.getMissingFeatures notice). */
export const WEBGL2_MISSING = /features required to run Godot projects on the Web are missing[\s\S]*WebGL2|WebGL2 - Check web browser/;

/**
 * Launches the first flag set (from index `from`) whose browser has WebGL 2.
 * Returns { browser, set, index, renderer, attempts } or { browser: null, attempts } when none has it.
 */
export async function launchWithWebGL2(kind, { gpu = false, from = 0, extraArgs = [], log = console.log } = {}) {
  const attempts = [];
  const sets = kind === 'firefox' ? [{ name: 'firefox' }] : flagSets(gpu);
  for (let i = from; i < sets.length; i++) {
    const set = sets[i];
    let browser;
    try {
      browser = await browserType(kind).launch(launchOptions(kind, set, extraArgs));
      const p = await probeWebGL2(browser);
      attempts.push({ flags: set.name, webgl2: p.ok, renderer: p.renderer ?? null, probes: p.tries });
      if (p.ok) { log(`browser: ${kind} (${set.name}), WebGL 2 renderer: ${p.renderer}`); return { browser, set, index: i, renderer: p.renderer, attempts }; }
      log(`browser: ${kind} (${set.name}) has no WebGL 2 - trying the next flags`);
    } catch (e) {
      attempts.push({ flags: set.name, error: String(e.message || e).split('\n')[0] });
      log(`browser: ${kind} (${set.name}) failed to start: ${String(e.message || e).split('\n')[0]}`);
    }
    await browser?.close().catch(() => {});
  }
  return { browser: null, attempts };
}
