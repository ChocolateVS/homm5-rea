# Web build (browser version)

The browser build is a routine output of the repository: one script turns the current sources into a static
folder that any web server can host. The page asks each player for **their own** game install (nothing of the
original game is in the repository or the build output) and runs the real game: the same Boot scene, `Game`
autoload, main menu, combat and adventure code as the desktop build.

Background: [WEB_PORT.md](WEB_PORT.md) (rules every workstream follows) and [WEB_SPIKE.md](WEB_SPIKE.md) (the
October 2026 feasibility spike). Godot 4.7 has no official C# web export; we use the third-party **2dog**
toolchain, which links Godot as a library into a .NET 10 `browser-wasm` app.

## Pinned toolchain

| piece | version | where it is pinned |
|---|---|---|
| .NET SDK | **10.0.402** (per user, does not touch the system dotnet; the desktop build stays on .NET 8) | `tools/web/setup.*`, `web/game/global.json` |
| wasm-tools workload | manifests **10.0.113** (feature band 10.0.100), brings **Emscripten 3.1.56** | `tools/web/workload-rollback.json` |
| 2dog | `2dog.godotsharp` / `2dog.engine` **4.7.2.124**, natives `2dog.browser-wasm` **4.7.2.28** | `web/game/Directory.Build.props` |
| Godot | **4.7.2** (`Godot.NET.Sdk/4.7.2`, 2dog's `v4.7.2.stable.mono.2dog`) | `web/game/H5Reborn.csproj` |
| renderer | Compatibility (WebGL 2), set on the staged copy of `project.godot` | `tools/web/build.sh` |
| threads | none (single-threaded wasm): **no SharedArrayBuffer, no COOP/COEP needed** | `TwoDogWebThreads=false` |
| test harness | Node ≥ 20, `playwright-core` **1.56.0**, its Chromium (headless) | `tools/web/harness/package.json` |

Packages come from nuget.org only (`web/game/nuget.config`), so a machine-level private feed cannot break restores.

## Setup from a clean machine

**Linux (e.g. Ubuntu 22.04 build server)**

```sh
sudo apt-get install -y git curl python3 libicu70   # python3: Emscripten; ICU: the .NET SDK itself
git clone <repo> h5reborn && cd h5reborn
tools/web/setup.sh            # .NET 10.0.402 -> ~/.local/share/h5web/dotnet10 + wasm-tools (≈ 1.5 GB)
tools/web/setup.sh --harness  # optional: also npm install + headless Chromium for the smoke test (needs Node ≥ 20)
```

**Windows** (Git for Windows provides the bash used by the build script)

```powershell
powershell -ExecutionPolicy Bypass -File tools\web\setup.ps1 [-Harness]   # .NET 10.0.402 -> %LOCALAPPDATA%\h5web\dotnet10
```

or `tools/web/setup.sh` from Git Bash. Both are idempotent. Override the SDK location with `H5_WEB_DOTNET_ROOT`.

## Build

```sh
tools/web/build.sh                 # -> web/dist/
tools/web/build.sh --out /srv/h5   # -> any folder
tools/web/build.sh --interp        # Mono interpreter only: quick build for checks (about half the speed)
```

What it does:
1. **Stage** `web/.build/game/` = `game/project.godot`, `game/scenes`, `game/src` + the web overlay `web/game/`
   (a .NET 10 `H5Reborn.csproj` that compiles the `src/H5.*` library sources in, the `H5Reborn.web` 2dog host,
   `src/Web/*.cs`, the page). `project.godot` is switched to `gl_compatibility` and gets the `WebProbe` autoload.
   The desktop projects and their `obj/` folders are never touched.
2. **Publish** the host (`dotnet publish -c Release`): C# → .NET assemblies, **AOT-compiled to wasm** (Mono AOT, `RunAOTCompilation`; `--interp`: interpreter only), libgodot + .NET
   runtime linked by Emscripten into `godot.wasm`, the Godot project exported to `godot.pck` (scenes and
   shaders only, ~40 KB), `.br`/`.gz` siblings of every file.
3. **Copy** the app bundle to the output folder and add `build-info.json` (commit, dirty flag, versions).

An AOT build takes ~12 minutes on a desktop, longer on a busy machine (the AOT compile of the game and GodotSharp assemblies, then the Emscripten link
and `wasm-opt`); `--interp` 4–6 minutes. Later builds reuse `web/.build` (and `web/.aot`). On Windows the AOT
compiler cannot open paths over 260 characters, so its intermediates go to `web/.aot/` (override with
`H5_WEB_AOT_OBJ=<short folder>` when the repository itself sits in a deep folder).
`web/.build`, `web/.aot`, `web/dist` and `web/smoke*` are git-ignored.

**AOT vs interpreter** (same commit, headless Chromium with the GPU, stock A2M1):

| | interpreter | AOT |
|---|---|---|
| main menu on screen | 37.7 s | 26.7 s |
| adventure map on screen | 90 s | 53 s |
| end turn, 5 computer players | 3.8 s | 3.4 s |
| frame rate: menu / adventure | 6 / 4.5 fps | 19 / 11.5 fps |
| `godot.wasm` (brotli) | 46 MB (9.5 MB) | 86 MB (20 MB) |

The jiterpreter (the interpreter's built-in JIT of hot traces) is on in both. Trimming is already on (the game
assembly is kept whole, Godot finds scripts by reflection).

### Output (a self-contained static folder)

```
index.html  h5data.js  godot.js  godot.wasm  godot.pck  godot.audio*.worklet.js
_framework/  (dotnet.js, dotnet.native.js, dotnet.runtime.js, *.wasm = .NET assemblies)
twodog.sizes.json  build-info.json  H5Reborn.web.runtimeconfig.json  package.json
every file also as .br and .gz
```

Transfer size with Brotli: `godot.wasm.br` ≈ 20 MB (AOT; 9.5 MB with `--interp`) + managed assemblies ≈ 4 MB.

## Serving it

Any static server. Requirements:

| response header | value | needed? |
|---|---|---|
| `Content-Type` for `.wasm` | `application/wasm` | **required** (streaming compilation) |
| `Content-Type` for `.js` / `.mjs` | `text/javascript` | required |
| `Content-Type` for `.pck`, `.dat` | `application/octet-stream` | recommended |
| `Content-Encoding: br` (or `gzip`) | serving the precompressed sibling (`file.br`) when `Accept-Encoding` allows | recommended (≈ 13 MB instead of 60 MB) |
| `Cache-Control` | `no-cache` (revalidate) for everything; the file names are not content-hashed | recommended |
| `Cross-Origin-Opener-Policy: same-origin` + `Cross-Origin-Embedder-Policy: require-corp` | | **optional**: the build is single-threaded and does not use SharedArrayBuffer. It works with them (tested, `smoke.mjs --coi`) and without |

**Secure context.** Serve over **HTTPS** (or open it as `http://localhost`). On plain `http://<LAN-IP>` the browser
withholds OPFS (`navigator.storage.getDirectory`) and the File System Access folder button, so the "remember"
copy is unavailable and the folder must be picked on every visit (`<input webkitdirectory>` still works).
A self-signed / internal-CA certificate (Caddy `tls internal`) is fine once the browser trusts or accepts it.

Caddy (as on the LAN server):

```
:8160 {
    tls internal
    root * /srv/h5reborn/dist
    file_server {
        precompressed br gzip
    }
    header {
        Cache-Control "no-cache"
        Cross-Origin-Opener-Policy "same-origin"
        Cross-Origin-Embedder-Policy "require-corp"
    }
    @wasm path *.wasm
    header @wasm Content-Type application/wasm
}
```

nginx:

```
location /h5/ {
    alias /srv/h5reborn/dist/;
    brotli_static on;   # ngx_brotli; else: gzip_static on;
    types { application/wasm wasm; text/javascript js mjs; text/html html; application/json json; }
    default_type application/octet-stream;
    add_header Cache-Control "no-cache";
}
```

Locally: `node tools/web/harness/serve.mjs web/dist 8080 [--host 0.0.0.0] [--coi]` (dependency-free; serves the
`.br` siblings), then open `http://localhost:8080/`.

Deploying = `rsync -a --delete web/dist/ server:/srv/h5reborn/dist/`.

## Using it

1. Open the page. A panel asks for the game: **Choose game folder…** (Chromium's File System Access picker), or the
   **Game folder** input (every browser, including Firefox/Safari; `webkitdirectory`), or the `.pak` files from the
   install's `data` folder. Pick the Tribes of the East install folder (stock 3.1 or MMH 5.5).
2. With **Remember** ticked (default) the page copies the picked files into the browser's private storage (OPFS)
   in the background (≈ 2 GB, about two minutes). The next visit starts without asking. **Forget the stored copy**
   removes it; `?fresh` ignores it once.
3. The game boots (intro, main menu …) exactly like the desktop build.

How the files are read: archives (`data/*.pak`, `UserMODs/*`) and music stay in the browser; the game sees an
install at `/h5install` in the wasm file system with empty placeholders for them, and
`H5.Core.IO.RandomAccessSources.Opener` routes `PakArchive` (and map archives, music) to
`H5Reborn.Web.BrowserFileSource`. Each read is a **synchronous `Range` request on the file's `blob:` URL** (the
only synchronous file read a browser main thread has), so all of the game's synchronous loaders run unchanged
and `data.pak` (1.4 GB) is never read whole; reads go through a 16 MB cache of 256 KB blocks. Small loose files
(Maps, profiles, hwcursors, DuelPresets, video descriptors; ~12 MB) are copied into the wasm file system at start.

### Saves, profiles and settings

Everything the game keeps (`user://`: profiles, saves, campaign progress, options, `video.cfg`, network
settings) is Godot's `user://` = `/userfs/godot/app_userdata/H5Reborn` in the wasm file system, which Godot's web
platform mounts as Emscripten **IDBFS** (IndexedDB database `/userfs`, store `FILE_DATA`): loaded when the engine
starts, written back after the game writes. Godot writes back after its own `FileAccess` writes; the web-only
autoload `WebStorage` (`web/game/src/Web/WebStorage.cs`) watches the folder every 2 s and triggers the same
write-back after System.IO writes, new folders and deletions too. It logs `[H5] web: user data <dir>: N files: …`
at start and `[H5] web: user data changed …` after each write-back. The data is per browser and per site (origin).

**Export / import** (`wwwroot/h5saves.js`): the small **Saves** button in the bottom-left corner (or `?saves`)
opens a panel. *Export* downloads `H5Reborn-saves-<date>.zip` with every file of `user://` (paths relative to it,
e.g. `profiles/Player/saves/save_0.sav`; the same layout as the desktop's
`%APPDATA%\Godot\app_userdata\H5Reborn`, so saves move between desktop and browser). *Import* takes such a zip
(adds / replaces those files) or loose `.sav` files (go to the current profile's `saves/`), stages them in a
separate IndexedDB database and reloads; the page merges them into `/userfs` *before* the engine mounts it, so the
running game's write-back can never undo an import. Clearing the site's data in the browser deletes the saves.

### Computer players' turns

The desktop computes all consecutive computer turns at once when the human ends the turn
(`AdventureScreen.RunComputerTurns` → `AdventureSession.RunComputerTurns(16)`), then plays their events back.
The browser has a single thread, so that would freeze the page for the whole batch: there the same batch runs
**one computer player's turn per frame** (`StepWebComputerTurns`); nothing else happens in between (no event
playback, no map input), the turn order, seeds and results are identical. A 6-player A2M1 first turn took ~3 s
with frames at most ~0.8 s apart. The network host's `AdventureNetSession.AiRunner` (a worker thread that can
pause a computer turn while a human fights its battle) runs inline in a browser; a browser cannot host network
games, so that path is unused there (if it ever were: a computer player's battle against a human would be
quick-resolved instead of fought — GAP).

