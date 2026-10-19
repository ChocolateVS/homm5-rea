# Combat audit — sweep 2: surrender hand-off, moat field, effect durations, boss spells

Per-ID rows merged into `docs/rules/AUDIT_COMBAT.md` by `tools/re/audit/ledger.py` (rows here override the earlier
fragments). Exe references are H5_Game.exe (GOG 3.1) addresses, analysed headless with Ghidra 12.1.4
(`tools/re/audit_sweep/ghidra.sh`, private project copy).

## Core mechanics (not ID rows)

| Mechanic | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| Surrender ransom hand-off | MATCH | 004da9a0 (CCombatArmy vtable 00e17bcc +0xf4), 004da8d0 | ransom = army gold value (FUN_004da8d0) / 2 with a signed truncating division (CDQ / SUB / SAR), handed to the object at +0x10 through its vf 0xc when that object is alive — a result sink, not an adjustment of the price (the "hero-object adjustment" left open in combat_heroes.md is this hand-off). The engine's trunc(value / 2) is the same | AuditRetreatTests.SurrenderCostIsHalfTheArmyGoldValue |
| Moat as a battlefield field | GAP | CArenaMoat (RTTI 00f7a578; field interface vtable 00e8df90), 00c50fa0, 00c50fb0, 00c50fd0, 00c515c0, 0085bbf0, 00a66050 | the moat is a field like Firewall: interface vf 4 = 2 (FUN_00a83aa0, same kind as CArenaFirewall), so it triggers on the cells a moving unit newly covers at a path step (FUN_0085bbf0 set "new minus old", called per step by the move FUN_00a66050); vf 0x18 (FUN_00c50fa0) is active only for units whose side differs from the moat's owner (+8) — the engine's "attackers only"; vf 0x1c (FUN_00c50fb0 → FUN_0085b6d0) the damage, vf 0x24 (FUN_00c50fd0) deals it through FUN_00a616e0 and logs CCombatLogMoatDamage; vf 0x28 (FUN_00c515c0) rolls each moat spell entry (0x14 bytes) with the ability roll FUN_00a5a170 and casts it (FUN_00c513e0), and may clear the caller's "keep moving" flag (condition FUN_004e4a20 / unit vf 0x48 > 0, not decoded). Open: the damage value of FUN_0085b6d0, the per-spell chance source, the stop condition and the CArenaMoatMine variant (vtable 00e8df24) — the engine's data-driven moat (Siege.EnterMoat) is not compared line by line |  |
| Effect duration ≤ 0 | GAP | 004e57d0, 00bbe150 (vf 0x260 → FUN_00a73da0 → FUN_004e4d00 / FUN_00a73ab0) | FUN_004e57d0 returns 0 for an Eval ≤ 0 and the effect is still created (FUN_004e4b20) and scheduled with that duration (FUN_00a73ab0 → scheduler vf 0x44 at the effect's +0x2c); what a 0-length timed effect does before its expiry event fires was not traced. The engine raises it to 1 turn (ApplyEffectSpell). Only reachable with spell power 0 for Base-0 durations (e.g. Curse: 0 + 1·power) |  |

## IDs

| ID | Status | Exe | Original behaviour / note | Test |
|---|---|---|---|---|
| `SPELL_UBER_CHAIN_LIGHTNING` | N/A | 0097c9f0 | boss spell 0xee: no shipped creature, hero or script casts it (only spell lists in multiplayer maps, GameMechanics/RefTables/UndividedSpells.xdb and the scripts/common.lua constant); immunity-table entries ported (combat_spells_s.md) |  |
| `SPELL_BOSS_FIREWALL` | N/A | 0097c9f0 | boss spell 0xef (Demonic targets immune): not cast by shipped content (same references as above) |  |
| `SPELL_UBER_METEOR_SHOWER` | N/A | 0097c9f0 | boss spell 0x11d: not cast by shipped content (same references as above) |  |
