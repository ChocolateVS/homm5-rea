# AI audit ledger (H5_Game.exe vs H5.Adventure/AI and H5.Combat/AI)

Adventure-map AI (NWorld::CPlayerAI, `src/H5.Adventure/AI/OriginalAdventureAI*.cs`) and the
combat AI (NWorld::CCombatAI, `src/H5.Combat/AI`), against the GOG 3.1 `H5_Game.exe` (Ghidra 12.1.4, private copy of
`tools/ghidra-projects/ai`). Addresses are VAs. Tunables: `docs/rules/AI.md` (offsets) and
`tools/aire/out/aichain.txt`; per-field readers were mapped with `GetterFieldUse` on the row getter 00c02a90
(Difficulty* row) and the block getter 00c02a80 (globals).

Status: **FIXED** compared and corrected (test named) · **MATCH** compared and identical · **GAP** known mismatch /
not ported / not traced · **UNVERIFIED** implemented, exe structure known, not compared line by line · **N/A** cannot
occur in our setup.

## Summary

| Status | Count |
|---|---|
| FIXED | 20 |
| MATCH | 12 |
| GAP | 33 |
| UNVERIFIED | 2 |
| N/A | 2 |

Adventure AI only; the combat AI has its own count in "Combat AI" below.

Tests: `tests/H5.Adventure.Tests/AiAuditTests.cs` (A), `OriginalAdventureAiTests.cs` (O), `AiDirectiveTests.cs` (D).

## Turn loop

| Area | Status | Exe | Original | Ours / evidence |
|---|---|---|---|---|
| Turn state machine | GAP | dispatcher 0086de76..0086df09 (switch on CPlayerAI +0x128), 0086d750 … 0086a350 | 15 phases run as a pump: each call works for ≤ DAT_00f440d4 = 30 ms (GetTickCount) or until a command is queued (+0x130), logs "%s: %u ms, %i cmds", resumes next frame. Order: 0 day start + threats (SFLB_InitDay 00867440, 0086d750 "Clear pathfinders", "Solve puzzlemap", "CHeck threats") → 12 roles (00869ad0 → 00868a40) → 1 hero step (0086b400 → 00869770) ↔ 2..5 (00867520, 0086c760, 008667d0, 0086c6b0: path/search sub-steps) / 10, 11 (0086c570, 0086c010 search limits) → 7 hire (0086b0a0; after a hire → 12) → 6 (0086d4e0, town commands FUN_00870050 / 009c5c60) → 13 (00868ca0) → 14 (0086a350) → 8 (00866700) → 9 end turn (00868eb0 "end turn: %i steps, %i.%i seconds") | synchronous `PlayTurn`; order now heroes → hire → the new hero → towns (build, recruit) → end turn. Phases 2–5, 10, 11, 13, 14, 8 not identified one by one; `MaxActionsPerTurn` (80 moves) and 8 steps per hero are our bounds, not the exe's |
| Phase order heroes / hiring / towns | FIXED | 0086b400 (0x128 = 7/8 after the last hero), 0086b39a (hire → 0xc), 0086b0b6 (hire → 6) | heroes move first; hiring comes after the last hero; a hire restarts the role/hero phases so the new hero acts the same day; town phase 6 after hiring | `PlayTurn` (was: build, hire, recruit, then heroes). UNVERIFIED that build/recruit live in phase 6 |
| AI turn time limit (`ai_time_limit`) | GAP | 0086de82..0086de8e, DAT_00f440d0 (default 180000 ms, console `setvar ai_time_limit`), "time limit (%d seconds)" | when GetTickCount − turn start (+0x12c, set by SFLB_InitDay) > limit, the AI logs and jumps to phase 9 (end turn) | not consumed (BaseLibrary.ConsoleVars stores it): a wall-clock cut would make peers diverge in lockstep play; our turn is bounded by `MaxActionsPerTurn` instead |
| Missed days warning | N/A | 00867440 ("We not't call AdvAI for some days. timeToWork = %i") | log only | — |
| End of turn | MATCH | 00868eb0 | the AI ends its turn after phase 8 | `EndTurnCmd` |
| Difficulty row | MATCH | 00c02a10 (getter 00c02a90), world vf 0x120, script map 00a33780 | engine difficulty 0 → Normal row (+0x84), 1 → Easy (+4), 2 → Hard (+0x104), 3 → Heroic (+0x184), other → a default block 010c57a0 (FUN_00943e10); GetDifficulty maps engine 0→1, 1→0, 2→2, 3/4→3 | `S.Difficulty` is the script scale, so `Difficulty[S.Difficulty]` picks the same row. GAP-detail: engine value 4 (script 3) would use the default block in the exe, the Heroic row here |

## Heroes, roles and destinations

