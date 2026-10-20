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
| FIXED | 13 |
| MATCH | 12 |
| GAP | 50 (11 of them combat AI) |
| UNVERIFIED | 3 |
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
| Garrison moves | GAP | 00869770 tail (town vf 0x18(1), hero vf 0x2ac, vf 0xf4 > 0 → FUN_0055af30), FUN_00868640 (0x00868768: hero vf 0x2ac, town owner, town vf 0x114 → garrison command FUN_00435cc0) | with no hero left, an AI-enabled garrisoned hero with move points is moved out; a finished AI-enabled hero standing in its own town is put into the garrison unless the town refuses (vf 0x114) | our AI never moves heroes in/out of garrisons; `TakeGarrison` (creature pickup) is reconstructed |
| BlockTownGarrisonForAI | UNVERIFIED | 0x008687a0 (in FUN_00868640) | the reader sits on the hero-into-garrison move | we apply it to the creature pickup `TakeGarrison` (the garrison move is not ported) |
| Commander finish command | GAP | FUN_00868640 (role vf 0x2bc == 1 → FUN_0086e920 command) | a finished role-1 hero posts an extra command | not ported |
| Hero spread weight | MATCH | 00868a40, 00866670, 004e9a80 | RolesSpread[role] · (1 + level · ArmyPowerCoefBonusPerHeroLevel); heroes with vf 0x2d4 ∈ {1, 2} are left out of the spread | `SpreadWeight` (O: `ExeConfirmedFormulas`); GAP-detail: the vf 0x2d4 exclusion (SetHeroRoleMode?) is not applied |
| Role assignment | GAP | hero vf 0x2bc (role), SetHeroRoleMode 005e7260 | role stored on the hero; assignment code not traced | reconstructed: strongest army = Commander, weakest of ≥ 3 = Supplier, rest Collector |
| Destination value: state merge | GAP | 00a78940 | (state, value64) pairs; parts merged with "negative state dominates, else max"; value = own + strategic parts (+0x28, +0x38) | only state 0 and 3 (attractor 2) exist here |
| Old-target bonus | FIXED | 00a78940 (0x00a78a67) | destination == the hero's remembered target (AI record +8): v ≥ 0 → ftol(v · SearchOldTargetMultiplier) + 20, else ftol((float)v / mult) + 20; no random factor | `OldTargetValue` (was: × mult, random applied too). A: `Old_target_bonus_multiplies_and_adds_twenty` |
| Random factor | FIXED | 00a78940 (0x00a78ab7), RNG vf 0xc = inclusive Range | r = Range(1, SearchBestPositionRandomFactor) + 100 − R; v > 0 → max(1, ftol(r·v·0.01f)) (0.01f = 0.0099999998, so 76·1000 → 759), else ftol(v / r · 100) | `RandomizedValue` (was: × (1 ± U·R/100)). A: `Random_factor_only_lowers_a_positive_value`. Our RNG stream is private (seeded per day/player/hero), not Nival's |
| Distance discount | FIXED | 00a7b630 (0x00a7b9b0) | state > 0, or state 0 and value > 0, with move cost > SearchAverageTerrainCost T → v · T / cost (int64), at least 5 | `DiscountedValue` (was: value / (1 + days)). A: `Distance_discount_divides_by_cost_over_average_terrain_cost` |
| Choice comparison | FIXED | 00a7b630 ("discarded, %s <= %s", "new best") | replace the best only on a higher state, or equal state and strictly higher value; a "critical" record (+0x1c) stops the scan | `Better`. A: `Higher_state_outranks_any_value_and_ties_keep_the_first`. GAP-detail: the critical flag and "discarded neutral for positive previous" filter are not ported |
| Move cost to a destination | GAP | record +0x18 (pathfinder) | real path move cost | Chebyshev distance × SearchAverageTerrainCost |
| Previous best kept with 3/2 | GAP | 00a7b630 (0x00a7b85c..0x00a7b8e8) | the hero's current destination enters the choice first, its value × T · 3 / (2 · cost) when cost > T | not ported |
| Strategic propagation | GAP | 00a79700 (SearchStrategicDistance +0x214 × T, StrategicCaptureDistance row +0x78) | destinations near others inherit their value (sums, min/max of states) | not ported |
| Search limit (wave spread) | GAP | 0086c010 ("Wave spread: search limit = %d, time = %d ms."), CPlayerAI +0x74 / +0xe0 | starts at 1500, then RolesSearchNearMaxCost / FarMaxCost (row +0x14 / +0x18), doubling per retry | reconstructed per role |
| Collector / supplier free scan | GAP | 00a78400 (RolesCollectorFreeScanMaxMoves +0x23c, RolesSupplierFreeScanMaxMoves +0x240) | role 2 / 3 destinations within that Chebyshev range of the hero always pass; others by owner / type tests | not ported |
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
| Hire cost | GAP | 00b4ced0 | 500 · (heroes + 4); × 0.9 when the third argument's vf 0xdc is 0x25 | engine `HeroHireCost` = 2500 flat (engine audit item; the AI uses the engine cost) |
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
| Level-up choice | GAP | 004ead50 (ValueOfSkillPerMastery +0x288, ValueOfPerkFeat +0x28c) | formula not traced | max of mastery · ValueOfSkillPerMastery vs ValueOfPerkFeat |
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
