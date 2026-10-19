# Ships, sea travel and water

Implemented in `src/H5.Adventure/Engine/Adventure.Boats.cs`, `Map/WaterMask.cs`, `State/BoatState.cs`,
`AI/OriginalAdventureAI.Sea.cs` (engine) and `game/src/Adventure/Water/` (client). Tests:
`tests/H5.Adventure.Tests/BoatTests.cs`. Tools: `h5tool adv-water`, `adv-water-rule`, `adv-water-probe`,
`adv-water-heights`, `adv-boats`.

## Which tiles are sea

Texture layers under the sea are ordinary ground textures, so water comes from terrain data:

* chunk 7 of `GroundTerrain.bin` is a quantised height ("plateau level", ≈ height × 8): 0 = sea bed
  (height 0.00), 8 = shore band (height exactly 1.00), 16/32/48… = land levels (≥ 1.6);
* a tile is **sea** when its four vertices are at level 0, **or** when the half-tile water mask
  (chunk 10, graded 0–255) is ≥ 200 at its centre (maps whose sea sits on a raised level, e.g. A2C2M5);
* scored over every object entrance on all 75 maps: 686/689 water-based objects on sea, 33524/33546
  land objects on land (13 misses are objects parked at (0,0)). The exe's passability builder was not traced.

## Ships

* Editor ships are `AdvMapBuilding` objects of type `BUILDING_SHIP_GALEON` (`MapObjects/Ship_Galley`,
  `Ship_Galeon`, `Ship_Junk`); at game start they become boats and stop being visitable. Heroes the
  editor put on water board the ship under them. (Previously misread as the "Demolish" creature bank;
  the wreck banks are `BUILDING_DEMOLISH` / `BUILDING_UNKEMPT`.)
* Model by race — exe `FUN_0045a0e0` (town enum → index) feeding `FUN_008dd470` (index → descriptor):
  Haven, Preserve, Academy, Fortress → Galleon; Dungeon, Necropolis, Inferno, Stronghold → Junk;
  no race → Galley.
* Shipyards (map `AdvMapShipyard`, town `TB_SHIPYARD`) build on their slipway = object position +
  `<ShipTile>` (not rotated: 57/64 stock slipways land on water; an editor ship sits exactly on
  A2C2M2's shipyard slipway). Cost 1000 gold + 10 wood (`Text/Interface/Shipyard/description_text`).
  States: deserted (slipway not water), blocked (ship/hero/object on it). Map shipyards offer the
  ship on visit (`PendingChoice "shipyard"`); towns use `BuildShipCmd(townObject)`.
  The exe's `CBuildShipCmd` (vtable 00e0dbf8, execute `FUN_0042de80`) and `CShipsPool` show no pool limit.

## Boarding, sailing, landing (fan manual p.123, p.301)

* Boarding/landing take the rest of the day's movement; with Snatch (`HERO_SKILL_SNATCH`) boarding
  costs nothing and landing a standard step (100/141).
* Land heroes may only step onto water to board an empty ship or attack a sailing hero; ships block
  paths like heroes. The ship follows its hero (moves, whirlpools, Instant Travel).
* Sea movement points (exe `FUN_00b4dd40`): base → ×1.5 Navigation (rounded) → ×(1 + 0.25 per Sextant)
  → + `LighthouseMaxSeaMovementBonus` (500) per owned Lighthouse. Week of Winds ×1.5 stays per the manual
  (not in that function; no data fields).

## Spells and objects

* **Vessel of Shalassa** (`SPELL_SUMMON_BOAT`, 4 mana) — exe `FUN_008656e0` case 0x31, check
  `FUN_008652f0`, `FUN_00864f00` (first free adjacent water tile), `FUN_00865230` + `FUN_00864920`
  (nearest empty ship on the floor by squared distance, later ships win ties). Fails with
  `ADVENTURE_SUMMON_BOAT_FAILED` when no ship exists; the mana is spent.
* **Whirlpool** — exe `FUN_0042e0f0` / `FUN_0045abf0`: lowest stack (tier, then base before upgrade,
  first slot on ties) loses (n+1)/2, or (n+1)/4 with `HERO_SKILL_FORTUNATE_ADVENTURER` (skill 0x21);
  a single stack of one creature is spared. (The manual's "rounded down" is rounding up in the exe.)
* **Shipwrecked Footman/Peasant** (`TREASURE_SHIPWRECK`, data MinResource 1/0) — exe `FUN_00a82720`
  with r = 0..99 (FUN_008e7840, min 0): Footman r<20 → 2000 gold, 20–49 minor, 50–94 major, 95–99 relic artifact;
  Peasant r<30 → 500 gold, 30–59 → 1000 gold, 60–94 minor, 95–99 relic. Flotsam and sea chest rules
  in the same function match the existing implementation.

## Battles

Battles at sea (sailing attacker, water tile) use arena-table terrain 6 → `Scenes/CombatArenas/Boat_Arena`.
TODO(exe): shore battles (ship vs land) and the land terrain → arena mapping.

## Client

* Ships: `BoatLayer` builds the stock ship models; a sailing ship is parented to its hero, whose model is
  hidden aboard (TODO(exe): whether the original also shows the hero on deck).
* Sea surface: `SeaPlane` at height 1.0 (the shore band) with `/_(Material)/dev/ReflectiveWater`
  (exe `FUN_00b6df40`); heroes, ships, path markers and water-based objects stand on it.
  TODO: coast foam/surf effects (`Effects/Environment/Water/Foam`, `Surf01/02`), the true plane height,
  and the jagged coastline where the plane cuts the terrain.
* Cursors: stock `BoardShip*`, `UnboardShip*`, `ShipMove*`. Shipyard offer from the stock shipyard texts.
* Network views show ships on explored tiles (or carrying an allied hero); boarding/landing events follow
  hero visibility.

## Not done

Town-screen shipyard button (hook: `BuildShipCmd(townObject)` from the town UI), Lighthouse/sea
objects' visuals beyond their models, AI shipyard purchases, ghost-mode at sea.
