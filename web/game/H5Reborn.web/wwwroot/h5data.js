// H5Reborn web: the player's own game data. Nothing of the original game is hosted; the player picks their install
// (or its data/*.pak) and the page serves byte ranges of those files to the game (H5Reborn.Web.BrowserData).
//
// Sources, in order:
//   1. a copy kept in the Origin Private File System (OPFS) from an earlier visit - no picking;
//   2. a folder picked with the File System Access API (showDirectoryPicker, Chromium);
//   3. <input type=file webkitdirectory> on the install folder (all browsers), or the .pak files themselves.
//
// Reads: C# calls readSync(file, offset, length, view). The browser main thread has exactly one synchronous way to
// read a File: a synchronous XMLHttpRequest with a Range header on the file's blob: URL (binary via the
// x-user-defined charset). A 1.4 GB data.pak is therefore never loaded whole - only the slices the game asks for.
'use strict';
(function () {
  const OPFS_DIR = 'h5install';
  const params = new URLSearchParams(location.search);
  const files = [];          // { name: 'data/data.pak', blob: File }
  let resolveReady;
  const readyPromise = new Promise((r) => (resolveReady = r));
  let chosen = false;

  // Install-relative paths the game reads. Archives are streamed; the rest are small and copied into the wasm FS.
  const KEEP = [
    /^data\/[^/]+\.pak$/i,
    /^usermods\/[^/]+\.(pak|h5u|zip)$/i,
    /^(maps|profiles|hwcursors|duelpresets|usercampaigns|video|music)\/.+/i,
  ];
  const keep = (p) => KEEP.some((re) => re.test(p));
  const complete = (list) => list.some((f) => /^data\/data\.pak$/i.test(f.name));

  // Diagnostics: the wasm linear memory, so the test harness can report its size.
  let wasmMemory = null;
  const grab = (imports, result) => {
    const inst = result?.instance ?? result;
    for (const m of [imports?.env?.memory, imports?.a?.memory, inst?.exports?.memory, ...Object.values(inst?.exports ?? {})])
      if (m instanceof WebAssembly.Memory && (!wasmMemory || m.buffer.byteLength > wasmMemory.buffer.byteLength)) wasmMemory = m;
    return result;
  };
  const origI = WebAssembly.instantiate, origS = WebAssembly.instantiateStreaming;
  WebAssembly.instantiate = (src, imports) => origI(src, imports).then((r) => grab(imports, r));
  if (origS) WebAssembly.instantiateStreaming = (src, imports) => origS(src, imports).then((r) => grab(imports, r));
  globalThis.h5WasmHeapBytes = () => wasmMemory?.buffer.byteLength;
  // Every growth of the wasm heap, logged in order with the game's own lines (the heap never shrinks: its size is
  // the high-water mark the 2 GB limit applies to).
  const origGrow = WebAssembly.Memory.prototype.grow;
  WebAssembly.Memory.prototype.grow = function (pages) {
    const r = origGrow.call(this, pages);
    if (this === wasmMemory || !wasmMemory) console.log(`[H5] web: wasm heap grew to ${Math.round(this.buffer.byteLength / 2 ** 20)} MB`);
    return r;
  };

  function use(list, how) {
    if (chosen) return;
    if (!complete(list)) { status('data/data.pak not found in the selection - pick the game folder (or its data/*.pak)'); return false; }
    files.length = 0;
    for (const f of list) files.push(f);
    chosen = true;
    const mb = files.reduce((s, f) => s + f.blob.size, 0) / 2 ** 20;
    status(`${files.length} files (${mb.toFixed(0)} MB) from ${how}`);
    console.log('H5DATA_READY ' + how + ' ' + files.length);
    hidePanel();
    resolveReady();
    return true;
  }

  // A plain file name (multi-file pick of the archives themselves) is placed where the game expects it.
  function place(p) {
    if (!p.includes('/')) {
      if (/\.pak$/i.test(p)) return 'data/' + p;
      if (/\.h5u$/i.test(p)) return 'UserMODs/' + p;
      if (/\.h5m$/i.test(p)) return 'Maps/' + p;
      // The stock key bindings (and options) the game copies into each new profile; no .pak holds them.
      if (/^(input|user)_a2\.cfg$/i.test(p)) return 'profiles/default_profile/' + p;
    }
    return p;
  }

  function fromFileList(list) {
    const out = [];
    for (const f of list) {
      const rel = (f.webkitRelativePath || f.name).replace(/\\/g, '/');
      // Strip the picked folder's own name: "Tribes of the East/data/data.pak" -> "data/data.pak".
      let p = f.webkitRelativePath ? rel.split('/').slice(1).join('/') : rel;
      // Picking the data folder itself: "data/data.pak" came out as "data.pak".
      if (f.webkitRelativePath && /^data\//i.test(rel) && !p.includes('/')) p = 'data/' + p;
      p = place(p);
      if (keep(p)) out.push({ name: p, blob: f });
    }
    return out;
  }

  async function fromDirectoryHandle(dir) {
    const out = [];
    async function walk(h, prefix, depth) {
      for await (const [name, child] of h.entries()) {
        const p = prefix + name;
        if (child.kind === 'directory') {
          if (depth < 4 && (depth > 0 || /^(data|usermods|maps|profiles|hwcursors|duelpresets|usercampaigns|video|music)$/i.test(name)))
            await walk(child, p + '/', depth + 1);
        } else if (keep(p)) out.push({ name: p, blob: await child.getFile() });
      }
    }
    await walk(dir, '', 0);
    if (!complete(out) && /^data$/i.test(dir.name)) { // picked the data folder itself
      for (const f of out) f.name = 'data/' + f.name;
      for await (const [name, child] of dir.entries())
        if (child.kind === 'file' && /\.pak$/i.test(name)) out.push({ name: 'data/' + name, blob: await child.getFile() });
    }
    return out;
  }

  async function opfsDir(create) {
    if (!navigator.storage?.getDirectory) return null;
    const root = await navigator.storage.getDirectory();
    try { return await root.getDirectoryHandle(OPFS_DIR, { create }); } catch { return null; }
  }

  async function fromOpfs() {
    const dir = await opfsDir(false);
    if (!dir) return [];
    const out = [];
    for await (const [fn, fh] of dir.entries())
      if (fh.kind === 'file' && !fn.endsWith('.part')) out.push({ name: decodeURIComponent(fn), blob: await fh.getFile() });
    return out;
  }

  // Streams the chosen files into OPFS (written as .part, renamed when complete) so the next visit starts without
  // picking. Runs in the background; the picked originals are used meanwhile.
  async function cacheToOpfs(list) {
    const dir = await opfsDir(true);
    if (!dir) { status('browser storage (OPFS) not available - the files will be asked for again next time'); return; }
    await navigator.storage.persist?.();
    let done = 0;
    for (const f of list) {
      const key = encodeURIComponent(f.name);
      try { if ((await (await dir.getFileHandle(key)).getFile()).size === f.blob.size) { done++; continue; } } catch { }
      const part = await dir.getFileHandle(key + '.part', { create: true });
      const w = await part.createWritable();
      await f.blob.stream().pipeTo(w);
      if (part.move) await part.move(key); else {
        const fin = await dir.getFileHandle(key, { create: true });
        const w2 = await fin.createWritable(); await (await part.getFile()).stream().pipeTo(w2);
        await dir.removeEntry(key + '.part');
      }
      done++;
    }
    console.log('H5DATA_OPFS_CACHED ' + done);
  }

  function status(text) {
    console.log('h5data: ' + text);
    const el = document.getElementById('h5-status');
    if (el) el.textContent = text;
  }

  // The picker's buttons had the focus; the game only receives keys while its canvas has it (Godot listens for keydown on
  // the canvas and cancels the browser's default there, e.g. Space scrolling or Tab moving the focus), so hand it back.
  function hidePanel() {
    const p = document.getElementById('h5-picker');
    if (p) p.style.display = 'none';
    const c = document.getElementById('canvas');
    if (c) { if (c.tabIndex < 0) c.tabIndex = 0; c.focus(); }
  }

  function buildUi() {
    const panel = document.createElement('div');
    panel.id = 'h5-picker';
    panel.innerHTML =
      '<div class="h5-card" role="dialog" aria-labelledby="h5-title">' +
      '<h1 id="h5-title">H5Reborn</h1>' +
      '<p class="h5-sub">Heroes of Might and Magic V: Tribes of the East</p>' +
      '<hr>' +
      '<p>To play, choose your own installed game folder (the one that contains the <code>data</code> folder), ' +
      'or select the <code>.pak</code> files from inside its <code>data</code> folder ' +
      '(add <code>profiles/default_profile/input_a2.cfg</code> to them for the keyboard shortcuts).</p>' +
      '<p>Your files stay on this computer. Nothing is uploaded.</p>' +
      '<div class="h5-actions">' +
      (window.showDirectoryPicker ? '<button id="h5-fsa" class="h5-btn" type="button">Choose game folder</button>' : '') +
      '<label class="h5-btn">' + (window.showDirectoryPicker ? 'Select folder (alternative)' : 'Choose game folder') +
      '<input id="h5-dir" class="h5-hidden-input" type="file" webkitdirectory></label>' +
      '<label class="h5-btn">Select .pak files' +
      '<input id="h5-paks" class="h5-hidden-input" type="file" multiple accept=".pak,.h5u,.h5m,.cfg"></label>' +
      '</div>' +
      '<p class="h5-check"><input id="h5-opfs" type="checkbox" checked>' +
      '<label for="h5-opfs">Remember the game files in this browser (about 2 GB), so you only choose them once.</label></p>' +
      '<p id="h5-status" aria-live="polite"></p>' +
      '<hr>' +
      '<p class="h5-foot">Press F11 for full screen. ' +
      '<button id="h5-forget" class="h5-btn h5-small" type="button">Forget stored files</button></p>' +
      '</div>';
    document.body.appendChild(panel);
    const pick = (list, how) => {
      if (!use(list, how)) return;
      if (document.getElementById('h5-opfs').checked && !params.has('nocache'))
        cacheToOpfs(files).catch((e) => console.warn('h5data: OPFS copy failed', e));
    };
    document.getElementById('h5-dir').addEventListener('change', (e) => pick(fromFileList(e.target.files), 'folder'));
    document.getElementById('h5-paks').addEventListener('change', (e) => pick(fromFileList(e.target.files), 'files'));
    document.getElementById('h5-fsa')?.addEventListener('click', async () => {
      try { pick(await fromDirectoryHandle(await window.showDirectoryPicker({ mode: 'read' })), 'folder'); }
      catch (e) { status('folder picker: ' + e.message); }
    });
    document.getElementById('h5-forget').addEventListener('click', async () => {
      const root = await navigator.storage?.getDirectory?.();
      await root?.removeEntry(OPFS_DIR, { recursive: true }).catch(() => {});
      status('stored copy removed');
    });
  }

  // Read-ahead (C#: RandomAccessSources.ReadAhead): ranges the game is about to read, in that order, are read here with
  // asynchronous Blob reads, several at a time and up to AHEAD_BYTES ahead of the game, while the game's own work goes on;
  // its synchronous reads then copy them from memory (readAheadTake) instead of a synchronous request each (1-6 ms).
  // A range is dropped once its expected bytes (`need`) were read, or when the game says it is done (readAheadDone).
  // Blob reads complete only while the page is between frames, so the queue runs ahead of the game's frame-sized steps.
  const AHEAD_BYTES = 96 << 20, AHEAD_PARALLEL = 32;
  const ahead = { queue: [], byFile: new Map(), inFlight: 0, held: 0, fetched: 0, fetchedBytes: 0, served: 0, late: 0, gen: 0 };
  function aheadPump() {
    while (ahead.inFlight < AHEAD_PARALLEL && ahead.queue.length && ahead.held < AHEAD_BYTES) {
      const r = ahead.queue.shift();
      if (r.dead) continue;
      ahead.inFlight++; ahead.held += r.length;
      const gen = ahead.gen;
      files[r.file].blob.slice(r.start, r.start + r.length).arrayBuffer().then((buf) => {
        if (gen !== ahead.gen) return;
        ahead.inFlight--;
        if (r.dead) { ahead.held -= r.length; } else { r.data = new Uint8Array(buf); ahead.fetched++; ahead.fetchedBytes += buf.byteLength; }
        aheadPump();
      }, (e) => { if (gen !== ahead.gen) return; ahead.inFlight--; ahead.held -= r.length; aheadDrop(r); console.warn('h5data: read-ahead failed', e); aheadPump(); });
    }
  }
  function aheadDrop(r) {
    r.dead = true;
    const list = ahead.byFile.get(r.file);
    const i = list ? list.indexOf(r) : -1;
    if (i >= 0) list.splice(i, 1);
    if (r.data) { ahead.held -= r.length; r.data = null; }
  }
  // The held range of `file` that contains [offset, offset + length), or null (ranges sorted by start).
  function aheadFind(file, offset, length) {
    const list = ahead.byFile.get(file);
    if (!list || !list.length) return null;
    let lo = 0, hi = list.length - 1, at = -1;
    while (lo <= hi) { const m = (lo + hi) >> 1; if (list[m].start <= offset) { at = m; lo = m + 1; } else hi = m - 1; }
    for (let k = at; k >= 0 && k > at - 4; k--) { const r = list[k]; if (r.start + r.length >= offset + length) return r; }
    return null;
  }

  const urls = new Map();
  const urlOf = (i) => { let u = urls.get(i); if (!u) { u = URL.createObjectURL(files[i].blob); urls.set(i, u); } return u; };

  globalThis.h5data = {
    // --- called from C# ([JSImport("globalThis.h5data.*")]) ---
    waitReady: () => readyPromise,
    count: () => files.length,
    name: (i) => files[i].name,
    size: (i) => files[i].blob.size,
    status,
    readSync: (i, offset, length, view) => {
      if (length <= 0) return 0;
      const x = new XMLHttpRequest();
      x.open('GET', urlOf(i), false);
      x.overrideMimeType('text/plain; charset=x-user-defined');
      x.setRequestHeader('Range', `bytes=${offset}-${offset + length - 1}`);
      x.send(null);
      if (x.status !== 206 && x.status !== 200) throw new Error(`h5data: read ${files[i].name} @${offset}: HTTP ${x.status}`);
      const s = x.responseText;
      const n = Math.min(s.length, length);
      const buf = new Uint8Array(n);
      for (let k = 0; k < n; k++) buf[k] = s.charCodeAt(k) & 0xff;
      view.set(buf, 0);
      return n;
    },
    readAhead: (text) => {
      for (const line of text.split('\n')) {
        if (!line) continue;
        const [file, start, length, need] = line.split(' ').map(Number);
        if (!(file >= 0 && file < files.length) || !(length > 0)) continue;
        const r = { file, start, length, need, served: 0, data: null, dead: false };
        ahead.queue.push(r);
        let list = ahead.byFile.get(file);
        if (!list) ahead.byFile.set(file, (list = []));
        let i = list.length;
        while (i > 0 && list[i - 1].start > start) i--;
        list.splice(i, 0, r);
      }
      aheadPump();
    },
    readAheadTake: (file, offset, length, view) => {
      const r = aheadFind(file, offset, length);
      if (!r) return false;
      // Still being read: its data can only arrive after the game yields, so read synchronously now and forget it.
      if (!r.data) { ahead.late++; aheadDrop(r); return false; }
      view.set(r.data.subarray(offset - r.start, offset - r.start + length));
      ahead.served++;
      if ((r.served += length) >= r.need) { aheadDrop(r); aheadPump(); }
      return true;
    },
    readAheadDone: () => {
      const s = `${ahead.fetched} ranges (${(ahead.fetchedBytes / 1048576).toFixed(1)} MB) read ahead, ${ahead.late} reads came too early, ${ahead.queue.filter((r) => !r.dead).length} not started`;
      ahead.gen++; ahead.queue = []; ahead.byFile.clear(); ahead.inFlight = 0; ahead.held = 0;
      ahead.fetched = ahead.fetchedBytes = ahead.served = ahead.late = 0;
      return s;
    },
    // --- test hooks ---
    params,
    cacheToOpfs: () => cacheToOpfs(files),
  };

  window.addEventListener('DOMContentLoaded', async () => {
    buildUi();
    if (params.has('fresh')) return;
    try {
      const cached = await fromOpfs();
      if (cached.length && complete(cached)) use(cached, 'browser storage');
    } catch (e) { console.warn('h5data: OPFS probe failed', e); }
  });
}());
