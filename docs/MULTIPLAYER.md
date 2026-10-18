# H5Reborn multiplayer

The original game found LAN games with broadcast packets and used Ubisoft's (now dead)
servers for internet play. H5Reborn replaces both with:

| How players connect | Works where | Notes |
|---|---|---|
| **LAN discovery** | same network | Games appear automatically in the original *Servers list* window. |
| **Join by address** | LAN, VPN, port-forwarded internet | Type `192.168.1.20`, `host:27615`, `[ipv6]:27615`, or `wss://games.example.com`. |
| **Online lobby + join code** | anywhere on the internet | A host lists its game on a master server; others pick it from the list or type the 6-letter code. NAT hole punching, falling back to a relay. |
| **Dedicated server** (`h5server`) | anywhere | Optional; same protocol, can also list itself online. |

Any player can host from inside the game. Gameplay is always authoritative on the host:
the server applies every command (taking the player's side from the connection, never
from the client) and every client keeps a lockstep replica whose state hash is checked
after each update (see `src/H5.Duel/DuelSession.cs`).

## Architecture

```
            ┌─────────────── one port (default 27615) ───────────────┐
 client ──► │ TCP  : native frames  ─┐                               │
 browser ─► │ TCP  : WebSocket (HTTP upgrade, wss via proxy) ─┼─► LobbyServer ─► game sessions (duel, …)
 internet ► │ UDP  : reliable channels (direct / punched / relayed) ─┘  │
            └────────────────────────────────────────────────────────┘
                     ▲ LAN beacon (UDP 27616)        ▲ master registration (UDP) + HTTP heartbeat
```

* **Transport-agnostic protocol** – every message is one frame on an `IFrameChannel`
  (`src/H5.Net/Transport`): `TcpFrameChannel` (4-byte length prefix),
  `WebSocketFrameChannel` (one binary message per frame) and `ReliableUdpChannel`
  (fragmentation, cumulative + selective acks, RTT-based retransmit, keepalive).
  `LobbyServer.AttachChannel` adopts any of them, so desktop, browser and NAT-traversed
  players share the same lobby and game.
* **Port sharing** – `PortSharing` peeks at each TCP connection: `GET ` means a WebSocket
  upgrade, anything else is a native client. Hosts and `h5server` therefore need a single
  TCP port (plus UDP on the same number).
* **Reconnects** – `Welcome` carries a resume token. If a connection drops during a game the
  server holds the seat for 90 s (`ReconnectGrace`); `NetClient` redials with the same
  connector and the server replays the missed `GameUpdate`s (or the whole game if the client
  lost its state). Commands that were in flight are simply re-issued by the player.
* **Version/data checks** – `Hello` carries the protocol version and a fingerprint of the
  game archives and mods; mismatches are refused with an explanatory message box.

### LAN discovery (`src/H5.Net/Discovery`)
Browsers probe UDP 27616 on the IPv4 limited broadcast, every interface's directed
broadcast, the multicast group `239.255.27.15` and the IPv6 link-local group `ff02::2715`
(per interface). Hosts answer each probe with a unicast JSON reply (name, port, players,
open rooms, protocol version, data fingerprint). This works on multi-homed PCs, Wi-Fi +
Ethernet bridges and IPv6-only links, unlike the original single broadcast.

### Online lobbies (`src/H5.Master`, `src/H5.Net/Master`)
The master server never sees game rules or state; games are hosted by players.

* **HTTP API** (JSON): `GET /api/info`, `GET /api/lobbies?fingerprint=&protocol=`,
  `GET /api/lobbies/code/{code}`, `POST /api/lobbies` (returns id, join code and a secret
  host token), `PUT /api/lobbies/{id}` heartbeat and `DELETE /api/lobbies/{id}` (both need
  the `X-Host-Token` header). Lobbies expire 45 s after the last heartbeat.
