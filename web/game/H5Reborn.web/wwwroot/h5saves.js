// H5Reborn web: the game's user:// folder (profiles, saves, settings) and its export / import.
//
// Godot keeps user:// (/userfs in the wasm file system) in IndexedDB (Emscripten IDBFS, database "/userfs"): it is
// loaded when the engine starts and written back after the game changes a file (Godot does it after a FileAccess
// write; H5Reborn.Web.WebStorage also asks for it after System.IO writes). It survives reloads in the same browser.
//
// Export: every file of user:// as a .zip (paths relative to user://, e.g. profiles/Player/saves/x.sav).
// Import: a .zip of that shape (or single .sav files, which go to the current profile's saves) is staged in a
// separate database and merged into "/userfs" on the next page start, before the engine mounts it - so the running
// game's own write-back can never undo the import. The page reloads to apply it.
'use strict';
(function () {
  const USERFS = '/userfs', STORE = 'FILE_DATA', IDB_VERSION = 21; // Emscripten IDBFS (DB_VERSION / DB_STORE_NAME)
  // user:// itself (Godot web: /userfs/godot/app_userdata/<project name>); zip paths are relative to it.
  const USER_DIR = USERFS + '/godot/app_userdata/H5Reborn';
  const PENDING_DB = 'h5reborn-import';
  const FILE_MODE = 0o100666, DIR_MODE = 0o40777;

  const req = (r) => new Promise((ok, fail) => { r.onsuccess = () => ok(r.result); r.onerror = () => fail(r.error); });
  const done = (tx) => new Promise((ok, fail) => { tx.oncomplete = ok; tx.onerror = () => fail(tx.error); tx.onabort = () => fail(tx.error); });

  function openDb(name, version, upgrade) {
    const r = indexedDB.open(name, version);
    r.onupgradeneeded = () => upgrade(r.result, r.transaction);
    return req(r);
  }
  // Same schema as IDBFS.getDB, so whichever side creates the database first, the other can use it.
  const openUserFs = () => openDb(USERFS, IDB_VERSION, (db, tx) => {
    const s = db.objectStoreNames.contains(STORE) ? tx.objectStore(STORE) : db.createObjectStore(STORE);
    if (!s.indexNames.contains('timestamp')) s.createIndex('timestamp', 'timestamp', { unique: false });
  });
  const openPending = () => openDb(PENDING_DB, 1, (db) => { if (!db.objectStoreNames.contains('files')) db.createObjectStore('files'); });

  // ---------------------------------------------------------------- zip (stored entries out; stored/deflated in)
  const CRC = (() => { const t = new Uint32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; } return t; })();
  const crc32 = (b) => { let c = 0xffffffff; for (let i = 0; i < b.length; i++) c = CRC[(c ^ b[i]) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };

  function zip(entries) { // [{ name, data: Uint8Array, date: Date }]
    const enc = new TextEncoder(), parts = [], central = [];
    let offset = 0;
    for (const e of entries) {
      const name = enc.encode(e.name), d = e.date || new Date(), crc = crc32(e.data);
      const time = (d.getHours() << 11) | (d.getMinutes() << 5) | (d.getSeconds() >> 1);
      const date = ((Math.max(1980, d.getFullYear()) - 1980) << 9) | ((d.getMonth() + 1) << 5) | d.getDate();
      const h = new DataView(new ArrayBuffer(30));
      h.setUint32(0, 0x04034b50, true); h.setUint16(4, 20, true); h.setUint16(6, 0x0800, true); h.setUint16(8, 0, true);
      h.setUint16(10, time, true); h.setUint16(12, date, true); h.setUint32(14, crc, true);
      h.setUint32(18, e.data.length, true); h.setUint32(22, e.data.length, true); h.setUint16(26, name.length, true);
      parts.push(new Uint8Array(h.buffer), name, e.data);
      const c = new DataView(new ArrayBuffer(46));
      c.setUint32(0, 0x02014b50, true); c.setUint16(4, 20, true); c.setUint16(6, 20, true); c.setUint16(8, 0x0800, true);
      c.setUint16(12, time, true); c.setUint16(14, date, true); c.setUint32(16, crc, true);
      c.setUint32(20, e.data.length, true); c.setUint32(24, e.data.length, true); c.setUint16(28, name.length, true);
      c.setUint32(42, offset, true);
      central.push(new Uint8Array(c.buffer), name);
      offset += 30 + name.length + e.data.length;
    }
    const size = central.reduce((s, p) => s + p.length, 0);
    const end = new DataView(new ArrayBuffer(22));
    end.setUint32(0, 0x06054b50, true); end.setUint16(8, entries.length, true); end.setUint16(10, entries.length, true);
    end.setUint32(12, size, true); end.setUint32(16, offset, true);
    return new Blob([...parts, ...central, new Uint8Array(end.buffer)], { type: 'application/zip' });
  }

  async function unzip(buf) { // -> [{ name, data }]
    const v = new DataView(buf), u8 = new Uint8Array(buf), dec = new TextDecoder();
    let eocd = -1;
    for (let i = buf.byteLength - 22; i >= Math.max(0, buf.byteLength - 65557); i--) if (v.getUint32(i, true) === 0x06054b50) { eocd = i; break; }
    if (eocd < 0) throw new Error('not a zip file');
    const count = v.getUint16(eocd + 10, true);
    let p = v.getUint32(eocd + 16, true);
    const out = [];
    for (let n = 0; n < count; n++) {
      if (v.getUint32(p, true) !== 0x02014b50) throw new Error('broken zip directory');
      const method = v.getUint16(p + 10, true), csize = v.getUint32(p + 20, true);
      const nlen = v.getUint16(p + 28, true), xlen = v.getUint16(p + 30, true), clen = v.getUint16(p + 32, true);
      const local = v.getUint32(p + 42, true);
      const name = dec.decode(u8.subarray(p + 46, p + 46 + nlen));
      p += 46 + nlen + xlen + clen;
      if (name.endsWith('/')) continue;
      const start = local + 30 + v.getUint16(local + 26, true) + v.getUint16(local + 28, true);
      const raw = u8.subarray(start, start + csize);
      let data;
      if (method === 0) data = raw.slice();
      else if (method === 8) data = new Uint8Array(await new Response(new Blob([raw]).stream().pipeThrough(new DecompressionStream('deflate-raw'))).arrayBuffer());
      else throw new Error(`${name}: unsupported zip method ${method}`);
      out.push({ name, data });
    }
    return out;
  }

  // ---------------------------------------------------------------- export / import
  async function listUserFiles() {
    const db = await openUserFs();
    try {
      const tx = db.transaction(STORE, 'readonly'), store = tx.objectStore(STORE);
      const [keys, values] = await Promise.all([req(store.getAllKeys()), req(store.getAll())]);
      const out = [];
      keys.forEach((k, i) => {
        const e = values[i];
        if (typeof k === 'string' && k.startsWith(USER_DIR + '/') && e?.contents) out.push({ name: k.slice(USER_DIR.length + 1), data: new Uint8Array(e.contents), date: e.timestamp instanceof Date ? e.timestamp : new Date() });
      });
      return out;
    } finally { db.close(); }
  }

  async function exportZip() {
    const files = (await listUserFiles()).filter((f) => !f.name.endsWith('.h5sync') && !f.name.endsWith('.tmp'));
    const blob = zip(files);
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `H5Reborn-saves-${new Date().toISOString().slice(0, 10)}.zip`;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 60000);
    console.log(`h5saves: exported ${files.length} files`);
    return { files: files.length, bytes: blob.size };
  }

  // The current profile (user://profiles/current.cfg), for single .sav files.
  async function currentProfile() {
    const f = (await listUserFiles()).find((x) => x.name === 'profiles/current.cfg');
    const m = f && /name\s*=\s*"([^"]+)"/.exec(new TextDecoder().decode(f.data));
    return m ? m[1] : 'Player';
  }

  async function stageImport(fileList) {
    const entries = [];
    for (const file of fileList) {
      const buf = await file.arrayBuffer();
      if (/\.zip$/i.test(file.name)) entries.push(...await unzip(buf));
      else if (/\.sav$/i.test(file.name)) entries.push({ name: `profiles/${await currentProfile()}/saves/${file.name}`, data: new Uint8Array(buf) });
      else throw new Error(`${file.name}: expected a .zip export or .sav files`);
    }
    const clean = entries
      .map((e) => ({ ...e, name: e.name.replace(/\\/g, '/').replace(/^\/+/, '') }))
      .filter((e) => e.name && !e.name.split('/').some((s) => s === '..' || s === '.'));
    if (!clean.length) throw new Error('nothing to import');
    const db = await openPending();
    try {
      const tx = db.transaction('files', 'readwrite');
      for (const e of clean) tx.objectStore('files').put(e.data, e.name);
      await done(tx);
    } finally { db.close(); }
    console.log(`h5saves: staged ${clean.length} files, reloading`);
    return clean.length;
  }

  // Runs before the engine mounts /userfs (index.html awaits it): moves staged imports into the IDBFS database.
  async function applyPendingImport() {
    if (!globalThis.indexedDB) return 0;
    const pdb = await openPending();
    let staged;
    try {
      const store = pdb.transaction('files', 'readonly').objectStore('files');
      const [keys, values] = await Promise.all([req(store.getAllKeys()), req(store.getAll())]);
      staged = keys.map((k, i) => ({ name: k, data: values[i] }));
    } finally { pdb.close(); }
    if (!staged.length) return 0;
    const db = await openUserFs();
    try {
      const tx = db.transaction(STORE, 'readwrite'), store = tx.objectStore(STORE), now = new Date();
      const dirs = new Set();
      for (const e of staged) {
        const parts = (USER_DIR.slice(USERFS.length + 1) + '/' + e.name).split('/');
        for (let i = 1; i < parts.length; i++) dirs.add(parts.slice(0, i).join('/'));
        store.put({ timestamp: now, mode: FILE_MODE, contents: e.data }, `${USER_DIR}/${e.name}`);
      }
      for (const d of dirs) store.put({ timestamp: now, mode: DIR_MODE }, `${USERFS}/${d}`);
      await done(tx);
    } finally { db.close(); }
    const pdb2 = await openPending();
    try { const tx = pdb2.transaction('files', 'readwrite'); tx.objectStore('files').clear(); await done(tx); } finally { pdb2.close(); }
    console.log(`H5SAVES_IMPORTED ${staged.length}`);
    return staged.length;
  }

  // ---------------------------------------------------------------- UI: a small corner button over the game
  function buildUi() {
    const css = document.createElement('style');
    css.textContent =
      '#h5-saves-btn{position:fixed;left:4px;bottom:4px;z-index:20;opacity:.28;font:12px system-ui,sans-serif;padding:2px 6px;' +
      'background:#0e121a;color:#e2e6ee;border:1px solid #4a5568;border-radius:4px;cursor:pointer}#h5-saves-btn:hover{opacity:1}' +
      '#h5-saves{position:fixed;left:4px;bottom:30px;z-index:21;display:none;padding:.8rem 1rem;max-width:min(92vw,420px);' +
      'background:rgba(14,18,26,.96);color:#e2e6ee;border:1px solid #4a5568;border-radius:6px;font:14px/1.4 system-ui,sans-serif}' +
      '#h5-saves button{margin:.2rem .3rem .2rem 0}#h5-saves p{margin:.4rem 0}';
    document.head.appendChild(css);
    const btn = document.createElement('button');
    btn.id = 'h5-saves-btn'; btn.type = 'button'; btn.textContent = 'Saves';
    btn.title = 'Export or import your saves, profiles and settings';
    const panel = document.createElement('div');
    panel.id = 'h5-saves';
    panel.innerHTML =
      '<p><b>Saves, profiles and settings</b> are kept in this browser (IndexedDB) and survive reloads.</p>' +
      '<p><button id="h5-saves-export" type="button">Export (.zip)</button>' +
      '<label><button id="h5-saves-import" type="button">Import…</button><input id="h5-saves-file" type="file" accept=".zip,.sav" multiple hidden></label>' +
      '<button id="h5-saves-close" type="button">Close</button></p>' +
      '<p style="opacity:.75">Import takes an exported .zip (adds / replaces those files) or .sav files (into the current profile), then reloads the page.</p>' +
      '<p id="h5-saves-status" style="opacity:.85"></p>';
    document.body.append(btn, panel);
    const say = (t) => { document.getElementById('h5-saves-status').textContent = t; };
    btn.addEventListener('click', () => { panel.style.display = panel.style.display === 'block' ? 'none' : 'block'; });
    document.getElementById('h5-saves-close').addEventListener('click', () => { panel.style.display = 'none'; });
    document.getElementById('h5-saves-export').addEventListener('click', async () => {
      try { const r = await exportZip(); say(`exported ${r.files} files`); } catch (e) { say('export failed: ' + e.message); }
    });
    const input = document.getElementById('h5-saves-file');
    document.getElementById('h5-saves-import').addEventListener('click', () => input.click());
    input.addEventListener('change', async () => {
      try { const n = await stageImport(input.files); say(`importing ${n} files, reloading…`); setTimeout(() => location.reload(), 300); }
      catch (e) { say('import failed: ' + e.message); }
    });
    if (new URLSearchParams(location.search).has('saves')) panel.style.display = 'block';
  }
  window.addEventListener('DOMContentLoaded', buildUi);

  globalThis.h5saves = { applyPendingImport, exportZip, stageImport, listUserFiles, zip, unzip };
}());
