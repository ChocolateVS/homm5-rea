// H5Reborn web build: decoded assets kept between visits (docs/WEB_BUILD.md, Loading an adventure map).
//
// The game's loaders are synchronous, IndexedDB is not: before building an adventure map the game names the entries it
// will read (prefetch), they are loaded into memory here, and the C# side then reads them synchronously (get). New
// entries (put) go into memory at once and into IndexedDB in the background. Keys come prefixed with the install's data
// fingerprint (names and sizes of the paks) and a format version, so a changed install never sees another's data.
// Database "h5reborn-decoded": store "data" (key -> bytes), store "info" (key -> {n: size, t: last write/use}).
// The total is kept under LIMIT bytes by dropping the entries used longest ago. ?nodecoded disables the cache.
(function () {
  'use strict';
  const LIMIT = 384 << 20;
  const params = new URLSearchParams(location.search);
  const enabled = typeof indexedDB !== 'undefined' && !params.has('nodecoded');
  const mem = new Map();
  let dbp = null, puts = 0, hits = 0, misses = 0, cleaning = false, flushTimer = 0;
  let pending = [];

  // New entries go to IndexedDB in batches (one transaction per quarter second).
  async function flush() {
    flushTimer = 0;
    const batch = pending; pending = [];
    if (!batch.length) return;
    try {
      const d = await db();
      const tx = d.transaction(['data', 'info'], 'readwrite');
      const now = Date.now();
      for (const [k, v] of batch) { tx.objectStore('data').put(v.buffer, k); tx.objectStore('info').put({ n: v.length, t: now }, k); }
      await done(tx);
      if (Math.floor(puts / 500) !== Math.floor((puts + batch.length) / 500)) cleanup();
      puts += batch.length;
    } catch (e) { console.warn('h5cache: write failed', e); }
  }

  function db() {
    if (!dbp) dbp = new Promise((res, rej) => {
      const r = indexedDB.open('h5reborn-decoded', 1);
      r.onupgradeneeded = () => {
        r.result.createObjectStore('data');
        r.result.createObjectStore('info').createIndex('t', 't');
      };
      r.onsuccess = () => res(r.result);
      r.onerror = () => rej(r.error);
    });
    return dbp;
  }
  const done = (tx) => new Promise((res, rej) => { tx.oncomplete = res; tx.onerror = tx.onabort = () => rej(tx.error); });

  // Drops the entries used longest ago until the total is under LIMIT (reads only the small "info" records).
  async function cleanup() {
    if (cleaning) return;
    cleaning = true;
    try {
      const d = await db();
      const infos = [];
      await new Promise((res, rej) => {
        const c = d.transaction('info').objectStore('info').index('t').openCursor();
        c.onsuccess = () => { const cur = c.result; if (!cur) return res(); infos.push([cur.primaryKey, cur.value.n]); cur.continue(); };
        c.onerror = () => rej(c.error);
      });
      let total = infos.reduce((a, [, n]) => a + n, 0);
      if (total <= LIMIT) return;
      const tx = d.transaction(['data', 'info'], 'readwrite');
      for (const [k, n] of infos) {
        if (total <= LIMIT * 0.8) break;
        tx.objectStore('data').delete(k); tx.objectStore('info').delete(k); mem.delete(k); total -= n;
      }
      await done(tx);
      console.log(`[H5] web: decoded cache trimmed to ${(total / 1048576).toFixed(0)} MB`);
    } catch (e) { console.warn('h5cache: cleanup failed', e); } finally { cleaning = false; }
  }

  globalThis.h5cache = {
    enabled: () => enabled,
    // Loads the named entries (keys joined by '\n') into memory; resolves to the number found.
    prefetch: async (joined) => {
      if (!enabled) return 0;
      const keys = joined ? joined.split('\n').filter((k) => k && !mem.has(k)) : [];
      if (!keys.length) return 0;
      const t0 = performance.now();
      let found = 0, bytes = 0;
      try {
        const d = await db();
        const tx = d.transaction(['data', 'info'], 'readwrite');
        const data = tx.objectStore('data'), info = tx.objectStore('info'), now = Date.now();
        for (const k of keys) {
          const r = data.get(k);
          r.onsuccess = () => {
            if (r.result === undefined) return;
            const v = new Uint8Array(r.result);
            mem.set(k, v); found++; bytes += v.length;
            info.put({ n: v.length, t: now }, k);
          };
        }
        await done(tx);
      } catch (e) { console.warn('h5cache: prefetch failed', e); }
      console.log(`[H5] web: decoded cache: ${found} of ${keys.length} entries (${(bytes / 1048576).toFixed(1)} MB) in ${(performance.now() - t0).toFixed(0)} ms`);
      return found;
    },
    get: (key) => {
      const v = enabled ? mem.get(key) : undefined;
      if (v) hits++; else misses++;
      return v || null;
    },
    put: (key, bytes) => {
      if (!enabled) return;
      const v = bytes.slice(); // the view points into wasm memory: copy
      mem.set(key, v);
      pending.push([key, v]);
      if (!flushTimer) flushTimer = setTimeout(flush, 250);
    },
    // Lets go of the in-memory copies (they stay in IndexedDB); called when a map is up.
    release: () => { const n = mem.size; mem.clear(); return n; },
    stats: () => `hits=${hits} misses=${misses} inMemory=${mem.size}`,
    clear: async () => { mem.clear(); const d = await db(); const tx = d.transaction(['data', 'info'], 'readwrite'); tx.objectStore('data').clear(); tx.objectStore('info').clear(); await done(tx); },
  };
  if (enabled) setTimeout(cleanup, 20000);
}());
