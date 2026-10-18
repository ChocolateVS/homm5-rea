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
* `FUN_00a5a170` ("combat ability roll"): success iff `Range(0,100) < chance` (inclusive, 101 outcomes; RNG slot 0x0c called with 0, 100); chance ≥ 100 always,
  ≤ 0 never. If it fails and the call names an attacker whose hero has HERO_SKILL_LUCKY_STRIKE
  (Soldier's Luck) a second roll is made — the luck roll passes no attacker, so Soldier's Luck
  never affects luck.
* `FUN_008a3ef0`: Absolute Luck (attacker) ⇒ always good luck; Absolute Protection (defender) ⇒
  roll normally, good luck becomes normal and anything else becomes **bad** luck; both ⇒ normal roll.
* Multipliers: `LuckAndMorale/GoodLuck_DamageMultiplier` (or `ElvenLuck_DamageMultiplier` with
  Elven Luck) and `BadLuck_DamageMultiplier`; morale sets ATB to `GoodMorale_ATBValue` /
  `BadMorale_ATBValue` (0x00a6cf90 / 0x00a6cd80).

* `FUN_00a59990` (ability trigger chance, whole percent): A/T = attacker/target total HP; base = min(75, 25 + ⌊3A/T⌋)
  if A > T else max(5, 25 − ⌊3T/A⌋) (integer division); the ProcFactor is an exponent on the failure chance:
  chance = 100 − ⌊100·(0.01·(100 − base))^factor + 0.5⌋. A first argument of 1 (Bash, Brutality, Crushing Blow,
  Flamewave, Taunt, Paw Strike, Bear Roar) doubles the factor when the hero has Soldier's Luck; those callers mostly pass
  no attacker to the roll (no second chance). Paw Strike (`FUN_00a5d1b0`) compounds the failure over every tile walked.
  See docs/rules/audit/combat_abilities.md.

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

## Battle RNG (resolved)

Nival's combat RNG (vtable methods at 0x008c0470 ff.): state `s`; each draw steps the MSVC LCG
twice (`s1 = s·0x343fd + 0x269ec3`, `s2 = s1·0x343fd + 0x269ec3`, store `s2`) and returns
`r = (s1 >> 7) ^ (s2 << 6)`. `+0x10 Next(n) = r mod n`; `+0x0c Range(min,max) = min + Next(max−min+1)`
(**inclusive**); float `(uint)r · (max−min) · 2⁻³² + min`. Consequence: the percent roll
`Range(0,100) < chance` has 101 outcomes (a nominal 10 % is 10/101). H5Reborn keeps its own
xoshiro generator (bit-exact sequences cannot match anyway) but reproduces these ranges.

## Combat obstacles (resolved, `src/H5.Combat/Obstacles.cs`)

`FUN_004b3860` (per battle) → parameters `FUN_004b33a0`/`FUN_004b3020` → up to 10 attempts of
placement `FUN_004b3650`/`FUN_004b34a0` + validation `FUN_004b2850`; objects created by `FUN_004b2e10`.
Works on the internal grid W×H = (12+4)×(10+2):
* Arena scene `ObstaclesGroup` with `FixedObstacles = true` (boat, abyss): listed tiles are absolute
  internal cells. No group (sieges, special arenas) ⇒ no obstacles.
* Free lines are **rows**: `n = min(FreeLines, H−4)` rows from `start = Range(1, H−n−1)`.
* `mode = Next(3)` → small (≤ 2 tiles) / big (> 2) / mixed candidates; `count = Range(Min, Max)` of the
  mode's DefaultStats pair, clamped to [0,100]. If no candidate matches, all are used (and a
  MinSmall/MaxSmall count is rolled and discarded).
* **`ObstaclesMaxArea` is never read**; the area limit is `(W−4)·(H−2)/4` = 30.
* Per obstacle: candidates are drawn with `Next(nValid)` (index 0 if only one) until one fits the
  remaining area (non-fitting ones are dropped; an empty pool ends placement). Rotation
  `Range(0,4)` quarter turns (k = 4 is a full turn). Anchors x ∈ [4, W−5], y ∈ [1, H−2]; every tile
  must stay in that zone, off free rows and off occupied tiles (`FUN_004b27b0`); one anchor by
  `Range(0, n−1)`. The area is spent even if no anchor fits.
* Validation: Dijkstra (8-way, cost 2/3) of a 2×2 creature from internal (3,2); every free zone cell
  must be covered by a reachable footprint, otherwise retry; after 10 failures the field is empty.

## Spell damage (resolved)

