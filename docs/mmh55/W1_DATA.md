# W1 — MMH55 data on the stock engine (the user's target)

Target: `HoMM 5 - Tribes of the East (5.5)` launched with `bin/H5_Game_PEST.exe` = stock 3.1 engine + PEST bytes,
no MMH55 DLL (PLAN.md §0). So everything below is **what the stock engine does with MMH55's data**, not what the
DLL-patched engines do. Addresses are VAs in stock `H5_Game.exe` (Ghidra copy `tools/ghidra-projects/pest`).

## 1. Fixed table sizes of the stock engine

Each RefTable loader is given a constant count:

| Table | Count (push) | Loader | MMH55 types.xml enum | Entries the stock engine ignores |
|---|---|---|---|---|
| `Creatures.xdb` | 180 (`00ae261f`) | `00ae25c0` → `00ae0100`, fill `00aef760` | CreatureType 1000 | 820 (`SANC_101`=180 … `CREATURE_999`) |
| `UndividedSpells.xdb` | 353 (`0084c9bf`) | `0084c960` → `0084b590`, fill `008525e0` | SpellID 379 | 26 (`SPELL_SKILL_TRAINING1`=353 … `SPELL_EFFECT_SHOCKED`) |
| `Artifacts.xdb` | 97 (`0084ca7f`) | `0084ca20` → `0084b670` | ArtifactEffect 196 | 99 (`ARTIFACT_RES_WOOD`=97 … `ARTIFACT_ORB_04`) |
| `Skills.xdb` | 221 (`0084cb3f`, also `00d8981e`) | `0084cae0` → `0084b750` | SkillID 251 | 30 (`HERO_SKILL_221` … `HERO_SKILL_250`) |
| `HeroClass.xdb` | 9 (`00ae29df`) | `00ae2980` | HeroClass 25 | 16 (`HERO_CLASS_HERETIC`=9 … `HERO_CLASS_SHAMAN`) |

Count getters used by iterations: `00acb380` → 0xb4 (creatures), `00847890` → 0x161 (spells), `008478a0` → 0x61
(artifacts), `008478b0` → 0xdd (skills).