### Audio

Browsers start Web Audio suspended until the page gets a click, key or touch. Godot resumes its context on input it
handles; `wwwroot/h5audio.js` also tracks every `AudioContext` the page makes and resumes them on the first (and any
later) gesture anywhere on the page, so the game is never left silent. The web build plays every audio player
through Godot's own mixer, like the desktop (`audio/general/default_playback_type.web=0`, "Stream", set by
`build.sh`): Godot's web default "Sample" (Web Audio buffer sources) left the music silent (smoke test: menu music
peak level 0 with Sample, 0.16 with Stream). Intro movies (Theora `video/logo-*.ogg`) play with picture and sound.
Without threads the mixer is fed from the page's main thread once per frame, so a frame longer than the audio buffer
makes the sound drop out. `build.sh` sets `audio/driver/output_latency.web=100` (a 128 ms buffer at 32 kHz; Godot's
web default gives 64 ms), and map loading is spread over frames (next section).

### Loading an adventure map

The map is no longer built in one synchronous step (which held the page for 35–50 s, sound off, no frame):

* `AdventureScreen` shows the stock loading picture (0066c070: UIGameRoot `CDBID_TEXTURE_PWL` over the screen) and
  builds the map with `AdvWorld.Build()`, an enumerator over terrain, sea / light per floor, each scenery batch, each
  object, each hero, ships and the camera, run for 40 ms per frame in the browser (50 ms on the desktop). The floors
  join the scene tree only when complete. Then the pieces of the shown floor become visible a few per frame behind the
  picture (the batch doubles while frames stay under 250 ms), so the renderer prepares their shaders over several
  frames; then the HUD, map scripts and save keys attach and `WhenLoaded` callbacks run. Campaign maps fill the PWL
  screen's `Progress` bar from `AdventureScreen.LoadProgress`. The built map, the order of nodes and the game state are
  the same as before (desktop screenshots of A2M1 after load differ by 0.03 / 255 on average, animation phase only).
