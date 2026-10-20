# Combat audit — creature artifacts (Academy micro-artifacts)

Per-ID rows merged into `docs/rules/AUDIT_COMBAT.md` by `tools/re/audit/ledger.py`. Exe references are H5_Game.exe
(GOG 3.1) addresses, analysed headless with Ghidra 12.1.4 (private copy of `tools/ghidra-projects/advint3`). Tests are in
`tests/H5.Combat.Tests/AuditMicroArtifactTests.cs`; the engine side is `src/H5.Combat/MicroArtifacts.cs`. Crafting, the
hero's bag and the transfer rules are in `docs/rules/AUDIT_ADVENTURE.md` (Arcane Forge rows).

## Data and readers (not ID rows)

| Mechanic | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| Effect enum | MATCH | 00ad2400 (to string), 00c0a560 | MicroArtifactEffectType 0 NONE, 1 WOUNDING, 2 PIERCING, 3 ARMOR_CRUSHING, 4 HEALTH, 5 DEFENCE, 6 MAGIC_PROTECTION, 7 LUCK, 8 MORALE, 9 SPEED, 10 HASTE (`H5.Core.Data.MicroEffect`) | |
| Artifact layout | MATCH | 004bcb50, 004bda10, 00c0a220, 00c0a2c0, 00c0a560, 00c0a770 | army stack +0x2c: vector of 8-byte {effect, level} entries; +0x10..+0x19 the per-effect sums as signed bytes (health, wounding, piercing, defence, armor crushing, magic protection, luck, morale, speed, haste); +0x20 the half-price flag. A stack carries one when the vector is non-empty (00c0a440). Every reader copies the summary with 00c0a2c0; its ten callers are 004ba970, 004ba9d0, 004bb170, 004bb2a0, 004bc0b0, 004bc190, 004bc470, 004e9b90 (AI army value), 0097af50, 00a629b0 | |
| Effect value (crafting) | MATCH | 00c0a2f0 (jump table 00c0a40c) | value from the hero's Knowledge K (hero vf 0x1c, min 1): wounding/health K/5+1, piercing/defence K/4+1, armor crushing/speed K/15+1, luck/morale K/10+1, haste K, magic protection trunc(85·sin(π/2·min(1, (ln(10K)/ln 600)³))) with the exe's 512-entry interpolated sine table (0046eae0, table 00e11de8; at π/2 it interpolates just below 1, so the maximum is 84: K 1 → 6, 10 → 47, 20 → 66, ≥ 50 → 84) | adventure `ArcaneForgeTests` |

## IDs

| ID | Status | Exe | Original behaviour / note | Test |
|---|---|---|---|---|
| `MAE_HEALTH` | FIXED | 004bc470 (local byte +4) | hit points = week-adjusted record HP + value, before Toughness, Ring of Life, the specialisations and the next-battle percentage; also without a hero. Engine had no creature artifacts | AuditMicroArtifactTests.PiercingDefenceHealthAndSpeedRaiseTheBaseValues |
| `MAE_PIERCING` | FIXED | 004bb170 (byte +6) | attack = record attack + value, then the hero's attack and bonuses | AuditMicroArtifactTests.PiercingDefenceHealthAndSpeedRaiseTheBaseValues |
| `MAE_DEFENCE` | FIXED | 004bb2a0 (byte +7) | defence = week-adjusted record defence + value, then the hero's defence; the Defend posture bonus (008a22f0) is computed on the sum | AuditMicroArtifactTests.PiercingDefenceHealthAndSpeedRaiseTheBaseValues |
| `MAE_SPEED` | FIXED | 004bc0b0 (byte +12) | speed = record speed + value, before the hero perks and the next-battle bonus | AuditMicroArtifactTests.PiercingDefenceHealthAndSpeedRaiseTheBaseValues |
| `MAE_HASTE` | FIXED | 004bc190 (byte +13, 00e15a80 = 0.01f, 00e0a7e4 = 1.0f) | the stack's initiative (after the hero factors) × (min(value, 50) · 0.01 + 1); applied with or without a hero | AuditMicroArtifactTests.HasteMultipliesInitiativeCappedAtFiftyPercent |
| `MAE_LUCK` | FIXED | 004ba970 (byte +10) | added to the week-adjusted army luck (kept in a Week of Balance, which drops only the combat effects 00a6abf0) | AuditMicroArtifactTests.LuckAndMoraleAreAdded |
| `MAE_MORALE` | FIXED | 004ba9d0 (byte +11) | added to the week-adjusted army morale; neutral-morale creatures (00b61ba0) still get 0 | AuditMicroArtifactTests.LuckAndMoraleAreAdded |
| `MAE_MAGIC_PROTECTION` | FIXED | 0097af50 (0097b2ec..0097b349, byte +9) | for a creature target, the protection factor × (1 − value · 0.01f); the spell then deals 1 − protection share as before | AuditMicroArtifactTests.MagicProtectionReducesSpellDamage |
| `MAE_ARMOR_CRUSHING` | FIXED | 00a629b0 (00a62cbc..00a62d08, byte +8), 004e3990, 004e4a80, 00989790 | after each strike on a live target (any strike, retaliations and shots included) the attacker's value is put on the target as SPELL_EFFECT_ARMOR_CRUSHING (vf 0x260, duration 0x7ffffffe); repeated applications add up | AuditMicroArtifactTests.ArmorCrushingAccumulatesPerStrikeAndSparesArmored |
| `MAE_WOUNDING` | MATCH | 00c0a700, 004e9b90 | no combat reader of the wounding byte; only the AI army value copies it. The forge cannot add it (00c0a700 rejects effect 1), so no stack carries it | |
| `MAE_NONE` | N/A | 00ad2400 | "This effect cannot be used." | |
| `SPELL_EFFECT_ARMOR_CRUSHING` | FIXED | 00989790 (case 0xb3), 004e3990, 004e4a80 | defence − value unless ARMORED (0x55), like Disrupting Ray (0xd) and 0xca; cumulative (004e3990 lists 0xd, 0x10, 0xac, 0xb3, 0xd1, 0x112, 0x12b, 0x13a, 0x13b). Open: whether Cleansing / dispel removes it (engine: dispellable) | AuditMicroArtifactTests.ArmorCrushingAccumulatesPerStrikeAndSparesArmored |
| `HERO_SKILL_ARTIFICIER` | MATCH | 004283b0, 00c0a700, 00c0a8f0, 00c0a450, 00cd2310 | no combat code: mastery (hero vf 0x174(0x11)) gates crafting (effects ≤ min(mastery, 3)), equipping (mastery > 0), the Ultimate half price (mastery ≥ 4) and the forge's upgrade to the hero's Knowledge; combat only reads the stack's artifact. Open: Melt Artifact consuming the artifact — the engine drops the effect count but keeps the hit point / attack / defence / speed / initiative part already in the stack's base values (the exe consumer was not traced) | |
