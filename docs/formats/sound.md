# Sound and music

## Descriptors — `Sounds/**`

* `Sounds/_(Sound)/**.xdb` → `Sound { SrcName (original .wav/.ogg path, informational), uid, Loop,
  Is3D, MinDistance, MaxDistance, Priority, StartSamples, EndingSamples, FrequencyVar, VolumeBase,
  VolumeVar, FinalSound }`. Variation fields randomise pitch/volume per play.
* `Sounds/_(Ambient)/**` — `AmbientSoundSet`, `AdvAmbientSoundList` (terrain ambience).
* `Sounds/_(Music)/**` — `Music` objects (per terrain / faction / battle).
* References: `BasicSkelAnim.Sound` (per creature animation), `SpellVisual.SoundEffect`,
  `AdvMapTile.AmbientSoundSet/AdventureMusic`, `UISPlaySound`, arena `AmbientSound/Music`.

## Data — keyed by `uid`

| archive | path | count | format |
|---|---|---|---|
| `soundsfx.pak` | `bin/Sounds/<uid>` | 2046 | 2007 × RIFF WAVE, `wFormatTag = 0x0011` (**IMA ADPCM**, MS block layout, e.g. 44.1 kHz mono, block align 1024), 39 × Ogg Vorbis |
| `sound.pak` | `bin/SoundsLoc/<uid>` | 884 | 882 × Ogg Vorbis (localised speech), 2 × WAV |
| install dir | `music/**.ogg` | 163 | Ogg Vorbis music (AdvMap-Terrain, Battle-Themes, AI-Themes, ...) |
| install dir | `video/` | — | cutscenes/logos (`logo-nival.ogg`, `logo-ubi6.ogg`, `intro_a1.xml`...) |

Match is by uid string, case-insensitive (pak paths are upper-case, descriptors mixed).

## Godot

* Ogg Vorbis: `AudioStreamOggVorbis.LoadFromBuffer(bytes)` at runtime — no conversion.
* IMA ADPCM WAV: Godot's runtime `AudioStreamWav` has an `ImaAdpcm` format but uses its own
  nibble layout; decode the Microsoft IMA ADPCM blocks to PCM16 ourselves (per block: 4-byte header
  per channel = initial sample i16 + step index u8 + pad, then 4-bit nibbles, low nibble first;
  standard IMA step/index tables) and feed `AudioStreamWav { Format = Format16Bits }`. ~60 lines;
  can also be done once into the `cache/`.
* `Is3D` sounds → `AudioStreamPlayer3D` with `UnitSize/MaxDistance` from Min/MaxDistance.

## Decoder (implemented)

`src/H5.Assets/Audio/ImaAdpcm.cs` decodes every WAVE in `bin/Sounds`: MS IMA ADPCM (0x0011, nearly
all), **MS ADPCM (0x0002, 5 files)** and PCM. `game/src/Audio/SoundLibrary.cs` resolves a Sound xdb by
`uid` (bin/Sounds, then bin/SoundsLoc), builds an `AudioStreamWav`/`AudioStreamOggVorbis` and plays it
with the descriptor's volume/pitch variation (3D sounds via `AudioStreamPlayer3D`).

## Music and ambience routing (implemented — game/src/Audio, src/H5.Assets/Audio/Music*.cs)

Exe evidence and behaviour are in `docs/rules/AUDIO.md`. Data used:

* `Sounds/_(Music)/*.xdb` → `Music { FileName, FadeIn, FadeOut, MinPausedTime, MaxPausedTime }` (ms).
  `FileName` is relative to the original working dir `bin/`: `..\Music\a2\Main_Menu.ogg` →
  `<install>/music/a2/Main_Menu.ogg` (every descriptor resolves — tested).
* `Sounds/_(Music)/TableRaceMusic.xdb` (UIGameRoot.TableRaceMusic): per race `TownAmbientSoundSet`,
  `TownMusic`, `TavernMusic`, `DwellingMusic`, `combatMusics[]`, `SiegeMusic`, `Win/Loose/RetreatCombatMusic`,
  `WaitMusic`, and building sounds. `TOWN_NO_TYPE` (neutrals) has only a combat theme.
* `UI/UIGameRoot.(UIGameRoot).xdb`: `musics` (MAIN_MENU_MUSIC_ID, MP_MAIN_MENU_MUSIC_ID,
  CREDITS_SCREEN_MUSIC_ID, HERO_FIND_GRAIL_MUSIC, BUILD_GRAIL_MUSIC), `sounds` (HERO_LEVEL_UP_SOUND,
  NEW_WEEK_*_SOUND, OBJECTIVE_*_SOUND, BUY/HIRE…), `ambientSounds` (INVISIBLE_AMBIENT_SOUND).
* `UI/uiconsts.(UIGameConstsH5).xdb`: `ButtonClickSound`, `DisableButtonClickSound` (→ UI/Sounds/*.(UISPlaySound)).
* `MapObjects/_(AdvMapTile)/**`: `AmbientSoundSet` + `AdventureMusic` per terrain tile.
* `(AdvMapHeroShared).AdventureMusic` (campaign heroes), `(Campaign)`: `AdventureMusic`, `Win/LoseMissionMusic`,
  and per mission `startMusics`, `additionalAdventureMusics`, `additionalWaitMusics`, `additionalCombatMusics`.
* Arena `(ArenaDesc)`/`CombatArenaTypes`: `AmbientSoundSet`/`AmbientSound`, `Music` (towns).
* `AmbientSoundSet { Loop, sounds[], MinPauseTime, MaxPauseTime }` (pauses in seconds, floats).
* Some original Oggs carry a malformed Vorbis comment ("Sony Ogg Vorbis 1.0 Final"); Godot logs a harmless warning.