| Area | Status | Exe | Original | Ours / evidence |
|---|---|---|---|---|
| Which hero acts next | FIXED | 00869770 (from 0086b400) | among the player's heroes not in the finished list (+0x30), the one with the lowest attack + defence + spell power + knowledge (hero vf 0x10/0x14/0x18/0x1c; strict <, first in list on ties) | `NextHero` (was: descending spread weight). A: `Weakest_hero_by_primary_stats_acts_first` |
| AI-disabled hero (EnableHeroAI false) | GAP | 0x008698ba (world vf 0x100(hero) → vf 0x44), FUN_00868640(hero, 0, 1) | a hero failing the gate is finished without acting | skipped (`AiEnabled`); that the vf 0x44 gate is the EnableHeroAI flag is not traced (the flag, hero vf 0x2ac, is read at 0x00868768 / 0x008699f8) |
| Garrison moves | FIXED | 00869770 (0x008698c2: heroes without a map object — garrisoned — are not candidates; tail: town vf 0x18(1), hero vf 0x2ac, vf 0xf4 > 0, FUN_0045a320 stacks > 0 → FUN_00435cc0(town, false)), FUN_00868640 (FUN_0086b5e0 role 2/3 → no garrison; hero vf 0x2ac, town owner, town vf 0x114 → FUN_00435cc0(town, true)); command execute 004f58e0 (town hero swap, 004f4200) | garrisoned heroes do not act until no map hero is left, then the first town's AI garrison hero with move points and troops comes out and acts; a finished commander / freelancer in its own town goes into the garrison unless the town is blocked for the AI | `NextHero` skips garrisoned heroes, `HeroOutOfGarrison`, `FinishHero` (was: never moved). A: `Idle_heroes_go_into_their_town_garrison_and_come_out_last`. GAP-details: the exe swaps with a garrison hero (ours refuses); our finish conditions are ours; town order = map list |
| BlockTownGarrisonForAI | FIXED | town vf 0x114 at 0x008687a0 (FUN_00868640) | a blocked town gets no AI hero put into its garrison | `FinishHero` honours `AiGarrisonBlocked`; it also still blocks our reconstructed creature pickup `TakeGarrison` (GAP: that pickup is ours) |
| Commander finish command | GAP | FUN_00868640 (role vf 0x2bc == 1 → FUN_0086e920 command) | a finished role-1 hero posts an extra command | not ported |
| Hero spread weight | MATCH | 00868a40, 00866670, 004e9a80 | RolesSpread[role] · (1 + level · ArmyPowerCoefBonusPerHeroLevel); heroes with vf 0x2d4 ∈ {1, 2} are left out of the spread | `SpreadWeight` (O: `ExeConfirmedFormulas`); GAP-detail: the vf 0x2d4 exclusion (SetHeroRoleMode?) is not applied |
| Role assignment | FIXED | phase 12 00869ad0 → 00868a40 (weights), comparator 00866650, merge sort 0086f6a0, partner 00866810 / 008668c0, distance 00a33600 | weight RolesSpread[current role] · (1 + level · coef), sorted ascending (stable); k = min((heroes + 1) / 3 + 1, list, heroes) ≥ 1 commanders (role 1) taken from the top; each keeps its role-2/3 partner if still listed, else takes the nearest remaining hero (dx² + dy² + (floors · map +0x6c)², strict <, heroes without a map tile skipped) as collector / supplier; the rest freelancers (role 0); roles persist from day to day; a hire re-runs the phase | `AssignRoles` (was: strongest ArmyPower commands, weakest of ≥ 3 supplies). A: `Roles_pick_the_heaviest_commanders_and_give_each_the_nearest_partner`. GAP-details: supplier test 00c5af30 (FUN_004eb050 before / after a simulated transfer vs DAT_00e72bf8) not ported (partners are collectors); floor factor map +0x6c taken as 0; SetHeroRoleMode heroes (vf 0x2d4 ∈ {1, 2} → role 0) not separated; roles not saved |
| Destination value: state merge | GAP | 00a78940 | (state, value64) pairs; parts merged with "negative state dominates, else max"; value = own + strategic parts (+0x28, +0x38) | only state 0 and 3 (attractor 2) exist here |
| Old-target bonus | FIXED | 00a78940 (0x00a78a67) | destination == the hero's remembered target (AI record +8): v ≥ 0 → ftol(v · SearchOldTargetMultiplier) + 20, else ftol((float)v / mult) + 20; no random factor | `OldTargetValue` (was: × mult, random applied too). A: `Old_target_bonus_multiplies_and_adds_twenty` |
| Random factor | FIXED | 00a78940 (0x00a78ab7), RNG vf 0xc = inclusive Range | r = Range(1, SearchBestPositionRandomFactor) + 100 − R; v > 0 → max(1, ftol(r·v·0.01f)) (0.01f = 0.0099999998, so 76·1000 → 759), else ftol(v / r · 100) | `RandomizedValue` (was: × (1 ± U·R/100)). A: `Random_factor_only_lowers_a_positive_value`. Our RNG stream is private (seeded per day/player/hero), not Nival's |
| Distance discount | FIXED | 00a7b630 (0x00a7b9b0) | state > 0, or state 0 and value > 0, with move cost > SearchAverageTerrainCost T → v · T / cost (int64), at least 5 | `DiscountedValue` (was: value / (1 + days)). A: `Distance_discount_divides_by_cost_over_average_terrain_cost` |
| Choice comparison | FIXED | 00a7b630 ("discarded, %s <= %s", "new best") | replace the best only on a higher state, or equal state and strictly higher value; a "critical" record (+0x1c) stops the scan | `Better`. A: `Higher_state_outranks_any_value_and_ties_keep_the_first`. GAP-detail: the critical flag and "discarded neutral for positive previous" filter are not ported |
| Move cost to a destination | GAP | record +0x18 (pathfinder) | real path move cost | Chebyshev distance × SearchAverageTerrainCost |
| Previous best kept with 3/2 | GAP | 00a7b630 (0x00a7b85c..0x00a7b8e8) | the hero's current destination enters the choice first, its value × T · 3 / (2 · cost) when cost > T | not ported |
| Strategic propagation | GAP | 00a79700 (SearchStrategicDistance +0x214 × T, StrategicCaptureDistance row +0x78) | destinations near others inherit their value (sums, min/max of states) | not ported |
| Search limit (wave spread) | FIXED | 0086c010 ("Wave spread: search limit = %d, time = %d ms."), CPlayerAI +0x70 attempts / +0x74 limit / +0xe0 max attempts, FUN_00866c30 | attempt 0: 1500 with 6 attempts, roles 1..3 RolesSearchNearMaxCost (+0x14) with 2; a remembered target (AI record +8 valid) raises it to stored cost (+0x10) + 200 + move points; player in danger → RolesSearchFarMaxCost (+0x18) with 1; each retry doubles, roles 1..3 → Far; out of attempts → the hero is finished | `SearchLimits`, `PlayerInDanger`, `ChooseDestination` retries (was: commander Far, others max(Near, 3 × move points)). A: `Search_limits_follow_the_wave_spread_attempts`. GAP-details: our cost is the approximation of the next row; FUN_00866c30's town threats are not ported (k = 0) |
| Collector / supplier free scan | GAP | 00a78400 (called from 00a7af80 per destination) | allied foreign towns are refused; role 1: anything within today's move points (record cost ≤ vf 0xf4), farther only objects with a hero (vf 0x2c), town (0x3c), ownable (4), army (0x28 with vf 0x24) or vf 0x1c / 0x20 / 0x58 / 0x5c interface; role 2 / 3 without a valid remembered target: anything on the hero's floor within Chebyshev RolesCollectorFreeScanMaxMoves (+0x23c) / RolesSupplierFreeScanMaxMoves (+0x240), farther only ownable objects of non-allies (2) or the partner / partner's town (3); role 0: everything | not ported (the vf 0x1c/0x20/0x58/0x5c interfaces are not identified) |
| Script attractors −1 / +1 | MATCH | 00bc5740 (+0x21c / +0x218), hero list FUN_00b4d5c0, player vf 0x1e8 | value −SearchNegativeAttractorValue / +SearchPositiveAttractorValue; hero entry first, else the player's | `AttractorPriority`, D: `Attractor_list_overwrites_removes_and_appends` |
| Script attractor priority 2 | FIXED | 00bc5740 (state ← 3), 00a7b630 | state 3 outranks every lower-state destination whatever its value; among them the normal value decides | was: nearest first with value 1e300. A: `Higher_state_outranks_…`. GAP-detail: whether state 3 skips the danger checks is not traced (it does here) |
| "AI will not risk its heroes" with priority 1 | GAP | — | from the script reference only; no exe test found | a hostile target with value ≤ 0 is skipped even with priority 1 |
| Hero attribute value | MATCH | 004e9f60 | Σ attribute · ValueOfAttribute* | O: `ExeConfirmedFormulas` |
| Artifact value by class | MATCH | 004eb840 | minor / major / relic / grail values | O: `ExeConfirmedFormulas` |
| Other object values | GAP | 004eae60 (hero value: ValueOfHero +0x2c4, spells 0x284 / summon 0x2dc, skills 004ead50, artifacts, army / 4), 004eb050 (mana 0x2a0, move points 0x2c0, talisman 0x314 / 0x318), 004ea150 / 004eb480 / 004eb790 (income × IncomeMultiplier 0x2c8), 004eafe0 (war machine), 00bc7340 (grail 0x2d0..0x2d8), 004ea5c0 (siege coefs), 008675f0 (path bonuses 0x2ec / 0x2f0), 00bca820 (ValueOfHundredMovePoints) | per-object gold equivalents | reconstructed constants in `ObjectValue` (treasure 250/500 per unit, mine income, dwelling IncomeMultiplier·40, shrine 2 spell levels, monster power / 2, ValueOfTown, ValueOfHero + Pow2) |

