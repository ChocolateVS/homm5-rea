# Original AI (H5_Game.exe) — findings and port status

Source: GOG *Tribes of the East* 3.1 `bin/H5_Game.exe`, analysed headlessly with Ghidra 12.1.4 on a private
project copy (`tools/aire/ghidra.sh`). The game was never run. Code: `src/H5.Combat/AI/**`,
`src/H5.Adventure/AI/**`. Status per item: **exact** (ported from decompiled code), **data** (tunable read
from DefaultStats.xdb exactly where the exe reads it, use reconstructed), **reconstructed** (structure from
the exe, details not yet traced).

## Tunables (DefaultStats.xdb `AI`)

`AI` is RPGStats+0xb4c (root reader 0x00960960, field reader FUN_0096f2c0): `Combat` at +4 (RPGStats+0xb50,
getter FUN_00a2f0c0), `Adventure` at +0xb8 (RPGStats+0xc04). The adventure block is copied once into a global at
0x010c5480 (FUN_00c029a0, validated by FUN_00c02300); AI code reads it through that getter with the same offsets.

| Combat field | RPGStats offset |
|---|---|
| Difficulty{Easy,Normal,Hard,Heroic} (MaxSpellLevelToCast int, then 4 bools) | 0xb54 + 12·d (+4 int, +8..+0xb bools) |
| KamikazeModeTreshold / PowerSavingTreshold / MaxRuneMoneyEstimate | 0xb84 / 0xb88 / 0xb8c |
| KamikazeMode / NormalMode / PowerSavingMode (7 floats each: EnemyDamage, MyDamage, EnemyTroopsHealth, MyTroopsHealth, EnemyWM, MyWM, CompareTreshold) | 0xb90 / 0xbb0 / 0xbd0 (+4…+0x1c) |
| Autoplacement (4 floats) | 0xbf0 (+4…+0x10) |

Adventure fields: Difficulty rows at +4 + 0x80·d (31 fields, see `AdventureAiStats`), then Search* at +0x204,
RolesSpread* +0x22c..+0x238, RolesCollector/SupplierFreeScanMaxMoves +0x23c/+0x240, Creature* coefficients
+0x24c..+0x278, ValueOf* +0x27c..+0x2dc, Siege* +0x2e0..+0x2f4, battle-bonus and talisman values +0x308..+0x318.
Full map: `tools/aire/out/aichain.txt` (regenerate with `ReaderChain.java 0096f2c0`).

## Combat AI (NWorld::CCombatAI, RTTI `.?AVCCombatAI@NWorld@@`) — `OriginalBattleAI`

* **Think loop** FUN_008d6a70 ("CombatAI start think", "CombatAI move by %f sec. lookAheadTime: %d, commands tried: %d"):
  generate candidates (FUN_008d6940) → evaluate (FUN_008d5eb0 without look-ahead, FUN_008d10e0 with) → choose (FUN_008d1cb0). **exact structure**
* **Candidate record** (0xd0 bytes): +8 command, +0xc Turns, +0x10 MovePrice, +0x14 combat value after the command,
  +0x5c reference value, +0xa8 mana after, +0xac Priority, +0xb0 compare type (1 = must beat start value,
  2 = health check), +0xb4 spell flag, +0xb8 command name, +0xc4 target.
* **Generators**: hero (FUN_008d2e90 strikes; FUN_008d6670 spells/abilities: summon 0x9f → FUN_008d28e0,
  untargeted → FUN_008d30e0, area → FUN_008d61b0, single → FUN_008d3380), shooter (FUN_008d6850 → FUN_008d3950
  "Shoot"), melee (FUN_008d68c0 → FUN_008d64e0 attacks, FUN_008d5b90 approach), invisible enemies (FUN_008d4f70),
  fallback (FUN_008d4ce0). The difficulty switches live at CCombatAI+0x1ac/+0x1b4/+0x1b7 and are cleared and the
  generators re-run when they leave no candidate. **reconstructed** (`Generate`; per-target/per-cell candidates as in
  the exe, spell level filter **data**, area centres honour CenterAreaSpellsOnCreatureOnly **data**)
* **Exclusions** (FUN_008d1cb0 log: "Excluded by start value", "…by start health value", "…by mana limit");
  nothing left → "End of choose. Chose defend.". **exact** for start value and defend fallback; mana reserve is
  CCombatAI+0x1b0 (0 in normal play); the health rule (compare type 2 vs CCombatAI+0x98) is not used by the port.
* **Comparator** FUN_008d0030: fewer Turns (when both ≥ 0 and different) → lower Priority value → better combat
  value (FUN_00bfae20) → non-spell over spell → lower MovePrice. **exact**
