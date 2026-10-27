# Terrain (`GroundTerrain.bin`, `UndergroundTerrain.bin`)

Decoder: `src/H5.Assets/Terrain/TerrainData.cs` (`TerrainReader.Read`). Container reader:
`src/H5.Core/Ui/Binary/NivalChunks.cs` (`ChunkSpan`).
Tools: `h5tool terrain-stats [-v]` (decodes all 293 terrain files in the stock archives — 0 failures),
`h5tool terrain-dump <arena|mapdir|file.bin> <outDir> [pxPerTile] [tilesPerTexture]` (PNGs of every grid
plus a textured, shaded blend preview).

The terrain file is referenced from the map's `map.xdb` / `(AdvMapDesc).xdb`
(`GroundTerrainFileName`, `UndergroundTerrainFileName`); the same format serves adventure maps
(e.g. `Maps/Multiplayer/A1L1`, 176×176, 22 layers) and combat arenas (`Maps/CombatArenas/*`,
40×40 … 96×96).

## Nival chunk container (shared by all `bin/` blobs and terrain)

A stream is a sequence of chunks `[id:u8][len][payload]`:

* `len` low bit 0 → one byte, `len = byte >> 1`;
* `len` low bit 1 → four bytes LE, `len = u32 >> 1`.

Payloads are raw values or nested chunk sequences (the format does not tag which). Conventions:

* arrays: one chunk per element with id 1 (some writers add id 2 = count first);
* hash maps: id 3 = element count, id 4 = bucket count, then all keys (id 1) then all values (id 2);
* 2D grids: `{[1] width, [2] height, [3] raw row-major data}`.

## Terrain layout

```
[4] version (4)
[1] {
  [1] {                       ← terrain object
    [2] TilesX   [3] TilesY
    [4] layers: [2] count?, then [1] per layer:
          [2] weights grid (TilesX+1)×(TilesY+1) bytes 0..255
          [3] { [3] string: tile href "/MapObjects/_(AdvMapTile)/Grass/Grass.xdb#xpointer(/AdvMapTile)" }
    [5]  heights grid (W+1)×(H+1) float32, world units (1 unit = 1 tile)
    [7]  (W+1)² bytes  vertex terrain class flags (see below)
    [8]  (W+1)² bytes  usually 0 (adventure maps: sparse 1s)
    [10] (2W+1)² bytes half-tile mask; water/river mask on adventure maps (values 0..255 graded)
         (2 files store W×W)
    [13] optional (W+1)² float32 (37 files) — second height-like field; empty {w,h} otherwise
    [14] 1 byte (0xF8 on arenas, 0x09 on A1L1)
    [15] (W+1)² bytes  usually 1; 0 in places on adventure maps
    [16] optional small grid (25², 33², 46², 60²) with sparse payload — not decoded
  }
}
```

Layer order is draw order: layer 0 is the base (all 255 on every map checked), later layers blend
over it with their per-vertex weight (bilinear between vertices). `blend.png` renders produced this
way match the expected look (beach arena: grass meeting a sandy river bed; A1L1: four grass
quadrants split by roads with rivers).

The tile xdb (`AdvMapTile`) gives `Texture`, optional `SecondTexture`/`Material`, `Priority`,
`Type` (`TT_GRASS`...), `MinimapColor`, ambient sound set and adventure music.

## Interpretation notes / unknowns

* **Exe layout** (STerrainInfo, serializer `004faaf0`): chunk 7 = per-vertex level (0 sea bed, 8 shore band, 0x10 per
  plateau step; underground 0x20 = rock), chunk 10 = half-tile water mask, chunk 14 = a bool the map load overwrites
  with "underground floor" (`00c0faa0`), chunk 15 = per-vertex "open" flag (0 closes the tile whose top-left vertex it
  is). How the adventure engine turns them into passability: `docs/rules/AUDIT_ADVENTURE.md` "Terrain passability".

* **Chunk 7**: arenas show 16 on land and 0 on the sand/beach side; A1L1 shows 16 (most), 0 along
  rivers, 32/48 on some patches. Probably a per-vertex ground category bitfield (water/shore/plateau).
  Needs confirmation from the exe or the map editor.
* **Chunk 10** traces rivers exactly on A1L1 → river/water depth mask at half-tile resolution.
* **Chunk 13/15/16/8/14**: unknown; preserved in `TerrainData` (13, 15, 8, 14) or skipped (16).
* **Texture tiling**: number of tiles covered by one repeat of a 1024² terrain texture is not in
  the data (`MappingSize` is 0). Previews assume 4–8; must be measured against the original
  (screenshot) or found in the exe.
* Y axis: grids are stored row 0 first; map object `Pos` uses the same (x, y) tile coordinates
  (see arena.md). Whether +y is north/up in the 3D view needs a screenshot comparison.