* **UDP rendezvous** (same socket as the relay, port 27621): the host sends `register`
  every 2.5 s from its game socket, which records its public endpoint and keeps its NAT
  mapping open. A joiner sends `join` (code or id); the master answers with the host's
  public + LAN endpoints and tells the host the joiner's endpoints. Both sides punch; the
  joiner connects to whichever endpoint answers.
* **Relay fallback** – if no punch succeeds within ~3 s (symmetric NATs, strict firewalls),
  both sides wrap datagrams in relay envelopes addressed to the master, which forwards them
  (per-session bandwidth cap). The reliable channel is identical either way.
* **WebSocket bridge** – `wss://master/ws/join?code=ABC123`: the master opens a reliable UDP
  channel to the host (the host already accepts packets from the master) and splices it to
  the WebSocket. This is how browser players, or desktops behind UDP-blocking firewalls,
  join desktop-hosted games.
* **WebRTC signalling** – `wss://master/ws/signal?code=…` relays `rtc_offer` /
  `rtc_answer` / `rtc_ice` between a browser joiner and the host. Desktop hosts currently
  answer `rtc_unsupported`, so browsers fall back to the bridge. (See *Decisions*.)
* **Router port mapping** – when a player publishes online, the host tries NAT-PMP and then
  UPnP IGD to forward its port (so direct connections work even without punching). This is a
  per-player setting (`MapRouterPorts`, default on) and is removed again when the game closes.
* **Abuse limits** – per-IP HTTP rate limiting (120/min, 10 lobby creations/min), per-IP UDP
  rendezvous limit, max lobbies per IP, relay bandwidth cap per session, idle expiry. No
  secrets are stored in the repository; everything is configured by environment variables.

## Hosting an online game (players)

1. Someone runs a master server (see below) — or you use a friend's.
2. In the game: **Multi Player → Local Network → Connect...**, enter the master's address in
   *Online server* (e.g. `https://heroes.example.com`). It is remembered.
3. To host: tick **Host this game** and **List online (join code)**, press *Host*, then
   *Create* a duel room. The join code is shown in the player panel. Alternatively host first
   and tick *List online* in the *Create Game* dialog.
4. To join: pick the game in the servers list, or type its 6-letter code in *Connect...*.

Without any master you can still play on a LAN (automatic) or by typing the host's IP
address (the host forwards TCP+UDP 27615, or both use a VPN such as Tailscale/ZeroTier).

## Running the master server (operators)

The master is a small ASP.NET Core app (`h5master`). It needs **one TCP port for HTTP /
WebSockets and one UDP port** for rendezvous and relay; UDP cannot go through an HTTP
reverse proxy, so it must reach the server directly.

### Docker + automatic TLS (recommended)
```
git clone <this repo> && cd H5Reborn/deploy/master
# edit Caddyfile: replace heroes.example.com with your DNS name
docker compose up -d --build
```
Open **80/tcp, 443/tcp and 27621/udp** in the server's firewall. Players use
`https://heroes.example.com` as their online server; browsers connect to
`wss://heroes.example.com/ws/...`.

### Without Docker
```
dotnet publish src/H5.Master -c Release -o /opt/h5master
H5_MASTER_URLS=http://127.0.0.1:27620 H5_MASTER_UDP_PORT=27621 dotnet /opt/h5master/h5master.dll
```
Put nginx/Caddy in front for TLS. nginx needs WebSocket upgrade headers:
```
location / {
    proxy_pass http://127.0.0.1:27620;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_read_timeout 1h;
}
```
(Run it as a systemd service with `Restart=always`.)

### Environment variables
| Variable | Default | Meaning |
|---|---|---|
| `H5_MASTER_URLS` | `http://0.0.0.0:27620` | HTTP/WebSocket listen URLs (`;`-separated) |
| `H5_MASTER_UDP_PORT` | `27621` | UDP rendezvous/relay port (advertised to clients via `/api/info`) |
| `H5_MASTER_UDP_BIND` | `0.0.0.0` | UDP bind address |
| `H5_MASTER_MAX_LOBBIES` | `2000` | Total listed games |
| `H5_MASTER_MAX_LOBBIES_PER_IP` | `4` | Games one address may list |
| `H5_MASTER_RELAY_KBPS` | `512` | Relay bandwidth cap per session |
| `H5_MASTER_MAX_RELAYS` | `2000` | Concurrent relay sessions |
| `H5_MASTER_REQUESTS_PER_MINUTE` | `120` | HTTP requests per IP per minute |
| `H5_MASTER_MOTD` | – | Message of the day (returned by `/api/info`) |