* `FUN_00a34ca0` / `FUN_00a34cd0` → `FUN_00a34c40`: a spell's damage / duration value is
  `Base + PerPower·SP` from the xdb table for the mastery, index clamped to the table length.
* `FUN_00976b90` (damaging spells: Magic Arrow … Implosion, Plague, Holy/Unholy Word, Land Mine, Wasp Swarm,
  Firewall, Stone Spikes, Deep Freeze, Divine Vengeance, Explosion, uber spells): base damage
  `round(max(1, value))`, then `round(×1.5)` for an empowered variant (`FUN_00a34100` = "is empowered",
  `FUN_00a34110` = base spell id). Call Storm = `CallStorm_DamagePerCreature × count`. Ported as
  `Battle.SpellBaseDamage`.
* Empowered spells (`SPELL_EMPOWERED_*`, mapped by `FUN_00973890`) have doubled TrainedCost in their xdbs
  and placeholder damage tables; the ×1.5 comes from the code above.
* Earthquake (spell 0x29, `FUN_0097d7b0`): wall damage in multiples of 50 (value / 50).

## Abilities and perks (resolved or data-backed)

* Treachery `FUN_008a0b60`: changes sides once when count/initial < `Treachery`/100 − Goblin King
  `0.01·level` (effect SPELL_SPEC_GOBLIN_KING); confirmed by the fan manual (level 20 ⇒ 10 %).
* Taunt `FUN_00a65d50`: every other TAUNT stack adjacent to the attacker rolls the trigger chance with
  `Taunt_ProcFactor`; the last success becomes the target. Not for Battle Dive / Pack Dive finishing strikes.
* Pack Hunter / Lizard Bite share the follow-up helper `FUN_00a63e90` (called from `FUN_00a64120`).
* Howl `FUN_00a5cbc0`: summoned stack's ATB = rand(0, `HowlMaxATBValueAtStart`).
* Auras are recomputed per unit by `FUN_008a4b70` (flags: fright aura, presence of commander 0x400,
  searing aura 0x800, Eldritch Aura 0x1000 from neighbours of either side, bravery, element vulnerabilities,
  weakening aura, shield other, magic resistance).
* Battle Dive / Pack Dive: activation spells 0x9a / 0x15a, finishing strikes 0x9b (×2 in `FUN_00a58de0`)
  and 0x15b (×`PackDive_DamageMultiplier`), delay `PackDive_DelayBeforAttack`.
* Magic Mirror `FUN_00983150`: chance `round(level·MagicMirrorLevelMultiplier + 10)` %; Spell Twister
  (HERO_SPEC_RADIANT) second roll `round((Radiant_MirrorProb + Radiant_MirrorProbPerLevel·level)·100)` %
  restricts the new target to the caster's side, otherwise any random stack.
* Resurrect Allies `FUN_009789b0` = 100 HP × Archangels; Gremlin Repair = count × min(power, 1000).
* Data/description-based (exe call sites located but not fully decompiled): Set Snares and Defile Magic
  (fan-manual outcome tables), Treant Union (TreeanUnionTargetHits/CasterHits), Power Feed, Bond of Light,
  Flamewave, Magnetism, Seduction (2 + log2(power ratio)), Counterspell, Mark of the Wizard, Imbue Arrow /
  Ballista, Elemental Vision (ElementalChainBonusPerInvocationTrained per mastery), Banish
  (Unsummon_DamagePerHeroLevel / PerSkillMastery), Remote Control, Plague Tent, Matron Salvo (10 % + 2 %/level).

## Placement (combat screen request)

* Not found in data, Lua or by string search; the original computes hero / war machine positions in code
  that was not located. H5Reborn keeps war machines just outside the grid behind their army (column −1 for
  the attacker, 12 for the defender; Ballista row 1, First Aid Tent row 8, others row 5) and heroes off-grid
  behind their side. The arena workstream's finding that the internal grid has a 2-column margin
  (internal x 0..1 and 14..15) is consistent with machines standing in that margin.

## ATB timing (resolved)

* `FUN_00a2d6c0(initiative)` = `ATBBaseTime / max(initiative, ε)` with `ATBBaseTime` (RPGStats+0x50, data 100000,
  default 100000 when ≤ 0): the time an empty gauge takes to fill. `FUN_00a2d700` does the same with
  `SpellInitiative` (+0x48). The ATB is therefore continuous floating-point time, which H5Reborn models directly.
