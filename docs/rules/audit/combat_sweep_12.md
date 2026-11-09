# Combat audit — sweep 12: Magic Attack line, Rainbow Breath walk

Per-ID rows merged into `docs/rules/AUDIT_COMBAT.md` by `tools/re/audit/ledger.py` (fragments are read in natural
order, so rows here override sweep 1–11). Exe references are H5_Game.exe (GOG 3.1) addresses, analysed headless with
Ghidra 12.1.4 (private project copy), disassembly read directly where the decompiler loses registers. Tests are in
`tests/H5.Combat.Tests/AuditSweep12Tests.cs`; code in `src/H5.Combat/Battle.Sweep12.cs`.

Facts used below. Unit interface (vtable 00e4fba4): vf 0x40 = the position (FUN_00a6a040; the footprint's maximum
corner, `Battle.ExePos`), vf 0x44 = the size (FUN_008a1980, no argument), vf 0xc0 = "covers the cell" (FUN_00a6d980:
the cell is in the vf 0xc4 cell list), vf 0xc = the unit's army (whose vf 0x64 lists its units). FUN_00b3d010(ECX = out,
EDX = &P, a, b) lists the square ring x ∈ [Px − a, Px + b] × y ∈ [Py − a, Py + b] in the order of `Battle.ExeRing`.
FUN_00a62030's EAX argument is the strike record's +0x1c in the real strike (FUN_00a673c0 at 00a6757c / 00a68482) and 1
from the damage estimate FUN_00a63020.

## IDs

| ID | Status | Exe | Original behaviour / note | Test |
|---|---|---|---|---|
| `ABILITY_MAGIC_ATTACK` | FIXED | 00a62030 (00a62060..00a620dc), 00a5c420, 00b3e8c0, 00b3e720 (00b3e720..00b3e8b1) | a ranged strike with ability 0x33 takes FUN_00a5c420(attacker, target, width 0, every unit of the battle (FUN_00af4450), kinds 1 / 9 and 3, EAX) → FUN_00b3e8c0 → FUN_00b3e720(attacker position A / size sA, target position B / size sB, width 0, units, unbounded 1): in doubled units D = 2(B − A) − sB + sA per axis and, for a cell c of a unit, P = 2(c − A) − (sA − 1); the unit is on the line when one of its cells has P·D ≥ 0 and (Px·Dy − Py·Dx)² ≤ (0 + 1)²·\|D\|² — with the unbounded flag there is no P·D ≤ \|D\|² limit, so the line runs on past the target to the edge of the field; flag 0x1000 (Eldritch) stacks are then removed (FIXED in the Eldritch row). `Battle.OnMagicLine`; the engine drew a Bresenham line between the two minimum corners that stopped at the target. Not compared: the order of the victims (the exe's battle list order, the engine's main target first) and the kinds filter (the engine's: every living unit but own-side obstacles) | AuditSweep12Tests.MagicAttackLineRunsPastTheTargetAndIsHalfACellWide |
| `ABILITY_RAINBOW_BREATH` | FIXED | 00a62030 (00a62226..00a62309), 00b3ea30 (00b3ea30..00b3efa7), 00b3d010, 00a6d980, 00b3d850, 00b3d720 | with ability 0x8e and EAX = 0 (EAX ≠ 0 — the damage estimate — gets no area; the real strike's +0x1c was not traced and is taken as 0): radius clamp((Range(1, 10) + luck) / 2 + 1, 1, 5) (MATCH), then FUN_00b3ea30(attacker, target, radius) — read from the disassembly: the first cell is a battle-RNG Random(n) pick among the cells of ring 1 around the attacker's position P (FUN_00b3d010(P, size, 1)) that the target covers (vf 0xc0); none → no area. d = that cell − P per axis, a negative component + size − 1. Then radius − 1 steps with k = 0, m = 0 and T = {1, 2, 2} (stack 0x6c..0x74): on ring FUN_00b3d010(P, size + m, m + 1), i = the index of the current cell (the ring length when it is not on it), the candidates in order are ring[(i + n − 1) % n] (kind 0) when k ≤ 0 and k > −T[m], ring[(i + 1) % n] (kind 1) when k ≥ 0 and k < T[m], current + d (kind 2) when m < 2; Random(count) picks one (kind 0: k − 1, 1: k + 1, 2: m + 1), it is logged ("selected (") and appended. The victims (FUN_00b3d850 → FUN_00b3d720 flag 0): for each walk cell in order, each unit of the target's army (target vf 0xc → vf 0x64) that is valid (FUN_00a6afd0) and covers the cell, once, × 1.0. `Battle.RainbowWalk` / `RainbowVictims`; the engine hit 1..radius stacks of either side straight behind the target | AuditSweep12Tests.RainbowBreathIsARandomWalkOverTheTargetsArmy, RainbowBreathRadiusOneIsTheTarget |
