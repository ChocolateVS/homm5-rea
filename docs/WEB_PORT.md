# Web port — constraints to respect now

The user wants a browser build once the desktop game matches the original. Godot 4.7 does not
officially export C# projects to the web (docs: tutorials/export/exporting_for_web); a third-party
route (2dog.browser-wasm, .NET 10 browser-wasm) exists but has **no System.Threading**. Upstream may
improve by the time we port. To keep the port feasible, all workstreams follow these rules:

1. **Assets come from the user's own copy.** Original files can never be hosted. The web build asks the
   player to pick their install's `data/*.pak` (File System Access API / file input) and keeps them in
   OPFS/IndexedDB. `PakArchive` must therefore read through a random-access source abstraction, not
   assume `FileStream` (data.pak is 1.4 GB — read slices on demand, never load whole).
2. **Rendering must work on the Compatibility renderer (WebGL 2).** Custom shaders: no compute,
   no Forward+-only features; test with `--rendering-method gl_compatibility` before relying on a feature.
   Keep VRAM/RAM budgets modest (wasm32 heap ≤ 4 GB, practical ~2 GB): stream and evict textures/models.
3. **No mandatory threads in client code paths.** Engine libraries (rules, scripting, AI) are already
   single-threaded and deterministic; keep it so. Client background work (asset decode, networking) must
   have a poll/main-thread fallback.
4. **Networking:** browsers can't open raw TCP/UDP — every server (master, dedicated, in-client host)
   accepts WebSocket clients; browser P2P uses WebRTC DataChannels with the master as signalling server;
   relay fallback works for WebSocket clients so desktop and browser players share lobbies.
   The user runs the master server on their own server.
5. **Audio:** Ogg/WAV via Godot; ADPCM is decoded in C# (fine on wasm).
