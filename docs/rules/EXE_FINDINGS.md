# Combat rules confirmed from H5_Game.exe

Source: `bin/H5_Game.exe` of the GOG *Tribes of the East* 3.1 install (13,089,792 bytes), analysed
headlessly with Ghidra 12.1.4 (`tools/re/`). Addresses are virtual addresses in that file. The
game was never run or modified.

## Method / tooling (`tools/re/`)

* `ghidra.sh <Script.java> args…` runs a script read-only against the analysed project.
* `StringXrefs`, `Decompile`, `Disasm`, `Callers`, `FindDisp` (memory-operand displacements),
  `FindVCall` (virtual calls by slot + pushed immediate), `FindImm`, `RefsInRange`, `DumpEnum`
  (enum→name switch tables), `StructMap` (recursive XML-reader walk → absolute field offsets).
* `annotate.py` rewrites decompiler output with enum names (`HasAbility(ABILITY_…)`, spell
  effects, hero skills, specialisations) and RPGStats field paths; `peread.py` reads constants.
* Generated maps (git-ignored, regenerate with the scripts): `out/enum_{ability,spell,skill,spec}.txt`,
  `out/rpg_combat.txt` (509 fields of `RPGStats.combat`).

### Key layout facts

* `FUN_00bcb3f0()` returns the `RPGStats` singleton (DefaultStats.xdb). Root reader at 0x00960960:
  `HeroInitiative`@0x44, `SpellInitiative`@0x48, `ATBEndTime`@0x4c, `ATBBaseTime`@0x50,
  `adventure`@0x58, `combat`@0x1cc. Inside `combat` (reader 0x00960760): `DamageIncrease`@+4,
  `DamageIncreaseCap`@+8, `DamageDecrease`@+0xc, `DamageDecreaseCap`@+0x10, `Creatures`@+0x30,
  `HeroSkills`@+0x37c, `Obstacles`@+0x610, `HeroSpecializations`@+0x634, `Spells`@+0x7e0,
  `LuckAndMorale`@+0x8fc.
* Creature virtual methods: `+0x28c HasAbility(ABILITY_*)`, `+0x28 GetEffect(SPELL_*)`,
  `+0x1d8 Count`, `+0x1ec/+0x1f0 Min/MaxDamage`, `+0x198 Attack`, `+0x19c Defence`, `+0x44 Size`,
  `+0x1d0 RollLuck` (via vtable 0x00e4fba4). Hero: `+0x290 HasSkill(HERO_SKILL_*)`,
  `+0x294 HasSpecialization(HERO_SPEC_*)`, `+0x23c Level`, `+0x174(skill) SkillMastery`.
* Rounding: float → int conversions use `_ftol` (0x00d11a60, truncation toward zero) or `FISTP`
  with the control word forced to truncate (`OR AH,0xc`).

## Physical damage pipeline (resolved)

Per attack (`FUN_00a673c0`):
1. **Luck is rolled once per attack** (vcall `+0x1d0`, `FUN_008a3ef0`) before the target loop;
   Elves' Double Shot re-rolls for its second shot.
2. For every target of the pattern: the **base roll is repeated per target** (`FUN_00a597a0`),
   except Lightning Breath, which divides one roll among its targets.
3. Avenger favoured-enemy critical (`FUN_00a5ae40`): the luck multiplier L becomes `L+1` if
   `L >= 1`, otherwise `1.0` (so crit + good luck = ×3, crit + bad luck = ×1).
4. Damage = `FUN_00a5c790(...)`, then `trunc(damage × patternShare)` (0x00a67bf3).

Base roll `FUN_00a597a0` (mode 0 = random): `count` from vcall `+0x1d8`, reduced by Confusion
(`round((1-v)·count)`) for ranged attacks and retaliations; Sleeping Strike target ⇒ min = max.
If `min == max` → `count·min`; if `count ≤ 3` → sum of `count` uniform rolls; else
`count · (r1+r2+r3) · 0.33333334f`. Modes 1/2/3 (min/avg/max) feed previews and the AI.

Attacker stage `FUN_00a58de0` (starts from the luck multiplier):
* melee shooter without NO_MELEE_PENALTY ×0.5; ranged with RANGE_PENALTY ×0.5;
* hero Offence mastery bonus (melee) or Archery (ranged), each clamped to [0,10];
* non-retaliation: Retribution `min(morale·0.05, 10)` (morale>0); retaliation: Training by
  mastery (Basic/Advanced/Expert/ExtraExpert fields);
* Jousting (melee, cells>0): `1 + clamp(perCell,0,100)·cells`, multiplied by
  `1 + CavalryCommander_JoustingBonus + PerLevel·level` for HERO_SPEC_JOUSTING;
* special attack ids: Incinerate (spell 0x142) ×IncinerateCoef, Pack Dive ×PackDive_DamageMultiplier,
  Flamestrike ×Flamestrike_DamageMultipiler, 0x9e ×0.5, 0x9b ×2, Leap/Harpoon bonuses;
* retaliation: Battle Frenzy effect stacks ×BoostFactor^n; Fierce Retaliation ×FierceRetaliationCoef.

