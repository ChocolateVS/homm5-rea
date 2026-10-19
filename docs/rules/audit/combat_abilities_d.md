# Combat audit — creature abilities, part D (attack patterns, strike helpers, remaining passives)

Per-ability rows merged into `docs/rules/AUDIT_COMBAT.md` by `tools/re/audit/ledger.py` (this fragment sorts after
parts A–C, so its rows override theirs for the same ID). Exe references are H5_Game.exe addresses (Ghidra 12.1.4).

## Attack patterns (FUN_00a62030, FUN_008aa990, FUN_00a5b630)

The exe has no ability test for Six-Headed, Three-Headed, Spray, War Dance, Whirlwind, Cross Attack, Fire Breath or
Acid Breath: every strike asks the attacker for a PatternAttack (creature vf 0xb8 = FUN_008aa990) and, when one is
returned, builds the victims from it (generic branch of FUN_00a62030):

* **Which pattern** (FUN_008aa990(ranged, cells walked, acid flag)): a creature whose definition has shots
  (def +0x4c) uses its pattern only for ranged strikes, any other creature only for melee strikes; Champion Charge with
  more cells walked than ChampionChargeMinimalDistance → DefaultStats ChampionChargePattern; the Acid Blood call →
  DefaultStats AcidicHydraPattern; otherwise the creature's own `PatternAttack`. Before that, FUN_00a62030 sends a
  ranged MAGIC_ATTACK, LIGHTNING_BREATH, RAINBOW_BREATH and a melee FLAMEWAVE to their own branches (special 0xb8 too);
  CRUSHING_BLOW uses the pattern with its own victim rule FUN_00b3d910 (kept as the engine's rule, not compared).
* **Anchor**: melee — the attacker's cell, ranged — the aimed cell (00a674f0–00a6757c). The exe's cell of a stack (vf
  0x40) is its footprint's maximum corner: a 2×2 stack covers pos−1 … pos (FUN_00b3d3f0, FUN_00b3d4e0; every 2×2
  pattern ring is centred on (−0.5, −0.5)). The engine's `Pos` is the minimum corner, so exe cell = Pos + size − 1.
  The engine aims a shot at the target's exe cell (the exe takes the cell from the attack context; for a large target
  hit through the UI this may be another of its cells — not traced).
* **Victims per rotation**: pattern cells + anchor, stacks covering them cell by cell (FUN_00b3d720); enemies only
  (FUN_00b3d850 with the opposing side) unless DamageAll (FUN_00b3d960: both sides); the attacker itself removed.
* **Rotation** (FUN_00a5b630): first the rotations containing the main target — the one with the MOST victims wins,
  ties go to the smaller angle |fmod(AngleRotateTo·0.017453292 − θ + π, 2π) ∓ π| (θ = atan2 from the anchor footprint
  centre to the main target centre, FUN_00a58b60), first one on equal angles; if none contains the main target, the
  non-empty rotation with the smallest angle (ties: more victims). No rotation with victims → the main target alone.
  RotateToMainTarget only moves the visual position.
* **Damage**: every victim is struck with its own roll; the blow is ×max(0, DamageToMainTargetCoefficient) for the
  main target, ×max(0, DamageToOtherTargetsCoefficient) for the others (truncated, 00a67bf3). The main target is not
  struck when the chosen rotation leaves it out (fallback rotation).

Consequences against the former engine rules: a 2×2 target fills the two cells in front of a 2×2 attacker, so
Gold/Green/Black/Red/Deep/Fire/Magma dragon breaths, the Lava Dragon's AcidBreath pattern and Champion Charge reach
nothing behind a large target; the Bloodeyed Cyclops' BloodEyeBeam and the Hydra's half circle are applied (they had no
engine rule); three-cell patterns take the rotation with the most enemies.

