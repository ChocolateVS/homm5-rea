# Adventure audit — float arithmetic under the x87 control word

H5_Game.exe runs every float operation under `_control87(0xa031f)` (WinMain 0x00d10bf0 → 0x009dc690: `_PC_24`,
`_RC_CHOP`; re-set at game start 0x0041ecd0, 0x00c9fa80): each FADD / FSUB / FMUL / FDIV / FSQRT chops to a 24-bit
mantissa — also when the operands or the stored results are doubles — and a plain FISTP (Ghidra: ROUND) truncates like
the explicit `FNSTCW; OR AH,0xc; FLDCW; FISTP` casts. Helper: `H5.Core.Numerics.X87`. See also
`docs/rules/audit/combat_x87.md` (combat) and AUDIT_RMG.md (the RMG, where the stock maps reproduce only this way).
Addresses are H5_Game.exe (GOG 3.1) VAs, disassembly read directly (Ghidra 12.1.4, private project copy).

A single chopped product truncated at once gives the exact product's integer part (integers below 2^24 are floats and
chopping never crosses one): such sites match a double port. Differences come from chains of chopped steps, from
round-to-nearest conversions where the exe truncates, and from `+ 0.5` roundings.

| Site | Status | Exe | Original behaviour | Ours before | Test |
|---|---|---|---|---|---|
| Talented Warrior experience | FIXED | 00af3430 (decompiled; FILD level · 0.02 (double 00e79ac8) + 1.0 (00e12e00), FIMUL exp, chop) | each step chopped to 24 bits although the constants are doubles: level 10 → 0.19999999, 1.1999999, 1500 exp → 1799 | double (1800) | AuditBattleExpTests.BattleExperienceAppliesTheWeekLast |
| Neutral-army experience coefficient | MATCH | 00af358e..00af35aa | FILD exp · float, chop: one product | `X87.Mul` | |
| Experience modifiers (Enlightenment, Turban, Chain Mail, Educational) | MATCH | 00b4bbb0 | integer percent | integer | AuditBattleExpTests.ExperienceModifierIsIntegerPercent |
| Maximum movement | FIXED | 00b4dd40 (00b4ddb0..00b4e01d) | Wanderer / Beater: FILD ⌈level / div⌉ · PerLevel + Base, FSTP float, clamp 0..100, FADD 1.0001f / 1.0f, FIMUL B, chop; Rash: FILD level · per + 1.0001f, FIMUL B; Logistics FILD mastery · 0.1f + 1.0001f, FIMUL L; Boots of Speed / Golden Sextant FILD n · 0.25f + 1, FIMUL; Navigation FILD S · 1.5f — each step chopped, then truncated. MMH 5.5 values (B 2800, Rash 0.0033): Rash level 3 → 2827 | the exact double product of the floats (2828); the Wanderer / Beater p rounded to nearest | AdventureX87Tests.RashMovementChopsEachStep |
| Step cost Rash reduction | FIXED | 00b500a0..00b500c2 | FILD level · float · 100.0f, both FMULs chopped, FISTP chop | one double product | |
| Step cost | MATCH | 00ca9a70 | already chopped (earlier audit) | | |
| Computer town growth | FIXED | 00cd27b0 (00cd28e9..00cd2941) | f = chop(week · c); carry = chop(f − trunc(chop(0.01f + f))); n' = trunc(chop(chop(n · c) + carry)) — both conversions FISTP under OR AH,0xc. Easy (c 0.5), week 1: 3 → 2, 5 → 3 | round half to even (1, 2) | AiAuditTests.Computer_town_growth_scales_with_the_difficulty_coefficient |
| Resource scaling of map buildings | FIXED | 009d0d10 (009d0d63..009d0d85) | FILD v · k, FISTP under OR AH,0xc: truncation | round half to even | |
| Arcane Forge magic protection level | FIXED | 00c0a2f0 (00c0a340..00c0a3a3), 0046eae0 | FILD k · 10.0 chopped, FYL2X (extended, not under the precision control), FDIVP chopped, cube chopped, min 1, · π/2 chopped to a float; table sine: t = chop(x · 81.48733f), i = trunc(chop(t − 0.5)), chop((T[j+1] − T[j]) · chop(t − i)) + T[j]; · 85.0 chopped, truncated | double, i rounded half to even | |
| Arcane Forge sine table values | GAP | 00e11de8 (514 floats) | the exe's table: T[1] = 0.0122715 | (float)sin(i·2π/512): T[1] = 0.012271538 | |
| Daily income (heroes, mines, towns) | MATCH | 00b4e100, 00b08b20 | integer arithmetic | integer | |
| Altar artifact experience, Arcane Training cost | MATCH | 00b01120, (spell cost) | one float product, truncated | | |
| AI money values | FIXED | 00a325b0 → 00a31eb0, 00a31a10 (00a31a69..00a31af1), 00a32130 (00a321b6), 00a32230 (00a32466..00a324fa), 00a31c20 (00a31c34..), 00a31bb0 (00a31bbd..00a31bdb) | doubles in memory, every FMUL / FIDIV / FDIVR / FADD chopped: week factor FILD d · 1/7; value · factor / cost; potential Σ growth · value; pot / total; weak · ((Mx − need) / Mx · x); demand cost trunc(chop(c · weak) + 0.5); demand / supply · v; average (v0 + … + v5) · 1/7 | double arithmetic | |
| Army power for diplomacy / AI danger | UNVERIFIED | — | not traced to the exe (fan manual formula, `Adventure.ArmyPower`) | double | |
| AI danger reach | UNVERIFIED | 00872330 | move points · SearchDangerEnemyMobilityMultiplier + Bonus: the conversion not compared | `Math.Round` | |
| Morale / luck sums | MATCH | — | integer sums (`HeroMorale`, `HeroLuck`) | integer | |

## Not yet checked

Power Bargain spell cost (`Adventure.Spells`: 1 − r − level · per, clamped, · cost), the marketplace artifact sell price
(`Math.Round` of cost · coefficient, FUN_00623160), `GameDatabase` / `MapLoader` position rounding, the AI's other value
formulas in src/H5.Adventure/AI (threats, fight estimates, recruit / build scoring) and the hero level-up table.
