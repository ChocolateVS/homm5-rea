// H5Reborn web build: compiles the game's WebGL shader programs ahead, in the background (docs/WEB_BUILD.md, Shaders).
//
// Godot compiles each shader program the first time something is drawn with it and waits for it: on Chrome / Windows
// (ANGLE on Direct3D) a big scene program takes 1-2 s, and the first main menu and map frames need ~50-100 of them, so
// the page froze for tens of seconds on a first visit. The browser keeps compiled programs in its GPU cache (later
// visits are fast) and finds them again for an identical program in a context with the same extensions enabled. So on a
// first visit this page compiles the programs listed in h5shaders.json (recorded from the game: `smoke.mjs
// --record-shaders`) in a hidden WebGL 2 context with KHR_parallel_shader_compile, several at a time without waiting,
// while the player picks the game folder and the engine boots; Godot's own compiles of the same programs then come from
// the cache. Nothing here changes what is drawn. Done on every visit: the browser's cache may not keep them all (a
// second visit in the same profile compiled the map's programs again), and a program already cached links in a few ms.
// Skipped when the browser has no KHR_parallel_shader_compile (it would block the page). ?noshaderwarm disables it.
// ?recordshaders (or globalThis.H5_RECORD_SHADERS): records every program the game links; h5shaders.recorded() returns it.
(function () {
  'use strict';
  const params = new URLSearchParams(location.search);
  const IN_FLIGHT = 6;

  // ---- recording ----------------------------------------------------------------------------------------------------
  const recording = params.has('recordshaders') || globalThis.H5_RECORD_SHADERS;
  const rec = { programs: [], seen: new Set(), exts: new Set() };
  if (recording && typeof WebGL2RenderingContext !== 'undefined') {
    const P = WebGL2RenderingContext.prototype;
    const src = new WeakMap(), type = new WeakMap(), att = new WeakMap(), tf = new WeakMap();
    const wrap = (name, f) => { const o = P[name]; P[name] = function (...a) { return f.call(this, o, ...a); }; };
    wrap('createShader', function (o, t) { const s = o.call(this, t); if (s) type.set(s, t); return s; });
    wrap('shaderSource', function (o, s, t) { src.set(s, t); return o.call(this, s, t); });
    wrap('attachShader', function (o, p, s) { (att.get(p) || att.set(p, []).get(p)).push(s); return o.call(this, p, s); });
    wrap('transformFeedbackVaryings', function (o, p, v, m) { tf.set(p, [Array.from(v), m]); return o.call(this, p, v, m); });
    wrap('getExtension', function (o, n) { const r = o.call(this, n); if (r && this.canvas && this.canvas.isConnected) rec.exts.add(n); return r; });
    wrap('linkProgram', function (o, p) {
      if (this.canvas && this.canvas.isConnected) {
        const e = { shaders: (att.get(p) || []).map((s) => [type.get(s), src.get(s)]), tf: tf.get(p) || null };
        const k = JSON.stringify(e);
        if (!rec.seen.has(k)) { rec.seen.add(k); rec.programs.push(e); }
      }
      return o.call(this, p);
    });
  }

  // ---- list format: shared line table, each shader a list of [first line, count] runs ---------------------------------
  function decode(list) {
    const shader = (runs) => { const out = []; for (let i = 0; i < runs.length; i += 2) for (let k = 0; k < runs[i + 1]; k++) out.push(list.lines[runs[i] + k]); return out.join('\n'); };
    return list.programs.map((p) => ({ shaders: p.s.map(([t, i]) => [t, shader(list.shaders[i])]), tf: p.tf || null }));
  }

  // ---- warming ------------------------------------------------------------------------------------------------------
  let state = 'idle', warmed = 0, total = 0, started = 0;
  async function warm() {
    if (recording || params.has('noshaderwarm')) return;
    let list;
    try {
      const r = await fetch('h5shaders.json', { cache: 'no-cache' });
      if (!r.ok) return;
      list = await r.json();
    } catch (e) { return; }
    const gl = document.createElement('canvas').getContext('webgl2', { alpha: false, antialias: false, stencil: false, depth: true });
    if (!gl) return;
    const supported = gl.getSupportedExtensions() || [];
    // The same extensions as the game's context: the cached program is only found again for the same set.
    for (const n of list.exts) if (supported.includes(n)) gl.getExtension(n);
    if (!gl.getExtension('KHR_parallel_shader_compile')) { state = 'no parallel compile'; return; }
    const programs = decode(list);
    total = programs.length; started = performance.now(); state = 'warming';
    let next = 0;
    const live = [];
    await new Promise((resolve) => {
      const step = () => {
        while (live.length && gl.getProgramParameter(live[0].p, 0x91B1 /* COMPLETION_STATUS_KHR */)) {
          const x = live.shift();
          gl.getProgramParameter(x.p, gl.LINK_STATUS);
          gl.deleteProgram(x.p); x.s.forEach((s) => gl.deleteShader(s));
          warmed++;
        }
        while (next < programs.length && live.length < IN_FLIGHT) {
          const e = programs[next++];
          const p = gl.createProgram();
          const s = e.shaders.map(([t, text]) => { const sh = gl.createShader(t); gl.shaderSource(sh, text); gl.compileShader(sh); gl.attachShader(p, sh); return sh; });
          if (e.tf) gl.transformFeedbackVaryings(p, e.tf[0], e.tf[1]);
          gl.linkProgram(p);
          live.push({ p, s });
        }
        if (live.length) setTimeout(step, 100); else resolve();
      };
      step();
    });
    state = 'done';
    console.log(`[H5] web: shaders warmed: ${warmed} programs in ${((performance.now() - started) / 1000).toFixed(1)} s`);
    gl.getExtension('WEBGL_lose_context')?.loseContext();
  }

  globalThis.h5shaders = {
    status: () => ({ state, warmed, total, seconds: started ? (performance.now() - started) / 1000 : 0 }),
    // Recorded programs, in the order the game linked them, and the extensions the game's context enabled.
    recorded: () => ({ exts: [...rec.exts], programs: rec.programs }),
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', warm); else warm();
}());