Range penalty `FUN_00a5c5a0`: unless NO_RANGE_PENALTY, ×0.5 when the **squared Euclidean distance
between the nearest cells** (`FUN_00b3c690` + `00b3c7a0`) exceeds `(width/2)²` (integer halving:
36 on 12×10). Separately, shooting across intact town walls ×0.5.

Defender stage `FUN_00a5a900` (A, D are ints):
1. Piercing Arrow: `D = trunc(D · PiercingArrowCoef)`.
2. Unless the target is **ARMORED**: Precise Shot (`dist² ≤ PreciseShotRange²`) ⇒ D = 0;
   Rider Charge `D = trunc(D · clamp(1 − 0.2·cells, 0, 1))`; Wildfire ballista ⇒ `D /= 2`.
3. Anti-Giant: +AntiGiantAttackInc to the attacker vs size>1, +AntiGiantDefenceInc to the defender
   vs size>1.
4. `AD(A,D)` (`FUN_00a58d30`): `1 + inc·(A−D)` if A>D else `1 / (1 − dec·(A−D))`; inc/dec clamped ≥ 0.
   **DamageIncreaseCap / DamageDecreaseCap are never read** by any combat code (only by the XML
   reader/metadata), so the bonus is uncapped.
5. Deep Freeze ×`max(1, value/100)`.
6. Ranged: Large Shield or shielded-by-ally ×0.5; Blade Barrier ×(1−x). Melee: Shield Wall
   ×(1 − min(clamp(cells·perCell, 0, 1), MaxReduce)).
7. Defender hero: melee ⇒ Defence mastery reduction; ranged ⇒ Evasion, then HERO_SPEC_EVASIVE
   `clamp(base + perLevel·level, 0, 1)` (Defence does not reduce ranged damage).
8. Death to Nonexistent vs summoned; Deflect Arrows ×(1 − clamp(v, 0, 0.95)) (ranged).
9. Forest Guardian (`FUN_00a59340`): Sylvan attacker vs favoured enemy ×(1 + clamp(0.02·level, 0, 10)).
10. Result `= trunc(x + 1e-4)`, minimum 1 (0x00a5ae1b).

## Luck and ability rolls (resolved)

* `FUN_00a6b820`: luck clamped to [−5, 5]; chance = 10·|luck| percent.
* `FUN_00a5a170` ("combat ability roll"): success iff `rand(0..99) < chance`; chance ≥ 100 always,
  ≤ 0 never. If it fails and the call names an attacker whose hero has HERO_SKILL_LUCKY_STRIKE
  (Soldier's Luck) a second roll is made — the luck roll passes no attacker, so Soldier's Luck
  never affects luck.
* `FUN_008a3ef0`: Absolute Luck (attacker) ⇒ always good luck; Absolute Protection (defender) ⇒
  roll normally, good luck becomes normal and anything else becomes **bad** luck; both ⇒ normal roll.
* Multipliers: `LuckAndMorale/GoodLuck_DamageMultiplier` (or `ElvenLuck_DamageMultiplier` with
  Elven Luck) and `BadLuck_DamageMultiplier`; morale sets ATB to `GoodMorale_ATBValue` /
  `BadMorale_ATBValue` (0x00a6cf90 / 0x00a6cd80).

## Retaliation (resolved)

The retaliation counter (creature field at secondary-base −0x80) is decremented by `FUN_008a1120`
unless the creature has UNLIMITED_RETALIATION, and set back to 1 at the creature's own turn start
(`FUN_008aa960`, which first runs the turn-start Take Roots handling `FUN_008a6910`), on Wait
(`FUN_008a1990`) and on Defend (`FUN_008a6be0`).

## Hero skills / specialisations (resolved)

* **Storm Wind** (`FUN_008a1e50`): only enemy **flyers**; speed −= `StormWind_SpeedPenalty` (data: 2,
  the fan manual's "−1" is wrong); results below 2 become 1. Initiative −`StormWind_InitiativePenalty` (flyers).
* **Fog Veil** (`FUN_008a2630`): enemy shooters' min and max damage × (1 − `FogVeil_DamageDecrease`)
  (data 0.25, not the manual's 10%) — applied to the damage range, so it also reduces their melee;
  initiative −`FogVeil_InitiativePenalty`.
* **Cavalry Commander** = HERO_SPEC_JOUSTING (spec enum 3): see Jousting above.
* Other specialisation fields read in the damage path: Evasive, Forest Guardian, Riders (0x32).

## Archangel / healing amounts (resolved)

`FUN_009789b0` (shared by the cast and its description text): Resurrect Allies (spell 0x9d) heals
`count × 100` HP; Gremlin Repair (0xad) `count × min(GremlinRepairPowerPreCreature, 1000)`;
Resurrect (0x30) / Animate Dead (0x2a) use the spell power table, scaled by the hero's army
size ratio when cast through a hero.

## Still open

* Lay Hands charges, First Aid Tent action count, war machine and hero placement, obstacle
  generation, ATB integer timing, the abilities listed in COMBAT.md — in progress.
