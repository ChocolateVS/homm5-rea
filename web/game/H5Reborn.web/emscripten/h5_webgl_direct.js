// Emscripten JS library (linked with --js-library by H5Reborn.web.csproj, single-threaded web build only).
//
// Godot asks for its WebGL context with explicitSwapControl (it ends each frame with emscripten_webgl_commit_frame).
// Without OffscreenCanvas, Emscripten (-sOFFSCREEN_FRAMEBUFFER=1, set by 2dog like Godot's own web link) emulates that
// with an offscreen framebuffer + preserveDrawingBuffer and copies it to the canvas in every commit; that copy starts
// with gl.getParameter() calls, which make the page wait for the GPU process to finish the whole frame (a browser CPU
// profile of the adventure map: ~54 % of the frame in getParameter; docs/WEB_BUILD.md, Performance).
//
// Godot's main loop runs from requestAnimationFrame and redraws the whole frame each time, so the browser's implicit
// swap at the end of the callback presents the same picture. This override creates the context straight on the canvas:
// no offscreen framebuffer, no copy, no preserveDrawingBuffer; emscripten_webgl_commit_frame then returns 0 without
// doing anything (explicitSwapControl stays set, no defaultFbo). Otherwise identical to Emscripten 3.1.56's
// emscripten_webgl_do_create_context (src/library_html5_webgl.js) for a build without threads / OffscreenCanvas.
addToLibrary({
  // (webglPowerPreferences is Emscripten-internal: its three values are spelled out below.)
  emscripten_webgl_do_create_context__deps: ['$GL', '$findCanvasEventTarget'],
  emscripten_webgl_do_create_context: (target, attributes) => {
    var a = {{{ getHeapOffset('attributes', 'i32') }}};
    var powerPreference = HEAP32[a + ({{{ C_STRUCTS.EmscriptenWebGLContextAttributes.powerPreference }}}>>2)];
    var contextAttributes = {
      'alpha': !!HEAP32[a + ({{{ C_STRUCTS.EmscriptenWebGLContextAttributes.alpha }}}>>2)],
      'depth': !!HEAP32[a + ({{{ C_STRUCTS.EmscriptenWebGLContextAttributes.depth }}}>>2)],
      'stencil': !!HEAP32[a + ({{{ C_STRUCTS.EmscriptenWebGLContextAttributes.stencil }}}>>2)],
      'antialias': !!HEAP32[a + ({{{ C_STRUCTS.EmscriptenWebGLContextAttributes.antialias }}}>>2)],
      'premultipliedAlpha': !!HEAP32[a + ({{{ C_STRUCTS.EmscriptenWebGLContextAttributes.premultipliedAlpha }}}>>2)],
      'preserveDrawingBuffer': !!HEAP32[a + ({{{ C_STRUCTS.EmscriptenWebGLContextAttributes.preserveDrawingBuffer }}}>>2)],
      'powerPreference': ['default', 'low-power', 'high-performance'][powerPreference],
      'failIfMajorPerformanceCaveat': !!HEAP32[a + ({{{ C_STRUCTS.EmscriptenWebGLContextAttributes.failIfMajorPerformanceCaveat }}}>>2)],
      majorVersion: HEAP32[a + ({{{ C_STRUCTS.EmscriptenWebGLContextAttributes.majorVersion }}}>>2)],
      minorVersion: HEAP32[a + ({{{ C_STRUCTS.EmscriptenWebGLContextAttributes.minorVersion }}}>>2)],
      enableExtensionsByDefault: HEAP32[a + ({{{ C_STRUCTS.EmscriptenWebGLContextAttributes.enableExtensionsByDefault }}}>>2)],
      explicitSwapControl: HEAP32[a + ({{{ C_STRUCTS.EmscriptenWebGLContextAttributes.explicitSwapControl }}}>>2)],
      proxyContextToMainThread: HEAP32[a + ({{{ C_STRUCTS.EmscriptenWebGLContextAttributes.proxyContextToMainThread }}}>>2)],
      // The one change: never render via the offscreen back buffer (see above).
      renderViaOffscreenBackBuffer: 0
    };
    var canvas = findCanvasEventTarget(target);
    if (!canvas) return 0;
    var contextHandle = GL.createContext(canvas, contextAttributes);
    if (contextHandle) out('[H5] web: WebGL context on the canvas directly (no offscreen back buffer)');
    return contextHandle;
  },
});
