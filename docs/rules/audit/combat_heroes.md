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
| Morale rolls | FIXED | 00a6ba00, 00a6b8f0 | abs(morale) clamped to 5; good (m ≥ 1) / bad (m ≤ −1) succeeds iff the RNG's inclusive Range(1,100) < 10·abs(m) → (10·abs(m)−1) % (9 % at ±1, 49 % at ±5). Engine used a continuous 10 %·|m| capped at 50 %. | AuditHeroTests.MoraleRollUsesTheInclusiveRangeAndClampsAtFive |
| Luck roll | MATCH | 00a6b820, 008a3ef0 | luck clamped ±5, ability roll 00a5a170; good result through 00a6a680 (Elven Luck multiplier) |  |
| Creature stats from the hero | MATCH | 004bc470, 004bbfb0, 004bc020, 004bc0b0, 004bc190 | HP: base +2 Toughness (mastery > 0), +2 per hero HP-effect item, set bonuses (Dragonish 4 pieces for the set tier, Ogres 2), Wyvern Tamer / Defender (see IDs), then ×clamp(100+pct,0,1000)/100 rounded (pct from hero vf 0x330 — source not identified, engine has none); min/max damage +1 Frenzy, max +1 Nature's Wrath for Sylvan; speed +2 March of the Machines (mechanical), +1 Fast and Furious; initiative +2 March of the Machines for Iron/Steel/Obsidian Golems only | AuditHeroTests.WyvernTamerHealthUsesLevelMinusOne, DefenderSpecialisationAddsLevelOverFiveHealth |
| Tactics phase columns | FIXED | 004d8c10 | a side qualifies when its hero has Tactics (mastery ≠ 0) and the enemy hero is not a Flagbearer of Darkness; the third deployment column is given only when exactly one side qualifies (both qualify → both cancelled). Engine lacked the Flagbearer rule. | AuditHeroTests.TacticsColumnsFollowTheOriginalRule |
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
| `HERO_SKILL_ABSOLUTE_LUCK` | MATCH | 008a3ef0, 00a6a680 | every strike is lucky (good-luck multiplier) unless the target's hero has Absolute Protection (then a normal roll) |  |
| `HERO_SKILL_ABSOLUTE_PROTECTION` | MATCH | 008a3ef0 | enemy rolls normally; a lucky result becomes normal, anything else becomes BadLuck_DamageMultiplier — unless the attacker's hero has Absolute Luck |  |
| `HERO_SKILL_ELVEN_LUCK` | MATCH | 00a6a680, 00a6b820 | good-luck multiplier = ElvenLuck_DamageMultiplier instead of GoodLuck_DamageMultiplier |  |
| `HERO_SKILL_ARTIFICIAL_GLORY` | MATCH | 00a751a0, 00a6acd0, 004ba9d0, 004bae10 | mechanical creatures of a hero with the perk get army morale clamped to ≥ 0 and roll only good morale (00a6ba00); without it their morale is 0 |  |
| `HERO_SKILL_TOUGHNESS` | MATCH | 004bc470 | +2 HP per creature when the Toughness mastery is > 0 |  |
| `HERO_SKILL_FRENZY` | MATCH | 004bbfb0, 004bc020 | +1 min and +1 max damage |  |
| `HERO_SKILL_FOREST_RAGE` | MATCH | 004bc020 | +1 max damage for Sylvan creatures |  |
| `HERO_SKILL_MARCH_OF_THE_MACHINES` | MATCH | 004bc0b0, 004bc190 | +2 speed for mechanical creatures; +2 initiative for Iron/Steel/Obsidian Golems (creatures 61/62/161) |  |
| `HERO_SKILL_FAST_AND_FURIOUS` | MATCH | 004bc0b0 | +1 speed for every creature in the army |  |
| `HERO_SPEC_WYVERN_TAMER` | FIXED | 004bc470 | creatures whose base creature is Wyvern (Wyvern, Foul Wyvern, Paokai): +((level−1)/LevelDivisor)·PerDividedLevel + Base HP — engine used level/LevelDivisor (+2 HP too many) | AuditHeroTests.WyvernTamerHealthUsesLevelMinusOne |
| `HERO_SPEC_DEFENDER` | FIXED | 004bc470 | Defenders and upgrades (base creature Defender): +level/5 HP — engine lacked the HP part | AuditHeroTests.DefenderSpecialisationAddsLevelOverFiveHealth |
| `HERO_SKILL_TACTICS` | FIXED | 004d8c10 | extra deployment column (see Tactics phase columns) | AuditHeroTests.TacticsColumnsFollowTheOriginalRule |
| `HERO_SPEC_FLAGBEARER_OF_DARKNESS` | FIXED | 004d8c10 | the enemy hero cannot use Tactics (the spec's only combat-code use; its +1 luck is a hero stat) — was missing | AuditHeroTests.TacticsColumnsFollowTheOriginalRule |