## Fight or flee, threats

| Area | Status | Exe | Original | Ours / evidence |
|---|---|---|---|---|
| Army strength estimate | GAP | 004e9b90 (CreatureOverHealth/Damage/Attributes/Initiative, MAE, luck/morale coefs +0x250..+0x278) | creature power with the Creature* coefficients | engine `ArmyPower` estimate |
| Aggressivity / cowardice | GAP | row +4..+0x10, readers 0086cd10, 00872220, 0086c010, 004e9a80 | attack/flee thresholds vs human / computer | reconstructed: target refused when enemy·(1+cowardice) ≥ own·aggressivity |
| Avenger coefficients | GAP | row +0x24 / +0x28 (0086cd10, 00872220, 00a78b60) | — | not used |
| Enemy danger reach | MATCH | 00872330 (+0x220 / +0x224) | moves · mobility multiplier + bonus | `EnemyDangers`; GAP-detail: the exe floods it over the pathfinder, we use Chebyshev × T |
| CheckHeroesDanger | UNVERIFIED | row +0x34 (0086c570) | enables the hero danger check | gates `EnemyDangers` |
| Threats | GAP | 0086d750 ("CHeck threats: %d ms"), 0086cd10 (Threats* row +0x50..+0x74) | threat map per day | not ported |
| CheckTownsInDanger / ConsiderGarrisoningHeroToImportantTown | GAP | row +0x30 (00a78b60), +0x38 (009c3f20) | town defence | not ported |
| Guards on the way | GAP | — | not traced | reconstructed power test |
| AI flee / surrender decision | GAP | 00c972d0 (also calls the hire cost 00b4ced0) | the AI may retreat / surrender | our combat AI never flees; the DenyAIHeroFlee / DenyAIHeroesFlee / SetAIHeroFleeControl gate (00c972d0 head, list test 00c97b10) is MATCH as `AiFleeDenied` → `BattleState.RetreatDenied` (D: `Flee_denial_checks_hero_then_player_list`) |

