# AI audit ledger (H5_Game.exe vs H5.Adventure/AI and H5.Combat/AI)

Adventure-map AI (NWorld::CPlayerAI, `src/H5.Adventure/AI/OriginalAdventureAI*.cs`) and a high-level pass over the
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
| FIXED | 23 |
| MATCH | 11 |
| GAP | 43 (11 of them combat AI) |
| UNVERIFIED | 2 |
| N/A | 2 |

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
| Move cost to a destination | FIXED | record +0x18 from the wave spread FUN_00c08be0 (0086c010), limit +0x74 | the pathfinder's path cost to the destination; destinations the flood does not reach are not found | `ReachableCosts` (the engine's reconstruction of the same flood, `Adventure.Simultaneous.cs`) once per choice up to the widest attempt limit (was: Chebyshev distance × SearchAverageTerrainCost; unreachable targets were only dropped after the choice). GAP-details: pathfinder mode 6 (00c076d0) and flight are not decoded (engine TODO); day boundaries are not modelled |
| Previous best kept with 3/2 | GAP | 00a7b630 (0x00a7b85c..0x00a7b8e8) | the hero's current destination enters the choice first, its value × T · 3 / (2 · cost) when cost > T | not ported |
| Strategic propagation | GAP | 00a79700 (SearchStrategicDistance +0x214 × T, StrategicCaptureDistance row +0x78) | destinations near others inherit their value (sums, min/max of states) | not ported |
| Search limit (wave spread) | FIXED | 0086c010 ("Wave spread: search limit = %d, time = %d ms."), CPlayerAI +0x70 attempts / +0x74 limit / +0xe0 max attempts, FUN_00866c30 | attempt 0: 1500 with 6 attempts, roles 1..3 RolesSearchNearMaxCost (+0x14) with 2; a remembered target (AI record +8 valid) raises it to stored cost (+0x10) + 200 + move points; player in danger → RolesSearchFarMaxCost (+0x18) with 1; each retry doubles, roles 1..3 → Far; out of attempts → the hero is finished | `SearchLimits`, `PlayerInDanger`, `ChooseDestination` retries (was: commander Far, others max(Near, 3 × move points)). A: `Search_limits_follow_the_wave_spread_attempts`. GAP-details: our cost is the approximation of the next row; FUN_00866c30's town threats are not ported (k = 0) |
| Collector / supplier free scan | GAP | 00a78400 (called from 00a7af80 per destination) | allied foreign towns are refused; role 1: anything within today's move points (record cost ≤ vf 0xf4), farther only objects with a hero (vf 0x2c), town (0x3c), ownable (4), army (0x28 with vf 0x24) or vf 0x1c / 0x20 / 0x58 / 0x5c interface; role 2 / 3 without a valid remembered target: anything on the hero's floor within Chebyshev RolesCollectorFreeScanMaxMoves (+0x23c) / RolesSupplierFreeScanMaxMoves (+0x240), farther only ownable objects of non-allies (2) or the partner / partner's town (3); role 0: everything | not ported (the vf 0x1c/0x20/0x58/0x5c interfaces are not identified) |
| Script attractors −1 / +1 | MATCH | 00bc5740 (+0x21c / +0x218), hero list FUN_00b4d5c0, player vf 0x1e8 | value −SearchNegativeAttractorValue / +SearchPositiveAttractorValue; hero entry first, else the player's | `AttractorPriority`, D: `Attractor_list_overwrites_removes_and_appends` |
| Script attractor priority 2 | FIXED | 00bc5740 (state ← 3), 00a7b630 | state 3 outranks every lower-state destination whatever its value; among them the normal value decides | was: nearest first with value 1e300. A: `Higher_state_outranks_…`. GAP-detail: whether state 3 skips the danger checks is not traced (it does here) |
| "AI will not risk its heroes" with priority 1 | GAP | — | from the script reference only; no exe test found | a hostile target with value ≤ 0 is skipped even with priority 1 |
| Hero attribute value | MATCH | 004e9f60 | Σ attribute · ValueOfAttribute* | O: `ExeConfirmedFormulas` |
| Artifact value by class | MATCH | 004eb840 | minor / major / relic / grail values | O: `ExeConfirmedFormulas` |
| Other object values | GAP | evaluator vtable 00e84e1c slot 0 = 00bc5740 (from 00a78b60 / 00a7af80 per destination): object vf 0x6c → value provider; provider vf 4(hero) true → provider vf 0 gives (state, value) directly (e.g. monsters 004eee00); otherwise 00bc5480 simulates the visit (player vf 0x13c(1) simulation mode, world vf 0x74(player, object)) and takes Δ of the estimate 00872220 (004eb790 player value: resources 004ea050 × demand weights (player +0x1d8 vf 8), income × IncomeMultiplier, heroes 004eb050, towns 004eb480, 004ea180; minus the avenger part; state 3 / −2 on win / loss); then the attractor adjustment | per-object gold equivalents | reconstructed constants in `ObjectValue` (treasure 250/500 per unit, mine income, dwelling IncomeMultiplier·40, shrine 2 spell levels, ValueOfTown, ValueOfHero + Pow2); monsters: see the next row. The visit simulation needs an engine state copy and is not ported |
| Neutral monster value | FIXED | provider 004eee00 → 00bc3af0 (survival 00bc3f90, minimum 0.75 at 00e80b18, experience value 00bc3700 / 004e9fe0 / 004baad0, rival armies 00bc3a40 / 004ea9d0 / 00a85030, unseen tile 0098dbf0) | A ≤ E or A < 1 refuses; survival f = 1 (A ≥ 10E) or sqrt(A² − E²)/A, losses × 0.25 when no human has seen the tile; f < 0.75 refuses; value = experience-value gain G = ValueOfHeroLevelPow2 · ((L + progress)² after − before), adjusted against the strongest rival player's hero armies m: own total T ≤ m → ((f·T − m) · 0.1 + G), T > m → G when f·T ≥ m else 0 | `MonsterFightValue`, `Survival`, `LevelValue`, `StrongestEnemyArmies`, `UnseenByHumans` (was: monster power / 2 behind a reconstructed power test). A: `Fight_estimate_…`. GAP-detail: a refused fight with a priority-2 attractor scores 5 here (exe: state 3 with value 0) |