* Characters of the same model and animation set share their decoded skeleton, mesh, skin and converted clips while the
  map is built (`CharacterBuilder.ShareScope`; each instance still has its own nodes and library; looping and one-shot
  copies of a clip are shared, never changed). A2M1 has 318 animated objects from 150 model / set pairs: the object
  pass went from 10.3 s to 3.7 s on the desktop.
* The global shader parameters (`H5Lighting`) are registered before the first material: materials updated while they
  were missing printed 14 000 warnings and made every rendering update of a staged load take seconds.
* `--load-trace` (always on in the browser) logs each stage (`[H5] load: …`) and per-kind totals (template building,
  model load, mesh build, texture read / decode / upload).

Measured on the same busy machine (headless Chromium with the GPU, A2M1, `smoke.mjs --only adventure`):

| | before (1782e22) | now |
|---|---|---|
| engine started → adventure on screen | 101 s | 62 s |
| longest time without a frame | 108 s | ~20 s (WebGL shader compiles of the first map frames) |
| wasm heap after load | 916 MB | 822 MB |
| frame rate on the map | 9.1 fps | 9–11 fps |

What the load still spends (browser, A2M1, `[H5] load:` buckets): converting ~150 characters (Granny decode, Oodle1 in
C#, clip baking) ~30 s on this busy machine, models / meshes ~9 s, texture reads ~4 s, WebGL shader compiles ~20 s
(Chrome keeps compiled shaders in its GPU cache, so later visits compile less). Since then:

* **Decoded characters kept between visits.** `AnimSetLoader.DecodedCache` (`IDecodedCache`, `H5.Assets`) stores each
  Granny skeleton and each baked clip (with its text events) under `skel/<v>/<uid>` / `clip/<v>/<rate>/<uid>`; in the
  browser that is IndexedDB (`wwwroot/h5cache.js`, database `h5reborn-decoded`, keys prefixed with the install's data
  fingerprint, at most 384 MB, least recently used dropped first; `web/game/src/Web/WebDecodedCache.cs`). IndexedDB is
  asynchronous and the builders are not, so before the object pass `AdvWorld.Build` names every key the map's objects
  will read and waits (frames keep coming) until they are in memory (`[H5] web: decoded cache: N of M entries`); they
  are let go once the map is built. A hit skips the pak read, the Granny / Oodle1 decode and the baking; the stored
  floats are the baked ones bit for bit (`DecodedCacheTests`). The desktop has no store (nothing changes there).
  `?nodecoded` turns it off; `smoke.mjs --only decoded` loads A2M1 twice in one browser profile.
* **Faster baking (both builds, same results).** `GrannyConvert.ToClip` samples with plain loops instead of LINQ
  `Select().ToArray()` (that generic path ran in the browser's interpreter: 8.7 % → 2.2 % of the object pass), and
  `CharacterBuilder` sets each Godot track's keys in one call (the track's `keys` array) instead of one interop call per
  key, whenever the key times are strictly increasing beyond Godot's merge tolerance (~85 % of the stock tracks; the
  rest are inserted key by key as before). `--anim-keys-check` builds every clip both ways and compares every key:
  0 differences over A2M1's clips.
