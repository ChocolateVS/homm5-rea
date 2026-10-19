# Combat audit — hero skills, part H (might/magic leftovers, week of the month, battle end)

Rows here override the computed status in `docs/rules/AUDIT_COMBAT.md` (merged by `tools/re/audit/ledger.py`; this file
sorts after `combat_heroes_skills_g.md`, so its rows win). Exe references are H5_Game.exe (GOG 3.1) addresses, analysed
headless with Ghidra 12.1.4 (`tools/re/audit_skills_h/ghidra.sh`, private project `tools/ghidra-projects/audit_skills_h`).

## Week of the month in combat

The "battle-context id" of the earlier passes is the **week of the month**: battle vcall +0x3c returns the MoonWeek enum
value (types.xml `MoonWeek`, `GameMechanics/RefTables/MoonWeeks.xdb`; e.g. 0x49 = 73 WEEK_OF_MIGHT, 0x4b = 75
WEEK_OF_MIGHTNMAGIC — "Non-magic damage increased by 50 % during battles"). The helpers sit at 00bcdaa0..00bce190; the
creature-race group is FUN_0045a0e0 (town 3/4/5/9 Haven/Sylvan/Academy/Fortress → 1, 6/7/8/10 Dungeon/Necropolis/
Inferno/Stronghold → 2, else 0). The engine had no week in combat at all; `BattleState.Week` is now filled by the
adventure (`AdvState.MoonWeek`), duels fight in WEEK_OF_NOTHING. Code: `src/H5.Combat/Battle.WeekAudit.cs`.

