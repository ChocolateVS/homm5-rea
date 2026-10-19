# Web port feasibility spike (October 2026)

**Result: feasible. One original creature (Footman: model + Granny skeleton + idle animation +
DDS textures) loads from the player's own `data.pak` and renders at 60 fps in headless Chromium,
running Godot 4.7.2 C# with the game's unchanged `H5.Core`/`H5.Assets`/`H5.Net` sources.**
Constraints we keep to until the port starts are in [WEB_PORT.md](WEB_PORT.md). This page covers
what the spike showed, what it measured, what blocks a full port, and the work left.

## Toolchain: the recommended route

Godot 4.7.x still does **not** export C# projects to the web officially. The 4.7 docs say so
("projects written in C# using Godot 4 currently cannot be exported to the web"), and the
4.7.2 mono editor rejects a Web export of a .NET project. The route that works today is the
third-party **2dog** toolchain (outfox/2dog, https://2dog.dev). It links Godot as a static
library (libgodot) into a normal .NET `browser-wasm` app, so the .NET runtime and the engine share
one wasm module and the game's C# runs on the Mono interpreter. Pinned versions:

| piece | version |
|---|---|
| .NET SDK | **10.0.402**, installed per user in `%LOCALAPPDATA%\h5web\dotnet10` (dotnet-install.ps1, `-NoPath`); the main projects stay on net8.0 / SDK 8 |
| workload | `wasm-tools` 10.0.113 (manifest 10.0.400). Brings Emscripten 3.1.56 |
| 2dog tool / managed packages | **4.7.2.124** (`2dog`, `2dog.engine`, `2dog.godotsharp`) |
| 2dog native binaries | **4.7.2.28** (`2dog.browser-wasm`). Same Godot as ours: `v4.7.2.stable.mono.2dog` |
| Godot SDK | `Godot.NET.Sdk/4.7.2` |
| renderer | Compatibility (WebGL 2), forced in `project.godot` |
| threads | single-threaded (2dog default; `WasmEnableThreads` is experimental in .NET 10) |

Alternatives we considered and rejected:
- **Wait for official support.** Upstream is still on C# GDExtension, with web export to follow.
  There is no date.
- **Godot 3 Mono web export.** It would mean a full engine downgrade.
- **Rewriting client code in GDScript or C++.** That throws away the C# client.

2dog is a single-maintainer project, so **pin the versions** and keep the spike as a canary that
we rebuild when we bump them.

### Layout and commands

```
web/spike/H5WebSpike/            Godot project (net10.0, 2dog.godotsharp). Compiles src/H5.Core,
                                 src/H5.Assets and src/H5.Net as linked sources, so the .NET 10 build
                                 never writes into the desktop projects' obj/ folders
  SpikeMain.cs                   scene: waits for paks → mount → decode → build → measure
  BrowserPak.cs                  [JSImport] bindings + BrowserBlobSource (IRandomAccessSource)
  NetProbe.cs                    WebSocket / HTTP / WebRTC probe (?master=ws://host:port)
  H5WebSpike.web/                2dog browser host (net10.0, browser-wasm) → AppBundle/
    wwwroot/h5pak.js             file picking, OPFS cache, Blob.slice reads
web/spike/harness/               node static server + headless Chromium runner (playwright-core 1.56,
                                 uses the cached ms-playwright chromium-1234; never opens a window)
```

```sh
. "$LOCALAPPDATA/h5web/env.sh"                    # puts the private .NET 10 SDK first on PATH
cd web/spike/H5WebSpike && dotnet publish H5WebSpike.web   # Release by default → H5WebSpike.web/AppBundle
cd ../harness && npm install
node run.mjs ../H5WebSpike/H5WebSpike.web/AppBundle --dir "G:/HoMM 5 - Tribes of the East" \
     --page "?master=ws://127.0.0.1:27620" --shot ../out/creature.png
# or: --paks a.pak,b.pak   |   --page "?opfs" --opfs-reload  (copy to OPFS, reload, start without picking)
node serve.mjs ../H5WebSpike/H5WebSpike.web/AppBundle 8080  # to open it in a real browser
```

`nuget.config` in the spike lists only nuget.org, because the machine-level config has a private
feed that breaks restores. A clean publish takes 3–5 min on an idle machine and up to 20 min while
other builds run. Most of that time is the Emscripten link of libgodot plus `wasm-opt -O2`.

## What ran in the browser

Everything below ran in headless Chromium 151 (SwiftShader WebGL 2) with no COOP/COEP headers:

1. **Engine boot.** Godot 4.7.2 starts with `OpenGL ES 3.0 (WebGL 2.0) – Compatibility`. Scene
   scripts written in C# run (`SpikeMain : Node3D`), and `async`/`await` continuations run between
   frames.
2. **File access, all three ways the player can pick files:**
   - `<input type=file multiple>` with the `.pak` files;
   - `<input webkitdirectory>` on the **install folder**. The page keeps `data/*.pak` and
     `UserMODs/*.pak|*.h5u` and ignores everything else;
   - a File System Access `showDirectoryPicker()` button (Chromium only).

   With "remember" ticked, the files are streamed into **OPFS**. The copy is written as `.part`
   and renamed when complete. On the next visit the paks come from OPFS without picking (tested
   with `--opfs-reload`).
3. **PakArchive over Blob slices.** `data.pak` (1.35 GB, 100 008 entries) mounts by reading only
   its tail and central directory (~10 MB). Each entry is then one `Blob.slice().arrayBuffer()`
   read, copied into wasm memory through a `[JSImport]` `MemoryView` span with no managed
   marshalling.
4. **Decoding with the game's own code.** These run unchanged: `XdbLoader`, `AnimSetLoader`,
   `ModelLoader`, `GeometryFile`/`GeometryConverter`, `Gr2File` + `Oodle1` + `GrannyConvert`
   (skeleton and idle clip) and `DdsDecoder`. They run through
   `PakFileSystem.RunWithPrefetchAsync` (see below).
5. **Rendering.** A skinned mesh with a 45-bone `Skeleton3D`, a looping `idle00` from the Granny
   clip, and per-material textures (screenshot: `web/spike/out/creature.png`, not committed).
6. **Networking.**
   - The client's own `WebSocketFrameChannel` (ClientWebSocket, which is the browser WebSocket
     here) connects to `H5.Master` `/ws/signal` and receives and decodes the master's `RvMessage`.
   - Godot's native `WebRtcPeerConnection` works on the web platform with no GDExtension:
     `Initialize`, `CreateDataChannel` and `CreateOffer` all succeed.