* **Combat value** (FUN_00bfc2a0): two 0x48-byte halves (own side via FUN_00bfbe50, enemy side), each holding
  [0] extras, [1] hero potential, [2] spell potential, [3] Damage, [4] AttackLevel, [5] Bonus, [6] Health,
  [7] Defence, [8] TroopsHealth, [9] TroopsDefence, [10..15] shooter/flyer/walker damage and attack, [16] additive.
  Per unit (FUN_00bfba50): Damage += expected damage (shooters × (2 − penalty), FUN_00bfb950);
  AttackLevel/Bonus/Defence/TroopsDefence are damage- or HP-weighted means of (stat + 1) (FUN_00bf9ee0).
  **exact** structure; expected damage, luck/morale bonus and hero potential **reconstructed**; [0], [2], [16]
  not modelled (0).
* **Measures**: damage FUN_00bfc4a0 = round(factor(AttackLevel,1)·Damage·Bonus) + hero share; troops
  FUN_00bf9a50 = round(TroopsHealth·max(0.1, 1/factor(1,TroopsDefence))); war machines FUN_00bf9ab0;
  power FUN_00bf9b80 = Health·max(0.1,1/factor(otherAttack,Defence)) + Damage·max(1,factor(AttackLevel,otherTroopsDefence)·Bonus) + extras. **exact**
* **Compare** FUN_00bfae20: outside kamikaze mode, the position that leaves the enemy no troops wins; otherwise
  Σ (Δenemy·EnemyCoef − Δown·MyCoef) over damage, troops and war machines with the mode's coefficients;
  better if total > clamp(CompareTreshold·0.01, 0, 1)·total. **exact**
* **Mode** FUN_00a2f4d0 (from FUN_00a30460): ratio = our power / enemy power; kamikaze below
  clamp(KamikazeModeTreshold, 0, 1000), power saving above clamp(PowerSavingTreshold, 0, 1000). **exact**
* **Rune purchase** FUN_008d0960: buy a rune when cost · priceScale < MaxRuneMoneyEstimate,
  priceScale piecewise in the power ratio (0.5 below 0.8; 7.5r−5.5 below 2; 6r−4 below 4; else 20). Not ported
  (runes are cast through the normal spell candidates).
* **Autoplacement** FUN_00a2fd40 ("Our/enemy shooters = %f, Our: Shooters part = %f"): uses the Autoplacement
  tunables for tactics deployment. Not ported; the AI ends tactics with the default placement.

## Adventure AI (NWorld::CPlayerAI) — `OriginalAdventureAI`

* Hero spread weight = RolesSpread[role] · (1 + level · ArmyPowerCoefBonusPerHeroLevel): FUN_00866670 (role 1
  Commander +0x22c, 2 Collector +0x230, 3 Supplier +0x234, else Freelancer +0x238) × FUN_004e9a80, sorted in
  FUN_00868a40. **exact**
* Hero attribute value FUN_004e9f60 = Σ attribute·ValueOfAttribute*. **exact**
* Artifact value by class FUN_004eb840 (minor +0x2b0, major +0x2b4, relic +0x2b8, grail +0x2d0). **exact**
* Enemy danger reach FUN_00872330 = moves·SearchDangerEnemyMobilityMultiplier + SearchDangerEnemyMobilityBonus,
  flood-filled from the enemy hero. **exact** formula; the flood is approximated by Chebyshev distance × SearchAverageTerrainCost.
* Turn loop: a 15-phase state machine (CPlayerAI +0x128) pumped in 30 ms slices and cut by `ai_time_limit`;
  heroes act one at a time, lowest attack+defence+spell power+knowledge first (FUN_00869770); hiring
  (FUN_0086b0a0) after the heroes, then the town phase. Destination values are (state, value) pairs: old-target
  bonus ×SearchOldTargetMultiplier + 20, random factor (Range(1,R) + 100 − R)·0.01f, distance discount v·T/cost,
  higher state first (FUN_00a78940 / FUN_00a7b630); script attractor priority 2 = state 3. **exact** (ported).
  Computer towns grow by TownCreatureGrowthCoef (FUN_00cd27b0), neutral towns at half. **exact**
* Still **reconstructed**: object values, roles, search limits, threats, town building/recruiting, boats, map
  spells, level-ups. Per-area ledger with addresses: `docs/rules/AUDIT_AI.md`.

## Validation (`h5tool duel-matrix --stride 3`, seed 1, 184 games each)

| Side 0 AI | Side 1 AI | side 0 wins | side 1 wins | unfinished | errors |
|---|---|---|---|---|---|
| original | greedy | 75 | 109 | 0 | 0 |
| greedy | original | 78 | 106 | 0 | 0 |
| original | original | 84 | 100 | 0 | 0 |

Overall the original port wins 181 of 368 games against the greedy stand-in (49%); both show the same
defender-side advantage as original vs original. About 1 s per duel.

`h5tool adv-sim <map> 28 3 --ai=mixed` (odd players original, even simple): runs 4 weeks on A2M1, A2C1M1 and a
user map with no errors; same seed gives the same final hash.