| ID | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| `WEEK_OF_MIGHT` | FIXED | 00bcdf10 ← 004bbfb0, 004bc020, 004bc710, 004bc780, 00bc1b90 (00bc1c78), 00bfdfb0 | x + x/2 (integer) on the creature record's min/max damage (before Frenzy / artifacts), on war machine min/max damage, on the hero strike right after its base (before the favoured-enemy doubling and Offender) and on tower damage (towns only: the no-town 50 returns before it). Was missing | AuditWeekTests.WeekChangesTheCreatureBaseStats, AuditWeekTests.MightWeekRaisesTheHeroStrike |
| `WEEK_OF_MIGHTNMAGIC` | FIXED | 00bcdf10, 00bcddf0 | both the Might ×1.5 above and the Magic spell cost / 2 | AuditWeekTests.WeekChangesTheCreatureBaseStats |
| `WEEK_OF_TOUGHNESS` | FIXED | 00bcdf30 ← 004bc470 | creature record hit points + hp/5 (integer) before Toughness / Ring of Life / specialisations. Was missing | AuditWeekTests.WeekChangesTheCreatureBaseStats |
| `WEEK_OF_FEEBLENESS` | FIXED | 00bcdd80 ← 004bb2a0 (flag 1) | group 1 creatures: record defence → trunc(def · 0.8f + 0.5). The attack getter 004bb170 calls the same helper with flag 0, so attack is never changed. Was missing | AuditWeekTests.WeekChangesTheCreatureBaseStats |
| `WEEK_OF_INFIRMITY` | FIXED | 00bcdd80 ← 004bb2a0 | the same for group 2 | AuditWeekTests.WeekChangesTheCreatureBaseStats |
| `WEEK_OF_IDLENESS` | FIXED | 00bcdd40 ← 004bc190 | group 1: record initiative (as float) × 0.8 (constant 00e1857c) before the flat and artifact bonuses. Was missing | AuditWeekTests.WeekChangesTheCreatureBaseStats |
| `WEEK_OF_CALM` | FIXED | 00bcdd40 ← 004bc190 | the same for group 2 | AuditWeekTests.WeekChangesTheCreatureBaseStats |
| `WEEK_OF_SORROW` | FIXED | 00bcdc90 ← 00bcdcf0 (luck 004ba970), 00bcdd00 (morale 004ba9d0, 004bae10 Artificial Glory, 004bd7b0) | group 1: base morale −2 and base luck −2 (before the effect terms 00989fd0 / 00989c40). Was missing | AuditWeekTests.WeekChangesMoraleAndLuck |
| `WEEK_OF_LIGHT` | FIXED | 00bcdc90 | group 2: base morale and luck −2 | AuditWeekTests.WeekChangesMoraleAndLuck |
| `WEEK_OF_HOPE` | FIXED | 00bcdc90 | every creature: base morale and luck +1 (neutrals too: no group test) | AuditWeekTests.WeekChangesMoraleAndLuck |
| `WEEK_OF_BALANCE` | FIXED | 00bcdc90, 00bcdd20 ← 00a6abf0, 00bcdd30 ← 00a6acd0 | base morale and luck become 0 and the stack's morale/luck skip the effect sums (00989fd0: spell effects, auras flags 4/0x20/0x2000, enemy artifacts 0x17/0x21; 00989c40: luck effects incl. Dead Luck); the minimum-morale rules (Bravery, Aura of Bravery) still apply | AuditWeekTests.WeekChangesMoraleAndLuck |
| `WEEK_OF_MAGIC` | FIXED | 00bcddf0 ← 00a34740 (00a347b9) | spell cost / 2 (integer) right after the base cost (×2 for empowered), before every discount, warcries included; creature casters reach the same function through 009781e0 (hero = null). Was missing | AuditWeekTests.WeekChangesSpellCostMasteryAndDamage |
| `WEEK_OF_ETHER` | FIXED | 00bcddf0 | spell cost × 2 at the same place | AuditWeekTests.WeekChangesSpellCostMasteryAndDamage |
| `WEEK_OF_EVOCATION` | FIXED | 00bcde10 ← 00a6a700 | the caster's mastery for a Destructive spell (school 0), if above none, becomes 3 (Expert) — applied after the caster's own mastery (so after Shatter); heroes and creature casters. Was missing | AuditWeekTests.WeekChangesSpellCostMasteryAndDamage |
| `WEEK_OF_ABJURATION` | FIXED | 00bcde10 | the same for Light (school 2) | AuditWeekTests.WeekChangesSpellCostMasteryAndDamage |
| `WEEK_OF_ALTERATION` | FIXED | 00bcde10 | the same for Dark (school 1) | (same helper) |
| `WEEK_OF_CONJURATION` | FIXED | 00bcde10 | the same for Summoning (school 3) | (same helper) |
| `WEEK_OF_WATER` | FIXED | 00bcde80 ← 00976b90 (00976c73), 00bce190 ← 00861630 | spell base damage x + x/2 for water spells (after the empowered ×1.5, before the element artifacts 00976db0); Summon Elementals: stack size n + n/2 when the summoned creature's element (record +0xa0) is water. Was missing | AuditWeekTests.WeekChangesSpellCostMasteryAndDamage |
| `WEEK_OF_FIRE` | FIXED | 00bcde80, 00bce190 | the same for fire | AuditWeekTests.WeekChangesSpellCostMasteryAndDamage |
| `WEEK_OF_EARTH` | FIXED | 00bcde80, 00bce190 | the same for earth | (same helper) |
| `WEEK_OF_AIR` | FIXED | 00bcde80, 00bce190 | the same for air | AuditWeekTests.WeekChangesSpellCostMasteryAndDamage |
| `WEEK_OF_CHAOS` | FIXED | 00bcde80 | every damage spell x + x/2 | AuditWeekTests.WeekChangesSpellCostMasteryAndDamage |
| `WEEK_OF_FIRENICE` | FIXED | 00bcde80 | × 2 for EVERY damage spell: the exe does not test the element for this week (its text says fire and ice) | AuditWeekTests.WeekChangesSpellCostMasteryAndDamage |

Not changed: the Elemental Balance counter-summon (00861630, already a GAP) also takes the week-adjusted count (min 1);
whether the catapult and First Aid Tent consume the week-adjusted war machine min/max of 004bc710/004bc780 is not
traced (only the Ballista is changed).

## Creature initiative with the hero, enemy artifacts

