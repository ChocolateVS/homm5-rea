# Random map generator (RMG)

Port of the TotE random map generator from `H5_Game.exe` into `src/H5.Rmg`. Data comes from the
stock archives (`RMG/Templates/*.xdb`, `RMG/Params/Default.xdb`, `GameMechanics/RefTables/RMGPresetTable.xdb`,
`RefTables/Creatures.xdb`, `RefTables/Artifacts.xdb`). Output is the adventure engine's `AdvMap`
(`RmgResult.ToAdvMap`), with flat terrain layers the client can render.

Tools: `h5tool rmg <template> <size> <seed> [--players=N] [--underground] [--monsters=0..4] [--png=out.png]`,
`h5tool rmg-stats [seeds]` (every template × size × seed), `h5tool rmg-ref` (the user's RMG maps in
`Maps/*RMG*.h5m` regenerated from the template/seed recorded in their description, counts side by side).
Analysis: private Ghidra project `tools/ghidra-projects/rmg`, scripts in `tools/rmgre/` (`ghidra.sh`).

## Status at a glance

| Stage | exe | Status |
|---|---|---|
| RNG | `0x00ba6650/6680/6700/6790/6800` | exact (64-bit LCG, unit-tested against the formula) |
| Option resolution | `0x0090e7e0` prologue | exact, incl. the one-draw-per-option rule |
| CreateMap draws | `0x0090cb00`, `0x00911500` | draw sequence ported |
| Zones, floors, races, players | `0x0090bd80`, base ctor `0x00925850` | exact: **player races of all reference maps are reproduced** (tests) |
| Zone centres | `0x00905070` | ported line by line (point cloud, shuffle, radius, k·0.97 retries) |
| Tile → zone fill | `0x00907d90` | ported line by line (seed circles, √3·W growth passes, steal test) |
| Border distance | `0x00904240` | ported |
| Town placement | `0x00923e50`, `0x00919810` | algorithm ported; positions approximate (within a few tiles of the reference on some maps, elsewhere different) |
| Mines | `0x0091c4a0` | ported (radius bands, 2/5 spread filter, guard levels, decoration ring) |
| Buildings by points / density | `0x009221d0`, `0x00921d70` | ported budget algorithm |
| Treasures / chests | `0x0091f8a0`, `0x00905e60` | count formula ported (tiles·density/10000 × multiplier) |
| Guards | `0x00ba7690`, `0x00ba6910` | single-stack picker ported (rand(30)+10 band search) |
| Connections, roads, statics, treasure blocks | `0x00904920`, `0x00904cf0`, `0x00906130`, `0x009198a0` | reimplemented from the observed structure, not line by line |

## RNG (0x00ba6650 …)

Globals `0x01040028` (draw counter), `0x01040030/34` (64-bit state).

* seed: `s = (long)seed` (sign-extended), counter = 0
* step: `s = s·0x343FD + 0x269EC3 (mod 2^64)`, counter++
* `Next(n)` (`0x00ba6700`): step, return `((s >> 16) & (2^47−1)) % n`; `n == 0` returns without stepping
* `Advance()` (`0x00ba6680`): step, value discarded — used in every "option not random" branch so the
  number of draws does not depend on the options
* `Float(a,b)` (`0x00ba6790`): step, `a + (b−a)·((s>>23) & 0x7FFFFFFF)·2^-31`
* shuffle (`0x00910090`): MSVC `random_shuffle`, `swap(a[i], a[rand(i+1)])`

The generator logs the counter per stage ("Rnd Counter(LoadTemplate): %d"); `RmgGenerator.Counters` records the same.

## Sizes

Tiles per side (`0x00e556a8`): 72, 96, 136, 176, 216, 256, 320 (tiny … impossible).
Template size units per index (`0x00903cd0`): 5, 10, 18, 31, 47, 66, 102; units → index (`0x00903d40`):
<8, <15, <25, <40, <60, <90, else 6.

## Option resolution (0x0090e7e0)

Exactly one draw each for underground, size and players:
* underground: `MaxMapSize == 1` → none; `MinMapSize > 102` → forced; random → `rand(2)`; else Advance
* size: random → `rand(Max−Min+1)+Min` (halved with underground), mapped to an index; else Advance
* players: random → `rand(Max−Min+1)+Min`; else Advance; clamped to the template
* if `units(size)·(ug+1) < MinMapSize` the size grows (and switches underground on above index 5)

CreateMap (`0x0090cb00`): monster level `rand(3)` or Advance; water `rand(2)` or Advance; map builder
(`0x00911500`): `Float(0, 2π)` + Advance; ambient light `rand(lights)`; random moons `rand(10)`.

## Zones (0x0090bd80)

Records `{size, floor, zone}` are merge-sorted by size (strided bottom-up merge `0x0090f6b0` whose tie
order is right-first), split between floors when underground (balance by total size, `rand(2)` while
within 2), re-sorted by template index (`0x0090f7d0`), then one `rand(2)` picks the zone class.
Per zone in index order:
1. race: random settings (`RACE_SPECIAL/RANDOM/NO_TYPE`) draw from the floor's list — surface
   {Haven, Preserve, Academy, Dwarf, Inferno, Necropolis, Stronghold} (+Dungeon without underground),
   underground {Dungeon, Inferno, Dwarf, Necropolis}; fixed settings keep their race