## Measurements

From headless Chromium, SwiftShader (software GPU), on the dev machine while other builds ran.
Real GPUs will do better on the render side.

| | |
|---|---|
| download, uncompressed | `godot.wasm` (engine + .NET runtime) **47.4 MB**; `_framework` managed assemblies ~7.5 MB; total AppBundle ~84 MB including .br/.gz siblings |
| download, Brotli | `godot.wasm.br` **10.0 MB** + managed ~3.2 MB → **~13 MB on the wire** |
| boot to first frame | **1.2 s** from local cache (0.4 s download + 0.8 s start); 2.9–5.6 s cold or under load |
| mount `data.pak` | **0.28–0.57 s** (6 reads, 10.2 MB read: tail + central directory). All six stock paks: 0.8 s |
| decode Footman | **0.39–0.85 s** (1.5 s under load): 22 prefetch rounds, 27 slice reads, 0.6 MB of entries |
| textures | 2 DDS (426 KB). They stay **DXT on the GPU** (S3TC available in WebGL 2) in 20 ms; the software `DdsDecoder` → RGBA8 fallback takes 74–110 ms |
| GPU build | ~50–140 ms (mesh, skin, animation, materials) |
| total, paks picked → creature on screen | **0.75–2.5 s** |
| frame rate | **57–60 fps** (rAF 60.1) at idle with one animated creature on an otherwise idle machine; 50–55 fps while parallel builds load the CPU (SwiftShader renders on the CPU); 33 fps while a 1.6 GB OPFS copy streams in the background |
| memory | wasm linear memory **256 MB**, which is the initial reservation (`--initial-memory=268435456`, max 2 GB) and did not grow; .NET GC heap 55–58 MB (49 MB committed); JS heap 22–90 MB; Godot video memory 23–28 MB |
| OPFS copy of all six paks (1.6 GB) | ~135 s headless, in the background |

