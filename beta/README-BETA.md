# H5Reborn beta

H5Reborn is a from-scratch reimplementation of *Heroes of Might and Magic V: Tribes of the East*
on the Godot 4 engine (C#/.NET 8). This is an early, testable beta: expect missing features and bugs.

**No original game assets are included.** H5Reborn reads the data (`data/*.pak`, music, video,
maps, cursors…) directly from **your own installed copy of Heroes V: Tribes of the East, version 3.1**
(GOG, Ubisoft or Steam), with or without **Might & Magic: Heroes 5.5** installed on top. Nothing in
your game folder is modified.

## What's new in this build

- Fixes for reported problems: battle flee/surrender buttons, main menu during battle, tactics
  phase, hero strike, casualty numbers, movement rules; adventure dialog OK buttons, sky dome,
  camera, monster tooltips; town start buildings, creature hire screen (Hire All), town camera,
  marketplace, tavern hire and rumours; text layout and fonts; unreadable map names.
- New game setup: map list and player setup, starting bonus choice; random maps (regenerated on load),
  rotating autosaves, save before combat, network game saves.
- Random map generator: the original's roads, guarded treasure groups and underground treasures.
- Adventure map: route colours (green / red in guard zones / grey beyond today), cursors, prison,
  Hut of Magi camera tour, artifact merchants, hero meeting screen (backdrop, war machines),
  Hill Fort, Tree of Knowledge, Witch Hut, flee/surrender with ransom and tavern return.
- Computer players: resource values, market trading, recruiting, building order, threats and
  town defence, hero meetings and army exchange, battle estimates, flee/surrender decision.
- Towns: mage guild spells generated as in the original, rune and warcry pages, weekly town
  update, Trade Guild, Mystic Pond, Stables, Crown of Leadership, mana refill rules.
- Battles: hundreds of rule fixes from the combat audit (runes cost resources, Guardian Angel,
  Summon Balor, Leap, Cowardice, moat damage and more); combat map scripts run.
- Option: "Extended combat camera" (off by default); extended adventure map zoom (on by default).

Earlier in this beta:

- Fixes for reported problems: held items (props) glitching, fog of war, black characters in story
  scenes, missing portraits, whole-object cursor and entrance routing on map objects.
- Adventure map: moon calendar weeks, Tower of the Astrologer, map tavern, Den of Thieves, Memory
  Mentor, map dwellings, garrisons, spell shop, Seer Huts, Sphinx, grail digging, shipyards.
- Campaigns: carry-over between missions; MMH 5.5 campaign menu. MMH 5.5 maps can be saved.
- **MMH 5.5 is the default target.** An install with MMH 5.5 is played with 5.5 rules and data;
  MMH 5.5 map scripts run (only on maps that have a map script, as on the original engine).
  To play plain 3.1 instead, point H5Reborn at a 3.1 install (see below) or set `H5_TARGET=stock`.
- Network games: simultaneous turns (PEST, as in MMH 5.5); several battles can run at the same time.

## Download

`H5Reborn-beta-windows-x64.zip`: Windows 10/11, 64-bit. (Linux builds are no longer provided.)

The archive contains the game client, the dedicated server and launch scripts. The .NET 8 runtime
is bundled (the server shares the client's copy, so it lives in the `data_H5Reborn_windows_x86_64`
folder); nothing else needs installing.

## Requirements

- Your own Heroes V: Tribes of the East 3.1 installation, optionally with MMH 5.5 (the folder that contains `data/data.pak`).
- A GPU with Vulkan support. Without a Vulkan driver, use OpenGL 3.3 with `--rendering-driver opengl3` (see below).
- 1920x1080 or larger display recommended.

## Pointing H5Reborn at your game folder

The game folder is searched in this order:

1. The `H5_GAME_DIR` environment variable.
2. `game_dir.txt` next to the executable: the first line that is not blank and does not start with `#`.
   Example: `C:\GOG Games\Heroes of Might and Magic V - Tribes of the East`.
3. Common install locations (with `H5_TARGET=stock`, MMH 5.5 installs are skipped here):
   `G:\HoMM 5 - Tribes of the East (5.5)` first, then `GOG Games`, `Games`, `GOG Galaxy\Games`, `Ubisoft`, and Steam `steamapps\common` on drives C: to G:.

If nothing is found, the log says how to fix it (run `H5Reborn.console.exe` to see it).

## Launching

| What | Launcher |
|------|----------|
| Main menu | `Play H5Reborn.bat` (or `H5Reborn.exe`) |
| Duel against the computer | `Duel vs Computer.bat [you] [enemy]` |
| Dedicated server | `Host Server.bat` |

- Duel arguments are duel preset numbers 0-23 (default: 0 against 3).
- The server listens on port 27615 (TCP and UDP). Other players choose
  **Multi Player > Local Network** and join the server's IP address. Server options:
  `--port N`, `--name NAME`, `--game-dir PATH`, `--bind ADDR`, `--no-lan`, `--public MASTER_URL`.
  The server needs the same game data as the players, because it checks that everyone uses identical rules.
- `H5Reborn.console.exe` runs the game with a console window that shows the log.

Useful engine options (put them before any `--`):
`--rendering-driver opengl3` (older GPUs, virtual machines), `--windowed`, `--resolution 1600x900`.

Settings, profiles and saves are stored in Godot's user folder:
`%APPDATA%\Godot\app_userdata\H5Reborn`

## Reporting problems

Please report your GPU, how you launched the game and the console output (`H5Reborn.console.exe`).