Fill (`00aef760`, same code for every table): if the xdb holds another number of items it formats "wrong amount of
objects (%d) in table %s, %d expected" (`00a47fd0` only `_vsnprintf`s into a buffer — nothing stops); the table is
sized to the count and slot *i* gets the item whose ID enum value is *i* (`00aefa60`, which also formats "wrong
objects numeration" when items are out of order). Items with values ≥ the count are never copied.

Lookups by id (`00ad5af0` hero class: `base + 8 + id*0x44`; `008481d0` artifact `*0x74`; `008481f0` skill `*0xfc`)
have **no bounds check**: an id ≥ the count reads past the table — undefined behaviour in the user's game.

**H5Reborn rule** (`H5.Core.Data.StockTables`, `CreatureDef/SpellDef/ArtifactDef.Stock`):
* Every iteration the stock engine does over a table only covers ids below the count, so items beyond it never enter
  a pool: random monsters (`Adventure.RandomMonster`), random/guild spells (`RandomSpell`, mage guild pool),
  random artifacts and the artifact merchant (`AdventureDb.ArtifactClass`), RMG creature and artifact lists
  (`RmgData`). MATCH by construction.
* Direct references to ids beyond the count (MMH55 hero classes, a map placing `ARTIFACT_LEGION_T1`, scripts
  giving a new artifact) are undefined in the stock engine. H5Reborn keeps loading the MMH55 definitions so such
  references resolve to the mod's data instead of garbage. **GAP (no faithful behaviour exists).** 98 MMH55 heroes
  use the 16 new classes (e.g. `Haven/AlaricMP` Heretic, `Preserve/Gillion` RangerA).

## 2. The 820 placeholder creatures
`CreatureType` 180..999 in MMH55's types.xml (`TOWN_NO_TYPE`, Snow Ape visual): outside the stock 180-slot table, so
the stock engine never offers them anywhere (no pool, no dwelling of the stock data refers to them). Excluded from
every pool above. They still load as definitions (harmless; see §1 direct references).

## 3. Other limits (audit)
| Item | Stock engine | H5Reborn | Status |
|---|---|---|---|
| Creature/spell/skill/artifact/class tables | fixed counts (§1) | dictionaries keyed by id, no fixed arrays (grep for 180/353/221/97/9: none) | MATCH for pools, GAP for direct ids (§1) |
| Hero class rules (`Class == KNIGHT/BARBARIAN/RANGER/WIZARD/WARLOCK/...`) | compare the class enum value | compare the class id string, so MMH55's Heretic (Haven) is not a Knight, Shaman (Stronghold) not a Barbarian — as in the exe | MATCH |
| Class names / skill tables of the 16 new classes | out-of-range reads (§1) | MMH55 `HeroClass.xdb` data (name, AttributeProbs, SkillsProbs, preferred spells) | GAP (§1) |
| Skills.xdb | 221 | MMH55's Skills.xdb also has 221 items (only the enum grew to 251) | MATCH |
| 6 hero skill slots, level cap, 12×10 battlefield | stock constants; MMH55 changes them only through 55u.dll groups (Hero8Skills, Levels, BattlefieldSize) | stock values | MATCH (DLL groups out of scope) |
| `CombatAbilities`, `WarMachines` tables | not enlarged by MMH55 (types.xml delta: only the five enums of §1 grew) | — | MATCH |

## 4. Tests on both fixtures
* Default: stock (`tests/stock.runsettings` sets `H5_TARGET=stock`). MMH55: `H5_GAME_DIR="…/HoMM 5 - Tribes of the East (5.5)" dotnet test H5Reborn.sln`.
* `tests/Shared/DataFixture.cs` (linked into every test project): `DataFixture.Mmh55`, `DataFixture.Pick(stock, mmh55)`,
  `[StockFact]` / `[StockTheory]` = the expected values are stock 3.1 data; skipped on MMH55 with that reason.
* Ported to both fixtures: `AdventureSpellTests.SpellCostsComeFromTheSpellData` (MMH55 Dimension Door 25, Summon Boat 5),
  `MovementTests.MovementPointsMatchManualTable` (BaseHeroMovement 2800), new `StockTablesTests` (table limits, BOM).
  Main menu layout (`InstallTests.MainMenuLayoutMatchesOriginal`) and Seer Huts (`SeerHutTests`) pass on MMH55 after the
  fixes in §5.
* Marked stock-only (83): creature/spell/skill/specialisation/artifact values that MMH55 rebalanced (Combat 62:
  AuditSpell*, AuditAbility*, AuditSpec*, AuditSkill*, Rules/Data/ExeFindings, deployment zones, obstacles, AI
  tunables; Adventure 12: movement/oasis bonuses, Necromancy, Witch Hut, market prices, Vessel of Shalassa, siege
  hand-off, AI formulas; RMG 6: stock template names `S0-1P2Z2K3T`/`S1-2P2-4Z4K1S` are not in MMH55's set and
  `4P-Diamond-XL-#` at its smallest size; Audio 2, Cinematics 1 (campaign set C1..)). Each still runs on stock.
  Porting each to MMH55 values needs the value checked against the MMH55 data by hand — open (W1 follow-up per area).

## 5. Fixes for MMH55 data
* `types.xml` in `MMH55-Frame.pak` starts with a UTF-8 BOM: every reader decodes with `TextDecoding.Decode`
  (`AdventureDb.EnumName` threw on every Seer Hut quest on MMH55 maps).
* Bool fields: MMH55's main menu layout has `<Second>fale</Second>`; `XmlHelpers.BoolValue` ("true"/"1" true,
  anything else false) replaces the strict `(bool?)` casts in the UI loader (the main menu crashed).
  The stock xdb bool reader was not traced (the exe's variant parser `00c2f7a0` takes only the exact "true").

## 6. Open (found by the MMH55 test run / smoke tests)
* `AdventureSaveTests.ScriptedMapSaveLoadContinuesIdentically` on MMH55: a Lua number is ±infinity and
  `LuaSnapshot.ToJson` cannot write it (W2, Lua runtime).
* `TownSpecialTests.DiscountsFollowTheExeFormula`, `TrainingCostsThreeTimesTheTargetAndSpendsPoints`: stock values
  (town specials helper).
* RMG `4P-Diamond-XL-#`: resolved (W5) — 4 start zones for up to 8 players; the exe also gives start towns to the first
  4 players only (0x0090bd80), the old test expected one per player. AUDIT_RMG.md.
* SP map `Maps/SingleMissions/A1SM1` (MMH55-SPMaps.h5u): MMH55 system scripts report "Player is not in game"
  (`H55-Core.lua:1758 H55_UpdateGameMode`), "Saved combat #-1 not found" (`H55-Core.lua:7687`) and
  "Incorrect player ID (-1)" (`A1SM1/MapScript.lua:39`) — native semantics for W2.
* One MMH55 DDS texture fails Godot's DDS header check ("linear size … does not match"); not identified yet.

## 7. Smoke tests (off-screen 1920x1080, 5.5 install, HEAD b44a131)
Main menu (MMH55 logo, "Version: 5.69k"), duel (`--combat-vs-ai=1,2`), MMH55 single mission A1SM1 (dialog scene,
then the adventure screen with `--skip-dialog-scenes`), town screen (`--adventure=A2M1 --town-open`): all start and
render, no crash or exception. Shots: scratchpad only.
