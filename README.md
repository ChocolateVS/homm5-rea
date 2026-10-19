# H5Reborn

A from-scratch reimplementation of *Heroes of Might and Magic V: Tribes of the East* (3.1) on
Godot 4 (C#/.NET 8), for Windows and Linux, with modern peer-to-peer and online-lobby multiplayer.

**You need your own copy of the original game.** H5Reborn contains no original assets: it reads
the game data (`data/*.pak`, music, video, maps…) at runtime from your installed copy. Point it at
the game folder with the `H5_GAME_DIR` environment variable or a `game_dir.txt` file next to the
executable; common GOG/Ubisoft/Steam (and Wine/Proton/Heroic/Lutris on Linux) locations are found
automatically.

**Beta builds:** Windows x64 and Linux x64 archives are in [`beta/`](beta/). See
[`beta/README-BETA.md`](beta/README-BETA.md) for requirements and launch options.

## Building

Requirements: .NET 8 SDK and Godot 4.7.2 .NET (mono) edition.

```sh
dotnet build H5Reborn.sln            # libraries, dedicated server, tools and tests
dotnet test H5Reborn.sln             # test suite (tests that need game data look for your install)
dotnet build game/H5Reborn.csproj    # the Godot client
```

Run the client from the Godot editor (open `game/project.godot`), or from the command line:
`godot --path game` (main menu) or `godot --path game -- --combat-vs-ai=0,3` (duel against the computer).
Dedicated server: `dotnet run --project src/H5.Server -- --port 27615`.

Release exports use `game/export_presets.cfg` (presets "Windows Desktop" and "Linux"), e.g.
`godot --headless --path game --export-release Linux out/H5Reborn.x86_64` (export templates for
4.7.2.mono must be installed).

Documentation: [`docs/`](docs/) (architecture, display, multiplayer, file formats, rules audits).