| ID | Status | Exe | Original behaviour / note | Test |
|---|---|---|---|---|
| `ABILITY_SIX_HEADED_ATTACK` | MATCH | 00a62030, 008aa990, SixHeadedAttack.xdb / Chaos_Hydra.xdb | the 12-cell ring around the 2×2 hydra, one rotation, DamageAll = false, coefficients 1: every adjacent enemy — the engine's rule gave the same victims; now taken from the data pattern | AuditAbilityTestsD.SixHeadedHitsEveryAdjacentEnemyButNoAlly |
| `ABILITY_WHIRLWIND` | FIXED | 00a62030, 00a5b630, HalfCircleLargeAttack.xdb | 12 rotations of a 6-cell half circle around the 2×2 Kshatri; the rotation holding the target with the most enemies is struck (ties: closest to the attack direction) — engine hit every adjacent enemy in the half plane facing the target | AuditAbilityTestsD.WhirlwindTakesTheHalfCircleWithTheMostEnemies |
| `ABILITY_CROSS_ATTACK` | FIXED | 008aa990, 00a62030, CrossAttack.xdb | the Flame Mage's pattern (aimed cell ± 1 orthogonally, enemies only) for its shots only (shooter definition → ranged pattern); around the aimed cell, not around every cell of a large target — engine took the four neighbours of every target cell | AuditAbilityTestsD.CrossAttackIsTheFlameMagesShotPatternOnly |
| `ABILITY_THREE_HEADED_ATTACK` | FIXED | 00a5b630, Cerberius.xdb / FirebreatherHound.xdb / Hydra.xdb | 8 rotations (Hydra: the 12-rotation half circle): of the rotations containing the target the one with the most enemies is used, not the one facing the target — supersedes the part-A row (angle choice) | AuditAbilityTestsA.ThreeHeadedPicksTheRotationWithTheMostVictims |
| `ABILITY_SPRAY_ATTACK` | FIXED | 00a5b630, Sprite.xdb / Pixie.xdb / SprayAttack.xdb | as Three-Headed (most victims) | AuditAbilityTestsA.ThreeHeadedPicksTheRotationWithTheMostVictims |
| `ABILITY_FIRE_BREATH` | FIXED | 00a62030, Deep_Dragon_Pattern.xdb, FirebreatherHound.xdb | dragons: (1,0),(2,0)-style lines from the 2×2 dragon's exe cell, 12 rotations, DamageAll = true — a 2×2 target covers both cells, so nothing behind it burns; Hound: its own pattern (part A) | AuditAbilityTestsD.LargeTargetsBlockTheCellBehind |
| `ABILITY_ACID_BREATH` | FIXED | 00a62030, Gold_Dragon.xdb / Green_Dragon.xdb | the pattern's second cell is the cell behind a 1×1 target only (a 2×2 target fills both); DamageAll = true | AuditAbilityTestsA.AcidBreathHitsOnlyTheCellBehindTheTargetIncludingAllies |
| `ABILITY_CHAMPION_CHARGE` | FIXED | 008aa990 (008aaa0d–008aaa2f), ChampionCharge.xdb | ChampionChargePattern when walked > ChampionChargeMinimalDistance; its (1,0),(2,0) cells from the 2×2 champion's exe cell reach behind a 1×1 target only | AuditAbilityTestsA.ChampionChargeNeedsMoreThanTheMinimalDistanceAndHitsAllies |
| `ABILITY_WAR_DANCE` | MATCH | 00a62030, War_Dancer.xdb | one rotation of all 8 neighbours, DamageToOtherTargetsCoefficient 0.5 — same victims as before, now from the data |  |
| `ABILITY_ACID_BLOOD` | FIXED | 00a673c0 (00a683ff–00a6859d), 008aa990 (008aaa51), 00a62030, AcidBlood.xdb | for every struck target with ACID_BLOOD on a non-ranged strike other than the Pack Dive finish (0x15b) — retaliation strikes included — the hydra runs AcidicHydraPattern from its own cell with the striker as main target (12 four-cell lines, DamageAll = true, so its own stacks on the line are hit); every victim takes (D + E) / 4 truncated (CDQ/SAR), D = the hydra's damage against it (FUN_00a597a0 roll, FUN_00a5c790), E = Elemental Chains (FUN_00977e60, not implemented for physical strikes in the engine: 0). Engine: the striker only, ×0.25 inside the attacker stage, never on retaliations. The luck argument of the 00a5c790 call is not identified (1 used) | AuditAbilityTestsD.AcidBloodSplashesTheLineThroughTheAttackerAndOnRetaliation, AcidBloodIsAQuarterOfTheHydrasDamageTruncated |
| `ABILITY_LIGHTNING_BREATH` | FIXED | 00a62030 (Lightning branch), 00b3d560, 00a673c0 (00a67b2c–00a67ba5) | targets: the main target plus the enemies next to it (FUN_00b3d560, FUN_004e5750 drops the attacker's own); outside retaliations one base roll is divided by the number of targets (main included) and each target is struck with that share; a retaliation strikes every target with its own full roll — engine gave the main target its full damage and the others 1/(n+1) of theirs | AuditAbilityTestsD.LightningBreathSharesOneRollAmongAllItsTargets |

## Strike helpers and passives

FUN_00a64120 runs after every melee strike (FUN_00a64830, inside the strike loop and before that strike's retaliation)
and after every melee retaliation strike (FUN_00a646b0), when the struck target is alive: Lizard Bite unless the strike
was a retaliation, then Pack Hunter if the striker has PACK_HUNTER. Both go through FUN_00a63e90(ability): every stack
of the striker's side with the ability, not on the target's side, adjacent to the target (FUN_00b3d3f0), neither the
striker nor the target, and not disabled (FUN_004e4a20: Blind 0x13, Frozen 0xc9, Sabotage 0x14a, Sleeping Strike 0x150,
Seduce 0x148) makes one full strike (FUN_00a673c0 with its no-luck flag: luck ×1, pattern and on-hit effects as usual,
no retaliation, no further helpers). Lizard Bite strikes carry special 0xb5 (SPELL_LIZARD_BITE_HIT: ×0.5, Riders
specialisation ×(1.2 + 0.01·level), FUN_00a58de0); Pack Hunter strikes have none. Not compared: the exe runs the
Cowardice reaction (FUN_00a629b0) before the helpers, the engine after them.

| ID | Status | Exe | Original behaviour / note | Test |
|---|---|---|---|---|
| `ABILITY_LIZARD_BITE` | FIXED | 00a64120, 00a63e90 (00a63f18–00a63f35: special 0xb5), 004e4a20, 00a58de0 | as above — engine: once after the whole attack (not per strike), luck rolled, any helper state allowed, an ability strike instead of a melee strike | AuditAbilityTestsD.LizardBiteJoinsAnyAllysStrikeWithoutLuckUnlessDisabled |
| `ABILITY_PACK_HUNTER` | FIXED | 00a64120, 00a63e90, 00a646b0 | as above: after every strike AND after the wolves' retaliations, any stack with PACK_HUNTER (not only the same creature), no luck; skip only the FUN_004e4a20 states — engine: once after the attack, never after a retaliation, same creature type only, skipped stunned (not frozen/sabotaged/seduced) helpers | AuditAbilityTestsD.PackHunterAlsoFollowsARetaliation |
| `ABILITY_TREACHERY` | FIXED | 008a0b60 (vtable 00e4fe9c slot 0x68) | once (flag +0x80): count / reference (initial count, ≥ 1) &lt; threshold, threshold single precision = (float)(Treachery × 0.01) − level × GoblinKing_TreacheryBonusPerLevel when the stack has SPELL_SPEC_GOBLIN_KING (0x13b, +0x20 = level); the ratio is compared in extended precision with the float threshold, so exactly 30 % (0.3 &lt; 0.300000012f) defects; the traitors lose Hypnotize (0x14) and Seduce (0x148). Engine: double threshold (30 % stayed), effects kept | AuditAbilityTestsD.GoblinsDefectAtExactlyThirtyPercent |
| `ABILITY_ELDRITCH_AURA` | FIXED | 008a4b70 (flag 0x1000, 008a4ee8 / 008a4f2a), 00b3b330, 00a62030 | flag 0x1000 when an adjacent stack of EITHER side (the enemy and the ally neighbour lists, not the stack itself; the text says "friendly") has the aura; a MAGIC_ATTACK shooter cannot shoot a flagged stack at all (FUN_00b3b330 returns 0 → not a legal target) and its magic line skips flagged stacks — engine: only allied neighbours and the mage itself, and the shot was taken but missed | AuditAbilityTestsD.EldritchAuraBlocksMagiShotsAtStacksBesideAnyCombatMage |
| `ABILITY_AURA_OF_BRAVERY` | MATCH | 008a4b70 (flag 0x10), 00a6acd0 | flag 0x10 from the stack itself or an adjacent ally; morale = max(m, AuraOfBraveryMoraleMin = 1) and BRAVERY's max(m, 1) only without the flag — with the stock value 1 the same as the engine's two max() |  |
| `ABILITY_AURA_OF_FIRE_VULNERABILITY` | GAP | 008a4b70 (flag 0x40), 00977a80 | flags MATCH: the stack itself or any adjacent stack of either side with the aura. Multiplier: FUN_00977a80 multiplies the spell damage before protection by clamp(VulnerabilityDamageCoef, 0, 1000) (1.5) with FPU rounding, once even with several flags, for the spell's element (Fire 2 / Water 3 / Air 1 / Earth 4); the engine folds a fixed 1.5 into the protection multiplier (equal up to rounding; the spell damage path is the spells audit's) |  |
| `ABILITY_AURA_OF_ICE_VULNERABILITY` | GAP | 008a4b70 (flag 0x80), 00977a80 | as fire (Water element) |  |
| `ABILITY_AURA_OF_LIGHTNING_VULNERABILITY` | GAP | 008a4b70 (flag 0x100), 00977a80 | as fire (Air element) |  |
| `ABILITY_SLEEPING_STRIKE` | FIXED | 00a629b0, 00a5e4f0 (00a5e518 PUSH 0xa), 00a5ddb0, 00982010 | the proc (SleepingStrike_ProcFactor) is rolled first; then immune are UNDEAD (ability 0xa — the part-A row read the decompiler's decimal "10" as 0x10 IMMUNITY_TO_BLIND), MECHANICAL and ELEMENTAL, and the effect passes the spell application check (Immunity to Magic, war machines, … = ExeSpellImmune); Immunity to Blind does not protect — engine: Blind-immune and mechanical/elemental immune, undead not, checked before the roll | AuditAbilityTests.SleepingStrikeImmunityIsUndeadMechanicalElementalNotBlindImmunity |
| `ABILITY_BLINDING_ATTACK` | FIXED | 00a629b0, 00a5e440, 00a5ddb0, 00bbf000 → 00982010 / 0097c9f0 | the proc is rolled first on the strike (no melee or "living" test); the Blind then passes the spell application check, whose table gives SPELL_BLIND to the derived Immunity to Blind (undead, mechanical, elemental or Immunity to Magic in the stack's own list, HasAbilityExe) — engine: no roll against non-living targets (Vampirism included) and only the explicit IMMUNITY_TO_BLIND | AuditAbilityTestsD.BlindingAttackUsesTheDerivedBlindImmunity |
