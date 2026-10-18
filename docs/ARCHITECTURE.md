# H5Reborn — architecture

Goal: a faithful reimplementation of *Heroes of Might and Magic V: Tribes of the East* (3.1) on
Godot 4 (.NET/C#). Graphics, UI, mechanics and gameplay must match the original; the new
engine adds modern networking. Like OpenMW/VCMI, **no original asset is shipped**: everything is
read at runtime from the user's install (`GameInstall.Locate()`, override with `H5_GAME_DIR`).
Derived caches (converted meshes/animations) go under `cache/` and are git-ignored.

## Projects
| Path | Purpose | May depend on |
|---|---|---|
| `src/H5.Core` | Pak VFS (`IO/`), XDB loading (`Xdb/`), typed game data (`Data/`) | — |
| `src/H5.Combat` | Deterministic combat rules engine (no Godot, no I/O) | Core |
| `src/H5.Assets` | Decoders for Nival binary formats: geometry, skeletons, animations, terrain | Core |
| `src/H5.Server` | Headless authoritative multiplayer server | Core, Combat |
| `src/H5.Tool` | `h5tool` dev CLI; add a command by adding a class implementing `ICommand` | all |
| `tools/H5.GrannyHelper` | 32-bit helper that calls the install's `granny2.dll` to decode GR2 data | — |
| `game/` | Godot 4.7 C# client | Core, Combat, Assets |
| `tests/*` | xUnit tests | — |

## Rules
- Engine libraries are pure .NET 8 with no Godot types, so server and tests share them.
- Gameplay must be deterministic: all randomness goes through a seeded RNG owned by the battle/game state.
- Constants come from game data (`GameMechanics/RPGStats/DefaultStats.xdb` etc.) whenever the data has them;
  hard-coded formulas cite their source (fan manual page, or exe address from disassembly).
- Original-format knowledge goes in `docs/formats/*.md` as it is discovered.

## Original data layout (inside `data/*.pak`, zip with >65535 entries, no Zip64)
- `GameMechanics/` rules (creatures, spells, skills, RPG constants) — XML `.xdb`
- `MapObjects/`, `Maps/`, `Characters/`, `_(Model)/`, `_(AnimSet)/`, `_(BasicSkelAnim)/` — XML descriptors
- `bin/Geometries/<uid>`, `bin/Skeletons/<uid>`, `bin/animations/<uid>` — binary blobs keyed by the `uid` in the descriptor
- `Textures/**.dds`, `UI/**.xdb`, `Text/**.txt` (UTF-16LE), `scripts/**.lua`
