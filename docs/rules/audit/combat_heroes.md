# Combat audit — heroes (skills, perks, specialisations, artifacts, war machines)

Rows here override the computed status in `docs/rules/AUDIT_COMBAT.md` (merged by `tools/re/audit/ledger.py`).
Exe references are H5_Game.exe (GOG 3.1) addresses, analysed headless with Ghidra 12.1.4
(`tools/re/audit_heroes/ghidra.sh`). Hero vtable slots seen in combat code: `0x290` HasSkill (perk / skill
present), `0x294` HasSpecialization, `0x174` GetSkillMastery (1 basic … 4 extra-expert), `0x23c` hero level.
FPU conversions in these functions set the x87 control word to truncation (`OR AH,0xc` before `FISTP`), so
the decompiler's `ROUND` is a truncation toward zero.

## Core mechanics (not ID rows)

| Mechanic | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| Native terrain | FIXED | 008a4800, 008a22f0 | +1 attack and +1 defence when the battlefield is native to the creature's race (battlefield vfunc 0x54(race); race ↔ terrain table from manual p.260). Engine had no native-terrain bonus. | AuditHeroTests.NativeTerrainGivesPlusOneAttackAndDefence |
| Defend posture | FIXED | 008a22f0 | defence = base + hero + Prayer + native; if defending: += max(1, trunc(that·f + 0.001)), f = DefenceBonusOnTakeRoots for ABILITY_TAKE_ROOTS creatures else DefenceBonusOnDefend (never both), × HoldGround_* multiplier; spell/effect bonuses (FUN_008d9390) are added afterwards and are not multiplied. Engine multiplied the whole defence incl. spells by (1+0.3[+0.5]). | AuditHeroTests.DefendBonusIsTruncatedOnTheBaseDefenceOnly, TakeRootsReplacesTheDefendFactor, DefendBonusIsAtLeastOne |
| Attacker multipliers | MATCH | 00a58de0 | luck × penalties × (1+clamp(Offence[mastery 1..3],0,10)) melee / (1+clamp(Archery_DamageBonus,0,10)) ranged with the Archery perk; retaliation: Training table (mastery 1..4); own strike: Retribution max(0,morale)·perMorale; Jousting with Cavalry Commander | RulesTests, AuditHeroTests.OffenceBonusOnlyForMasteriesOneToThree |

## IDs

| ID | Status | Exe | Original behaviour / note | Test |
|---|---|---|---|---|
| `HERO_SKILL_OFFENCE` | FIXED | 00a58de0 | melee ×(1+clamp(SkillOffence Basic/Advanced/Expert DamageBonus,0,10)) by mastery 1/2/3; any other mastery value gives nothing — engine gave the Expert bonus to mastery 4 | AuditHeroTests.OffenceBonusOnlyForMasteriesOneToThree |
| `HERO_SKILL_DEFENCE` | FIXED | 00a5a900 | melee damage ×(1−clamp(SkillDefence reduce,0,1)) for mastery 1/2/3 only (mastery 4: none) — engine gave Expert to mastery 4 | (DefenceReduction) |
| `HERO_SKILL_ARCHERY` | MATCH | 00a58de0 | ranged strikes ×(1+clamp(SkillOffence/Archery_DamageBonus,0,10)) when HasSkill(0x23) |  |
| `HERO_SKILL_TRAINING` | MATCH | 00a58de0 | retaliation strikes ×(1+clamp(Training Basic/Advanced/Expert/ExtraExpert DamageBonus,0,10)) by mastery 1..4 |  |
| `HERO_SKILL_RETRIBUTION` | MATCH | 00a58de0 | non-retaliation strikes ×(1+clamp(max(0,morale)·Retribution_DamageBonusPerMorale,0,10)) |  |
| `HERO_SPEC_JOUSTING` | MATCH | 00a58de0, 0094aee0 | Jousting factor (1+clamp(clamp(perCell,0,10),0,100)·cells) × (1+clamp(CavalryCommander_JoustingBonus,0,1000)+clamp(…PerLevel,0,1000)·level) (struct fields +4/+8) |  |
| `HERO_SKILL_HOLD_GROUND` | FIXED | 008a22f0 | multiplies the defend factor by HoldGround_DefendBonusMultiplier, or the Take Roots factor by HoldGround_TakeRootsBonusMultiplier (see Defend posture) | AuditHeroTests.TakeRootsReplacesTheDefendFactor |
| `HERO_SKILL_DEFENSIVE_FORMATION` | MATCH | 008a22f0 | Fortress race (9) only: += clamp(trunc(10·log10(A/B)), 0, Defensive_MaxDefenseBonus) — trunc matches the manual's floor; operands A (unit+0xf0) / B (unit+0xa0 vf 0x24) taken as adjacent dwarven power / own power per manual p.323 (not traced further) |  |
| `HERO_SKILL_OFFENSIVE_FORMATION` | MATCH | 008a4800 | same formula with Offensive_MaxOffenseBonus |  |
| `HERO_SKILL_DEATH_TO_NONEXISTENT` | MATCH | 00a5a900 (00a5adc6) | damage ×DeathToNonexistent_DamageToSummonsMult when the target has a summoner link (vf 0x6c → 0x58) |  |
