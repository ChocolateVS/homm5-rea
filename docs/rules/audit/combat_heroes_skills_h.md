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
| `WEEK_OF_MAGIC` | FIXED | 00bcddf0 ← 00a34740 (00a347b9) | spell cost / 2 (integer) right after the base cost (× 2 for mass (00a33e20) and empowered (00a33f70) spells), before every discount, warcries included; creature casters reach the same function through 009781e0 (hero = null). Was missing | AuditWeekTests.WeekChangesSpellCostMasteryAndDamage |
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

## Battle-end experience

The award is CCombat (`.?AVCCombat@NWorld@@`, vtable 00e79d3c) slot 0xf8 = `FUN_00af3430`, per winning side; the
sides are CCombatArmy objects (interface vtable 00e17bcc at object +0x20). Steps, in this order:

| Step | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| Base | FIXED | 004d9c60 (side vcall 0x90 on the opponent) | Σ killed × creature Exp (record +0x80), or its hit points (+0x64) when Exp < 1. Engine had no fallback | (data: no shipped creature has Exp < 1) |
| +500 for the enemy hero | FIXED | 00af34bc..00af34d1, 004d99b0 | only when the opponent had a hero that is lost: not fled / surrendered (vcall 0xbc / 0xec) and not saved by Guardian Angel. Engine gave the defending winner +500 also when the attacker fled or surrendered | AuditBattleExpTests.BattleExperienceAppliesTheWeekLast |
| second +500 | GAP | 00af34d9..00af34e4 | +500 more when the opponent's field +0x38 (vcall 0x14) is set — the object it holds is not identified (setter not found); not modelled | |
| Talented Warrior | FIXED | 00af34ec..00af353b | hero spec 0x1b HERO_SPEC_TALENTED_WARRIOR: exp = trunc((level · 0.02 + 1.0) · exp) (doubles 00e79ac8 / 00e12e00). Was missing | AuditBattleExpTests.BattleExperienceAppliesTheWeekLast |
| Neutral coefficient | MATCH | 00af353f..00af35ae, 0098ded0 | when the opponent is a map monster: × the difficulty's ExpCoefs float (chop) — the engine's NeutralsNotInCampaign / InCampaign table, now applied after +500 / Talented Warrior | |
| Hero modifier | FIXED | CHero vcall 0x19c = 00b4bbb0 | exp + (5·Enlightenment mastery (≤ 4) + 10·Turban of Enlightenment + 20·Chain Mail of Enlightenment (per copy) + Educational_2_GainExperienceBonusPercents with 2+ Educational set pieces)·exp / 100 in integers. Engine used a double multiplier (100 → 114 instead of 115), capped Enlightenment at 3 and lacked the set bonus. GainExperience uses it for every source (whether other exp sources call vcall 0x19c is not traced) | AuditBattleExpTests.ExperienceModifierIsIntegerPercent |
| Week | FIXED | 00bcdc60 (00af35c9) | Week of Folly exp / 2, Week of Honor exp × 2 on the final amount (after +500 and the modifier); engine applied it to the base only | AuditBattleExpTests.BattleExperienceAppliesTheWeekLast |

## Might skills: hero morale/luck, war machines, Pendant of Mastery

Hero primary-stat getters are on the CHero interface vtable 00e7e484 (CHero + 28): +0x8 luck `00b4ca10`, +0xc morale
`00b4cae0`, +0x174 GetSkillMastery `00b4d150`, +0x19c experience modifier `00b4bbb0`, +0x330 battle-bonus array
`00b5ac30`.