## Pak source abstraction (committed to H5.Core)

- `IRandomAccessSource` (`src/H5.Core/IO/RandomAccessSource.cs`) has three implementations:
  - `FileRandomAccessSource`: positional `RandomAccess` reads. It is used on the desktop and
    replaces the FileStream + lock.
  - `MemoryRandomAccessSource`: used by tests.
  - The spike's `BrowserBlobSource`. `SupportsSyncRead = false`.
- `PakArchive(IRandomAccessSource)`, `PakArchive.OpenAsync`, `ReadAsync`. `PakArchive(string)`
  is unchanged.
- `PakFileSystem.MountAsync`, `ReadAllBytesAsync` and `PrefetchAsync`, plus
  **`RunWithPrefetchAsync(work)`**. On the main browser thread, Blob data can only be read
  asynchronously, while every decoder in the game is synchronous. A synchronous read of an entry
  that has not been fetched yet therefore records the path and throws `PakEntryNotReadyException`.
  The runner fetches every recorded path and runs the (side-effect-free) load again.
  - Misses are recorded even when a decoder swallows the exception.
  - The work lambda must build fresh loaders on each round, because caches such as
    `AnimSetLoader._skeletons` must not keep the null from a failed round.
  - The Footman needs 22 rounds and about 0.4 s. A real game screen should first prefetch the
    paths it already knows (a manifest per screen or per creature), so the number of rounds stays
    small.
- Tests: `tests/H5.Tests/PakSourceTests.cs`. They cover file vs memory, sync vs async reads, reads
  of slices rather than the whole archive, prefetch, and swallowed misses. The desktop path is
  unchanged and `dotnet test H5Reborn.sln` passes.

## Threads (item 4): every place, and its fallback

The single-threaded browser runtime **throws `PlatformNotSupportedException` on `new Thread`,
`Thread.Start` and blocking waits on the main thread** (`Monitor.Wait`, `SemaphoreSlim.Wait`,
`Task.Wait`, `.Result`). `Task.Run` does not throw, but its work runs later on the same thread
between frames. Async loops are therefore fine, and blocking loops hang the page.