## Fight or flee, threats

| Area | Status | Exe | Original | Ours / evidence |
|---|---|---|---|---|
| Army strength estimate | FIXED | fight sums 004ea5c0 → 004ea520 (004bab00, 004bb150 shooter, 004ba950 flyer), 00c02ac0 | per stack max(Power, 1) · ftol(level · ArmyPowerCoefBonusPerHeroLevel + 1) · count (× SiegeShootersCoef for shooters in mode 2, × SiegeFlyerCoef for flyers in mode 1), ftol-accumulated; a human-owned hero's army × (1 + CowardiceVersusHuman) | `AiArmyPower`, `HeroLevelFactor` (A: `Fight_estimate_uses_power_sums_square_law_survival_and_experience_value`); used by the monster value. GAP-details: the war-machine part 004ea220 (town garrison machines, 00cd0a30) not ported; the estimate stack value 004e9b90 (max(Power,1)·100 + Creature*Coef · over-base health / damage / attack+defence (hero-less getters), MAE armor crushing / magic protection / speed bytes, × (1 + luck·Good/BadLuckCoef) (1 + morale·Good/BadMoraleCoef, −1 in a tavern town) (1 + over-initiative·OverInitiativeCoef), ×1.4 shooters when hero vf 0x324 (unidentified) > 0, × count) belongs to the player / hero estimates and is not ported |
| Aggressivity / cowardice | GAP | row +4 / +8 (FUN_00c02ae0: aggressivity vs human / computer defender owner, the fight value coefficient in 00bc3af0), +0xc (00c02ac0: human armies × (1 + CowardiceVersusHuman) in 004ea5c0); readers 0086cd10, 00872220 | monsters: coefficient 1.0 (no defender object) — FIXED via the monster value; heroes / towns: G · aggressivity (+ hero value / enemy players for heroes) | heroes and towns still reconstructed (target refused when enemy · (1 + cowardice) ≥ own · aggressivity) |
| Avenger coefficients | GAP | row +0x24 / +0x28 (0086cd10, 00872220, 00a78b60) | — | not used |
| Enemy danger reach | FIXED | 00872330 (+0x220 / +0x224) | reach = moves · SearchDangerEnemyMobilityMultiplier + SearchDangerEnemyMobilityBonus, flooded over the enemy's path costs | `DangerTiles` floods each enemy hero's `ReachableCosts` up to that reach (was: Chebyshev × T). GAP-detail: which heroes count as dangerous (army power above ours, `ArmyPower`) is reconstructed |
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
| Combat AI difficulty | GAP | CCombatAI +0x1ac / +0x1b4 / +0x1b7 from AI/Combat/Difficulty* | per-difficulty switches and spell level | every `OriginalBattleAI` (adventure quick combat, sessions, net) runs with `AiDifficulty.Normal`; the exe's row choice for combat is not traced |
| EnableHeroAI | GAP | see "AI-disabled hero" | — | — |
| MoveHero on an AI hero | GAP | 005cef50 | sets an AI move target | moves at once (SCRIPTS.md) |
| DenyAIHeroesFlee | MATCH | 005e2760, 00c9741e | — | see "AI flee / surrender decision" |
| SetAIPlayerAttractor | FIXED | 005e00e0 → 00bc5740 | see attractor rows | — |
| DoNotGiveTurnToPlayerAIIfNoTownsAndActiveHeroes | MATCH | 0098e410 | — | `GetsTurn` (D) |
| Determinism | N/A | — | the exe AI is single-machine | private RNG per day/player/hero; O: `PlaysDeterministicallyWithoutStalling` |