* **Shaders compiled ahead** (next section): the map's warm-up phase went from ~22 s to ~1.5 s.

Measured (headless Chromium with the GPU, A2M1, busy machine; `smoke.mjs --only decoded`):

| | first visit | second visit (same browser) |
|---|---|---|
| object pass (`load: objects`) | 32.7 s | 15.7 s |
| character templates (`ch template`) | 26.4 s | 11.7 s |
| decoded-cache hits | 0 / 655 | 655 / 655 |
| map warm-up phase (shaders) | 1.5 s | 1.5 s |
| page opened → adventure on screen | 75 s | 60 s |
| wasm heap after load | 820 MB | 724 MB |

### Shaders (first visit)

Godot compiles a WebGL program the first time something is drawn with it and waits for it; on Chrome / Windows (ANGLE
on Direct3D) a big scene program takes 1–2 s, and the first menu frames need ~50 programs, the first map frames ~80
more, so a first visit froze for tens of seconds. Browsers keep compiled programs in their GPU cache and find them
again for an identical program in a context with the same extensions enabled. `wwwroot/h5shaders.js` compiles the
programs listed in `wwwroot/h5shaders.json` in a hidden WebGL 2 context (the game context's extensions, from the list)
with `KHR_parallel_shader_compile`, six at a time without waiting, from the moment the page opens (while the player
picks the folder and the engine boots); Godot's own compiles of the same programs then come from the cache. It never
changes what is drawn; without `KHR_parallel_shader_compile` it does nothing (it would block the page); `?noshaderwarm`
turns it off. It runs on every visit (a program already cached links in milliseconds).

The list is recorded from the game, not written by hand: `node smoke.mjs <dist> --install … --gpu --only
menu,duel,adventure --record-shaders web/game/H5Reborn.web/wwwroot/h5shaders.json` (every program the game links, in
first-use order, plus the extensions its context enabled; 111 programs, 1.4 MB, ~60 KB compressed). Re-record it after a
Godot / 2dog bump or when the game's shaders change: stale entries only cost a compile, missing ones are compiled by
Godot as before.

| headless Chromium, GPU, folder picked 20 s after the page opened | before | now |
|---|---|---|
| time Godot waited for program links: main menu | 11.4 s | 0.13 s |
| … then A2M1 | 19.4 s | 0.26 s |
| 111 programs compiled in the background | – | 6 s (no frame longer than 0.4 s meanwhile) |
| longest frame gap up to the main menu | ~20 s | 8 s (engine start + menu scene load) |

### Frame rate

A browser CPU profile of the adventure map (`H5_WEB_PUBLISH_ARGS=-p:WasmNativeStrip=false tools/web/build.sh` keeps
function names; `tools/web/harness/prof.mjs` records one headless) showed, in order:

1. **Half of every frame waiting for the GPU process in `getParameter`.** Godot asks for its WebGL context with
   `explicitSwapControl`; without OffscreenCanvas, Emscripten (`-sOFFSCREEN_FRAMEBUFFER=1`, as in Godot's own web link)
   emulates that by rendering into an offscreen framebuffer with `preserveDrawingBuffer` and copying it to the canvas in
   every `emscripten_webgl_commit_frame`, and that copy starts with `gl.getParameter()` calls, each a synchronous round
   trip that waits for the whole frame's commands. `web/game/H5Reborn.web/emscripten/h5_webgl_direct.js` (an Emscripten
   JS library linked by `H5Reborn.web.csproj`, single-threaded build only) creates the same context straight on the
   canvas: no offscreen buffer, no copy; the commit does nothing and the browser presents when the
   `requestAnimationFrame` callback (where Godot's web main loop draws) returns. Same picture. The page logs
   `[H5] web: WebGL context on the canvas directly`.
2. **A quarter of the frame animating ~300 map objects, mostly off screen** (Godot's `AnimationMixer` and skeleton
   updates). Objects outside the camera's view (with a margin for their size and shadow), on the other floor, or with
   the whole map hidden behind the town screen, that play a *looping* clip, are held: their `AnimationPlayer` switches
   to manual processing and the skipped time is owed; when they come back into view (or start another clip) the
   player first advances by the owed time in one step, so the clip is exactly where continuous playback would have put
   it (`AdvWorld.PauseOffscreenAnimations`; desktop too). One-shot clips always play normally; heroes always animate.
   `--adventure-animate-all` turns it off to compare.
3. Smaller C# costs: `Viewport.GetCamera3D()` called every frame for the sky dome cost ~2 ms in the browser (its
   interop call went through the interpreter); the map's own camera is used when it is the current one.
   `WebStorage`'s 2-second watch of `user://` used System.IO's directory enumeration, ~40 ms per scan even for an empty
   folder; it now walks the folder with Godot's `DirAccess`.

Headless Chromium with the GPU, A2M1 (busy machine):

| | 4739f4f | WebGL direct only | now |
|---|---|---|---|
| frame rate on the map (`rafFps`) | 9.9 fps | 16.7 fps | 48–51 fps |

What a frame spends now: Godot's rendering, the remaining on-screen animation, audio mixing.

### Multiplayer (joining from the browser)

A browser cannot open TCP/UDP sockets or listen for connections, so in the browser:

* **Join a desktop-hosted game on the LAN**: every desktop host (and `h5server`) serves WebSocket clients on its
  normal game port (27615, `PortSharing`: an HTTP `GET` upgrade is a WebSocket, anything else a native client).
  In *Multiplayer → Connect…* type the host's address (`192.168.1.20` or `192.168.1.20:27615`): the browser build
  turns it into `ws://192.168.1.20:27615/`; a full `ws://` / `wss://` URL also works. The host needs no change
  (allow TCP 27615 in its firewall, as for desktop players).
* **Page served over HTTPS** (as on the LAN server: Caddy terminates TLS): browsers only allow secure `wss://`
  sockets from an `https://` page (`ws://` is blocked as mixed content). The web server that serves the page also
  forwards a path to the game host (an `h5server` or a desktop host on the LAN, port 27615), and players type that URL
  in *Multiplayer → Connect…*, e.g. **`wss://h5reborn.int.rowansserver.com/h5host/`** (the trailing `/` matters;
  `https://…` is accepted too and means the same). On an `https` page a plain address is refused with
  "This page is https: enter the host's wss:// address (a proxy)". The host itself needs no change: its game port
  answers WebSocket upgrades on any path, and it pings every 20 s, so idle proxy timeouts do not drop the game.

  Caddy (the site block that serves the game; `handle_path` strips `/h5host` before forwarding; one path per host):

  ```
  h5reborn.int.rowansserver.com {
      tls internal
      handle_path /h5host/* {
          reverse_proxy 192.168.1.20:27615      # the h5server / desktop host on the LAN
      }
      handle {
          reverse_proxy 127.0.0.1:8080          # nginx with the static files (as now), or root + file_server
      }
  }
  ```

  nginx (only needed if Caddy hands everything to nginx instead; Caddy forwards WebSocket upgrades by itself):

  ```
  location /h5host/ {
      proxy_pass http://192.168.1.20:27615/;    # trailing / strips the /h5host/ prefix
      proxy_http_version 1.1;
      proxy_set_header Upgrade $http_upgrade;
      proxy_set_header Connection "upgrade";
      proxy_read_timeout 1h;
      proxy_send_timeout 1h;
  }
  ```

  Tested by `smoke.mjs --only lobby-wss`: the page over `https` (self-signed certificate) from
  `serve.mjs --tls --proxy /h5host/=127.0.0.1:<port>`, which forwards upgrades the way the snippets above do, joins an
  `h5server` at `wss://127.0.0.1:<port>/h5host/` (data check included). Locally:
  `node tools/web/harness/serve.mjs web/dist 8443 --tls --proxy /h5host/=192.168.1.20:27615`.
* **Online join code**: through the master server's WebSocket bridge (`/ws/join?code=…`, `src/H5.Master`), which
  relays to the lobby's host over UDP; the master URL from *Online server* is used (`https` → `wss`). Listing
  online lobbies from the browser needs the master's HTTP API reachable from the page (same origin or CORS).
  Running the master / `h5server`: [MULTIPLAYER.md](MULTIPLAYER.md).
* **Hosting** is desktop-only (the browser says so); LAN discovery (UDP broadcast) does not exist in a browser.
* The data check (`GameInstall.DataFingerprint`, names and sizes of the paks) now uses the picked files' real
  sizes, so a browser and a desktop with the same install match.
* In the browser the WebSocket connect waits up to 45 s (a long synchronous load can hold the page while the
  socket is already open).

## Test page / flags (check future commits in the browser)

The page passes `?args=` to the game exactly like `godot -- <args>` on the desktop (`OS.GetCmdlineUserArgs`),
so every dev flag works:

| URL | opens |
|---|---|
| `/?args=--skip-intro` | main menu |
| `/?args=--combat-vs-ai=1,2` | duel against the AI (duel presets 1 vs 2) |
| `/?args=--adventure=A2M1` | adventure map (any `--adventure=<map>` query) |
| `/?args=--adventure=A2M1%20--adventure-reveal` | several args: space separated (`%20`) |
| `/?args=--adventure=A2M1%20--adventure-shadow-splits=1` | the sun's shadow with 0 (off), 1, 2 or 4 (default) splits, to compare frame rates |
| `/?fresh` | ignore the stored copy and ask for the folder |
| `/?nocache` | do not copy the picked files to browser storage |
| `/?nodecoded` | do not use the decoded-character store (IndexedDB) |
| `/?noshaderwarm` | do not compile the listed shader programs ahead |
| `/?recordshaders` | record the programs the game links (`h5shaders.recorded()`; used by `smoke.mjs --record-shaders`) |
| `/?args=--adventure=A2M1%20--adventure-animate-all` | animate every map object every frame (off-screen objects are normally held) |
| `/?args=--adventure=A2M1%20--anim-keys-check` | build every clip key by key too and compare (`[H5] anim keys check: …`) |

The web-only autoload `WebProbe` logs `[H5] web: scene <name>`, `[H5] web: main menu on screen`,
`[H5] web: adventure on screen` and every 10 s a stats line (fps, GC heap, video memory, number and MB of file
reads, parsed xdb documents and their size) to the browser console.

**Headless smoke test** (never opens a window; uses the local install):

```sh
cd tools/web/harness && npm install
node smoke.mjs ../../../web/dist --install "/path/to/Tribes of the East" [--gpu] [--coi] \
     [--only menu,duel,adventure,endturn,persist,decoded,audio,lobby,remember] [--map-arg --adventure=A2M1] \
     [--server src/H5.Server/bin/.../H5.Server.dll] [--out DIR] [--pick-delay s] [--init extra.js] [--record-shaders file]
```

It serves the folder, picks the install through the page's folder input, waits for each screen's log marker and
saves `DIR/<scenario>.png`, `<scenario>.log` and `results.json` (time, fps, wasm heap). `--gpu` renders with the
machine's GPU (still headless); the default SwiftShader software renderer works anywhere but is slow for 3D.
`remember` checks the OPFS flow (first visit picks and copies, second visit starts without picking).
`decoded` loads A2M1 twice in one browser profile and reports both object passes and the second visit's
decoded-cache hits (fails without hits). `--pick-delay` waits like a player choosing the folder (the shader warm-up runs
meanwhile); results.json carries each page's `shaderWarm` state. `--init` adds a script before the page's own
(instrumentation); `--record-shaders` writes the shader warm-up list (see *Shaders*).
`endturn` ends the first turn on the map (`--save-dev=endturn`) and waits for every computer player and the human's
day 2 (`[H5] web: adventure day … human turn`); `maxFrameGapMs` is the longest the page went without a frame after
the map was up. `persist` quicksaves (visit 1), reloads in the same browser profile and checks the save is listed
from IndexedDB (visit 2), round-trips it through the export zip and import, and checks the imported copy (visit 3).
`audio` loads the main menu in a browser with the normal autoplay policy and samples what reaches the speakers
before / after a key press (`?audiotest` taps the Web Audio output); `intro` plays the intro movies.
`lobby` starts the desktop dedicated server (`--server <H5.Server.dll>`) on a random local port and joins it from
the page over WebSocket (`--goto=create --connect=ws://127.0.0.1:<port>/`).
Exit code 0 = every scenario reached its marker without a page error. Screenshots contain original game art:
they stay local (git-ignored).

## Verified (November 2026, headless Chromium, stock 3.1 install)

| scenario | result |
|---|---|
| boot → main menu (`?args=--skip-intro`) | 3D menu background, profile prompt, menu. Engine start ~2 s after the page; mount of 103 036 pak entries 1.5 s; menu on screen ~15 s (first-time shader compiles) |
| duel (`--combat-vs-ai=1,2`) | arena, both armies, hero, UI; ~9 s from start to the combat screen |
| adventure (`--adventure=A2M1`, 136×136) | terrain, castle, objects, HUD, start-of-week dialog (same picture as the desktop build); ~60 s to load (Mono interpreter) |
| COOP/COEP headers on/off | both work (final run with them, as on the LAN server) |
| remember (OPFS) | first visit picks + copies 350 files / 1.9 GB; second visit starts from browser storage without picking |
| frame rate | headless with the GPU (busy machine): menu 7–28 fps, duel ~50 fps, adventure 13–16 fps; SwiftShader: menu < 1 fps |
| memory | wasm heap 256 MB at start, ~370 MB menu, ~440 MB duel, **~1.3 GB adventure** (GC heap ~200 MB, VRAM ~390 MB) |
| file reads | menu 232 reads / 81 MB, duel 469 / 130 MB, adventure ~2 200 / 580 MB (of a 1.9 GB install) |

Web-platform fixes made for this: the adventure sky dome assumed Forward+'s `[0, w]` reverse-Z clip depth and
covered the map on the Compatibility renderer (desktop `--rendering-method gl_compatibility` showed the same; fixed for both);
`TargetPrompt` also reads `H5_GAME_DIR` from the managed environment; music loads from a buffer when its file
is virtual.

## Not working yet in the browser (ranked)

1. **Performance (CPU).** The adventure map runs at ~50 fps headless (was ~10: see *Frame rate*); map loading is
   spread over frames behind the loading picture, shaders are compiled ahead on a first visit (*Shaders*) and decoded
   characters are kept between visits (*Loading an adventure map*). Still open: a first visit's object pass (~30 s on a
   busy machine: synchronous range reads of the picked files, ~1-2 ms each, Granny decode, Godot resource building),
   and ~8 s of engine start and main menu loading before the first menu frame.