Behind a proxy the client derives the UDP endpoint from the master URL's host name and the
advertised UDP port, so the DNS name must resolve to the machine receiving UDP.

### Pointing clients at a master
* Desktop: *Connect...* dialog → *Online server* (stored in `user://net_settings.json`), or
  the `H5_MASTER` environment variable, or `--master=URL` on the command line.
* Dedicated server: `h5server --public https://heroes.example.com [--map-ports]` or `H5_MASTER`.
* Browser build (future): the same setting; it uses the `wss://…/ws/join` bridge (and WebRTC
  once implemented).

## Dedicated server
```
h5server [--port 27615] [--name "My server"] [--bind ADDR] [--no-lan] [--public MASTER_URL] [--map-ports]
```
Needs a local copy of the game data (for rules and the data fingerprint). Forward TCP+UDP on
its port, or list it on a master.

## Decisions

* **Reliable UDP instead of QUIC.** `System.Net.Quic` cannot run on a socket we already
  hole-punched, cannot be wrapped in the relay envelope, and needs TLS certificates on every
  player's PC. Turn-based traffic is tiny, so a simple fixed-window reliable channel
  (`ReliableUdpChannel`) is sufficient and keeps punching, relaying and bridging on one socket.
* **Master is optional and stateless.** Hosts stay authoritative; the master only lists,
  introduces and relays. Restarting it drops listings (hosts re-publish) but not running games
  on direct paths.
* **WebSockets on the game port.** Browsers can't open raw TCP/UDP, and wss through a reverse
  proxy is the standard way to reach them, so every host and server accepts WebSocket clients
  on its normal port, and the master bridges WebSocket joiners to desktop hosts.
* **WebRTC via Godot, not a .NET library.** Godot's web export exposes the browser's native
  `WebRTCPeerConnection`, and desktop Godot has the official webrtc-native extension. The
  mature .NET stack (SIPSorcery) would add a large dependency with a modified licence. So the
  master implements signalling now (`/ws/signal`, `RvMessage` rtc_*), `IRtcAnswerer` is the
  host-side hook, and the DataChannel adapter will live in the Godot layer when the web build
  starts. Until then browsers use the bridge, which already lets them play with desktop hosts.
* **No threads required on the client path.** `NetClient` is driven by `Poll()` from the game
  loop; reconnects are async continuations, not threads; `WebSocketFrameChannel` uses only
  async I/O; `UdpHost.Bind(..., manualPump: true)` replaces its receive/timer tasks with a
  `Pump()` call per frame. Desktop-only pieces (TCP channel, LAN discovery, hosting) may use
  background tasks because a browser can't use those sockets anyway.
* **Adventure-map multiplayer** plugs in the same way as duels: implement
  `IGameSessionFactory`/`IGameSession` for an `H5.Adventure` session (authoritative engine +
  lockstep replica with state hash, like `DuelSession`/`DuelReplica`) and register it in
  `LobbyService.HostAsync` and `SessionFactories.All`. Transport, lobby, reconnect/replay and
  online listing need no changes.

## Tests
`tests/H5.Net.Tests` (plus `tests/H5.Tests`):
reliable UDP under 20 % loss with a 400 KB frame, thread-free manual pump, LAN discovery,
native + WebSocket clients on one port, master listing/join codes, a full duel over a
hole-punched path, a full duel over the relay with simulated symmetric NATs, a full duel with
a WebSocket (browser-style) joiner through the master bridge, WebRTC signalling fallback,
reconnect mid-duel with replay, and UPnP AddPortMapping against a fake router.
