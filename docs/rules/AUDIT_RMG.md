# Random map generator audit ledger

Every stage of the random map generator (`src/H5.Rmg`) compared with `H5_Game.exe` (Ghidra 12.1.4, image VAs; project
copied from `tools/ghidra-projects/rmg`, scripts in `tools/rmgre/scripts`) and the original data. The target is the
stock 3.1 engine running either the stock templates or MMH 5.5's 94 template files (`MMH55-Templates.pak`, 72 new);
MMH55's `MMH55_Utility.exe` RMG byte patches are out of scope (docs/mmh55/PLAN.md §0). Status: **MATCH** (already as
the exe), **FIXED** (changed to match), **GAP** (known difference or not built), **UNVERIFIED** (implemented from data or
observation, exe path not traced). Rows carrying two statuses count once for each. Stage overview and formulas:
`docs/rules/RMG.md`.

Rows at 2026-10-27: 27 FIXED, 7 MATCH, 9 GAP, 8 UNVERIFIED.

Tools: `h5tool rmg-stats [seeds] [--threads=N] [--filter=s] [--max-players]` generates every template at every
size / underground / player count the RMG screen lists it for; `h5tool rmg-ref [--detail]` regenerates the user's
stock RMG maps (template, seed, races, monster level and the recorded Resources / Experience levels) and prints
counts (per shared object with `--detail`).

Sweep (MMH 5.5 install, 93 template names from 94 files, 1 seed): **465 runs, 0 failures**, 2 unreachable objects
(4P-Ultima-I/XLu and 8P-Epic-I/XLu at impossible size, 3 players), 1569 s on 8 threads.