2. ~~Adventure AI turns (threads)~~ — fixed: computer turns run one per frame (see *Computer players' turns*).
3. ~~Saves, profiles and settings do not persist~~ — fixed: `user://` persists in IndexedDB, with export / import
   (see *Saves, profiles and settings*).
4. **Multiplayer** — joining works (see *Multiplayer*): a browser joined a desktop `h5server` over `ws://` with the
   data check passing (`smoke.mjs --only lobby`). Open: hosting and LAN discovery (impossible in a browser),
   online lobby *listing* needs the master same-origin or with
   CORS, WebRTC peers are not implemented, and a browser cannot host network games (so computer turns of a
   network game never run in a browser).
5. ~~Audio~~ — fixed: unlock on any gesture, music/sounds through Godot's mixer, intro movies verified (see *Audio*).
   Map loads now run in short steps with a 128 ms audio buffer; single steps (one character's decode) can still exceed it
   on a slow machine.
6. **Memory ceiling.** wasm32 heap max 2 GB. Decoded texture images were kept alive until the GC finalized their
   wrappers; `Game.LoadTexture` and the adventure terrain now free them after upload: the A2M1 (136×136) heap peak
   went from **1346 MB to 820 MB** (GC heap ~210 MB, VRAM ~390 MB). The page logs every heap growth
   (`[H5] web: wasm heap grew to N MB`, h5data.js) next to the game's lines. Shared character data cut A2M1 to 822 MB;
   the largest maps fit: 176×176 two floors 819 MB, 216×216 917 MB. Opening an adventure map now first lets go of what
   the previous screens loaded and nothing uses (`Game.ReleaseUnused`: textures no material holds, the decoded Granny
   cache; a reload of A2M1 released 94 textures). The parsed-xdb cache (`XdbLoader`) is bounded: least recently used
   documents are dropped past 32 MB of xdb text (`XdbLoader.DefaultMaxCachedBytes`; A2M1 reads ~3 000 documents, ~4 MB;
   a dropped document is parsed again on its next use). Still open: mobile browsers without S3TC would need RGBA8
   textures (4× VRAM).
7. ~~Data fingerprint~~ — fixed: `RandomAccessSources.Length` gives the picked files' real sizes.
8. **First visit download/copy.** The OPFS copy of ~2 GB takes ~2 minutes in the background; the picked originals
   are used meanwhile. Browser storage quotas may refuse it on small disks (then the folder is asked for each visit).
9. **Toolchain risk.** 2dog is a single-maintainer project; versions must match Godot exactly. Keep this build
   (and its smoke test) running on every bump, and switch to Godot's official C# web export when it ships.