## Towns, hiring, resources

| Area | Status | Exe | Original | Ours / evidence |
|---|---|---|---|---|
| Building choice | GAP | phase 6 (0086d4e0 → 00870050 / 009c5c60), WeakMoneyForDeniedBuildings 00a32230, SupplyDayIncomes 00a325b0 | town plan not traced | invented priority list (hall, market, dwellings 1..7, fort, guild, tavern, blacksmith, rest) |
| Build period | GAP | town vtable slot 00e958c4 → 00ccedf0 | the town returns TownBuildPeriodInDays (row +0x2c) for a computer owner, 1 otherwise; consumer not traced | AI builds only when (day − 1) % period == 0 |
| Recruiting | GAP | TownCreaturesReserveCoef (+0x280): no reader through 00c02a80 found | — | reconstructed: tiers 7→1, keep TownCreaturesReserveCoef of the gold |
| Computer town growth (TownCreatureGrowthCoef) | FIXED | 00cd27b0 (town vtable slot 00e95828), FUN_00465610 / 00465640 | computer-owned town: n = round(n·c + (week·c − round(0.01 + week·c))), c = clamp(coef, 0, 10), week = day counter / 7 + 1 (stock Easy 0.5) | `Adventure.OwnerAdjustedGrowth` / `ComputerTownGrowth` in the weekly growth (was not applied). A: `Computer_town_growth_scales_…`, `Easy_computer_towns_grow_at_half_rate_on_stock_data`. GAP-detail: day counter taken as 0-based |
| Neutral town growth | FIXED | 00cd27b0 (owner 0 → n / 2) | a neutral town's weekly growth is halved | `OwnerAdjustedGrowth`. A: `Neutral_town_growth_is_halved_and_human_towns_are_untouched` |
| Hiring gates | FIXED | 0086b0a0 | heroes < FUN_0098de60 − not-for-hire (player vf 0xb0); with ≥ 1 hero, Σ heroes of computer players (00866b60) < HireHeroesGlobalLimit (+0x44); heroes ≥ HireHeroesBase + PerTown · towns (+0x3c / +0x40) only allows specially marked heroes | `HiringAllowed` (was: per-player min(global, base + perTown·towns) and a 2 × cost reserve). A: `Hiring_gates_follow_the_hire_phase` |
| One hire per day | FIXED | +0x60 set at 0x0086b38f, cleared in SFLB_InitDay (0x008674e6) | at most one hire per pass | one hire per turn. GAP-detail: the second clear at 0x0086ca78 is not traced |
| Tavern offers and gold | FIXED | 0086b2a0..0086b36e (player vf 0x14c(0..1)), 00b4ced0 vs resources +0x18 | the two offers in order; with heroes > 0 an offer is skipped when its cost exceeds the gold; best score wins, first on ties | first acceptable offer / town (score GAP below) |
| Over-limit exception | GAP | 0086aa50 (settings object player vf 0x28 → vf 0x88(hero); coef 10.0) | over the soft limit only heroes the AI-hiring settings mark are hired, with coef 10 | nobody is hired over the limit |
| Hire score | GAP | 0086aa50, race coefs 008683f0 ("calculated hire heroes races coefs", "H%0.2f P%0.2f A%0.2f D%0.2f N%0.2f I%0.2f"), 004eae60, 004ea050 | coef[race] · hero value · (1.0 same race town / 0.1) / (distance + 1), best town per offer | not ported |
| Town in danger | GAP | 008672d0 | towns under threat are skipped | not ported |
| Hire cost | FIXED | 00b4ced0 (callers 004f4570 / 004f53ec town, 0086ac3f hire score town, 0086b2db AI offer gate / 004f46a0 / 004f5700 map tavern / 00c9758a surrender: no town) | 500 · (heroes + 4); with a town whose vf 0xdc (00cd3640: STownSpecialization +0x5c, the Bonus) is 0x25 = TOWN_BONUS_HEROES: (heroes + 4) · 4500 / 10 | engine `Adventure.HireCost(player, town?)` (780b222, tavern helper); the AI offer gate uses `HireCost(me)` with no town (was the flat 2500). Test: `TavernMentorTests` |
| Tavern filters (EnableAIHeroHiring, AllowHeroHiringByRaceForAI, AllowHiringOfHeroForAI, …InTown) | MATCH | 0086aa50 (settings vf 0x54 town, vf 0x7c → 00c069d0, town vf 0x134 → 00c069d0) | per offer and town | `HireHeroes` checks `AiHiringDisabledTowns`, `AiHireFilter`, the town filter via `TownTavernOffers` |
| AllowPlayerTavernRace / Hero reader | GAP | player +0x644 | not found in this audit | applied at tavern roll (SCRIPTS.md) |
| SetPlayerHeroesCountNotForHire reader | MATCH | 0086b0e7 (vf 0xb0), 004f4570 | heroes < max − count | `HiringHeroLimit`, `HiringAllowed` |
| Max heroes per player | GAP | 0098de60 | 8, or DAT_00ffad44 (1..8) when player vf 0x18c; capped 6 / 8 by a map setting | constant 8 |
| Resource use / market | GAP | — (no AI market code identified) | — | our AI never trades |

