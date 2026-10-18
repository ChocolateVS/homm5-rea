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
