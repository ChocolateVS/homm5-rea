// H5Reborn web spike: lets the player pick their own game data (never hosted) and serves random-access
// slices of the picked files to C# (H5WebSpike.BrowserPak via [JSImport]). Files are read with
// Blob.slice(...).arrayBuffer() - a 1.4 GB data.pak is never loaded whole.
// Sources, in order of preference:
//   1. files previously copied to the Origin Private File System (OPFS) - no picking on later visits
//   2. a folder picked with the File System Access API (showDirectoryPicker, Chromium)
//   3. <input type=file webkitdirectory> (all browsers) or a plain multi-file input
'use strict';
(function () {
  const OPFS_DIR = 'h5paks';
  const files = [];          // { name, blob }
  const pending = new Map(); // token -> Uint8Array
  let nextToken = 1;
  let ready = false;         // set once a complete set has been chosen

  // Diagnostics: remember the wasm linear memory so the harness can report its size.
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

  const isPak = (p) => /(^|\/)data\/[^/]+\.pak$/i.test(p) || /(^|\/)usermods\/[^/]+\.(pak|h5u)$/i.test(p) || /^[^/]+\.(pak|h5u)$/i.test(p);

  function use(list, how) {
    files.length = 0;
    for (const f of list) files.push(f);
    ready = files.length > 0;
    status(`${files.length} pak(s) from ${how}: ` + files.map((f) => `${f.name} (${(f.blob.size / 2 ** 20).toFixed(1)} MB)`).join(', '));
  }

  function fromFileList(list) {
    const out = [];
    for (const f of list) {
      const rel = (f.webkitRelativePath || f.name).replace(/\\/g, '/');
      // Strip the picked folder's own name: "Tribes of the East/data/data.pak" -> "data/data.pak".
      const p = f.webkitRelativePath ? rel.split('/').slice(1).join('/') : rel;
      if (isPak(p)) out.push({ name: p, blob: f });
    }
    return out;
  }

  async function fromDirectoryHandle(dir) {
    const out = [];
    for await (const [name, h] of dir.entries()) {
      if (h.kind !== 'directory' || !/^(data|usermods)$/i.test(name)) continue;
      for await (const [fn, fh] of h.entries())
        if (fh.kind === 'file' && isPak(`${name}/${fn}`)) out.push({ name: `${name}/${fn}`, blob: await fh.getFile() });
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

  // Streams the chosen files into OPFS (written as .part, renamed when complete) so the next visit
  // can start without picking. Runs in the background; the originals are used meanwhile.
  async function cacheToOpfs(list) {
    const dir = await opfsDir(true);
    if (!dir) { status('OPFS not available'); return; }
    await navigator.storage.persist?.();
    for (const f of list) {
      const key = encodeURIComponent(f.name);
      const part = await dir.getFileHandle(key + '.part', { create: true });
      const w = await part.createWritable();
      await f.blob.stream().pipeTo(w);
      if (part.move) await part.move(key); else {
        const done = await dir.getFileHandle(key, { create: true });
        const w2 = await done.createWritable(); await (await part.getFile()).stream().pipeTo(w2);
        await dir.removeEntry(key + '.part');
      }
      status(`cached ${f.name} in OPFS`);
    }
    console.log('H5PAK_OPFS_CACHED ' + list.length);
  }

  function status(text) {
    console.log('h5pak: ' + text);
    const el = document.getElementById('h5-status');
    if (el) el.textContent = text;
  }

  function buildUi() {
    const panel = document.createElement('div');
    panel.id = 'h5-picker';
    panel.style.cssText = 'position:fixed;top:8px;left:8px;z-index:10;padding:8px 10px;border-radius:6px;' +
      'background:rgba(10,14,20,.85);color:#dde;font:13px system-ui,sans-serif;max-width:60vw';
    panel.innerHTML =
      '<div style="margin-bottom:4px">Select your Heroes V: Tribes of the East install (data/*.pak stay on your machine)</div>' +
      '<label>Install folder <input id="h5-dir" type="file" webkitdirectory></label> ' +
      '<label>or .pak files <input id="h5-paks" type="file" multiple accept=".pak,.h5u"></label> ' +
      (window.showDirectoryPicker ? '<button id="h5-fsa">Choose folder (File System Access)</button> ' : '') +
      '<label><input id="h5-opfs" type="checkbox"> remember (copy to browser storage)</label> ' +
      '<button id="h5-forget">forget cached copy</button>' +
      '<div id="h5-status" style="margin-top:4px;opacity:.8"></div>';
    document.body.appendChild(panel);
    const pick = (list, how) => {
      if (!list.length) { status('no data/*.pak found in the selection'); return; }
      use(list, how);
      if (document.getElementById('h5-opfs').checked) cacheToOpfs(list).catch((e) => status('OPFS copy failed: ' + e));
    };
    document.getElementById('h5-dir').addEventListener('change', (e) => pick(fromFileList(e.target.files), 'folder input'));
    document.getElementById('h5-paks').addEventListener('change', (e) => pick(fromFileList(e.target.files), 'file input'));
    document.getElementById('h5-fsa')?.addEventListener('click', async () => {
      try { pick(await fromDirectoryHandle(await window.showDirectoryPicker({ mode: 'read' })), 'File System Access'); }
      catch (e) { status('folder picker: ' + e.message); }
    });
    document.getElementById('h5-forget').addEventListener('click', async () => {
      const root = await navigator.storage?.getDirectory?.();
      await root?.removeEntry(OPFS_DIR, { recursive: true }).catch(() => {});
      status('cached copy removed');
    });
    if (new URLSearchParams(location.search).has('opfs')) document.getElementById('h5-opfs').checked = true;
  }

  globalThis.h5pak = {
    // --- called from C# ([JSImport("globalThis.h5pak.*")]) ---
    ready: () => ready,
    count: () => files.length,
    name: (i) => files[i].name,
    size: (i) => files[i].blob.size,
    read: async (i, offset, length) => {
      const buf = new Uint8Array(await files[i].blob.slice(offset, offset + length).arrayBuffer());
      if (buf.length !== length) throw new Error(`short read ${files[i].name} @${offset}: ${buf.length}/${length}`);
      const t = nextToken++;
      pending.set(t, buf);
      return t;
    },
    take: (token, view) => { const b = pending.get(token); pending.delete(token); view.set(b, 0); },
    param: (name) => new URLSearchParams(location.search).get(name),
    hidePicker: () => { const p = document.getElementById('h5-picker'); if (p) p.style.display = 'none'; },
    // --- diagnostics ---
    cacheToOpfs: () => cacheToOpfs(files),
  };

  window.addEventListener('DOMContentLoaded', async () => {
    buildUi();
    if (new URLSearchParams(location.search).has('fresh')) return;
    try {
      const cached = await fromOpfs();
      if (cached.length && !ready) use(cached, 'OPFS cache');
    } catch (e) { console.warn('h5pak: OPFS probe failed', e); }
  });
}());