## Map spells, artifacts, skills, boats

| Area | Status | Exe | Original | Ours / evidence |
|---|---|---|---|---|
| Adventure spells | GAP | DimDoorStartCostLimit +0x228 (00ceb190, called by 00a79700) | Dimension Door / Town Portal etc. in the search | our AI casts no map spell |
| Artifact pickup | MATCH | 004eb840 | by class | `ObjectValue` |
| Artifact equip / exchange | GAP | — | — | engine defaults only |
| Level-up choice | FIXED | 004e6d30 case 3 ("level up", "selected skill/perk %s (mastery %d, value %d)"), value 00c5abd0, skill table 008481f0 (0xfc-byte records) | each offered skill/perk is worth Skills.xdb AIRacesValues[hero town type (vf 0x254 = +0x84) − 3]: role 1 CommanderValue, 2 / 3 CollectorSupplierValue, 0 FreelancerValue (other town type / role → −1; the mastery is only logged); the options at the maximum (running max starts at 0, ≥ joins, > clears) are kept and one is drawn at random (008e7840 over [0, n − 1]) | `LevelUps`, `BestLevelUpOptions`, `SkillAiValue`, `SkillDef.AiRaceValues` (was: mastery · ValueOfSkillPerMastery vs ValueOfPerkFeat; those two belong to the hero value 004ead50). A: `Level_up_keeps_every_option_with_the_maximum_value`, `Skill_value_reads_the_races_table_by_role`. GAP-detail: option order is ours (skills, then perks); the draw uses our private RNG |
| Grail search | GAP | GrailSearchOpenPartsMinimum / PercentMatchNeed row +0x48 / +0x4c (00a79700, 00bc6530), grail values 00bc7340 | the AI digs when enough of the puzzle is open | not ported |
| Boats | GAP | — (not traced) | — | `OriginalAdventureAI.Sea.cs` reconstructed (walk to an empty boat when nothing is reachable, sail to the shore nearest the best target) |

## Difficulty, bonuses, script hooks

| Area | Status | Exe | Original | Ours / evidence |
|---|---|---|---|---|
| Start resources per difficulty | UNVERIFIED | StartHuman/ComputerResources (DefaultStats) | — | `Adventure.Setup.cs` (reader not traced in this audit) |
| Combat AI difficulty | FIXED | 008d0840, 004f66c0, 00a2f0d0 | think level from the side (human 5) and the engine difficulty; level ≤ 3 → Normal row, ≥ 4 → Heroic row | see "Combat AI" (rows "Think level", "Difficulty* row", "Difficulty source") |
| EnableHeroAI | GAP | see "AI-disabled hero" | — | — |
| MoveHero on an AI hero | GAP | 005cef50 | sets an AI move target | moves at once (SCRIPTS.md) |
| DenyAIHeroesFlee | MATCH | 005e2760, 00c9741e | — | see "AI flee / surrender decision" |
| SetAIPlayerAttractor | FIXED | 005e00e0 → 00bc5740 | see attractor rows | — |
| DoNotGiveTurnToPlayerAIIfNoTownsAndActiveHeroes | MATCH | 0098e410 | — | `GetsTurn` (D) |
| Determinism | N/A | — | the exe AI is single-machine | private RNG per day/player/hero; O: `PlaysDeterministicallyWithoutStalling` |

## Combat AI

NWorld::CCombatAI (vtable 00e91d38; constructor 00c977e0, think vf 00c97930) builds a think object on the stack
(FUN_008d27a0) and runs FUN_008d6a70: generate (008d6940) → evaluate (008d5eb0) → choose (008d1cb0). The think object
holds a settings block at +0x114 (FUN_00a303f0 / FUN_00a30460; settings +0x98 → think +0x1ac, +0xa0 → +0x1b4, +0xa3 →
+0x1b7, …). Code: `src/H5.Combat/AI/OriginalBattleAI.cs`, `CombatValue.cs`, `AiStats.cs`. Tests:
`tests/H5.Combat.Tests/OriginalAiExeTests.cs` (X), `OriginalAiTests.cs` (T).

