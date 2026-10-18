# Combat audit — creature abilities

Per-ability rows merged into `docs/rules/AUDIT_COMBAT.md` by `tools/re/audit/ledger.py` (rows here override the
computed status). Exe references are H5_Game.exe addresses (Ghidra 12.1.4). Strike-context flags used below, from
FUN_00a64120 / FUN_00a629b0: `+0x14` = ranged strike, `+0x21` = retaliation strike, `*param_2` = target will retaliate.

| ID | Status | Exe | Original behaviour / note | Test |
|---|---|---|---|---|
| `ABILITY_WOUND` | FIXED | 00a629b0, 00a5e590 | proc (Wound_ProcFactor) on any strike incl. ranged/retaliation; target must be a creature, not MECHANICAL, not ELEMENTAL; duration = round(f·clamp(Wound_EffectDurationInRounds,0,100)) — engine lacked the mechanical/elemental immunity | AuditAbilityTests.WoundNeverAffectsElementals |
| `ABILITY_MARK_OF_FIRE` | FIXED | 00a629b0, 00a5e610 | only on RANGED strikes (+0x14), MarkOfFire_ProcFactor — engine also applied it in melee | AuditAbilityTests.MarkOfFireOnlyOnRangedStrikes |
| `ABILITY_SLEEPING_STRIKE` | FIXED | 00a629b0, 00a5e4f0 | proc SleepingStrike_ProcFactor on any strike; immune: IMMUNITY_TO_BLIND, MECHANICAL, ELEMENTAL (undead NOT immune); target then doesn't retaliate — engine excluded undead and ignored blind immunity | AuditAbilityTests.SleepingStrikeImmunityIsBlindMechanicalElementalNotUndead |
| `ABILITY_WHIP_STRIKE` | FIXED | 00a629b0, 00a5e2c0 | melee only (incl. retaliation); r = Rng(100): r<40 Slow, r<75 Weakness, else Berserk; power WhipStrikeSpellPower (creature power if <1) — engine cast Slow/Curse/Berserk uniformly | AuditAbilityTests.WhipStrikeCastsSlowWeaknessOrBerserk |
| `ABILITY_FEAR` | FIXED | 00a629b0, 00a5e1b0, 00a5d790 | only on the attacker's own strike (never a retaliation, +0x21) and only while the target would retaliate; mind-immunity check (00a5e1b0); the target flees instead of retaliating — engine could trigger on retaliations / when no retaliation was pending | AuditAbilityTests.FearNeverTriggersOnRetaliation |
| `ABILITY_BLINDING_ATTACK` | MATCH | 00a629b0, 00a5e440 | BlindingAttack_ProcFactor on the strike; Blind at basic mastery with BlindingAttackSpellPower (creature power if <1) via the spell application (immunities by spell rules, FUN_00a5ddb0 not compared); a blinded target doesn't retaliate |  |
| `ABILITY_PURGER` | UNVERIFIED | 00a629b0, 00a5d690 | trigger verified (any strike, Purger_ProcFactor, chance on post-hit HP, second roll); the dispel itself (00a5d690 → 004e6470) not yet compared |  |
| `ABILITY_COWARDICE` | UNVERIFIED | 00a629b0, 00a5d790 | exe: melee only, while the target would retaliate; the stack runs (00a5d790(0)) and, if it can't, gains UseCowardiceRP rage; engine models the run after the whole attack — order not yet confirmed |  |
| `ABILITY_MANEURE` | UNVERIFIED | 00a629b0, 00a5d790, 00b3d3f0 | exe: melee only, attacker without NO_ENEMY_RETALIATION, not a retaliation; runs (00a5d790(1)) then shoots back (00b3d3f0) unless blocked — half-damage factor not yet confirmed |  |
| `ABILITY_BASH` | FIXED | 00a675e6, 00a59a90, 00a59990 | chance base min(75, 25+⌊3A/T⌋) / max(5, 25−⌊3T/A⌋) with HP before the hit; Bash_ProcFactor as an exponent; Soldier's Luck doubles the factor (flag 1) and gives NO second roll; never on a retaliation unless Rune of Stunning; target not mechanical | AuditProcChanceTests.ChanceUsesIntegerRatioAndFactorAsExponent |
| `ABILITY_CRUSHING_BLOW` | FIXED | 00a5af70 | 2×2 area (unchanged) PLUS a Bash-like chance (CrushingBlow_ProcFactor, flag 1, no second roll, never on a retaliation, creature targets): ATB 0 and no retaliation — the stun was missing | AuditAbilityTests.CrushingBlowCanStunTheTarget |
| `ABILITY_PAW_STRIKE` | FIXED | 00a5d1b0 | one roll per tile walked: fail = Π(100−p)·0.01 in floats, chance = round-to-nearest((1−fail)·100+0.5); none when standing still or vs HOLD_GROUND; flag 1 — engine used p·tiles/2 | AuditProcChanceTests.PawStrikeRollsOncePerTileWalked |
| `ABILITY_WARDING_ARROWS` | FIXED | 00a5b000 | every ranged shot, WardingArrows_ProcFactor (flag 0, second roll); ATB = max(0, ATB − clamp(WardingArrowsATBAffect,0,1)); no damage requirement — engine required damage > 0 and could go below 0 | AuditProcChanceTests.ChanceUsesIntegerRatioAndFactorAsExponent |
| `ABILITY_PIERCING_ARROW` | GAP | 00a5d420, 00a5a900 | Force Arrow: −50 % defence verified; the extra proc (PiercingArrow_ProcFactor, flag 0, no second roll, not HOLD_GROUND) pushing the target one tile and stunning it is now implemented, but the exe's push-cell search (FUN_00a5ceb0, angle π/3) is approximated as straight away from the shooter | AuditAbilityTests.ForceArrowCanPushAndStun |
| `ABILITY_BRUTALITY` | FIXED | 00a65260 (00a657a0) | Assault proc after the first strike uses Brutality_ProcFactor (engine used 1), flag 1, second roll | AuditStrikeTests |
| `ABILITY_TAUNT` | FIXED | 00a65d50 (00a65e0a) | Taunt_ProcFactor with flag 1 (Soldier's Luck doubles) and no second roll | AuditProcChanceTests.ChanceUsesIntegerRatioAndFactorAsExponent |
| `ABILITY_FLAMEWAVE` | FIXED | 00a62030 (00a62397) | FlameWave_ProcFactor with flag 1 and no second roll | AuditProcChanceTests.ChanceUsesIntegerRatioAndFactorAsExponent |
| `ABILITY_BEAR_ROAR` | UNVERIFIED | 00a5e800 | factor flag 1 + second roll verified; the per-victim size multiplier / victim set (stats +0x354 block) not yet compared |  |
| `HERO_SKILL_LUCKY_STRIKE` | FIXED | 00a59990, 00a5a170 | Soldier's Luck works two ways by caller: flag-1 abilities (Bash, Brutality, Crushing Blow, Flamewave, Taunt, Paw Strike, Bear Roar) double their ProcFactor; callers that name the attacker (on-hit group, Brutality, Warding Arrows, Bear Roar) get a second Range(0,100) roll | AuditProcChanceTests |
| `ABILITY_NO_ENEMY_RETALIATION` | MATCH | 00a5fee0, 00a629b0 | attacker with it gets no retaliation unless the target has SPELL_EFFECT_PREPARATION and Preparation_RetaliateToNER (true in DefaultStats); also suppresses Maneuver's return shot |  |
| `ABILITY_FAST_ATTACK` | MATCH | 00a5fee0 | no retaliation against a FAST_ATTACK attacker while the target is under SPELL_SLOW |  |
| `ABILITY_INVISIBILITY` | MATCH | 00a5fee0 | no retaliation against an attacker under SPELL_ABILITY_INVISIBILITY |  |
| `ABILITY_NO_MELEE_PENALTY` | MATCH | 00a58de0 | melee damage ×0.5 for SHOOTER stacks without NO_MELEE_PENALTY |  |
| `ABILITY_RANGE_PENALTY` | MATCH | 00a58de0, 00a5c5a0 | ranged damage ×0.5 for stacks with RANGE_PENALTY (and they skip the distance penalty) |  |
| `ABILITY_NO_RANGE_PENALTY` | MATCH | 00a5c5a0 | skips the (width/2)² distance penalty; wall penalty still applies |  |
| `ABILITY_JOUSTING` | MATCH | 00a58de0 | melee, cells walked > 0: ×(1 + clamp(clamp(perCell,0,10),0,100)·cells), Cavalry Commander factor |  |
| `ABILITY_PRECISE_SHOT` | MATCH | 00a5a900, 00a5a8b0 | ranged, not ARMORED target: defence 0 when squared nearest distance ≤ PreciseShotRange² |  |
| `ABILITY_RIDER_CHARGE` | MATCH | 00a5a900 | defence ×clamp(1 − cells·RiderCharge_DefenceReducePerPathCell, 0, 1), not vs ARMORED |  |
| `ABILITY_REGENERATION` | MATCH | 00bbb080, 008a3340 | turn start: heal Range(max(0,HydraRegenerationMin), max(min, HydraRegenerationMax)) inclusive on the top creature, no resurrection (combined with SPELL_REGENERATION in one heal in the exe) |  |
| `ABILITY_UNLIMITED_RETALIATION` | MATCH | 008a1120 | the retaliation counter is only decremented without the ability |  |
| `ABILITY_VORPAL_SWORD` | FIXED | 00a673c0, 00a63020 | +1 creature killed on the strike, only vs creature targets without CRYSTAL_SCALES — engine ignored both conditions | AuditAbilityTests.VorpalSwordDoesNotWorkOnCrystalScales |
| `ABILITY_FIRE_SHIELD` | FIXED | 00a5bcf0 | melee only; clamp(FireShield_Damage_Multiplier,0,10) × min(damage, holder's HP before the blow), dealt as fire spell damage when ≥ 0.5 — engine counted overkill damage (spell-damage rounding in 0097d740 not compared) | AuditAbilityTests.FireShieldIgnoresOverkill |
| `ABILITY_MAGMA_SHIELD` | FIXED | 00a5bdd0 | same as Fire Shield with MagmaShield_Damage_Multiplier | AuditAbilityTests.FireShieldIgnoresOverkill |