| where | what | browser status |
|---|---|---|
| `src/H5.AdvNet/AdventureNetSession.cs` `AiRunner` (l.733–800) | AI turns on a `new Thread`, ping-pong with two `SemaphoreSlim.Wait()`s so an AI turn can pause for a human battle | **BLOCKER.** No fallback. It needs a resumable form, for example the AI turn as an `async` method awaiting a `TaskCompletionSource` for the battle result, or an explicit continuation/coroutine. Single-player adventure AI uses the same path once the AI attacks the player. |
| `src/H5.Net/Transport/TcpFrameChannel.cs:48-49` | `Task.Run(ReadLoop/WriteLoop)` over TCP | not used in the browser (no raw sockets). Browser clients use `WebSocketFrameChannel` |
| `src/H5.Net/Transport/UdpHost.cs:60-61` | `Task.Run(ReceiveLoop/TickLoop)` | no UDP in browsers. `ManualPump` mode exists, but the browser path is WebSocket or WebRTC |
| `src/H5.Net/Discovery/LanDiscovery.cs:120-173` | UDP broadcast loops | no LAN discovery in browsers. Hide it |
| `src/H5.Net/LobbyServer.cs:90-101` | accept and sweep loops | a browser cannot listen. Hosting from a browser needs the WebRTC answerer (below) |
| `src/H5.Net/Master/MasterClient.cs:133,170` | `Task.Run(async Loop)` heartbeat; `Task.Run` punch burst | async with no blocking, so it would work, but it is host-side UDP. N/A in browsers |
| `src/H5.Master/RendezvousService.cs:58` | server only | N/A |
| `src/H5.Net/Transport/WebSocketFrameChannel.cs` | async receive loop + queue drain, no threads | **works** (verified) |
| `game/src/**` | only main-thread `async`/`await` (Godot synchronization context); no `Thread`, `Task.Run`, `WorkerThreadPool` or `ResourceLoader.LoadThreaded` | fine |
| `src/H5.Core`, `H5.Assets`, `H5.Combat`, `H5.Adventure`, `H5.Scripting`, `H5.Rmg`, `H5.Save` | single-threaded (`ConcurrentDictionary`/`lock` are harmless); `LuaSnapshot`'s `Thread` is a Lua coroutine record | fine |

The only real threading blocker is therefore `AiRunner`. Asset decoding is already synchronous
on the main thread. Large screens will need time-slicing (decode N items per frame) to avoid
hitches, because there is no background decode in a single-threaded build.

## Networking (item 5)

- **WebSocket works, after one fix.** `WebSocketFrameChannel.ConnectAsync` set
  `ClientWebSocketOptions.KeepAliveInterval`, which throws `PlatformNotSupportedException` in
  browsers. It is now guarded with `OperatingSystem.IsBrowser()` (committed with this spike).
  With the fix, the browser connects to `ws://master/ws/signal`, gets the master's `fail` RvMessage
  for an unknown code and decodes it.
- **JSON.** The H5.Net records use reflection-based `System.Text.Json`, and trimmed browser apps
  disable it (`JsonSerializerIsReflectionDisabled`). The spike sets
  `JsonSerializerIsReflectionEnabledByDefault=true`. For the port, add a source-generated
  `JsonSerializerContext` for the `MasterProtocol` and `Protocol` messages, which is smaller and
  trim-safe.
- **HTTP lobby API is blocked by CORS.** `GET /api/info` from the page origin fails because
  `H5.Master` sends no `Access-Control-Allow-Origin` header. Fix it in one of two ways:
  - (a) serve the web client from the same origin as the master, behind the same nginx/Caddy;
  - (b) add `app.UseCors(...)` for the read-only `/api/lobbies*`/`/api/info` endpoints, with a
    configurable origin list.

  WebSockets are not subject to CORS. The master should still check `Origin` on `/ws/*` if it
  wants to restrict which sites can embed the client.
- **WebRTC is feasible.** On the web platform, Godot exposes the browser's
  `RTCPeerConnection`/`RTCDataChannel` as `WebRtcPeerConnection`. In the spike, `Initialize`,
  `CreateDataChannel` and `CreateOffer` all succeeded. The signalling path (`/ws/signal`,
  RvMessage `rtc_offer`/`rtc_answer`/`rtc_ice`) already exists in the master. What is missing:
  - an `IFrameChannel` adapter over `WebRtcDataChannel`, in the Godot layer;
  - a desktop `IRtcAnswerer` (webrtc-native GDExtension) or browser-to-browser hosting.

  Until those exist, browsers join through the `/ws/join` relay bridge, which already lets
  desktop and browser players share a lobby. Browsers cannot host over UDP or TCP. Browser-hosted
  games need WebRTC, with a TURN server for symmetric NATs.

## Blockers and risks for the full port

1. **`AiRunner` threads.** This must become resumable (see the table above) before any adventure
   game can run in a browser.