* The `ATBMin` / `ATBMax` / `*_RealTime` / `ATBVisualTime` fields belong to a settings struct whose reader
  (`FUN_0094cd80`) and metadata (`FUN_0094cb90`) have no callers and no vtable pointers: dead code.
  `ATBEndTime` (+0x4c) has no reader reached through the RPGStats getter either.

## Creature ability charges (resolved: Lay Hands)

Creature records (`FUN_00ad90c0`) hold `SpellPoints` @+0x74 (mana), `SpellPoints1` @+0x78 and `SpellPoints2` @+0x7c.
In the data `SpellPoints1` is the per-combat charge count of activated abilities (Archangel Resurrect Allies 1,
Master Gremlin Repair 1, Genies' random casts 3, Stormlords/Thunder Thanes 1, …) and `SpellPoints2 = 1` on every
Inferno creature (Gating once per combat). The Paladin has 0 / 0 and Lay Hands (`SPELL_ABILITY_LAY_HANDS`, 0x9c)
costs no mana; its in-game description (unlike every limited ability's) states no limit. Lay Hands is therefore
usable every turn; the former once-per-combat restriction was removed.

## Consume Artifact (data-backed)

`HERO_SKILL_MELT_ARTIFACT` (0x44, spell `SPELL_ABILITY_MELT_ARTIFACT` 0x40). DefaultStats Wizard section:
`MeltArtifact_HealPerArtifactEffectPerHeroLevel` = 10 (RPGStats+0x684) and `MeltArtifact_ATBUsage` = 0.25 (+0x688).
No direct reader through the RPGStats getter was found (accessed through a cached sub-struct pointer); the values
match the fan manual's formula (p.137): heal (and resurrect) 10 × level × (1 + crafted effects), 0.25 turn.

## Sieges (resolved unless noted)

* **Data.** `AdvMapTownShared/Combat` (reader `FUN_009fd430`: walls, ArenaDesc, ArenaMap, GroundType) lists
  `ArenaBuilding`s (reader `FUN_00930e70`: Pos, Size, Object, Locator, Shooter, Shot, Type, HitEffect, HitSound,
  DestroyEffects{Upgrade, RuinLevel}, blockedTiles, blockedTilesForDestroyed). Type enum (`FUN_0092f3e0`):
  0 BUILDING_ANY, 1 WALL, 2 GATE, 3 LEFT_TOWER, 4 BIG_TOWER, 5 MOAT, 6 RIGHT_TOWER, 7 MAGIC_WALL. Hit points:
  `ArenaBuildingsStats` BaseHealth + HealthFortBonuses[fort level] (`FUN_0092faa0`), matching the manual's table.
* **Grid.** Building tiles span internal x 0..17, y 0..15: the siege field is 14×14 with the usual 2-column /
  1-row margin (field cell = internal − (2, 1)), consistent with the obstacle code's battle types 1–3
  (`FUN_004b33a0`) marking extra free rows.
* **Arena building methods** (secondary vtable 0x00e8832c): `+0x0` type, `+0x4` set gate open flag (gate only),
  `+0x10 IsTargetable` (`FUN_00bfc9d0`: gate while closed, side towers while their shooter exists, never the moat,
  always walls / central tower), `+0xc` tower damage (`FUN_00bfdfb0`).
* **Tower damage** `FUN_00bfdfb0`: 0 for non-towers; 50 if the battle has no town; base 70 (BIG_TOWER) or 50
  (side towers); if the tower has `SPELL_TOWN_TOWERS` (town specialisation) base = trunc(base × 1.25); damage =
  trunc(town level ratio (town vcall +0x74, float) × base); then `FUN_00bcdf10` adds half (×1.5) when a battle
  value (vcall +0x3c) is 0x49 or 0x4b — **unidentified**, not applied.
* **Catapult** `FUN_00b3b7d0`: chance 20 without hero, else (WarMachines mastery + 2) × 10, plus
  `((level + 2) / 3) × 2` with HERO_SPEC_WAR_MACHINES (0x08, the only reference to that spec). Unless forced,
  `Range(1,100) > chance` retargets to a random entry of `FUN_00b3b560`'s list (targetable buildings including
  destroyed ones, excluding the aimed one; BIG_TOWER only when no side tower stands). Luck is rolled (vcall
  +0x1d0) and multiplies the damage roll.
* **Wall penalty** `FUN_00a5c5a0`: for ranged attacks, if the shooter is not inside (`FUN_00b3de90`) and the target
  is, and the fortification list between them (`FUN_00b3e920`, all buildings except MAGIC_WALL → `FUN_00b3e720`)
  is non-empty, damage ×0.5 (0x00e0ab70). `FUN_00b3de90` only applies on siege fields to rows strictly inside the
  vertical margins and builds its test from buildings other than BIG_TOWER and MOAT; Ghidra removed part of the
  geometry, so H5Reborn implements "east of the fortification line of that row" and a segment test against intact
  solid buildings (the moat is not solid).
* **Earthquake** (`FUN_0097d7b0`, case 0x29): min = trunc(round(duration value) / 50), max = trunc(round(damage
  value) / 50), each standing building takes `Range(min, max) × 50` (data: 0–100 / 0–200 / 50–300 / 100–400).
  Tremors (HERO_SKILL_SHAKE_GROUND 0x8b): damage `round(SP × ShakeGroundDamagePerPower + ShakeGroundDamage)` and
  ATB −`ShakeGroundATBReduce` to every unit inside the town (`FUN_00b3de90`).
* **Moat** values and spells are data (`TownTypesInfo`); the stop-on-entry rule and the per-cell single use of
  Academy mines follow the manual (p.308–309) — the moat trigger code was not located.
* **Not located:** tower initiative and targeting (Initiative 10 and the automatic Ballista's target rule are used),
  automatic catapult target choice (random valid target), hero / war machine cells.

## Still open

* Exact hero / war machine cells (see Placement); the tower ×1.5 condition (`00bcdf10`); moat trigger code;
  tower initiative / targeting; automatic catapult targeting.

## Deployment axis and combat views (resolved)

Question: do the armies face each other along the 12-cell axis (engine) or across the 10-cell depth?
**Verdict: along the 12-cell axis — the engine is right**; what differs by phase is the camera.

* **Exe — obstacle generator** (`FUN_004b3860` family, see "Combat obstacles"): on the 16×12 internal grid random
  obstacle anchors are confined to x ∈ [4, W−5] (field columns 2..9) while y spans every row, so the two outer
  columns at each end of the 12-cell axis are always free: those are the deployment zones (2 columns, 3 with
  Tactics). Fixed layouts agree: Boat_Arena blocks the middle columns x 6..9 (water between two armies).
* **Stock manual text** (Game Manual 3.0.pdf p.14, "Combat Screen"): "the attacking army is displayed on the left,
  as well as your hero and any war machines". The mid-battle screenshot (p.14, second image) fits a 12-cell near
  edge with the eye 24.9 cells (= `Rod` 50 of `Cameras/CombatArena/CombatArena.xdb` at 2 units per cell) from the
  target, pitch 46.4°, vertical FOV ~31–35°.
* **Stock manual tactics screenshot** (p.14, first image): the deploying army's band is *across* the near edge and
  is 10 cells wide (cell outlines 19.5 px apart across a 193 px edge in the 271 px image), i.e. the tactics view
  looks along the 12-cell axis from behind the deploying army. A free fit gives vertical FOV 35.6°, matching the
  combat screen object's default FOV of 35.0 (constructor `FUN_0054ba40` stores 35.0 / 1.0 / 1000.0 as FOV / clip
  near / clip far); with FOV 35 the fit leaves 1.2 px residual: pitch 43.4°, eye 20.2 cells from a target on the
  centre line 5.0 cells in from the deploying army's edge. The manual also says the enemy army is not visible
  during tactics.
* The combat camera object in the exe is an orbit camera (fields Rod, Pitch, two angles, FOV, clip, `Anchor` —
  readers `FUN_009fea20`/`FUN_009feb80`/`FUN_009fedd0`/`FUN_009fef60`); the conversion from the camera xdb's
  `Pos`/`Rot` (`(-5.68,-5.09,9.81)` / `(-30,-66.8,0)`, a Maya-style rotation) was not traced, so the client uses
  the screenshot fits. Console command `combat_camera_limits` (`FUN_00543e50` → `FUN_00543430`) toggles between
  the free (`+0x3b8`, `FUN_007d6ee0`) and limited (`+0x3bc`, `FUN_007d5ec0`) camera controllers.
* Sieges: walls x 7–9, central tower x 13 (field cells) ⇒ the town is on the defender's (right, high-x) side.
* Duel arenas: duels are fought on Maps/DuelMode/PresetMap (grass), so the arena comes from the grass entry of
  `GameMechanics/CombatArenaTypes/CombatArenaTypes.xdb` (average: Grass_Big_01, Grass_Big_02, Grass_Small_03),
  loaded as `GameDatabase.ArenaTable` (`BattleSetup.PickArena(db, rng, terrain)`).

Tests: `tests/H5.Combat.Tests/DeploymentAxisTests.cs`.
