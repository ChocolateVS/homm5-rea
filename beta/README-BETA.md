# H5Reborn beta

H5Reborn is a from-scratch reimplementation of *Heroes of Might and Magic V: Tribes of the East*
on the Godot 4 engine (C#/.NET 8). This is an early, testable beta: expect missing features and bugs.

**No original game assets are included.** H5Reborn reads the data (`data/*.pak`, music, video,
maps, cursors…) directly from **your own installed copy of Heroes V: Tribes of the East, version 3.1**
(GOG, Ubisoft or Steam), with or without **Might & Magic: Heroes 5.5** installed on top. Nothing in
your game folder is modified.

## What's new in this build

- Fixes for reported problems: held items (props) glitching, fog of war, black characters in story
  scenes, missing portraits, whole-object cursor and entrance routing on map objects.
- Adventure map: moon calendar weeks and the Tower of the Astrologer; map tavern, Den of Thieves and
  Memory Mentor; correct hero hire cost; map dwellings with the hire screen; garrisons; spell shop;
  neutral monsters join or flee as in the original; wells, springs, stables and sirens fixed;
  adventure AI fixes.
- Towns: Arcane Forge and creature artifacts; faction buildings (Avengers' Guild, sacrificial altar).
- Battles: shaman and sanctuary bonuses; many fixes from the combat audit.
- Campaigns: carry-over between missions fixed; MMH 5.5 campaign menu. MMH 5.5 maps can be saved.

Earlier in this beta:

- **MMH 5.5 is the default target.** An install with MMH 5.5 is played with 5.5 rules and data;
  MMH 5.5 map scripts run (only on maps that have a map script, as on the original engine).
  To play plain 3.1 instead, point H5Reborn at a 3.1 install (see below) or set `H5_TARGET=stock`.
- Network games: simultaneous turns (PEST, as in MMH 5.5); several battles can run at the same time.
- Battles: tactics phase can leave a stack out or split it.
- Adventure map: Seer Huts, Sphinx, grail digging, shipyards; Haven training, Necropolis shrine,
  sacrifice pits, slave market.

## Downloads

| File | Platform |
|------|----------|
| `H5Reborn-beta-windows-x64.zip` | Windows 10/11, 64-bit |
| `H5Reborn-beta-linux-x64.tar.gz` | Linux x86_64 (a current desktop distribution) |

Each archive contains the game client, the dedicated server and launch scripts. The .NET 8 runtime
is bundled (the server shares the client's copy, so it lives in the `data_H5Reborn_*` folder);
nothing else needs installing.

## Requirements

- Your own Heroes V: Tribes of the East 3.1 installation, optionally with MMH 5.5 (the folder that contains `data/data.pak`).
- A GPU with Vulkan support. Without a Vulkan driver, use OpenGL 3.3 with `--rendering-driver opengl3` (see below).
- 1920x1080 or larger display recommended.

## Pointing H5Reborn at your game folder

The game folder is searched in this order:

1. The `H5_GAME_DIR` environment variable.
2. `game_dir.txt` next to the executable: the first line that is not blank and does not start with `#`.
   Example: `C:\GOG Games\Heroes of Might and Magic V - Tribes of the East`,
   or on Linux `~/Games/heroic/Heroes of Might and Magic V - Tribes of the East`.
3. Common install locations (with `H5_TARGET=stock`, MMH 5.5 installs are skipped here):
   - Windows: `G:\HoMM 5 - Tribes of the East (5.5)` first, then `GOG Games`, `Games`, `GOG Galaxy\Games`, `Ubisoft`, and Steam `steamapps\common` on drives C: to G:.
   - Linux: `~/.wine`, Steam (`steamapps/common` and Proton `steamapps/compatdata/*/pfx`), Heroic (`~/Games/Heroic`),
     Lutris prefixes in `~/Games/*`, Bottles, `~/Games` and `~/GOG Games`.

If nothing is found, the log says how to fix it (run `H5Reborn.console.exe` on Windows, or start
the game from a terminal on Linux, to see it).

## Launching

| What | Windows | Linux |
|------|---------|-------|
| Main menu | `Play H5Reborn.bat` (or `H5Reborn.exe`) | `./play.sh` (or `./H5Reborn.x86_64`) |
| Duel against the computer | `Duel vs Computer.bat [you] [enemy]` | `./duel-vs-computer.sh [you] [enemy]` |
| Dedicated server | `Host Server.bat` | `./host-server.sh` |

- Duel arguments are duel preset numbers 0-23 (default: 0 against 3).
- The server listens on port 27615 (TCP and UDP). Other players choose
  **Multi Player > Local Network** and join the server's IP address. Server options:
  `--port N`, `--name NAME`, `--game-dir PATH`, `--bind ADDR`, `--no-lan`, `--public MASTER_URL`.
  The server needs the same game data as the players, because it checks that everyone uses identical rules.
- `H5Reborn.console.exe` (Windows) runs the game with a console window that shows the log.

Useful engine options (put them before any `--`):
`--rendering-driver opengl3` (older GPUs, virtual machines), `--windowed`, `--resolution 1600x900`.

Settings, profiles and saves are stored in Godot's user folder:
- Windows: `%APPDATA%\Godot\app_userdata\H5Reborn`
- Linux: `~/.local/share/godot/app_userdata/H5Reborn` (or under `$XDG_DATA_HOME`)

## Linux notes

- Extract with `tar xzf H5Reborn-beta-linux-x64.tar.gz`. The executables keep their permissions.
  If they lost them, run `chmod +x H5Reborn.x86_64 data_H5Reborn_linuxbsd_x86_64/h5server *.sh`.
- The game data can stay inside a Wine/Proton/Heroic/Lutris prefix. File and folder names are
  matched without regard to case (`UserMODs`, `Maps`, `profiles`, `Music`, `video`, `hwcursors`),
  so a GOG copy extracted on Linux works too.
- Needs X11 or Wayland (via XWayland), plus Vulkan or OpenGL drivers (Mesa or the vendor driver),
  ALSA or PulseAudio/PipeWire, fontconfig and libicu, all of which a standard desktop install has.
- On a headless machine, the server needs no GPU or display: run `./host-server.sh`.
- If the game fails to start or renders black with Vulkan (for example in a VM or under WSL, where only
  the software `llvmpipe` Vulkan driver exists), use `./play.sh --rendering-driver opengl3`.
- Reading the game data from a slow mount (network share, WSL `/mnt/c`) makes startup slow.
  A local copy of the game folder is much faster.

## Reporting problems

Please report the platform, GPU, how you launched the game and the console output
(`H5Reborn.console.exe` on Windows, terminal output on Linux).
