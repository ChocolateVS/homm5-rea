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
| `ABILITY_PURGER` | UNVERIFIED | 00a629b0, 00a5d690 | trigger verified (any strike, Purger_ProcFactor); the dispel itself (00a5d690 → 004e6470) not yet compared |  |
| `ABILITY_COWARDICE` | UNVERIFIED | 00a629b0, 00a5d790 | exe: melee only, while the target would retaliate; the stack runs (00a5d790(0)) and, if it can't, gains UseCowardiceRP rage; engine models the run after the whole attack — order not yet confirmed |  |
| `ABILITY_MANEURE` | UNVERIFIED | 00a629b0, 00a5d790, 00b3d3f0 | exe: melee only, attacker without NO_ENEMY_RETALIATION, not a retaliation; runs (00a5d790(1)) then shoots back (00b3d3f0) unless blocked — half-damage factor not yet confirmed |  |