2. **2dog dependency.** The project is third-party and young, and its versions must match our
   Godot exactly (4.7.2 ↔ 4.7.2.x). Keep the canary build, and switch to official export if it
   ships.
3. **Synchronous decoders vs asynchronous file data.** `RunWithPrefetchAsync` works, but each
   miss is one round. Real screens (battle: two armies, terrain, objects, effects, UI) should
   prefetch known path lists. The full-port loading code also needs per-screen loading phases.
4. **No background threads.** Long loads block frames: battle entry, adventure map load, and RMG
   generation (which is CPU-heavy in the Mono interpreter). Time-slice them, or measure
   `WasmEnableThreads` (it needs COOP/COEP and is experimental in .NET 10).
5. **Interpreter speed.** The game's C# runs interpreted, not AOT. Animation decoding and Oodle
   are fine at spike scale. AI turns, RMG and Lua-heavy maps must be measured. 2dog/.NET can AOT
   (`RunAOTCompilation`), at the cost of size and build time.
6. **Textures.** Godot's DDS loader needs the full mip chain. The spike uses `Game.PadMipChain`'s
   logic (copied as `DdsMips.cs`) to keep DXT compressed on the GPU, and the browser reports S3TC.
   Mobile browsers have no S3TC (they use ETC2/ASTC), so the software `DdsDecoder` fallback (RGBA8)
   must stay. That needs about 4× the VRAM, so budget it.
7. **Memory.** The wasm32 heap is capped at 2 GB in this build (`--max-memory=2147483648`). Keep
   entry bytes transient (`ClearPrefetched`) and evict textures and models per screen.
8. **Iteration time.** A publish takes 3–20 min. Develop on the desktop and use the web build as
   CI.

## Work breakdown for the full port (after desktop parity)

1. **Build.** Add a `web/H5Reborn.web` 2dog host next to `game/`, building the real game project.
   This needs a net10.0 build of the game assembly in a separate obj path, or a multi-target
   `net8.0;net10.0` for the game. Add a CI job that publishes it and runs the headless harness smoke test.
2. **Install picker.** Write the front-end screen in Godot UI that drives `h5pak.js`: folder, files,
   OPFS "remember", and mod `.h5u` files. Port `GameInstall.EnumeratePaks` ordering to a
   name-based overload in H5.Core, replacing the copy in `BrowserPak.cs`. Add a version check by
   pak fingerprint.
3. **Async loading layer.**
   - `Game` boots with `MountAsync`.
   - Every screen's loader gets a prefetch manifest plus `RunWithPrefetchAsync`.
   - Long work is time-sliced into frame-budgeted steps.
   - Add a loading-screen progress API.
4. **Remove mandatory threads.** Make `AiRunner` resumable, and audit Godot-side `WorkerThreadPool`
   use if any appears.
5. **Networking.**
   - Source-generated JSON contexts.
   - A browser transport selection in `NetClient`/lobby UI: WebSocket and the master bridge only;
     hide LAN, direct IP and hosting.
   - CORS or same-origin deployment of the master.
   - A WebRTC `IFrameChannel` adapter plus a desktop answerer (webrtc-native).
6. **Rendering audit on Compatibility.** `H5Lit.gdshader`, the water/overlay shaders and the
   particle bake all need it. Test every screen with `--rendering-method gl_compatibility` on the
   desktop first (cheap), then in the browser.
7. **Audio.** Ogg/WAV through Godot web audio, ADPCM decoded in C#. Browsers require a user
   gesture before audio can start.
8. **Saves and profiles.** Store `user://` in the browser (IndexedDB-backed via Godot) and offer
   export/import of save files.
9. **Performance pass.** Measure the interpreter on AI, RMG and Lua, and decide between AOT and
   threads. Set texture and VRAM budgets.
10. **Deployment.** A static host with Brotli (~13 MB transfer). COOP/COEP only if threads are
    enabled.
