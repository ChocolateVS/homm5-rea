# Combat audit — runes, warcries, creature-cast spells and spell effects (spells part R)

Per-ID rows merged into `docs/rules/AUDIT_COMBAT.md` by `tools/re/audit/ledger.py` (rows here override the computed
status). Exe references are H5_Game.exe (GOG 3.1) addresses, analysed headless with Ghidra 12.1.4
(`tools/re/audit_spells_r/ghidra.sh`, private project copy). Tests are in `tests/H5.Combat.Tests/AuditRuneWarcryTests.cs`.

Rune casting: each rune registers a (predicate, apply) pair under its spell id with FUN_0041a130 (registrations at
0041a6a0, 004b44e0, 00862a80, 008d9bd0, 00a69c00, 00b40ad0, 00c20840, 00c54690, 00c9d250, 00c9d2c0); FUN_00419f00 checks
"already used (this turn)" and the resource cost before the predicate. Apply handlers add the effect with
vf 0x260(id, duration, value) or vf 0x25c(id, actions, …): 0x7fffffff / 0x7ffffffe are the whole-battle durations.

Warcries: FUN_009be6c0 casts (switch on 0x122..0x127), FUN_009bdaa0 is the target filter (true = invalid),
FUN_009bdbd0 the rage points, FUN_009bddb0 Fear My Roar's chance, FUN_009bdec0 / FUN_009be2a0 the spellbook predictions.

Tool note: `tools/re/annotate.py` used to parse decimal vcall arguments as hex (Ghidra prints e.g. `HasAbility(10)`,
which is ABILITY_UNDEAD, but was labelled 0x10 = IMMUNITY_TO_BLIND). Fixed in commit 4e9da99; annotations made before
it are wrong for every decimal argument 10..99.

## Core mechanics (not ID rows)

| Mechanic | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| Creature speed order | FIXED | 008a1e50 (008a1f87..008a1fa5) | base (+ Familiar Ground) → Wound trunc((1 − clamp(Wound_SpeedDecrease,0,1))·speed) in chop mode → enemy Storm Wind on flyers (−penalty, <2 → 1) → Rune of Charge ×2 → enemy Necromancers 2-piece −DeSpeed → Demon Rage speed bonus (FUN_008d9390) → Battlecry ((level−1)/div·per + base); clamped at 0. The engine added Battlecry and rage before Storm Wind / Charge (so Charge doubled them), applied Wound last and clamped at 1 | AuditRuneWarcryTests.SpeedAppliesChargeBeforeBattlecry |
| Warcry rage points | FIXED | 009bdbd0, 009be6c0 (→ 008a56f0) | Call of Blood gives its target trunc(level·CallOfBlood_RPPerCasterLevel·m); every other warcry gives the caster's army trunc(RPToArmyOnWarcryLevel{1,2,3}·m) (Rallying Cry 1, Word of the Chief / Fear My Roar 2, Battlecry / Shout of Many 3); m = Voice of Rage's CallOfBlood / Other multiplier; War Leader adds the hero level to both. The engine also gave army rage on Call of Blood and had no War Leader bonus | AuditRuneWarcryTests.WarcryRagePointsFollowTheExe |
| Warcry caster level | MATCH | 009bdb70 | hero level + MightyVoice_HeroLevelBonus with Mighty Voice (only for warcries, FUN_00a345e0 spell type 6) | |

## IDs

