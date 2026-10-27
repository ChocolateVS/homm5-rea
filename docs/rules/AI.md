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

Per-decision ledger with addresses and status: `docs/rules/AUDIT_AI.md`, section "Combat AI".

* **Think loop** FUN_008d6a70 ("CombatAI start think", "CombatAI move by %f sec. lookAheadTime: %d, commands tried: %d"):
  generate candidates (FUN_008d6940) → evaluate (FUN_008d5eb0 without look-ahead, FUN_008d10e0 with) → choose (FUN_008d1cb0).
  The think object is built by FUN_008d27a0 from CCombatAI's think vf 00c97930; no candidate cap. **exact structure**
* **Think level and difficulty row**: think +0x18 = 4, then FUN_008d0840: human side → 5, computer side →
  FUN_004f66c0(engine difficulty: 0, 1 → 0; 2 → 4; 3 → 5; 4 → 6). Row getter FUN_00a2f0d0: level ≤ 3 → DifficultyNormal,
  ≥ 4 → DifficultyHeroic (Easy and Hard rows are never read). **exact** (the engine difficulty's source field CCombat+0x94 is
  UNVERIFIED)
* **Candidate record** (0xd0 bytes, defaults FUN_008d7550): +8 command, +0xc Turns (0), +0x10 MovePrice (0), +0x14 combat value
  after the command, +0xa8 mana after, +0xac Priority (2, set by FUN_008d00d0 / FUN_008d08f0), +0xb0 compare type (0; spells
  FUN_008d02b0: 1 = not worse than the start, 2 = also keep own HP), +0xb4 spell flag, +0xb8 command name, +0xc4 target.
* **Generators** (FUN_008d6940): hero strikes FUN_008d2e90 + spells FUN_008d6670 (summon 0x9f → FUN_008d28e0, untargeted →
  FUN_008d30e0, area → FUN_008d61b0 / FUN_008d12a0 undominated centres, single → FUN_008d3380); a creature that can shoot:
  spells + shots (FUN_008d6850 → FUN_008d3950) only; otherwise FUN_008d68c0: spells, guard friendly shooters (FUN_008d58c0) and
  block enemy shooters (FUN_008d4500) while the enemy out-shoots us, attacks from the ring cells FUN_00b3d010 sorted by path
  cost (FUN_008d4330 → FUN_008d3c40; ConsiderOnlyShortestPathToEnemy keeps the first per enemy), and the approach (FUN_008d5b90,
  same on the whole-path map) only when nothing else was added; then invisible enemies (FUN_008d4f70) and FUN_008d4ce0.
  "Too weak" (FUN_00a2f430) restricts spells to damage spells and a list (FUN_008d0bd0); empty → switches cleared, rerun.