## Combat AI (high level): behaviour not from the exe

The combat AI (`OriginalBattleAI`, `CombatValue`) follows the exe structure documented in `AI.md` (think loop
008d6a70, comparator 008d0030, combat value 00bfc2a0, compare 00bfae20, mode 00a2f4d0). Parts that are ours:

| Item | Status | Exe | Notes |
|---|---|---|---|
| Candidate cap `MaxCandidates` (96; 24 for adventure quick combat) | GAP | 008d6940 | no cap known in the exe; ours truncates the generated list |
| Approach moves (closest reachable cell per enemy, Turns = rounds to strike range) | GAP | 008d5b90 | reconstructed |
| UseDefendShootersMoves / UseBlockEnemyMoves cell choice | GAP | 008d6940 generators | reconstructed (cell next to a threatened friendly shooter / an enemy shooter) |
| Area-spell centres between enemy pairs | GAP | 008d61b0 | reconstructed (CenterAreaSpellsOnCreatureOnly honoured) |
| Shooter penalty share, luck/morale bonus (10 %/point, ≤ 50 %), hero strike potential (mean over targets) | GAP | 00bfb950, 00bfba50 | approximations; value slots [0], [2], [16] are 0 |
| Health compare rule (compare type 2 vs +0x98) | GAP | 008d1cb0 | not used |
| Rune purchase (MaxRuneMoneyEstimate) | GAP | 008d0960 | not ported |
| Tactics autoplacement | GAP | 00a2fd40 | not ported: tactics end at once with the default placement |
| Look-ahead search | GAP | 008d10e0 | not ported (lookAheadTime 0 in normal play) |
| Flee / surrender | GAP | 00c972d0 | never flees |
| Difficulty | GAP | — | always Normal (see above) |