| ID | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| `HERO_SPEC_FURIOUS` | FIXED | 004bc190 (004bc1f0..004bc25b) | creature initiative × (1 + clamp(Furious_InitiativeBonusBase + level·Furious_InitiativeBonusPerLevel, 0, 5)) on the (week-adjusted) record value, before March of the Machines and the artifacts. Was missing | AuditSkillTestsH.FuriousSpecialisationsScaleCreatureInitiative |
| `HERO_SPEC_MASTER_OF_INITIATIVE` | FIXED | 004bc190 (HasSpec 0x45, same branch) | the same factor as Furious (own army); the engine's 1 + 0.01·level factor in InitiativeOf (taken from the description) is removed. Its enemy Prudent part in 008a4330 is unchanged | AuditSkillTestsH.FuriousSpecialisationsScaleCreatureInitiative |
| `RING_OF_HASTE` | FIXED | 004bc190 (004bc2c4) | army creatures' initiative +20 % (any number of rings), inside the artifact percent p: init = (p + 100)·init·0.01 with p ≥ −100. Was missing | AuditSkillTestsH.ArmyArtifactsScaleCreatureInitiative |
| `ARTIFACT_RING_OF_CELERITY` | FIXED | 004bc190 (004bc2d8) | +10 % per ring | (same helper) |
| `DRAGON_SCALE_ARMOR` | FIXED | 004bc190 (004bc2ef) | +5 % each for creatures with combat size > 1 (record +0xdc) | AuditSkillTestsH.ArmyArtifactsScaleCreatureInitiative |
| `DRAGON_SCALE_SHIELD` | FIXED | 004bc190 (004bc309) | +5 % each for size-1 creatures | AuditSkillTestsH.ArmyArtifactsScaleCreatureInitiative |
| `DRAGON_BONE_GRAVES` | FIXED | 004bc190 (004bc31e..004bc35d) | +10 % each for creatures without shots, not ABILITY_CASTER / ABILITY_RANDOM_CASTER, not flying | AuditSkillTestsH.ArmyArtifactsScaleCreatureInitiative |
| `DRAGON_WING_MANTLE` | FIXED | 004bc190 (004bc363) | +10 % each for flyers | AuditSkillTestsH.ArmyArtifactsScaleCreatureInitiative |
| `DRAGON_TEETH_NECKLACE` | FIXED | 004bc190 (004bc380) | +10 % each for ABILITY_CASTER / ABILITY_RANDOM_CASTER creatures | (same helper) |
| `DRAGON_EYE_RING` | FIXED | 004bc190 (004bc3b1) | +10 % each for shooters (record shots > 0) | AuditSkillTestsH.ArmyArtifactsScaleCreatureInitiative |
| `ARTIFACT_OGRE_CLUB` | FIXED | 004bc190 (004bc3ce) | −5 % each | AuditSkillTestsH.ArmyArtifactsScaleCreatureInitiative |
| `ARTIFACT_OGRE_SHIELD` | FIXED | 004bc190 (004bc3e3) | −5 % each | AuditSkillTestsH.ArmyArtifactsScaleCreatureInitiative |
| `NIGHTMARISH_RING` | FIXED | 00989fd0 (tail) | −2 morale per ring to the stacks facing the hero (army vcall 0x10 = enemy army's hero). Was missing | AuditSkillTestsH.EnemyArtifactsLowerMoraleAndLuck |
| `CLOAK_OF_MOURNING` | FIXED | 00989fd0, 00989c40 (tails) | −2 morale and −2 luck per cloak to the enemy stacks. Was missing | AuditSkillTestsH.EnemyArtifactsLowerMoraleAndLuck |
| `ARTIFACT_JINXING_BAND` | FIXED | 00989c40 (tail) | −2 luck per band to the enemy stacks. Was missing | AuditSkillTestsH.EnemyArtifactsLowerMoraleAndLuck |

Also in 004bc190: Hero March of the Machines adds 2.0 to the float (Iron/Steel/Obsidian Golems 61/62/161) and the result
is at least 1 before the artifact percent; afterwards a per-creature bonus byte (army slot +0x2c, ≤ 50 %) multiplies —
not modelled (no source in shipped content found).

**HP percentage multiplier (was a GAP in `combat_heroes_specs.md`)**: 004bc470 ends with (hp·clamp(100 + b, 0, 1000) + 50)
/ 100 where b = hero vcall 0x330 (= hero + 0x270, `FUN_00b5ac30`) field +0x10. The struct is the script battle-bonus
array of `GiveHeroBattleBonus` (advmap-startup.lua: HERO_BATTLE_BONUS_LUCK 0, MORALE 1, ATTACK 2, DEFENCE 3,
HITPOINTS 4 (+0x10, percent), INITIATIVE 5 (+0x14, added to the initiative before the artifact percent), SPEED 6).
GAP: the engine's GiveHeroBattleBonus stores types 2..6 in settings only; when the bonus is cleared and how attack,
defence and speed use it is not traced.
