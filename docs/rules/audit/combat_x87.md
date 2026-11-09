# Combat audit — the damage pipeline's float arithmetic under the x87 control word

H5_Game.exe's WinMain 0x00d10bf0 → 0x009dc690 calls `_control87(0xa031f)`: every exception masked, `_PC_24` (24-bit
mantissa) and `_RC_CHOP` (round toward zero), re-set at game start (0x0041ecd0, 0x00c9fa80; see AUDIT_RMG.md, where the
RMG reproduces the stock maps only this way). So every FADD / FSUB / FMUL / FDIV / FSQRT of the combat code chops to a
float, a plain FISTP truncates, and the explicit `FNSTCW; OR AH,0xc; FLDCW; FISTP` sequences truncate too. The engine
helper is `H5.Core.Numerics.X87` (exact chopped Add / Sub / Mul / Div / Sqrt). Exe references are H5_Game.exe (GOG 3.1)
addresses, disassembly read directly (Ghidra 12.1.4, private project copy). Code: `src/H5.Combat/Battle.Attack.cs`
(`BaseDamageRoll`, `ComputeDamage`, `AttackDefenceFactor`, the pattern share in the strike loop). Tests:
`tests/H5.Combat.Tests/DamageX87Tests.cs`, `ExeFindingsTests.PiercingArrowScalesDefence`.

The ledger's core row "Damage pipeline … under the x87 control word" (ledger.py MECH) carries the status; details:

## Core mechanics (not ID rows)

| Site | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| Attack / defence factor | FIXED | 00a58d30 (00a58d30..00a58da1) | d = A − D (FLD / FSUB float, FSTP float); coefficient DamageIncrease (RPGStats +0x1d0) for d > 0, else DamageDecrease (+0x1d8), a value ≤ 0 replaced by 0.0 (00e0be10); p = coefficient · d; d > 0 → p + 1.0 (FADD 00e0a7e4), else 1.0 / (1.0 − p) (FSUBR, FDIVR); each step chopped. 0.05f · 2 + 1 = 1.0999999 (not 1.1), 1 / 1.25 = 0.79999995 (not 0.8). The engine used double arithmetic | DamageX87Tests.AttackBonusFactorChops, DefenceFactorChops, ExactFactorsStayExact |
| Damage accumulator and final truncation | FIXED | 00a5c870 (00a5c88e..00a5c8b1), 00a5c790 (00a5c80e..00a5c85f), 00a5a900 (00a5aae1..00a5ab06, 00a5ae11..00a5ae31), 00d11a60, 00e3dee4 | the base roll (00a597a0, FSTP float) is the first stack argument of 00a5c790 and of 00a58de0, which multiplies its factors onto it one by one; 00a5c790 stores it (FSTP float) and multiplies the range penalty 00a5c5a0 (FMUL float, 00a5c848); 00a5a900 multiplies the attack/defence factor onto that same argument (FMUL [arg 1], FSTP back) and its defender factors; at the end FLD acc, FMUL its 4th argument, FMULP the Forest Guardian factor (FUN_00a59340), FADD 1e-4f (00e3dee4 = 9.9999997e-05), `_ftol` 00d11a60 (FISTP qword under the chop control word) truncates; result < 1 → 1. Every step chopped, so the epsilon absorbs the chop error of small stacks only: 1000 peasants with attack 2 over defence 0 deal 1099 (1000 · 1.0999999 + 1e-4f), 2500 with defence 5 over attack 0 deal 1999 — the engine's double arithmetic gave 1100 / 2000. With 10 peasants both give 11 | DamageX87Tests.AttackBonusDamageTruncatesTheChoppedProduct, DefenceDamageTruncatesTheChoppedProduct |
| 4th argument of 00a5a900 | UNVERIFIED | 00a5ae15, 00a5c790 (arg 4), 00a673c0 (00a67bb9..00a67bc9), 00a5c870 (arg 5) | a float multiplied in after the defender factors and before Forest Guardian; from 00a673c0 it is a local of the strike loop — taken to be the luck multiplier, which ours multiplies first (the products chop in a different order: an ulp-level difference only). Not traced to the luck roll 00a6b820 here | |
| Forgetfulness kept count | FIXED | 00a597a0 (00a59809..00a5982f) | count = FISTP-chop((1.0 − v) · count): FSUBR from 1.0 (00e0a7e4), FIMUL count, OR AH,0xc → truncation. The engine used Math.Round (3 · 0.5 → 2 instead of 1) | DamageX87Tests.ForgetfulnessTruncatesTheKeptCount |
| Base roll arithmetic | FIXED | 00a597a0 (00a59888..00a5992a) | min == max (FUCOMPP of the two floats) → FILD count · min (FMUL float); count ≤ 3 → 0.0 + roll + roll … (FADD float, FST float each); count > 3 → three rolls summed (FADD, FSTP float), then FILD count · sum, · 0.33333334f (00e19950), chopped. The engine summed in double and multiplied count · Σ · (float)(1/3) in double | DamageX87Tests (min == max through every ComputeDamage test) |
| Base roll values | UNVERIFIED | 00a597a0 (00a598d4 / 00a59906: RNG vf 0x8(min, max)) | the per-creature roll is the battle RNG's vf 0x8 float range; ours is `Rng.RangeD` (double, cast to float) — the RNG stream and its float formula are not compared here | |
| Attacker-stage factors | UNVERIFIED | 00a58de0 | each factor is multiplied onto the accumulator in turn (chopped); 1 + x factors are clamp(x) then FADD 1.0 (FUN_00465610 / 00465640 = min / max) — ported as `OnePlus` / `OneMinus` on the float x. Not compared: the order of the factors inside 00a58de0 against ours, where the luck multiplier enters (see the 4th-argument row), and factors whose inner products the exe also chops (Retribution per · morale, Jousting per · cells and Cavalry Commander, Evasive base + per · level, Forest Guardian base + per · level) — ours computes those inner terms in double and rounds them to float once | |
| Range penalty | FIXED | 00a5c790 (00a5c843..00a5c84c) | FMUL float of the 00a5c5a0 factor onto the stored accumulator, chopped (×0.5 is exact; the walls halving shares the function) | |
| Pattern share | FIXED | 00a673c0 (00a67bf3..00a67c18) | FILD damage, FMUL float share, FISTP under chop. The engine multiplied int × float in C# float arithmetic (round to nearest) before the truncation | |
| Early 0x7b branch of 00a5c790 | UNVERIFIED | 00a5c790 (00a5c79e..00a5c7ff) | with ability 0x7b and a target vf 0x84 ≠ 0: FILD (vf 0x1d8 count) → float, · RPGStats +0x508, FISTP-chop — the whole pipeline is skipped; not traced to an ability name / engine path here | |

## Not yet checked for x87 semantics (other float → integer sites of the combat code)

Spell damage and its rounding (00976b90, `EXE_FINDINGS` "Spell damage rounding / empowered ×1.5"), the drain / heal
conversions (`DrainHeal`, Life Drain, Vampirism), the Lightning Breath shared roll (00a67b2c..00a67ba5: roll / targets),
Chain Shot shares (00a64ad0, which also calls 00a58de0 / 00a5c5a0 / 00a5a900 on its own path), Fire / Magma Shield and
Mark of Fire multipliers, Deadly Strike, damage over time (00bbb900), the ATB tick (already single precision, see the
ATB core row), the combat AI's estimates (out of scope here), and the attacker-stage factor order above.