| ID | Status | Exe | Original behaviour / note | Test |
|---|---|---|---|---|
| `SPELL_RUNE_OF_CHARGE` | FIXED | 00b40aa0, 008a1e50, 00b40a60 | vf 0x25c(id, 1): one action; speed ×2 after Wound and Storm Wind, before the Necromancers set, rage and Battlecry (see core row); removed by 00b40a60 at the end of the creature's action | AuditRuneWarcryTests.SpeedAppliesChargeBeforeBattlecry |
| `SPELL_RUNE_OF_EXORCISM` | FIXED | 008d9b30 → 004e6470 / 004e4000, 004e3960, 004e3ac0 | removes every effect FUN_004e4000 accepts for the caster's own side: harmful (spell kind ≠ positive), timed (duration not 0x7ffffffd/e/f), no "undispellable" flag, not from a warcry, and not Holy Charge, Demonic Strike, Dash, Poisonous Attack, Wound, Venom, goblin Sacrifice, Powerful Blow, Prepared Position or Battle Rage. No spell-level limit — the engine only removed level ≤ 3 effects | AuditRuneWarcryTests.ExorcismHasNoSpellLevelLimit |
| `SPELL_RUNE_OF_ELEMENTAL_IMMUNITY` | FIXED | 00c54620, 00c546b0, 00ad02b0 | for the whole battle (0x7fffffff); value = b + 8a with a = 1 + Random(4), r = Random(3), b = r + 1 (r + 2 when a ≤ b), in the exe element enum 1 air, 2 fire, 3 water, 4 earth; the spell-immunity check (0097c9f0 → 00c546b0) compares the spell's element with both. The engine drew a different pair from the same RNG calls ((a + 1 + r) mod 4 over a fire/water/air/earth list) | AuditRuneWarcryTests.ElementalImmunityRollsTwoDistinctExeElements |
| `SPELL_RUNE_OF_DRAGONFORM` | FIXED | 004b4460 → 004bb000, 004b4400, 0097af50 | refused ("COMBAT_RUNIC_SPELL_CANT_DRAGONFORM") when the creature's base creature is id 0x29 / 0x37 / 0x53 / 0x68 (Bone, Green, Deep, Fire Dragon — every dragon line); the engine matched "Dragon" in the (localised) name. Lasts one turn (round of time per turn) | AuditRuneWarcryTests.DragonformRefusesDragonLinesById |
| `SPELL_RUNE_OF_STUNNING` | FIXED | 00c9d290, 00a59a90, 00a5d370, 00a5af10 | vf 0x25c(id, 1): one action. Shares Bash's chance (FUN_00a59990 with Bash_ProcFactor, Soldier's Luck doubles, no second roll) and its MECHANICAL immunity: one roll when the creature has Bash or the rune; Bash only off retaliation, the rune on any strike; the stun zeroes ATB and cancels the retaliation. The engine rolled the rune separately after the blow (old ProcChance), with no mechanical immunity and no duration | (Battle.Attack Bash/rune block) |
| `SPELL_RUNE_OF_ETHEREALNESS` | MATCH | 0041a640, 008a34c0 | one turn (round of time per turn); grants ABILITY_INCORPOREAL through the dynamic HasAbility (engine Unit.Has) | |
| `SPELL_RUNE_OF_MAGIC_CONTROL` | GAP | 00862a50, 00a5ca80 → 004e6470 / 004e4000 | whole battle until consumed (0x7fffffff); on attack the candidates come from FUN_004e4000 for the target's opposite side — every beneficial, timed, dispellable effect (any school). The engine only steals Light-school effects; the choice and transfer code was dropped by the decompiler (unreachable blocks in 00a5ca80) and is not ported | |
| `SPELL_WARCRY_RALLING_CRY` | FIXED | 009be6c0 case 0x122, 009bdaa0 | targets every own stack except Undead and Mechanical (elementals included); morale bonus ((level−1)/div·per + base) for RallingCry_Duration (004e57d0); army rage per core row. The engine also skipped elementals (NeutralMorale) | AuditRuneWarcryTests.RallyingCryAndWordOfTheChiefTargets |
| `SPELL_WARCRY_CALL_OF_BLOOD` | FIXED | 009be6c0 case 0x123, 009bdbd0, 009bdaa0 → 008a16b0 | target must be a Stronghold creature whose army hero has Blood Rage (engine: Demon-Raged); it gets trunc(level·CallOfBlood_RPPerCasterLevel·m) + hero level for War Leader; no army rage | AuditRuneWarcryTests.WarcryRagePointsFollowTheExe |
| `SPELL_WARCRY_WORD_OF_THE_CHIEF` | FIXED | 009be6c0 case 0x124, 009bdd40, 009bdaa0 | friendly hero strike (008cfb50), ATB + (base + perLevel·level), target rage trunc(WordOfTheChief_RPBonus·m), army rage per core row; refuses Undead and Mechanical only — the engine also refused elementals | AuditRuneWarcryTests.RallyingCryAndWordOfTheChiefTargets |
| `SPELL_WARCRY_FEAR_MY_ROAR` | FIXED | 009bddb0, 009be6c0 case 0x125, 009bdaa0 (jump at 009bdb2b: HasAbility(0x13)) | chance clamp((caster level − target level + 10)·5, 0, 100), 0 against HOLD_GROUND; rolled with the ability roll FUN_00a5a170 without an attacker (Range(0,100) < chance); success = frightened (FUN_009be240), failure shows FLYING_SIGN_RESIST; only IMMUNITY_TO_MIND_CONTROL makes the target invalid. The engine used a continuous 0.5 + 0.05·diff roll, ignored HOLD_GROUND and refused undead / elementals / mechanicals | AuditRuneWarcryTests.FearMyRoarChanceAndTargets, FearMyRoarHoldGroundAndImmunity |
| `SPELL_WARCRY_BATTLECRY` | FIXED | 008a1e50, 009be6c0 case 0x126, 004e57d0 | the effect stores the caster level; speed bonus ((level−1)/Battlecry_SpeedBonus.LevelDivisor·PerDividedLevel + Base) — the engine added Base only; one turn (004e57d0 case 0x126 → 1.0) | AuditRuneWarcryTests.SpeedAppliesChargeBeforeBattlecry |
| `SPELL_WARCRY_SHOUT_OF_MANY` | FIXED | 009be6c0 case 0x127, 009bde00, 009bdaa0, 00a616e0 → 00a61600 | damage = trunc(n·ShoutOfMany_DamagePerCreatureInArmy) in single precision, n = creatures of the caster's army not controlled by the other side (FUN_00977670); dealt straight through the stack damage pipeline (Arcane Armor, Blood Rage transfer, Last Stand) — no resistance roll, protection or immunity (the only target filter is "has an army", 009bdaa0 case 0x127). The engine sent it through spell damage (resistance, protection, immunity) and counted enemy-controlled stacks | AuditRuneWarcryTests.ShoutOfManyCountsOwnControlledCreatures |
| `SPELL_EFFECT_WOUND` | FIXED | 008a1e50 (008a1f52..008a1fa9) | speed part: trunc((1 − clamp(Wound_SpeedDecrease, 0, 1))·speed) applied right after the base speed (before Storm Wind / Charge); the engine multiplied at the end without truncation order (initiative part: spells partition) | AuditRuneWarcryTests.SpeedAppliesChargeBeforeBattlecry |