| ID | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| `HERO_SKILL_LEADERSHIP` | MATCH | 00b4cae0 | hero morale = base fields + Leadership mastery (when > 0) − 1 with Pariah − a curse object's penalty; the engine adds the mastery the same way |  |
| `HERO_SKILL_PARIAH` | MATCH | 00b4cae0, 00bc04b8 | Fallen Knight: hero morale − 1 and +5 spell power for Dark spells (engine `Sk.FallenKnight`) |  |
| `HERO_SKILL_LUCK` | MATCH | 00b4ca10 | hero luck = base fields + Luck mastery (when > 0) − curse + town-building terms; the engine adds the mastery the same way |  |
| `HERO_SKILL_WAR_MACHINES` | MATCH | 004bd7b0, 004bc7f0, 004bc660, 004bb770/004bb7d0, 004bc710/004bc780, 004bb8c0/004bb920 | HP = record + mastery·WarMachines_HealthBonusPerSkillTrained, × PerkSpecificHealthMultiplier (truncated) with Ballista (id 1) / First Aid (3) / Catapult for ids 2 AND 4 (the Ammo Cart uses the Catapult perk — as the engine does); attack/defence/min/max damage from the record tables by mastery (index clamped to the table); Ballista attack + level with HERO_SPEC_WAR_MACHINES (spec 8) and damage × (hero vcall 0x1c + vcall 0x10, the engine's Attack + Knowledge — the two getters not decompiled further); the tables hold integers, so the engine's rounding is exact |  |
| `PEDANT_OF_MASTERY` | FIXED | 00b4d150, 00875550 | GetSkillMastery: for the hero class's racial skill (Knight Training, Ranger Avenger, Wizard Artificier, Demon Lord Gating, Necromancer Necromancy, Warlock Invocation, Runemage Runelore, Barbarian Blood Rage) +1 with the artifact (also from none), results outside 0..4 → 0. Was ignored | AuditSkillTestsH.PendantOfMasteryRaisesTheRacialSkill |
| `DRAGON_TALON_CROWN` | FIXED | 00a75930 → 004bae60 | war machine initiative × (0.1 · crowns + 1), + RunicMachines_InitiativeBonus (float; the rounded copy at 004bd7b0 +0x68 is not the combat value). Was missing | AuditSkillTestsH.DragonTalonCrownSpeedsUpWarMachines |
| `ARTIFACT_RING_OF_CELERITY` | FIXED | 004bc190 (004bc2d8), 00a75930 (00a7597e) | creatures: +10 % per ring inside the army artifact percent; war machines: × 1.1 (00e0d4b0) with any number of rings. Was missing | AuditSkillTestsH.ArmyArtifactsScaleCreatureInitiative, AuditSkillTestsH.DragonTalonCrownSpeedsUpWarMachines |

**Surrender ransom adjustment** (GAP kept): 004da9a0 passes trunc(value/2) to vcall 0xc of the CCombatArmy's hero pointer
(army + 0x30). That pointer's vtable is not the 00e7e484 interface (its +0xc is the fastcall morale getter, while the
ransom call pushes an argument), so the adjusting function is still unidentified.

## Magic skills: hero cast ATB, Erratic Mana