* **Exclusions** (FUN_008d1cb0): type 1 dropped when the start is better, type 2 also when own HP drops ("…by start health
  value"), spells under the mana limit FUN_00a2f280 (not ported); the survivors are shuffled with the combat RNG (not ported)
  and scanned with the comparator; nothing left → "End of choose. Chose defend.".
* **Comparator** FUN_008d0030(cur, cand) — true = cand better: fewer Turns (when both ≥ 0 and different) → lower Priority →
  better combat value (FUN_00bfae20(cur, cand)) → non-spell over spell → lower MovePrice. **exact**
* **Combat value** (FUN_00bfc2a0): two 0x48-byte halves — first the ENEMY (FUN_00bfbe50 with combat vf 0xe0(side)), then
  our side — each holding [0] extras, [1] hero potential, [2] spell potential, [3] Damage, [4] AttackLevel, [5] Bonus, [6] Health,
  [7] Defence, [8] TroopsHealth, [9] TroopsDefence, [10..15] shooter/flyer/walker damage and attack, [16] additive.
  Per unit (FUN_00bfba50): Damage += expected damage (shooters × (2 − f), FUN_00bfb950: f = share of threatening enemy walkers);
  AttackLevel/Bonus/Defence/TroopsDefence are damage- or HP-weighted means of (stat + 1) (FUN_00bf9ee0).
  **exact** structure; expected damage, luck/morale bonus, hero potential and the threat matrix **reconstructed**; [0], [2],
  [16] not modelled (0).
* **Measures**: damage FUN_00bfc4a0 = round(factor(AttackLevel,1)·Damage·Bonus) + hero share; troops
  FUN_00bf9a50 = round(TroopsHealth·max(0.1, 1/factor(1,TroopsDefence))); war machines FUN_00bf9ab0;
  power FUN_00bf9b80 = Health·max(0.1,1/factor(otherAttack,Defence)) + Damage·max(1,factor(AttackLevel,otherTroopsDefence)·Bonus) + extras. **exact**
* **Compare** FUN_00bfae20(this, arg) — true = arg better: outside kamikaze mode, the position that has lost all OWN troops
  loses; otherwise Σ (Δenemy·EnemyCoef − Δown·MyCoef) over damage, troops and war machines; better if total &gt;
  clamp(CompareTreshold·0.01, 0, 1)·total. The coefficients are PowerSavingMode for every mode (the mode switch falls through,
  0x00bfaf5d..0x00bfaf8a). **exact**
* **Mode** FUN_00a2f4d0 (from FUN_00a30460): ratio = our power / enemy power; kamikaze below
  clamp(KamikazeModeTreshold, 0, 1000), power saving above clamp(PowerSavingTreshold, 0, 1000). **exact**
* **Rune purchase** FUN_008d0960: buy a rune when cost · priceScale < MaxRuneMoneyEstimate,
  priceScale piecewise in the power ratio (0.5 below 0.8; 7.5r−5.5 below 2; 6r−4 below 4; else 20). Not ported
  (runes are cast through the normal spell candidates).
* **Strategy flags** FUN_00a2fd40 ("Ranged advantage", "Our: Shooters part = %f", "Strategy: %s %s"): the defensive flag
  (think +0x1ac) and the spread/large flags from the Autoplacement tunables. Not ported in the think; the battle-start
  placement is ported separately (`ExeAutoPlacement`).

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
* Values (ported, AUDIT_AI.md "Other object values"): stack value FUN_004e9b90, hero value FUN_004eae60 (hire
  score), hero estimate FUN_004eb050, town value FUN_004eb480, player value FUN_004eb790 (resource weights taken as 1.0: the per-turn AI money values FUN_00a325b0 are not decoded),
  position value FUN_00872220; a destination is valued by its provider directly or by simulating the visit
  (FUN_00bc5480) on a `StateCopy` of the game. Threats FUN_0086cd10, town protection FUN_00a78b60, free scan
  FUN_00a78400, strategic propagation FUN_00a79700, the town build list FUN_009c33d0 (the simulated builder
  FUN_009c5e20 is dead code), greedy purchase FUN_00cde4c0 and the hire score FUN_0086aa50 are ported. **exact**
  structure, GAP-details per row.
* Still **reconstructed** or missing: boats (no exe AI logic beyond the flood), map spells (pathfinder edges),
  grail search, the market trade 008e70d0, the supplier test, flee / surrender (combat side). Per-area ledger with
  addresses: `docs/rules/AUDIT_AI.md`.

## Validation (`h5tool duel-matrix --stride 3`, seed 1, 184 games each)

Measured before the combat AI audit (AUDIT_AI.md "Combat AI") and not re-run since. After it, an original-vs-original
`duel-matrix 1 --stride 25` took 1.55x the time of the previous version on the same machine (no candidate cap, every
strike cell in the Heroic row).

| Side 0 AI | Side 1 AI | side 0 wins | side 1 wins | unfinished | errors |
|---|---|---|---|---|---|
| original | greedy | 75 | 109 | 0 | 0 |
| greedy | original | 78 | 106 | 0 | 0 |
| original | original | 84 | 100 | 0 | 0 |

Overall the original port wins 181 of 368 games against the greedy stand-in (49%); both show the same
defender-side advantage as original vs original. About 1 s per duel.

`h5tool adv-sim <map> 28 3 --ai=mixed` (odd players original, even simple): runs 4 weeks on A2M1, A2C1M1 and a
user map with no errors; same seed gives the same final hash.