## Screen options and template list

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Templates the screen lists | 0x0090c880 (IRandomMapGenerator vf3; fills the template combo, the random template is `FUN_00b25910() % n` of it, 0x007993a0) | test templates never; fixed size: units (0x00903cd0) x0.7 rounded with water (0x00e15698), x2 with a fixed underground, within [MinMapSize, MaxMapSize]; fixed underground or random size with a fixed underground needs MaxMapSize > 9; fixed players within [MinPlayers, MaxPlayers] | `RmgTemplate.ListedFor`; rmg-stats iterates exactly these combinations | FIXED (was: every size whose units lay within 2x the limits, max players only) |
| Template list identity | 0x0090c880 loads every `RMG/Templates` resource | one entry per file | `RmgDatabase.Templates` keyed by `Name`: two MMH55 files share a name, one of them is hidden | GAP |
| Players on the map | 0x009058e0 writes `players.[i].ActivePlayer` for i < min(Players (+0x28), 8) | every chosen player is active, with or without a start zone | `RmgResult.ToAdvMap`: Active = p < Players | MATCH |
| Player records vs start zones | 0x0090bd80: random-race zones with CanBePlayerStart take the next PlayersInfo record while records remain, else append one (0x00910820) while fewer than Players; fixed-race zones likewise | a template with fewer start zones than players (MMH55 4P-Diamond-XL-#: 4 start zones, up to 8 players) gives start towns to the first ones only, the others are active without town or hero | same; the W1 note "4P-Diamond-XL-# places 4 start towns wrongly" was the old test's expectation, not a generator fault. Test `MorePlayersThanStartZonesKeepsTheExtraPlayersActive` | MATCH / GAP (what the adventure engine does with an active player without town and hero at day 1 is the adventure workstream's) |
| PlayersInfo from the screen | 0x00799230: only when the player count is not random (+0x14) and +0x95 is clear; one record per player, race = combo + 2, out of 3..10 → 1 (random) | | `RmgOptions.PlayerRaces` padded with 1 | MATCH |
| Resources / Experience combos | UIGameRoot EP_RESOURCE_LEVEL / EP_EXP_LEVEL (5 items "1".."5"), 0x00799230 → InitialParams +0x98 / +0xa0 (out of 0..4 → 2), generator +0xa8 / +0xb0, saved as `ResourceMultiplier` / `ExpMultiplier` (RESOURCE_/EXP_ MISERABLE, LITTLE, NORMAL, LOTS, MUCH = 0..4, enum 0x009fce30) | factors 0.2, 0.5, 1, 2, 4 (jump tables 0x009060fc / 0x00906110, and the inline switch in 0x00907150) | `RmgOptions.ResourceLevel` / `ExpLevel` (default 2); rmg-ref reads them from the map (all nine stock reference maps: MISERABLE) | FIXED (was: a ×2 "calibration" for treasures and chests, ×1 for upgrades) / UNVERIFIED (the combos' initial selection when the screen opens; ours 2) |

## Guards

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Minimum power | 0x00ba7690 | raw power < 100 → "no monster set at %d:%d", no draws | `PickGuard` | FIXED (was: tested after the coefficient) |
| Monster level coefficient | 0x00ba7690 switch on +0x34, constants 0x00e12fe4 / 0x00e1a704 / 0x00e826fc / 0x00e11c80 / 0x00e57d34 | value = round(power x 0.4 / 0.9 / 1.7 / 4 / 12) | same | MATCH |
| Two-stack army templates | 0x00ba7690: `Float(0,1) < 0.6` (0x00e18580) → 0x00ba71b0 with round(value x 0.9); group `RMG/CustomArmyTemplates/SimpleTemplates/AutoTemplates/TestTemplateGroup` (0x00e568c0, 0x00ba7e30) | templates whose [MinPower, MaxPower] holds the value, rand(n); per stack coef x (value / Σ coef·Power), remainder / Power added to the last stack; accepted when the first stack has a creature and a count (0x00ba7040 → 0x00914cc0: main stack + `AdditionalStacks`), otherwise the single-stack picker with the unscaled value | `PickArmyTemplate`, `RmgDatabase.ArmyTemplates`, `RmgObject.AdditionalStacks` → `MapObject.AdditionalStacks` | FIXED (was: not generated, one draw consumed) |
| Single stack | 0x00ba6910 (rand(30) at 0x00ba6998, factor 1.2 at 0x00e12f1c) | r = rand(30); creatures except 0, 0x59, 0x72 (any town: argument 1) with (r+10)/k·Power ≤ value ≤ (r+10)·k·Power appended per pass, **list not cleared between passes**, until ≥ 10 entries; rand(n); count = trunc(value / Power) (FIDIV + _ftol 0x00ba6ad1) | `PickSingleStack` (double-precision band, k stored as float) | FIXED (was: list cleared per pass, count ≥ 1, creatures without Power/MonsterShared skipped) |
| Guard object | 0x00914b40 | `Custom` = true, `Amount`; mood/courage not written (map defaults) | Monster written with Custom, Amount, plus MONSTER_MOOD_AGGRESSIVE / CAN_FLEE_JOIN | GAP (mood/courage defaults; the rotation draw `Rng.Next(4)` of our PlaceGuard is not traced) |
| Guard power per site | towns (TownGuardStrenght, 0x009058e0 via vf 0x20: BasicLevelGuardPower x level), mines (0x0091c4a0), connections, upgrade buildings (0x00ba79f0 with the preset entry's GuardStrenght) | | `GuardPower(level)` | UNVERIFIED (0x00ba79f0 and the connection guard 0x00904920 not decompiled line by line) |
| Which buildings are guarded | 0x00920ec0 / 0x00921d70 / 0x00921360 / 0x009221d0 / 0x00920000 call no guard function; 0x0091f140 calls 0x00ba79f0 | only upgrade buildings (and mines, dwellings, towns, connections, treasure blocks) | `SpendBudget(upgrades: true)` only | FIXED (was: every budget object with a GuardStrenght) |

## Object placement (per zone, 0x00907150)

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Zone order and Graal zone | 0x00907150 | zones in template list order; the Graal zone is a list position (rand(#zones) or one Advance) | template list order, position compare | FIXED (was: ordered by zone Index, compared with Index) |
| Stage order | 0x00907150 | mines 0x0091c4a0, abandoned mines 0x00920630, town/hero 0x0091c3a0, dwellings 0x0091e910, Graal/obelisks, upgrades 0x0091f140, prisons 0x00920370, cartographers 0x00920c50, shrines 0x00920000, resource buildings 0x00920ec0, treasuries 0x00921360, luck/morale 0x00921d70, shops 0x009221d0, buffs 0x00918b30, 0x009219a0, treasures 0x00905e60 (surface zones only), roads 0x0091bdb0; after the zone loop: roads 0x00904cf0, statics 0x00906130, underground treasures 0x00906380, treasure blocks 0x00905d00, terrain finish 0x00904ad0 (driver 0x0090e7e0) | same | FIXED (roads per zone were missing) |
| Far-candidate filter | 0x0091bb10 (+ 0x009191a0 flag 4, 0x00919710) | max object distance M over zone tiles with border distance > 2 and occupancy != 2; candidates: zone tiles with distance > 2M/3 and border distance > 0; empty → nothing | `FarCandidates` for prisons, cartographers, shrines, resource/treasury/luck-morale buildings, shops, obelisks, Redwood Observatory, Den of Thieves | FIXED (was: fractions 1/2, 1/3, 2/3 per caller, occupancy filter on the candidates, fallback to all tiles) |
| Object distance field | 0x009191a0 flag 4: distance to the zone list +0x68, 10000 (0x00e57d84) when empty, 1000 outside zones | +0x68 is also the road-node list of 0x0091bdb0 | distance to every non-static object of the zone and the town | UNVERIFIED (which placements push a node, 0x0091a420, not traced) |
| Footprint and path tests | 0x00919be0, 0x009145b0, mark 0x0091a420 | a path failure keeps the candidate (prisons, cartographers, budgets: retried) | `CanPlace` (one test, a failure always drops the candidate) | UNVERIFIED |
| Budget loop | 0x00920ec0 etc. | while entry 0 fits: n = leading entries (preset order) that fit, rand(n), stop if it does not fit or no candidates, place, add value | `SpendBudget` | FIXED (was: pool sorted by value, values < 1 counted as 1) |
| Budgets | 0x00920ec0: tiles·ResourceBuildingsDensity/10000; 0x00921d70: tiles·(LuckMoralBuildingsDensity+40)/10000; 0x00921360 / 0x009221d0: TreasureBuildingPoints / ShopPoints; 0x0091f140: tiles·round(UpgBuildingsDensity x Exp factor)/10000 | | same | FIXED (resource and upgrade budgets had +40) |
| Shrines | 0x00920000, table 0x00ffa068 (init 0x00dc8bd0), costs 0x00e57d74 | ShrinePoints buy Shrines of Magic 1/2/3 for 6/10/12 points (not the race preset's NewShrines); needs > 5 points, continues while 6 more fit | `PlaceShrines` | FIXED |
| Buff buildings | 0x00918b30 is `return;` | BuffPoints place nothing | none | FIXED (was: placed by points) |
| Redwood Observatory, Den of Thieves | 0x009219a0, objects 0x00ff9984 / 0x00ff99a4 (init 0x00dc86f0) via 0x009216f0 | tiles/4000 + 1 observatories per zone; zones without a player: one Den of Thieves when rand(10) < 2; template RedwoodObservatoryDensity / DenOfThieves unused | `PlaceBuilding` | FIXED (was: not generated) |
| Building placer | 0x009216f0 | ≤ 100 attempts of rand(n) candidate + rand(4) quarter turn | `PlaceBuilding` (also obelisks) | FIXED |
| Cartographers | 0x00920c50, object 0x00ffa028 | LandCartographer per zone; stops when the candidate list is empty | loop over `FarCandidates` | FIXED (was: not generated) |
| Prisons | 0x00920370 | `RandomHero` = true | prison object only | GAP (RandomHero flag not written) |
| Upgrade-building candidates | 0x0091f140 | distance > 2M/3, no border test | `UpgradeCandidates` | FIXED |
| Mines, abandoned mines, dwellings | 0x0091c4a0, 0x00920630 (distance > 4M/5 over tiles with border distance > 1 passing 0x009157b0; path test 0x009151c0), 0x0091e910 (distance > 2M/3) | | fractions 2/5 (mines), 4/5 (abandoned mines), 2/3 (dwellings) over `SpreadCandidates` | FIXED (abandoned mines were 2/5, dwellings 1/3) / UNVERIFIED (pre-filters 0x009157b0, mine radius bands) |

## Treasures

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Treasure table | 0x0091f8a0 (rand(9) at 0x0091fb45), table 0x00ff9ee8 (init 0x00dc8ad0) | Campfire, Chest, Crystal, Gems, Gold, Mercury, Ore, Sulfur, Wood; chests use entry 1 | `TreasureTable` | FIXED (was: a quarter minor artifacts — invented) |
| Count | 0x00905e60 | tiles · trunc(TreasureDensity x Resources factor) / 10000, chests with TreasureChestDensity x Experience factor (FISTP with chop) | same | FIXED (was: rounded, x2) |
| Surface / underground | 0x00907150: 0x00905e60 only when zone floor +0xf4 == 0; 0x00906380 ("additional objects set", after the statics 0x00906130): 0x00905e60 for every template zone whose zone has +0xf4 != 0 | surface zones get treasures and chests with their other objects, underground zones after the statics | `PlaceUndergroundTreasures`; test `UndergroundZonesGetTreasuresAfterTheStatics` | FIXED (was: underground zones got none) |
| Candidates and failure | 0x0091f8a0 | zone tiles with border distance > 0, distance > 2M/3, occupancy != 2; type drawn before the candidate; an exhausted list ends the zone's treasures | same | FIXED |
| Treasure blocks | 0x009198a0 → 0x00ba8050 / 0x00ba82c0 / 0x00ba94c0 (CTreasureBlockDistributor) | block sites on tiles at distance 1 of the 0x38-flag field with ≥ 2 qualifying 8-neighbours (0x00f76c28), apart by Params +0x84 from other blocks and the zone's list; TreasureBlocksTotalValue split evenly (or by weight, +0xa0); each block: a guard 0x00ba7690 facing the town, artifacts / piles / chests from tables 0x01040e10 / 0x01040f10 | blocks of 5 000–15 000 value: one artifact, two piles, one guard | GAP (not ported; most of the reference maps' artifacts, chests and a third of their monsters come from here) |
| Mine decorations | 0x0091c4a0, table 0x00ff9e08 (init 0x00dc8a50: Wood, Ore, Mercury, Crystal, Sulfur, Gems, Gold) | | `ResourcePiles` | MATCH |

## Other stages

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| RNG, option draws, zones, races | see RMG.md | | | MATCH (unit tests) |
| Zone centres, fill, border distance | 0x00905070, 0x00907d90, 0x00904240 | | ported line by line | MATCH |
| Town placement | 0x00923e50, 0x00919810 | | algorithm ported, positions differ on some reference maps | UNVERIFIED |
| Connections | 0x00904920 | | passage near the centre line, monoliths when not adjacent | GAP (not line by line) |
| Road builder | 0x0091aee0 (constants 0x00e156d8, 0x00e15a80, 0x00e0a7e4, 0x00e57da0, 0x00e3dee4, 0x00e0ab70; tables 0x00f467d8 / 0x00f467f8 / 0x00f46798) | distances from 1000, source 0; pass k expands zone tiles with chop(dist) == k (x 1..W-2 outer, y 1..H-2 inner): orthogonal (100-bd)·0.01+1, diagonal (100-bd)·0.01+1.41, +(5-bd) when bd < 5; flag == 2 is a wall; stop when the target is reached or no zone tile is left at 1000 (800 logs "pure road algo failed.", > 2000 aborts); end flags cleared for the search and OR-ed back; walk back from the target with Next(2) choosing the 8-neighbour scan direction, first strictly lower neighbour, until within 0.5 of the source; every tile appended to the output list and OR-ed with the road flag; all conversions chop | `BuildRoad`, `RoadSearch` (pass buckets in scan order; test `RoadSearchMatchesTheFullScanTranscription` checks it against the literal full-scan loop) | FIXED (was: A* from a hub, not the exe's) |
| Object paths | 0x0091bdb0, per zone right after its treasures (0x00907150) | for each node i of +0x68 a path (flag 0x20, list +0x8c) to the nearest later node (< 1000, first on ties), node 0 when none | `ConnectObjectNodes` | FIXED (was: none) |
| Main and mine roads | 0x00904cf0 (floors × the floor's zone table) → 0x0091bf90 | network +0x74 starts at the town +0x0c (when +0xf8, set by 0x00923e50) or the first connection point; each connection point +0xc0 joined to its nearest network tile (flag 8, appended to +0x74); each mine point +0x11c joined (flag 0x10, list +0x80) to the nearest network tile with index % 13 == 5, or a nearer mine-road tile with index % 11 == 7 | `BuildZoneRoads` | FIXED (was: hub-to-passage paths, town zones only) |
| Road nodes and points | 0x0091a420 (blockedTiles +0x54 → flag 2; activeTiles +0x6c → flag 4, node +0x68, and the mine list +0x11c from 0x0091c4a0; a third list +0x84 with non-zero offsets → node, flag 4); 0x00925a00 (connection guard point → +0xc0, +0x98, +0x68) | | `Commit` writes flags 2/4 and the visit-tile nodes; border passages add both sides' points; mines their visit tiles | UNVERIFIED (our connections, guards and statics are not the exe's; the +0x84 list, the mine decoration flags of 0x0091c4a0 and a teleport's +0xc0 point (0x0091a950) are not ported) |
| Road painting | 0x00904ad0 → 0x00916570 | tiles with flag 8 or 0x10 painted with the zone preset's Tiles RoadTile (+0x4c, flag 8) or SecondaryRoadTile (+0x58), all four corners at 0xff | `PaintRoads` after the treasure blocks, one tile entry per tile | FIXED (was: RoadTile on hub paths of town zones) |
| Statics / obstacles | 0x00906130 | | border/edge fill and interior clusters from race presets | GAP |
| Terrain painting | 0x00915dc0, 0x00918270 | race tiles, transitive tiles (Params TransitiveTileIntensity), water stages | race default + road tiles, flat | GAP |
| Water zones, shipyards, lakes, water treasures | CGameWaterBorderedZone (0x00bae920), Params WaterTreasures | | not generated | GAP |
| Graal and obelisks | 0x00921a50, 0x00921a10 → 0x009216f0 | | `PlaceGrailAndObelisks` (obelisks through `PlaceBuilding`) | FIXED (candidate filter 0x0091bb10 now used) / UNVERIFIED (Graal candidates) |

## Reference maps after this pass (`rmg-ref`, stock install)

Mines, prisons, towns: equal on all nine maps. Buildings: 95/95, 92/95, 49/52, 57/52 … (was 133/95, 125/95, 83/52,
75/52); shrines within ±2. Monsters ~65 % and treasures/artifacts ~30 % of the reference: the missing share is the
treasure blocks (GAP above).
