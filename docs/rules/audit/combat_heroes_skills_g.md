# Combat audit — hero skills, part G (magic & other)

Rows here override the computed status in `docs/rules/AUDIT_COMBAT.md` (merged by `tools/re/audit/ledger.py`; this file
sorts after `combat_heroes_skills.md`, so its rows win). Exe references are H5_Game.exe (GOG 3.1) addresses, analysed
headless with Ghidra 12.1.4 (`tools/re/audit_skills_g/ghidra.sh`).

Spell protection (exe `FUN_0097d2a0`, ECX = spell, EDX = caster unit, stack: target, …, a "predict only" flag, a "may
roll" flag): resistance chance `FUN_0097c9f0` (100 = immune; rolled only when allowed and not predicting), then — for
the spells `FUN_004e5cb0` lists (Magic Arrow … Armageddon, Holy/Unholy Word, Land Mine, Wasp Swarm, Explosion, Fire/Magma
Shield, Chilling Bones, Firewall, Stone Spikes, Deep Freeze, Divine Vengeance, Shout of Many, Call Storm, Liquid Flame
Breath, and Disrupting Ray when the caster has skill 0x2a) — the target side's protection product `FUN_0097af50` and
the caster's Irresistible Magic. `FUN_0097af50` (ECX = caster, EAX = target): Magic Fist (spell 2) and spell 0x10 get no
protection; target hero's Protection ×0.85 (mastery 0x25 > 0, any spell); Boneward ×0.8 and Sap Magic ×0.8 only when the
spell school (field +0x88, enum 00acff90: 0 = MAGIC_SCHOOL_DESTRUCTIVE) is 0, Sap Magic also only when the caster's
vfunc 0x78 (its hero object) is non-null; Luck of the Barbarian ×clamp(1 − (luck>0)·MagicProofPerLuck, 0, 1); then
per element the protection artifacts, the Fire Protection buff and FIRE_PROOF_50; Magic Proof 50/75/25 (exclusive
else-chain), Rune of Dragonform, creature micro-artifact percent (vfunc 0x6c), Blade Barrier, Dwarven 2-piece set.
Damage boosts (Mark of Fire, elemental vulnerability) are NOT part of this product: they multiply the raw damage in
`FUN_00977a80`.

| ID | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| `HERO_SKILL_SPELLPROOF_BONES` | FIXED | 0097af50 (0097afc0) | Boneward: ×0.8 (constant 00e1857c) for Destructive spells on every stack of the hero; no undead condition (text: "any Destructive Magic spells to all troops"). Engine also required an undead target | AuditSkillTestsG.BonewardProtectsLivingTroopsFromDestructiveSpellsOnly |
| `HERO_SKILL_MAGIC_CUSHION` | FIXED | 0097af50 (0097b009) | Sap Magic: ×0.8 only for Destructive spells cast by a hero (caster vfunc 0x78 non-null). Engine reduced every spell from any caster | AuditSkillTestsG.SapMagicOnlyReducesDestructiveSpellsOfHeroes |
| `HERO_SKILL_LUCK_OF_THE_BARBARIAN` | FIXED | 0097af50 (0097b051..0097b0ae) | when the stack's luck (vfunc 0x1ac) is positive: ×clamp(1 − 1·LuckOfTheBarbarian_MagicProofPerLuck, 0, 1) — the luck test is SETG (0/1), so it does not scale with luck. Engine scaled by the luck value (luck 3 → ×0.85 instead of ×0.95) | AuditSkillTestsG.LuckOfTheBarbarianIsAFlatFactorWhenLuckIsPositive |
| `HERO_SKILL_ELEMENTAL_OVERKILL` | FIXED | 00977a80, 0097d600 | Exorcism: ×2 against summoned targets (target vfunc 0x70 summoner, its vfunc 0x58) after Cold Death and before protection, for every damage spell reaching FUN_00977a80 through FUN_0097d600 (bolt/chain/area resolvers, Holy/Unholy Word, Earthquake/Wasp 0097d7b0, 0097f610, 00984a00 …) — no school check. Engine limited it to Destructive spells | AuditSkillTestsG.ExorcismDoublesEveryDamageSpellAgainstSummonedStacks |
| `HERO_SKILL_CHILLING_STEEL` | FIXED | 00a5c000, 0097d740, 0097d2a0 | 0.1 × damage dealt as cold damage via FUN_0097d740 with stand-in spell Ice Bolt (ECX = 4), no caster: immune or resisted (roll) → 0, else trunc((1 − p/100)·amount + 0.5) with p the target side's protection percent. Engine skipped protection and the resistance roll | AuditSkillTestsG.PerkElementalDamageIsReducedByProtectionOfItsStandInSpell |
| `HERO_SKILL_CHILLING_BONES` | FIXED | 00a5bc50, 0097d740 | 0.05 × damage dealt to an attacker of an undead stack, via FUN_0097d740 with stand-in spell SPELL_SKILL_CHILLING_BONES (ECX = 0xcf; school Special, element water — so Boneward / Sap Magic don't apply). Engine skipped protection | AuditSkillTestsG.PerkElementalDamageIsReducedByProtectionOfItsStandInSpell |
| `HERO_SKILL_ANCIENT_SMITHY` | FIXED | 00a5ed90 (call 00a5effb), 0097d740 | Fiery Wrath: 0.1 × damage dealt (creatures only) as fire damage via FUN_0097d740 with stand-in spell Fireball (ECX = 5), no caster; no Mark of Fire multiplier (FUN_00977a80 is not on this path). Engine skipped protection and doubled it on Mark of Fire targets | AuditSkillTestsG.PerkElementalDamageIsReducedByProtectionOfItsStandInSpell |
| `HERO_SKILL_WILDFIRE` | FIXED | 00a5ed90, 0097d740 | Flaming Arrows: for a shot (vfunc 0x7c type 1) of a hero with the perk the extra fire amount is 50.0 (0x42480000), dealt through the same FUN_0097d740 call (Fireball stand-in): protection and resistance roll apply, no Mark of Fire. Engine dealt a flat 50 (×2 on Mark of Fire) | AuditSkillTestsG.PerkElementalDamageIsReducedByProtectionOfItsStandInSpell |
| `HERO_SKILL_PROTECTION` | MATCH | 0097af50 (0097af97) | target hero's mastery of skill 0x25 > 0 → protection product starts at 0.85 (0x3f59999a), for any spell in FUN_004e5cb0's list (school-independent); the 14 % truncation is handled by the protection percent (spells audit) |  |
| `HERO_SKILL_FIRE_PROTECTION` | MATCH | 0097af50 (0097b142) | fire element: ×0.5 when the target carries the perk's buff SPELL_SKILL_FIRE_PROTECTION (0x61, the skill's spellBuffs), and ×0.5 again with FIRE_PROOF_50. The engine's perk ×0.5 is the same factor. Not compared: whether the buff can be dispelled |  |
| `HERO_SKILL_BARBARIAN_FIRE_PROTECTION` | MATCH | 0097af50 (0097b142) | same buff SPELL_SKILL_FIRE_PROTECTION (Skills.xdb spellBuffs), same ×0.5 |  |
| `HERO_SKILL_INVOCATION` | MATCH | 0097d2a0 (0097d42c..0097d47f), 004e5cb0 | Irresistible Magic: for spells listed by FUN_004e5cb0, share' = (1 − {0,20,40,50,75}[caster hero mastery of 0x12] %) × share (table 00f46f18), also when fully resisted (share 1). Engine formula identical (ApplyProtectionPercent); spell-set difference noted below |  |

Notes for other partitions (not changed here):

- **Damage boosts clamped away (spells/abilities):** the engine multiplies Mark of Fire (×2) and the gargoyle
  vulnerability auras (×1.5) into `SpellDamageMultiplier`, whose result `ApplyProtectionPercent` clamps to ≤ 1 — so the
  boosts never increase spell damage. The exe applies them to the raw damage in `FUN_00977a80` (effect 0xf6 with fire;
  target flags 0x40/0x80/0x100/0x200 vs elements 2/3/1/4 → ×VulnerabilityDamageCoef). `SpellDamageMultiplier` now has a
  `protectionOnly` switch that leaves them out, for whoever moves them.
- **Plague** is not in FUN_004e5cb0's list (no protection product, no Irresistible), while the engine's `IsDamageSpell`
  includes it.
- **Hellfire** (Demonic Fire buff 0x5d, Demonic Retaliation 0x5c, Demonic Flame ×1.5) is computed in the same function
  00a5ed90 and ends in the same Fireball-stand-in protection call; its chance roll is still ambiguous, so it was left on
  `DealElementalDamage` (unprotected).
- **Fire / Magma Shield** (00a5bcf0 / 00a5bdd0) use the same protection with stand-ins SPELL_EFFECT_FIRE_SHIELD (0xcd) and
  SPELL_EFFECT_MAGMA_SHIELD (0xf8); `PerkElementalAmount` can serve them (abilities partition).
- **UI predictions** (`Battle.UiPrediction.cs`) still show the perks' extra damage before protection.
