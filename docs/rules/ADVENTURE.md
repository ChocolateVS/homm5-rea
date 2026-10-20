# Adventure layer (`src/H5.Adventure`)

Pure .NET rules for the adventure map, towns, heroes and the turn cycle. Same conventions as
`H5.Combat`: deterministic, seeded RNG inside the state (`AdvState.Rng`), command → events
(`Adventure.Apply`), `AdvState.ComputeHash()` for lockstep/desync checks, JSON with `"$t"` tags
(`AdvJson`).

## Map loading (`Map/`)
- `MapCatalog.All` finds every playable map: `Maps/Multiplayer|Scenario|SingleMissions/**/map.xdb`
  in the archives plus user maps `<install>/Maps/*.h5m` (zips layered over the game data).
  75/75 load (`h5tool adv-stats`).
- `MapLoader` reads players (team, race, colour, main town/hero links by `item_` id, tavern filters),
  objectives (primary/secondary, common/per-player, `DieInWeekWithoutTowns`), map script (MapScript.xdb →
  .lua source), both floors' terrain (`GroundTerrain.bin`/`UndergroundTerrain.bin` via `H5.Assets.Terrain`)
  and all 22 object kinds (190k statics, treasures, monsters, buildings, mines, towns, heroes …) with their
  shared descriptors (`SharedCatalog`: footprint tiles, type, kind-specific data).
- **Tile terrain** = topmost layer covering ≥ half the tile (average corner weight ≥ 128); classes from
  `AdvMapTile/Type`: `TT_GRASS/DIRT/SAND/LAVA/SNOW/TAIGA/SUBTERRANEAN/DWARVENMINES`, roads
  (`*_ROAD`), `TT_BIG_WATER` (sea), `TT_SMALL_WATER` (rivers/bogs: impassable).
- **Footprints**: shared `blockedTiles`/`activeTiles` are offsets from `Pos`, rotated by `Rot` in quarter
  turns **counter-clockwise** in map space. Evidence: `h5tool adv-footprint-check` — entrance tiles of
  rotated objects are obstructed 3.4% of the time with CCW vs 13–23% for the alternatives.

## Rules implemented (`Engine/`) — sources
| Area | Behaviour | Source |
|---|---|---|
| Start | resources per difficulty (`StartHuman/ComputerResources`), towns from map (initial/max building levels, garrison, mage guild), map heroes, auto starting hero for `HeroInTown` players, tavern pool | DefaultStats, manual p.325 |
| Turns | day/week/month, daily town-hall gold (500/1000/2000/4000), silo income (`TownTypesInfo`), mine yields, moon-week income/growth effects, weekly town growth (+50% Citadel, +100% Castle), external dwelling growth, neutral weekly growth coefs, elimination (no town & no hero; 7 days without towns when the map says so), last-team-standing victory | manual p.224, 269-272, 325 |
| Movement | Dijkstra over 8-connected tiles; 100/141, terrain +25%/+50%, native terrain free, Pathfinding halves, Boots of the Open Road removes, roads 75/105; sea 100/141 | manual p.299-300 |
| Movement points | 2500, Logistics with Nival rounding then Boots ×1.25 — reproduces the manual table exactly; Navigation/Sextant/Lighthouses/Week of Winds at sea; Week of Flame | manual p.300, DefaultStats |
| Vision | look range 12 (+4 Scouting), circular reveal | DefaultStats |
| Visits | resources & treasures (chest gold/exp choice, 4% minor artifact, campfire, flotsam), artifacts & scrolls, mines/dwellings/lighthouses capture, shrines, keymaster/border guards, prisons, +stat buildings (once per hero), morale/luck/movement locations (until next battle), stables (week), mana wells/springs, weekly windmill/water wheel/garden, one-off loot, sirens, observatory, hut of magi, teleports (gates, monoliths, whirlpools by GroupID), creature banks (DefaultStats `Banks`: guard variants, reward ranges, recharge) | manual p.260-270 |
| Towns | build (cost, prerequisites, "town level" = sum of built levels, one per day), recruit (shared base/upgrade pool, alt upgrades), tavern hire, blacksmith (native machine ×1 else ×3), mage guild learning, garrison transfers, town capture (+500 exp) | manual p.224-225, 298 |
| Heroes | experience (Enlightenment +5/10/15%, enlightenment artifacts), level table, attribute by class probability, offers: ≤2 skills (one new weighted by class probabilities rescaled over unknown skills, one improvement) and ≤2 perks (one standard, one class-specific with per-class prerequisites); Graduate, Dark Revelation | manual p.294-298, HeroClass.xdb, Skills.xdb |
| Neutrals | courage (always join/fight), join/flee/fight from power ratio and mood, chance to join modifiers, ×3 price (×2 with Diplomacy), Week of Diplomacy; mood coefficients 0.5/1/1.5/2 −0.2·Diplomacy **confirmed in the exe** (`FUN_008da210`, `FUN_008e1ad0`, `FUN_008dc380`); splitting into 2–4 stacks ±1, 50% one upgraded | manual p.319-321 |
| Battles | `CreatePendingBattle(seed)` builds an `H5.Combat` battle (attacker side 0) with next-battle morale/luck; `ResolveBattleCmd` applies survivors, experience (creature Exp, neutral ExpCoefs, Folly/Honor, +500 hero), removes the loser, loots banks / captures towns; `QuickCombat` resolves with the combat AI | manual p.298 |

## Tools and tests
- `h5tool adv-load <map>`, `adv-stats`, `adv-footprint-check`, `adv-sim <map> [days] [seed] [--trace]`,
  `adv-lua-api`.
- `tests/H5.Adventure.Tests`: all maps load; manual movement-point and step-cost tables; army power
  example (p.320); level-up offer constraints; building costs/once per day; income; determinism
  (same seed, serialise-and-continue, rejected commands leave state unchanged).

## Known gaps / TODO(exe)
Marked `TODO(exe)` in code where behaviour is approximated pending disassembly:
- Random neutral stack size when the map leaves it random (currently ~1–2 weeks of growth × 4 × difficulty coef).
  Lead: `RMG/Params/Default.xdb` `CreatureStackParams` (BasicAmount 25, MinAmount 6, MaxAmount 100) and the
  exe readers at `0049ed10`/`0049edb0`; the adventure-map generator itself is not yet located.
- Neutral join threshold random range (`FUN_008e1ad0`: `rand(100)·0.1·MoodCoef` seen; manual says `random(1;7)·MoodCoef`).
- Guard zones (8 neighbours) and whether destination or source tile sets step cost.
- Tavern offer rules; hero hire cost (2500) and cap (8).
- Map hero `OverrideMask` bit meanings (only bit 1 = name observed); empty editable sections fall back to the shared hero.
- Mana regeneration, Estates, town specialisations, hero specialisations on the adventure map, external dwelling
  growth bonuses, artifact set effects outside combat, Enlightenment's extra attribute points.
- Not implemented yet: boats/shipyards, markets/trading, Hill Fort upgrades, Witch Hut, Sylanna's Ancients,
  Memory Mentor, altars, Seer Huts/quests, Sphinx riddles, obelisks/grail, cartographer, Den of Thieves,
  ghost mode, AI players (only `SimpleAdventureBot`, not the original AI), Lua scripting (API inventoried by
  `adv-lua-api`: 154 native functions, led by `Trigger`, `SetObjectiveState`, `SetRegionBlocked`), campaigns,
  non-`DEFEAT_ALL` objectives, siege specifics (handled by H5.Combat).