| Status | Count |
|---|---|
| FIXED | 18 |
| MATCH | 4 |
| UNVERIFIED | 2 |
| GAP | 14 |

### Think level, difficulty row, compare

| Decision | Status | Exe | Original | Ours / evidence |
|---|---|---|---|---|
| Think level | FIXED | 008d27a0 (+0x18 = 4), 008d0840, 004f66c0 | nested look-ahead think → 2; combat vf 0x110 (CCombat+0x138) 1 → 3, 2 → 1; else a human side (side player vf 0x190) → max(level, 5) = 5; a computer side → FUN_004f66c0(combat vf 0x38): 0, 1 → 0; 2 → 4; 3 → 5; 4 → 6 | `ExeThinkLevel` (was: no level; the row came from a constructor argument every caller left at Normal). X: `ThinkLevelAndRowFollowTheExe` |
| Difficulty* row | FIXED | 00a2f0d0 (`CMP ECX,3; JG` / `CMP ECX,4; JGE`) | level ≤ 3 → DifficultyNormal (RPGStats+0xb60); ≥ 4 → DifficultyHeroic (+0xb78); the Hard branch (3 &lt; level &lt; 4, +0xb6c) is dead and DifficultyEasy (+0xb54) is never read. Stock rows: Easy = Normal (spell level 3, creature-only area centres, shortest path only), Hard = Heroic (level 5, free centres, every strike cell): Easy/Normal computer → Normal row; Hard/Heroic computer and any human side (auto combat) → Heroic row | `ExeRow`, used by `Decide` (`LastLevel`, `LastRow`). X: `DecisionsUseTheRowOfTheGameDifficultyAndSide` |
| Difficulty source | UNVERIFIED | combat vf 0x38 = 00af7e70 (CCombat+0x94; the constructor 00af7280 stores 4) | no other direct store found (likely the serialized combat setup); its five values match the engine difficulty (script map 00a33780: engine 0→1, 1→0, 2→2, 3/4→3) | the adventure difficulty mapped to the engine scale (`EngineDifficulty`). Wired: quick combat (`QuickCombat.Resolve` → `OriginalBattleAI.For(S.Difficulty, QuickCombat.HumanSides)`, used by the adventure AI and the net session) and `AdventureCombatSession(…, gameDifficulty)`; duels and the combat screen use Normal. GAP-detail: `AdventureScreen` does not pass `Session.S.Difficulty` to `AdventureCombatSession` yet (file owned by another workstream) |
| Combat kind vf 0x110 | GAP | 00af7f30 (CCombat+0x138, 0 in 00af7280) | values 1 / 2 give levels 3 / 1 | meaning not traced; our battles behave as 0 |
| Candidate cap | FIXED | 008d6940, 008d5eb0 | every generated candidate is evaluated | `MaxCandidates` (96, 24 in quick combat) removed; identical commands are simulated once (same value) |
| Compare weights | FIXED | 00bfae20 (0x00bfaf5d..0x00bfaf8a) | the mode switch falls through every case (three calls of the AI/Combat getter, one `ADD EAX,0x80`): PowerSavingMode (RPGStats+0xbd0) for every mode | `CombatAiStats.CompareCoefs` (was: the current mode's row). X: `TheCompareAlwaysReadsPowerSavingWeights` |
| Own-troops rule | FIXED | 00bfae20 (0x00bfaf33..0x00bfaf57), halves 00bfc37d / 00bfc390 | outside kamikaze, a position without OWN creature HP (+0x68 = [8] of the second half, which FUN_00bfc2a0 fills with our side; the first half is combat vf 0xe0(side), the enemy) loses to one with | `IsBetter` (was: wiping out the enemy wins). T: `LosingAllOwnTroopsIsWorseOutsideKamikaze` |
| Compare formula, threshold | MATCH | 00bfae20 | Σ (Δenemy·EnemyCoef − Δown·MyCoef) over damage / troops / war machines; better if total &gt; clamp(threshold·0.01, 0, 1)·total. FUN_00bfae20(this, arg) answers "is arg better" (008d0030 at 0x008d0068..0x008d0086) | `IsBetter(x, y)` = FUN_00bfae20(y, x) |
| Mode | MATCH | 00a2f4d0 | kamikaze / normal / power saving from the power ratio | `SelectMode`; the mode now only gates the own-troops rule |
| Comparator | MATCH | 008d0030 | fewer Turns (both ≥ 0) → lower Priority → better value → non-spell → lower MovePrice | `CandidateBetter`. T: `ComparatorPrefersFewerTurnsThenPriorityThenNonSpellThenCheaperMove` |
| Candidate defaults | FIXED | 008d7550 | Turns 0, MovePrice 0, Priority 2, compare type 0, spell flag 0 | `Candidate` (was: Turns −1, Priority 0) |
| Start-value exclusion | FIXED | 008d1cb0 (0x008d1d27..0x008d1d3b) | compare type 1 is dropped when FUN_00bfae20(candidate, start): the start is strictly better; ties stay | `Excluded` (was: dropped unless strictly better). X: `ExclusionDropsOnlyWhatTheStartBeats` |
| Start-health exclusion | FIXED | 008d1cb0 | compare type 2 is dropped as type 1, or when own Health [6] after (+0x74) &lt; at the start (think +0x98) | `Excluded` (was not used). X: same |
| Spell compare types | FIXED | 008d02b0 (set by every spell generator) | 1: RESURRECT_ALLIES, SCATTER_SHOT, GATING, SUMMON_BALOR, EXPLOSION, RANDOM_CAST_DARK(_LIGHT), DASH, WORD_OF_THE_CHIEF, DEATH_WAIL, ORDER_OF_THE_CHIEF; 2: BERSERKER_RAGE, SUMMON_OTHER; else 0 | `SpellCompareType` (was: every spell and ability had to beat the start). X: `SpellCompareTypesComeFromTheExeTable`. Our ability ids are mapped to SPELL_ABILITY_* by name |
| Mana limit | GAP | 00a2f280 (settings +0x9c → think +0x1b0), 008d1cb0 | spell candidates with mana after (+0xa8) &lt; limit are dropped. Limit: hero vf 0x18c → 0; a human hero whose vf 0x144 string is empty → M; our damage (FUN_00bfa1b0) &lt; enemy Health → 0; ≤ 10 × Health → round((1 − Health / damage) · M); else M. M = hero vf 0 → vf 4 (not identified) | not ported: limit 0 (no spell is dropped) |
| Survivor order | GAP | 008d1cb0 (FUN_008c06c0, FUN_008d70f0) | the surviving indices are shuffled with the combat RNG before the comparator scan | generation order (deterministic); differs only on exact ties |
| Too weak | FIXED | 00a2f430 ("We are too weak to win"), 008d6670, 008d0bd0, 008d6940 retry | ratio &lt; clamp(KamikazeModeTreshold): only damage spells (FUN_00977c80) and BATTLE_DIVE, SCATTER_SHOT, EXPLOSION, BERSERKER_RAGE, STORMBOLT, DEATH_WAIL, LEAP, PACK_DIVE, INVISIBILITY; when nothing is generated the switches are cleared and generation repeats | `TooWeak`, `AllowedWhenTooWeak`, retry in `Decide` (was not ported) |

### Generators

| Decision | Status | Exe | Original | Ours / evidence |
|---|---|---|---|---|
| Dispatch | FIXED | 008d6940, 008d6850, 008d68c0 | hero: strikes + spells; a creature that can shoot (vf 0x1e0): spells + shots only; otherwise spells, guard/block, attacks, and the approach only when those added nothing | `Generate` (was: shooters also got melee, approach and guard moves). X: `ShooterGeneratesOnlyShotsAndSpells`, `ApproachOnlyWithoutAttacksAndWithTurns` |
| Dispatch: war machines, fallbacks | GAP | 008d6940 (vf 0x7c type 1 → 008d3950(1) skipping SPELL_BLIND / SLEEPING_STRIKE targets, then (0); 2 → 008d5c00; 3 → spells), 008d4f70 (invisible enemies), 008d4ce0 ("Conserve-rage") | — | war machines use the shooter / spell path; the two fallbacks are not ported (nothing left → defend) |
| Spell level | MATCH | 008d6670 (FUN_00a34330 ≤ row +4) | MaxSpellLevelToCast | `def.Level` check |
| Other spell gates | GAP | 008d6670 | FUN_00a345b0 (spell +0x88 == 5) unless think +0x1b5; a shooter with think +0x1ac only FUN_00a34550 spells; MELT_ARTIFACT skipped when think +8 → vf 4 → vf 0x18c; level 2 → only the 008d0bd0 list; GATING → 008d28e0; untargeted 008d30e0; single targets 008d3380 | not applied; targets from our legal actions |
| Area spell centres | FIXED | 008d61b0, 008d12a0, 008d6ce0, 008d04c0 | CenterAreaSpellsOnCreatureOnly (except FROST_RING) or kind 0 → single-target generator; else every centre (x 2..W−3, y 1..H−2) is tried, the stacks hit are split into own / enemy (spell +0xd0 class: a hostile spell needs an enemy), and only centres not dominated (enemies ⊆ and own ⊇ another's; equal keeps the first) are kept | `SpellCells` (was: enemy cells plus midpoints of close pairs). X: `AreaCentresKeepOnlyUndominatedCells`. GAP-detail: our whole field is scanned (exe border offsets vs our grid not traced); only the 4 spells with an engine area take part; the 008d3380 target list is taken as the enemy creature cells |
| Spell priority | FIXED | 008d08f0 | 2, lowered to the minimum FUN_008d00d0(target, caster, 0) over the stacks hit | `SpellPriority` (was 0) |
| Target priority | UNVERIFIED | 008d00d0 | war machine: flag 0 → 1, else HP &gt; 2·damage (FUN_00a5c870) → 2, Ballista 0, Ammo Cart 2 if FUN_00a2f8f0, others 1; a creature with vf 0x58 and no SPELL_PHANTOM → 1; Turns &gt; 0 and MovePrice &gt; 0 → 1 when round(init·speed) &lt; round(target init·speed·2/3); else 0 | `TargetPriority` (was 0). GAP-detail: vf 0x58 and FUN_00a2f8f0 not applied; damage = our mean damage × count |
| Strike cells | FIXED | 008d4330, 008d3c40, 00b3d010, 008d6dd0 | per enemy, the ring of anchor cells (right column, top row right to left, left column, bottom row) sorted by path cost (stable); each valid one is a candidate (Turns 0); with ConsiderOnlyShortestPathToEnemy (level ≠ 2) only the first valid one per enemy | `Attacks`, `Ring` (was: every strike cell, no per-target limit). X: `RingCellsFollowTheExeOrder`, `ConsiderOnlyShortestPathKeepsOneStrikeCellPerTarget`. GAP-detail: compare type 1 when the target's vf 0x8c is set; catapult targets (+0x1b6); the +0x1b7 target filter (FUN_008d0440); STRIKE_AND_RETURN "Move-no-attack" |
| Approach | FIXED | 008d5b90 → 008d4330 (whole-path map +0xfc) | as strike cells, on the whole-path map; the command walks as far as this turn allows; Turns = turns to the cell | `ApproachMoves` (was: one closest cell per enemy, or only the nearest enemy). GAP-detail: the planner FUN_00c53f60 / 00c540c0 is approximated (budget-free Dijkstra, turns = ⌈cost/budget⌉ − 1, flyers stop at the reachable cell nearest the ring cell); the +0x1ac gate and the siege branch 008d4610 are not ported |
| Guard / block gate | FIXED | 008d64e0, 00bf9eb0 | only when !think +0x1b7, the actor's vf 0x58 is false and the enemy's shooter power (FUN_00bf9d30) exceeds ours | shooter-power gate in `Generate` (was: always). GAP-detail: +0x1b7 (FUN_00a2f840 count ≤ 2 at 0x00a3060c) and vf 0x58 not applied |
| Defend shooter | FIXED | 008d58c0, 008d4070 | for every other friendly stack that can shoot and lacks NO_MELEE_PENALTY, every ring cell reachable now: Turns = turns + 1, Priority 2 | `DefendShooterMoves` (was: one cell next to a threatened shooter). GAP-detail: the extra rage candidate (+0x1bd) |
| Block enemy | FIXED | 008d4500 → 008d3c40 (no attack) | for every enemy that can shoot, moves to its ring cells; first only with ConsiderOnlyShortestPathToEnemy | `BlockEnemyMoves` (was: one cell). GAP-detail: enemy side vf 0x1a0 gate, FUN_00b3e1c0 filter (+0x1bc) |
| Strategy "defensive" | GAP | 00a2fd40 ("Ranged advantage", +0x98 → +0x1ac) | set when FUN_00a2f710(enemy, enemy shooter power 0) or (OurShootersMinRelativePower &lt; our shooter power / our damage and OurShootersMinRelativeAdvantage &lt; our / enemy shooter power), unless FUN_00a2f710(us, ours 0); stops the approach and limits shooters' spells | not ported (flag false) |

### Combat value and the rest

| Decision | Status | Exe | Original | Ours / evidence |
|---|---|---|---|---|
| Shooter factor | UNVERIFIED | 00bfb950 | (2 − f)·damage; f = 1 for a non-creature or with &lt; 2 units, else the expected-damage share of enemy non-shooters that threaten the shooter (FUN_004b1660 on the FUN_004b23b0 matrix), 1 without such enemies | `ThreatShare` (was: 1 if blocked, else half the far-HP share). GAP-detail: the threat matrix is not decoded (ours: within speed + 1) |
| Expected damage | GAP | 00bfb880, 00bfa280, 00a314c0 | round(f · round((min + max)·count·0.5)); f = 0 for a walker that can reach nobody (FUN_004b1770 / 004b1810) | mean damage × count |
| Luck / morale bonus [5] | GAP | 00bfb4b0 | luck/morale (FUN_004e4920), initiative, speed − 5, shooter / range / NO_MELEE_PENALTY factors, clamp 0..13 | 10 % per point, ≤ 50 % |
| Hero potential [1] | GAP | 00bfbe50, 0085b4a0, 00af1c00, 00a2d700 | Σ vf 0x1c of the side's kind 0/3/5 units that can act, ÷ n = clamp(round(time to act / ATB + 1), 1, 2); none when n &gt; 2 | mean hero strike over targets |
| Slots [0], [2], [16], HP adjustments | GAP | 00bfba50, 00bfa360, 00bfa610 | per-unit extras, spell potential, INCORPOREAL / CELESTIAL_SHIELD / DEFLECT_ARROWS / INVISIBILITY HP adjustments | 0 / not applied |
| Rune purchase | GAP | 008d0960 | buy a rune when cost · priceScale &lt; MaxRuneMoneyEstimate | not ported |
| Tactics phase | GAP | 00a2fd40 / 00a30730 strategy flags | the AI's own tactics placement | the AI ends tactics at once; the battle-start placement itself is ported (`ExeAutoPlacement`, AUDIT_COMBAT `AUTO_PLACEMENT`) |
| Look-ahead | GAP | 008d10e0 | lookAheadTime &gt; 0 | not ported (0 in normal play) |
| Flee / surrender | GAP | 00c97930 → 00c972d0 (+0x10 flee, +0x11 surrender) | the think vf decides retreat before thinking | never flees; the deny gate is MATCH (see "AI flee / surrender decision") |