Hero spell cast (exe `FUN_00bbf610`, the hero's cast command): cost from 009781e0, Erratic Mana, Defile Magic, mana
spent; then the ATB kept after the cast: 0.5 for a mass spell (00a33e20 maps SPELL_MASS_* to its base spell), else
0.1 · mastery of Sorcery (warcries: Voice); Magis set; − Distract of the enemy hero; − DetainMagic_CasterExtraATBDrop when
the enemy hero has the school's Detain perk (004e5f60: 0xc2/0xc6/0xca/0xce by school).

| ID | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| `HERO_SKILL_SORCERY` | MATCH | 00bbf610 (00bbf8a8..) | kept ATB 0.1 (float 00e12f10) × Sorcery mastery; mass spells 0.5 — the engine's 0.5 also requires the mass perk, which every hero casting a SPELL_MASS_* spell has |  |
| `HERO_SKILL_VOICE` | MATCH | 00bbf610 | warcries use the Voice mastery instead of Sorcery |  |
| `HERO_SKILL_DISTRACT` | MATCH | 00bbf935 | − Distract_EnemyHeroInitiativeDrop when the enemy hero HasSkill(0xa2) |  |
| `HERO_SKILL_DETAIN_DESTRUCTIVE` | MATCH | 004e5f60 | − DetainMagic_CasterExtraATBDrop for the matching school (likewise Detain Dark / Light / Summoning) |  |
| `HERO_SKILL_CHAOTIC_SPELLS` | FIXED | 00bbf6a6..00bbf751 | Erratic Mana: for a positive cost, ability roll of 50 (FUN_00a5a170), then the hero pays trunc(r · cost), r uniform in [0.6, 0.9], at most cost − 1, at least 0. Engine had no implementation | AuditSkillTestsH.ErraticManaPaysSixtyToNinetyPercent |
| `ARTFSET_EFFECT_MAGIS` | FIXED | 00bbf8f0..00bbf930 | with 4+ pieces (any class) or 2+ pieces and hero class 3 (Wizard): kept ATB r + (1 − r) · Magis_4_2Wizard_AfterCastATBLessPercents · 0.01. Engine added the percent to r and only for Wizards | AuditSkillTestsH.MagisSetScalesTheCastAtbByTheRemainder |
| Hero spell points | FIXED | 00bbf610 (hero mana field −0x68 decremented by the cast) | the combat hero is the adventure CHero: a battle starts with the hero's current spell points and the points spent stay spent. Engine refilled the hero to Knowledge·10(·1.5) for every battle and kept the adventure mana unchanged; now `HeroInstance.Mana` carries the current points in and `ResolveBattleCmd.HeroMana` writes them back (local, quick combat and network battles) | AuditBattleExpTests.HeroManaCarriesIntoAndOutOfBattle |

## Magic skills: hero spell mastery, Shatter and Weaken

Hero spell mastery `FUN_00b4b440` (caster vcall 0x20, called from the generic `00a6a700`): Advanced when the spell is cast
from an item (00a34470: cast record +0x1c), Expert with Absolute Wizardry, else the school skill (0 Destructive → 9,
1 Dark → 10, 2 Light → 11, 3 Summoning → 12; other schools 0); Power of Haste / Power of Stone raise to Advanced when the
cast spell id is SPELL_HASTE (0x18) / SPELL_STONESKIN (0x19); then the week. `00a6a700` applies the week again and the
enemy hero's Shatter (`004e6190`, ids 0xbf/0xc3/0xc7/0xcb with Weaken 0xc1/0xc5/0xc9/0xcd); the spell power getter
`00a6a7e0` passes the same mastery to `004e6190` for the power loss.

| ID | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| `HERO_SKILL_DESTRUCTIVE_MAGIC` | MATCH | 00b4b440 | school 0 mastery = Destructive Magic (likewise Dark 10 / Light 11 / Summoning 12) |  |
| `HERO_SKILL_DARK_MAGIC` | MATCH | 00b4b440 | school 1 |  |
| `HERO_SKILL_LIGHT_MAGIC` | MATCH | 00b4b440 | school 2 |  |
| `HERO_SKILL_SUMMONING_MAGIC` | MATCH | 00b4b440 | school 3 |  |
| `HERO_SKILL_ABSOLUTE_WIZARDY` | FIXED | 00b4b440 (00b4b45d) | every hero spell at Expert (before the week and Shatter). Was missing | (HeroMastery, see AuditSkillTestsH) |
| `HERO_SKILL_SHATTER_DESTRUCTIVE_MAGIC` | FIXED | 004e6190 | mastery − {Basic,Advanced,Expert}ShatterMagic/SpellTrainedDecrease[spell level − 1] AFTER the week, clamped 0..3; when that reaches −1: power − max(1, trunc(p · SpellpowerDecreaseForTrainedDrop1BelowNone)), −2 or below: Drop2; integer, result ≥ 1. Engine applied the week after the drop and multiplied the power (×0.75 / ×0.5) | AuditSkillTestsH.WeekMasteryComesBeforeShatter, AuditSkillTestsH.ShatterAndWeakenSubtractFromSpellPower |
| `HERO_SKILL_SHATTER_DARK_MAGIC` | FIXED | 004e6190 | the same for Dark | (same code) |
| `HERO_SKILL_SHATTER_LIGHT_MAGIC` | FIXED | 004e6190 | the same for Light | (same code) |
| `HERO_SKILL_SHATTER_SUMMONING_MAGIC` | FIXED | 004e6190 | the same for Summoning | (same code) |
| `HERO_SKILL_WEAKEN_DESTRUCTIVE` | FIXED | 004e6190 | power − max(1, trunc(p · WeakenMagic_SpellPowerDecrease)) (subtracted together with the Shatter loss, result ≥ 1). Engine multiplied by (1 − 0.25) | AuditSkillTestsH.ShatterAndWeakenSubtractFromSpellPower |
| `HERO_SKILL_WEAKEN_DARK` | FIXED | 004e6190 | the same for Dark | (same code) |
| `HERO_SKILL_WEAKEN_LIGHT` | FIXED | 004e6190 | the same for Light | (same code) |
| `HERO_SKILL_WEAKEN_SUMMONING` | FIXED | 004e6190 | the same for Summoning | (same code) |
| `HERO_SKILL_POWER_OF_HASTE` | GAP | 00b4b4c1..00b4b4e4 | the exe raises the mastery to Advanced when the cast spell id (00a34110) is SPELL_HASTE (0x18); the engine (and the perk text "Mass Haste on advanced level") use SPELL_MASS_HASTE — whether 00a34110 returns the base spell for mass casts is not traced; unchanged |  |
| `HERO_SKILL_POWER_OF_STONE` | GAP | 00b4b4eb..00b4b50e | the same with SPELL_STONESKIN (0x19) vs SPELL_MASS_STONESKIN; unchanged |  |

Not changed: for runic / warcry schools 00b4b440 returns 0 while the engine uses Runelore / Voice mastery — runes and
warcries are resolved by their own code (spells R partition).

## Hero spell power

`FUN_00bc00d0` (hero caster vcall 0x24): hero spell power (vcall 0x164) — returned unchanged for item casts — + combat
field +0xec; + clamp(town effect values, 0, 1000) for SPELL_TOWN_DARK_GUARDIAN / DIETY_OF_FIRE / BLAZING_ANVIL; − the
SPELL_SKILL_MIGHT_OVER_MAGIC effect's value; a switch on the RAW spell id (two jump tables at 00bc05b8 / 00bc05f4);
Twilight +3 (Dark and Light), Fallen Knight +5 (Dark); enemy Shrug Darkness × multiplier (chop) for Dark; + the Power
Feed effect value; at least 1. Then 004e6190 (Shatter / Weaken) in 00a6a7e0.

| ID | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| `HERO_SKILL_MASTER_OF_QUAKES` | FIXED | 00bc0331 | +4 for SPELL_LAND_MINE (0x26), SPELL_ARCANE_CRYSTAL (0x11a) and SPELL_BLADE_BARRIER (0x11c) — not Earthquake (0x29 falls outside the table), despite the perk text. Engine used Land Mine + Earthquake | AuditSkillTestsH.HeroSpellPowerPerkTableFollowsTheExe |
| `ARTIFACT_BAND_OF_CONJURER` | FIXED | 00bc035a | +4 for Summon Elementals (artifact 60). Was missing | AuditSkillTestsH.HeroSpellPowerPerkTableFollowsTheExe |
| `HERO_SPEC_WEILDER_OF_FIRE` | FIXED | 00bc0270 | Fireball (raw id 5): + ((level − 1) / WeilderOfFire_FireballSpellpowerBonus/LevelDivisor) · PerDividedLevel + Base. Was missing | AuditSkillTestsH.HeroSpellPowerPerkTableFollowsTheExe |
| `HERO_SKILL_MASTER_OF_SICKNESS` | MATCH | 00bc021e | +4 for Unholy Word |  |
| `HERO_SKILL_MASTER_OF_WRATH` | MATCH | 00bc0247 | +4 for Holy Word |  |
| `HERO_SKILL_MASTER_OF_ANIMATION` | MATCH | 00bc0426 | +4 for Magic Fist and Animate Dead |  |
| `HERO_SKILL_MASTER_OF_CREATURES` | MATCH | 00bc0392 | +4 for Summon Elementals and Conjure Phoenix |  |
| `HERO_SPEC_MASTER_OF_ELEMENTS` | MATCH | 00bc03d0 | Summon Elementals / Conjure Phoenix + ((level − 1)/div)·per + base (MasterOfElements_SpellpowerBonus) |  |
| `HERO_SKILL_RUNIC_ARMOR` | MATCH | 00bc02db | + RunicArmor_SpellpowerBonus for Celestial Shield |  |
| `HERO_SKILL_TWILIGHT` | MATCH | 00bc0483, 00bc04e8 | +3 for Light and Dark spells |  |
| `HERO_SKILL_SHRUG_DARKNESS` | FIXED | 00bc0530..00bc0575 | enemy hero's perk: Dark spells' running power × ShrugDarkness_EnemySpellpowerMultiplier, truncated (engine kept the fraction) | AuditSkillTestsH.HeroSpellPowerPerkTableFollowsTheExe |
| Hero spell power order | FIXED | 00bc00d0, 00a6a7e0 | the perk bonuses use the raw spell id (empowered variants get none); Power Feed is added before the minimum of 1 and before Shatter / Weaken (engine added it after them and allowed 0) | AuditSkillTestsH.HeroSpellPowerPerkTableFollowsTheExe |

Not compared: the engine's Might over Magic subtracts half the enemy's spell power, the exe subtracts the value of the
SPELL_SKILL_MIGHT_OVER_MAGIC effect (set in 00c562f0, not decompiled); the town-building effects are applied by the
adventure hand-off as hero spell power.

## Spell resolution side effects (00983790)

| ID | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| `ARTIFACT_TWISTING_NEITHER` | FIXED | 00983790 (artifact 0x43 check) | Pendant of Conflux: when an enemy HERO casts (caster vcall 0x74 non-null), the artifact's owner gets (mana before − mana after) / 2 spell points (setter vcall 0x22c; whether it caps at the maximum is not traced — the engine does not cap). Was missing | AuditSkillTestsH.PendantOfConfluxGainsHalfTheEnemysSpentMana |
| `HERO_SKILL_INTELLIGENCE` | MATCH | 00b4c8f0 | maximum spell points = clamp(HeroManaPerKnowledge, 1, 1000) · Knowledge; with Intelligence trunc(clamp(Intelligence_ManaPointsBonus, 0.5, 1000) · that) — the engine's formula |  |
| `HERO_SKILL_RESISTANCE` | MATCH | 0097c9f0 | Magic Resistance perk: +15 to the stack's resistance chance (see `combat_spells.md`, ResistanceChanceSumsLikeTheExe) |  |
| `STAFF_OF_VEXINGS` | FIXED | 008a4330 (008a4619..008a4647) | the enemy hero's Staff of Vexings: stack initiative factor × (1 − 0.2 (float 00e12ff8) · staffs), after Storm Wind / Fog Veil. Was missing | AuditSkillTestsH.EnemyStaffOfVexingsSlowsTheArmy |
| `HERO_SKILL_STORM_WIND` | MATCH | 008a4330 (008a457f..008a45ae), 008a1e50 | enemy hero's perk: flyers' initiative × (1 − StormWind_InitiativePenalty); speed in 008a1e50 (see the engine's SpeedOf) |  |
| `HERO_SKILL_FOG_VEIL` | MATCH | 008a4330 (008a45b2..008a45f0), 008a2630 | enemy hero's perk: shooters' (004bb150) initiative × (1 − FogVeil_InitiativePenalty) and damage range × (1 − FogVeil_DamageDecrease) |  |
