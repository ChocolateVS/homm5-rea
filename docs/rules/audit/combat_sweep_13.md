# Combat audit — sweep 13: Resurrect Allies target check

Per-ID rows merged into `docs/rules/AUDIT_COMBAT.md` by `tools/re/audit/ledger.py` (fragments are read in natural
order, so rows here override sweep 1–12). Exe references are H5_Game.exe (GOG 3.1) addresses, analysed headless with
Ghidra 12.1.4 (private project copy), disassembly read directly where the decompiler loses registers. Tests are in
`tests/H5.Combat.Tests/AuditSweep13Tests.cs`; code in `src/H5.Combat/Battle.Sweep13.cs`.

Facts used below. Spell record (FUN_008496a0): +0xcc IsAimed, +0xcd IsAreaAttack, +0xce CanSelectDead (FUN_00a34650),
+0xd0 Target (0 = TARGET_FRIEND: FUN_00a34550 is "≠ 0"; 1 = TARGET_HOSTILE: FUN_00a34520 is "≠ 1"). The application
check FUN_00982010 with its side flag (the last argument): a TARGET_FRIEND spell on a non-friend (FUN_009776a0 →
FUN_004e5790) → COMBAT_CANT_CAST_ONLY_FOR_FRIENDLY (00e6162c), a TARGET_HOSTILE spell on a friend (FUN_004e5750) →
COMBAT_CANT_CAST_ONLY_FOR_HOSTILE (00e61608); without CanSelectDead a dead target (vf 0x5c) →
COMBAT_CANT_CAST_SPELL_ONLY_LIVE (00e615e4); an ability (FUN_00a34610) on its own caster is refused (0098247f); then
abilities go to FUN_0097f610. Unit vf 0x28(id) = the effect of that id (FUN_00a6d330), vf 0x1d8 = the stack size.

## IDs

| ID | Status | Exe | Original behaviour / note | Test |
|---|---|---|---|---|
| `ABILITY_RESURRECT_ALLIES` | FIXED | 00982010 (009823dc..0098248c), 0097f610 (the FUN_00a34130 immunity roll, case 0x9d), 00a34130, 0097a230 (0097a230..0097a4e3), 00977da0, 00b3f330, 008a40c0, 00977980, 00a6be20, 009789b0 case 0x9d, 00986fe0 | RessurectAllies.xdb: Target TARGET_FRIEND, CanSelectDead true. The target check in the exe's order: a friend (side test), dead or alive, not the caster; FUN_0097f610: FUN_0097d2a0 at 100 % (FUN_00a34130 lists 0x9d) → COMBAT_CANT_CAST_SPELL_IMMUNITY (the immunity table, as for Mark of Fire); case 0x9d: Undead (10), Mechanical (9), Elemental (12) refused, a charge (vf 0x6c slot vf 0x2c > 0, else COMBAT_ABILITY_ALREADY_USED); FUN_0097a230: a creature (vf 0x6c); a dead stack needs FUN_00977da0 — no Phantom effect (vf 0x28(0x28)) and each of its cells (vf 0xc4) answered by FUN_00b3f330 with the stack itself (its place free); then vf 0x1fc(1) = FUN_008a40c0 (top creature below full health, or the count below the initial count) else COMBAT_CANT_RESURRECT_ONE_CREATURE; FUN_00977980 / FUN_00a6be20 (creatures raised by the amount, capped at the initial count) can only refuse a dead stack with nothing to raise, which 100 · N ≥ 100 never is. The amount is FUN_009789b0 case 0x9d = caster stack size (vf 0x1d8) × 100 (MATCH), healed with resurrection. `Battle.ResurrectAlliesRefusal`; the engine offered dead allies only and moved a dead stack whose place was taken to the nearest free place. Not ported: FUN_00977da0's vf 0x4c ≠ 1 for a dead stack (a unit state, not identified) | AuditSweep13Tests.ResurrectAlliesHealsALivingWoundedAlly, ResurrectAlliesRefusesADeadStackWhosePlaceIsTaken |
| `SPELL_ABILITY_RESURRECT_ALLIES` | FIXED | 00986fe0, 0097a230, 009789b0 case 0x9d | the activated spell of ABILITY_RESURRECT_ALLIES — that row | AuditSweep13Tests.ResurrectAlliesHealsALivingWoundedAlly |