2. one Advance in the start-zone bookkeeping; start zones take the next player record — a race chosen
   on the setup screen wins over the drawn one (that is why player 1 differs from a "random" run on
   the user's maps)
3. the base zone constructor `0x00925850` keeps one draw as the zone's seed (`+0x13c`)

Zones live in a Nival hash map (13 buckets, head insertion, prime rehash `0x00b62030`); iteration
order matters for centres and fill, `NivalHashMap` reproduces it.

## Zone centres (0x00905070)

`W·H/100` points `(x = rand(W), y = rand(H))` (the draws are `rand(H)` then `rand(W)`). Per attempt:
shuffle; radius `R = round(√(size/Σsize · W·H · k) / 3)` (×√2 with underground), k starts at 0.9;
per floor: shuffle again, then each zone takes the first point at least R from the edges and
`R + R_other` from every other zone of the floor — unplaced zones keep their previous (initially 0,0)
centre and still reject points. Any failure: `k ·= 0.97` and retry.

## Fill (0x00907d90)

Seed: each tile gets the first zone (hash order) whose circle (centre rounded) contains it.
Then `⌈1.732·W⌉` passes, each with a random sweep direction (`rand(2)`, `rand(2)`):
* unassigned tile: count 8-neighbour zones (order N, E, S, W, NW, NE, SE, SW); the first maximum
  (hash order over a fresh 13-bucket map) with count > 2 claims it
* interior assigned tile (x, y in 6 … W−7): if no neighbour is unassigned, the first-maximum
  neighbour zone with count > 2 steals it when `tiles(other)/tiles(own) < size(other)/size(own)` and
  `Float(0,1) > 0.4`
Claims apply at the end of the pass; zone tile lists are rebuilt only then (`0x0091bc80`) — so in the
first pass nothing can be stolen (division by an empty list).

## Objects

* Border distance (`0x00904240`): border tiles = a 4-neighbour outside the map or in another zone;
  others get the Euclidean distance to the nearest border tile of their zone.
* Towns (`0x00923e50`): candidates with border distance > R/2; each try draws a candidate and a
  quarter turn; the anchor (first active tile) must be ≥ 2R/3 from the border (relaxed after 100
  tries); footprint failures drop the candidate; the town's point light draws twice
  (`0x0092a400`). Zones without a town anchor at their tile centroid (`0x00919810`).
* Mines (`0x0091c4a0`): wood/ore between Mine1 radii of the town, others between Mine2 radii; tiles in
  the outer 2/5 of the zone's distance range are skipped; guard = `BasicLeverGuardPower × level`
  (Mine1 / Mine2 / Gold levels); up to two decoration tiles nearby (p = 0.8).
* Point budgets (`0x009221d0`): while the cheapest entry fits, pick `rand(#affordable)` and place it.
  Density budgets (`0x00921d70`): `tiles · (density + 40) / 10000`.
* Treasures (`0x0091f8a0`): `tiles · density / 10000`; the density is scaled by a switch on the
  generator's +0xa8 / +0xb0 settings (0.2, 0.5, 1, 2, 4; `0x00905eac`).
* Guards (`0x00ba7690`): value = power × monster coefficient (0.4, 0.9, 1.7, 4, 12 for weak …
  impossible); with p = 0.6 the original uses a two-stack army template (`RMG/CustomArmyTemplates`),
  otherwise `0x00ba6910`: `r = rand(30)`, creatures (except ids 0, 0x59, 0x72) whose Power·(r+10)
  lies within ×k of the value, k = 1.1 growing ×1.2 until ≥ 10 candidates; count = value / Power.

## Calibrations (not decoded, tuned against the user's RMG maps)

* Treasure and chest multipliers default to the ×2 entry (index 3): it reproduces the reference
  treasure/chest counts; the UI setting that selects it was not traced.
* Upgrade buildings use ×1.
* A quarter of loose treasures are minor artifacts (the exe's treasure table at `0x00ff9efc` was not decoded).
* Treasure blocks: blocks of 5 000–15 000 value (an artifact, two piles, a guard of the block value)
  until the zone's `TreasureBlocksTotalValue` is spent; the original `0x00ba8050` was not ported.
* Statics: zone borders, unassigned tiles and the map edge are filled with the zone race's BigStatics /
  Mountains / OneTileBigObjects / OneTileSmallBlockers, plus interior clusters (one seed per 45 free
  tiles); walkable routes from the zone hub to every object and passage are reserved first.
* Connections: adjacent zones get a passage near the line between their centres (3 wide when
  `Wide`), guarded with `GuardStrenght × BasicLeverGuardPower`; non-adjacent or cross-floor
  connections get a pair of two-way monoliths.

Reference comparison (`h5tool rmg-ref`, same template/size/seed/players/races): mines, prisons and towns
match exactly on every map; dwellings, shrines, monsters, statics and treasures are within about ±40%.

## Gaps

* Bit-exact reproduction stops after zone creation: centre/fill/town stages are ported but the
  resulting positions differ from the reference maps (likely footprint-test and anchor-rotation
  details in `0x00919be0` / `0x00b0be60`, and the water/terrain stages `0x00915dc0` / `0x00918270`).
* No `.h5m` writer yet (the original writes `Maps/RMG/<guid>/` with map.xdb, GroundTerrain.bin, texts,
  minimap); maps exist in memory as `AdvMap`.
* Grail/obelisks follow the screen's EP_GRAAL_ON_MAP option (0x00907150 / 0x00921a50 / 0x00921a10, see
  AUDIT_ADVENTURE.md "Random maps"); their candidate filters are not ported.
* Water zones, shipyards, lakes, Den of Thieves / Redwood Observatory densities,
  cartographers, the two-stack guard templates and per-zone point lights are not generated.
* Terrain is flat and uses only each race's default and road tiles (no transitive/secondary tiles).
