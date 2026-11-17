// H5Reborn web: "Report a problem" - sends a bug report to the server the page came from (POST api/report, received
// by tools/web/report-server; payload format and deployment: docs/WEB_BUILD.md, "Bug reports").
//
// A report holds the player's text plus, as the player chooses: a screenshot of the game canvas (downscaled JPEG),
// the newest save from user:// (h5saves.js), and the tail of the console log (the game's [H5] lines). Always: the
// build (build-info.json), the browser, and what the game last said about itself (globalThis.h5game, set by the
// web-only WebProbe autoload: scene, map, day, player).
// Open with the small button in the bottom-right corner, F9 (not bound by the game), or ?report.
'use strict';
(function () {
  const ENDPOINT = globalThis.H5_REPORT_URL || 'api/report';
  const FORMAT = 'h5reborn-report/1';
  const SHOT_MAX_W = 1280, SHOT_QUALITY = 0.8;
  const SAVE_MAX = 4 * 1024 * 1024;       // bytes; the server takes up to 6 MB
  const LOG_LINES = 1500, LOG_MAX = 256 * 1024;
  const HOTKEY = 'F9';

  // ---------------------------------------------------------------- console tail (from page start)
  const log = [];
  let logChars = 0;
  for (const level of ['log', 'info', 'warn', 'error']) {
    const orig = console[level].bind(console);
    console[level] = (...args) => {
      try {
        const line = (level === 'log' || level === 'info' ? '' : level.toUpperCase() + ': ') +
          args.map((a) => (typeof a === 'string' ? a : a instanceof Error ? `${a.name}: ${a.message}` : safeJson(a))).join(' ');
        log.push(line.length > 4000 ? line.slice(0, 4000) + '…' : line);
        logChars += line.length + 1;
        while (log.length > LOG_LINES || logChars > LOG_MAX) logChars -= log.shift().length + 1;
      } catch { /* never break logging */ }
      orig(...args);
    };
  }
  function safeJson(v) { try { return JSON.stringify(v); } catch { return String(v); } }
  window.addEventListener('error', (e) => console.error(`uncaught: ${e.message} (${e.filename}:${e.lineno})`));
  window.addEventListener('unhandledrejection', (e) => console.error(`unhandled rejection: ${e.reason?.message || e.reason}`));

  // ---------------------------------------------------------------- what goes into a report
  // The WebGL canvas is not preserved between frames: read it inside an animation frame callback, which runs after
  // the engine's own one of the same frame (registered earlier), while the frame is still in the drawing buffer.
  function captureCanvas() {
    const src = document.getElementById('canvas');
    if (!src || !src.width || !src.height) return Promise.resolve(null);
    return new Promise((resolve) => requestAnimationFrame(() => {
      try {
        const scale = Math.min(1, SHOT_MAX_W / src.width);
        const c = document.createElement('canvas');
        c.width = Math.round(src.width * scale); c.height = Math.round(src.height * scale);
        const g = c.getContext('2d');
        g.fillStyle = '#000'; g.fillRect(0, 0, c.width, c.height);
        g.drawImage(src, 0, 0, c.width, c.height);
        resolve(c.toDataURL('image/jpeg', SHOT_QUALITY));
      } catch (e) { console.warn('h5report: screenshot failed', e); resolve(null); }
    }));
  }

  async function newestSave() {
    if (!globalThis.h5saves?.listUserFiles) return null;
    const saves = (await globalThis.h5saves.listUserFiles()).filter((f) => /\.sav$/i.test(f.name));
    saves.sort((a, b) => b.date - a.date);
    return saves[0] || null;
  }

  const toB64 = (u8) => { let s = ''; for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000)); return btoa(s); };

  async function buildInfo() {
    try { const r = await fetch('build-info.json', { cache: 'no-store' }); return r.ok ? await r.json() : null; } catch { return null; }
  }

  /** The report as the server takes it (see docs/WEB_BUILD.md, "Bug reports"). */
  async function buildPayload({ text, contact, screenshot, save, withLog }) {
    const info = await buildInfo();
    const game = globalThis.h5game && typeof globalThis.h5game === 'object' ? { ...globalThis.h5game } : {};
    const args = new URLSearchParams(location.search).get('args');
    if (args) game.args = args;
    return {
      format: FORMAT,
      text,
      contact: contact || undefined,
      client: {
        platform: 'web', commit: info?.commit, dirty: info?.dirty, built: info?.built, aot: info?.aot, godot: info?.godot,
        userAgent: navigator.userAgent, language: navigator.language, page: location.origin + location.pathname,
        viewport: `${innerWidth}x${innerHeight}@${devicePixelRatio}`, time: new Date().toISOString(),
      },
      game,
      log: withLog ? log.join('\n') : undefined,
      screenshot: screenshot || undefined,
      save: save ? { name: save.name, data: toB64(save.data) } : undefined,
    };
  }

  async function send(payload) {
    const r = await fetch(ENDPOINT, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
    let body = null;
    try { body = await r.json(); } catch { /* not the report server */ }
    if (!r.ok || !body?.ok) throw new Error(body?.error || (r.status === 404 ? 'this site has no report server' : `HTTP ${r.status}`));
    return body.id;
  }

  // ---------------------------------------------------------------- UI (themed like the data picker in index.html)
  const CSS = `
#h5-report-btn{position:fixed;right:4px;bottom:4px;z-index:20;opacity:.28;padding:2px 8px;cursor:pointer;
  font:700 12px Georgia,serif;color:#f6e7bf;background:linear-gradient(180deg,#7a2a14,#57180a);border:1px solid #c9a24f;border-radius:4px}
#h5-report-btn:hover,#h5-report-btn:focus-visible{opacity:1;outline:none}
#h5-report{position:fixed;inset:0;z-index:30;display:none;align-items:center;justify-content:center;padding:16px;background:rgba(7,4,3,.55)}
#h5-report.open{display:flex}
#h5-report .h5r-card{width:min(100%,560px);max-height:calc(100vh - 32px);overflow:auto;padding:1.1rem 1.5rem 1rem;color:#2b1d0e;
  font:16px/1.45 Georgia,"Palatino Linotype","Book Antiqua",serif;background:linear-gradient(180deg,#efe1bb 0%,#e3cf9f 55%,#d6bd86 100%);
  border:3px solid #8a6a2c;border-radius:6px;box-shadow:0 0 0 2px #2b1d0e,0 0 0 5px #b8913f,0 18px 60px rgba(0,0,0,.75),inset 0 0 40px rgba(110,72,20,.35)}
#h5-report h2{margin:0 0 .5rem;text-align:center;font-size:1.5rem;letter-spacing:.05em;color:#5a1d0c;text-shadow:0 1px 0 rgba(255,240,200,.7)}
#h5-report textarea,#h5-report input[type=text]{box-sizing:border-box;width:100%;font:15px/1.4 Georgia,serif;color:#2b1d0e;
  background:rgba(255,248,225,.75);border:2px solid #8a6a2c;border-radius:4px;padding:.4rem .5rem}
#h5-report textarea{min-height:8rem;resize:vertical}
#h5-report label.h5r-row{display:flex;gap:.5rem;align-items:flex-start;margin:.35rem 0;font-size:.95rem}
#h5-report label.h5r-row input{margin-top:.3em;accent-color:#7a2a14}
#h5-report .h5r-dim{color:#6b4a1e;font-size:.85rem}
#h5-report img{display:block;max-width:100%;max-height:150px;margin:.3rem 0 .2rem;border:2px solid #8a6a2c}
#h5-report .h5r-actions{display:flex;gap:.6rem;justify-content:center;margin:.8rem 0 .3rem}
#h5-report button{cursor:pointer;padding:.45rem 1.1rem;font:700 1rem Georgia,serif;color:#f6e7bf;background:linear-gradient(180deg,#7a2a14,#57180a);
  border:2px solid #c9a24f;border-radius:4px;box-shadow:0 2px 6px rgba(0,0,0,.45),inset 0 1px 0 rgba(255,220,150,.35);text-shadow:0 1px 1px #000}
#h5-report button:hover,#h5-report button:focus-visible{background:linear-gradient(180deg,#9a3a1c,#6c2010);outline:none}
#h5-report button:disabled{opacity:.55;cursor:default}
#h5-report-status{min-height:1.4em;text-align:center;font-weight:700;color:#5a1d0c}`;

  let panel, shot = null, save = null, busy = false, lastFocus = null;
  const $ = (id) => document.getElementById(id);

  function buildUi() {
    const css = document.createElement('style');
    css.textContent = CSS;
    document.head.appendChild(css);
    const btn = document.createElement('button');
    btn.id = 'h5-report-btn'; btn.type = 'button'; btn.textContent = 'Report a problem';
    btn.title = `Send a bug report to this site's owner (${HOTKEY})`;
    panel = document.createElement('div');
    panel.id = 'h5-report';
    panel.setAttribute('role', 'dialog'); panel.setAttribute('aria-modal', 'true'); panel.setAttribute('aria-labelledby', 'h5-report-title');
    panel.innerHTML = `<form class="h5r-card" id="h5-report-form" novalidate>
<h2 id="h5-report-title">Report a problem</h2>
<label for="h5-report-text" class="h5r-dim">What happened, and what did you expect? (Where in the game, what you clicked…)</label>
<textarea id="h5-report-text" maxlength="20000" required></textarea>
<label class="h5r-row"><input type="checkbox" id="h5-report-shot" checked><span>Include a screenshot of the game
  <img id="h5-report-shot-img" alt="screenshot preview" hidden></span></label>
<label class="h5r-row"><input type="checkbox" id="h5-report-save"><span>Include my newest save <span id="h5-report-save-name" class="h5r-dim"></span></span></label>
<label class="h5r-row"><input type="checkbox" id="h5-report-log" checked><span>Include the game's log <span class="h5r-dim">(recent console lines)</span></span></label>
<label for="h5-report-contact" class="h5r-dim">Your name (optional)</label>
<input type="text" id="h5-report-contact" maxlength="200" autocomplete="nickname">
<p class="h5r-dim">The build, your browser and the current screen / map are always sent.</p>
<div class="h5r-actions"><button type="submit" id="h5-report-send">Send report</button><button type="button" id="h5-report-cancel">Cancel</button></div>
<div id="h5-report-status" role="status"></div>
</form>`;
    document.body.append(btn, panel);
    btn.addEventListener('click', open);
    $('h5-report-cancel').addEventListener('click', close);
    panel.addEventListener('mousedown', (e) => { if (e.target === panel) close(); });
    $('h5-report-form').addEventListener('submit', (e) => { e.preventDefault(); submit(); });
    try { $('h5-report-contact').value = localStorage.getItem('h5report.contact') || ''; } catch { /* no storage */ }
    if (new URLSearchParams(location.search).has('report')) open();
  }

  const say = (t) => { $('h5-report-status').textContent = t; };
  const isOpen = () => panel?.classList.contains('open');

  async function open() {
    if (!panel || isOpen()) return;
    lastFocus = document.activeElement;
    shot = await captureCanvas();             // before the dialog is shown (it is not on the canvas anyway)
    const img = $('h5-report-shot-img');
    img.hidden = !shot; if (shot) img.src = shot;
    $('h5-report-shot').disabled = !shot; $('h5-report-shot').checked = !!shot;
    panel.classList.add('open');
    say('');
    $('h5-report-text').focus();
    save = null;
    $('h5-report-save').disabled = true; $('h5-report-save').checked = false; $('h5-report-save-name').textContent = '(looking…)';
    try {
      save = await newestSave();
      if (save && save.data.length > SAVE_MAX) { $('h5-report-save-name').textContent = `(${save.name.split('/').pop()} is too big to send)`; save = null; }
      else $('h5-report-save-name').textContent = save ? `(${save.name.split('/').pop()}, ${Math.ceil(save.data.length / 1024)} KB)` : '(no saves yet)';
    } catch (e) { $('h5-report-save-name').textContent = '(could not read saves)'; }
    $('h5-report-save').disabled = !save;
  }

  function close() {
    if (!isOpen() || busy) return;
    panel.classList.remove('open');
    (lastFocus && lastFocus !== document.body ? lastFocus : document.getElementById('canvas'))?.focus?.();
  }

  async function submit() {
    if (busy) return;
    const text = $('h5-report-text').value.trim();
    if (!text) { say('Please describe the problem.'); $('h5-report-text').focus(); return; }
    const contact = $('h5-report-contact').value.trim();
    try { localStorage.setItem('h5report.contact', contact); } catch { /* no storage */ }
    busy = true; $('h5-report-send').disabled = true; say('Sending…');
    try {
      const payload = await buildPayload({ text, contact, screenshot: $('h5-report-shot').checked ? shot : null,
        save: $('h5-report-save').checked ? save : null, withLog: $('h5-report-log').checked });
      const id = await send(payload);
      console.log(`h5report: sent report ${id}`);
      $('h5-report-text').value = '';
      say(`Thank you! Report ${id} was sent.`);
      setTimeout(() => { busy = false; close(); }, 1800);
    } catch (e) {
      say('Could not send the report: ' + e.message);
      busy = false;
    } finally { $('h5-report-send').disabled = false; }
  }

  // Keys: F9 toggles the dialog; while it is open the game sees no keys (typing, Esc closes). Registered in the
  // capture phase before the engine starts, so this runs before the engine's own handlers.
  for (const type of ['keydown', 'keyup', 'keypress']) {
    window.addEventListener(type, (e) => {
      if (e.key === HOTKEY || e.code === HOTKEY) {
        e.stopImmediatePropagation(); e.preventDefault();
        if (type === 'keydown' && !e.repeat) { if (isOpen()) close(); else open(); }
        return;
      }
      if (isOpen()) {
        e.stopImmediatePropagation();
        if (type === 'keydown' && e.key === 'Escape') { e.preventDefault(); close(); }
      }
    }, true);
  }

  window.addEventListener('DOMContentLoaded', buildUi);
  globalThis.h5report = { open, close, buildPayload, send, captureCanvas, log };
}());
