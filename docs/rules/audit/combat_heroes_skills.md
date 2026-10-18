# Combat audit — hero skills and perks

Rows here override the computed status in `docs/rules/AUDIT_COMBAT.md` (merged by `tools/re/audit/ledger.py`;
this file sorts after `combat_heroes.md`, so its rows win). Exe references are H5_Game.exe (GOG 3.1) addresses,
analysed headless with Ghidra 12.1.4 (`tools/re/audit_skills/ghidra.sh`). Hero vtable slot `0x290` = HasSkill;
unit vfunc `0x190` = "moves by endpoints only" (flying), as used by the flee/move code (`008a0250` callers).

Stack damage pipeline (exe `FUN_00a61600`, entered from `FUN_00a616e0` by creature strikes, hero strikes, damage
spells, the catapult, and directly by Corrupted Soil / Ride-by attack): Arcane Armor absorption (`FUN_00a59660`)
→ Blood Rage damage-to-rage transfer (`FUN_00a5b9d0`, HealthToRageTransferCoef) → Last Stand (`FUN_00a594f0`).
Perk extra elemental damage goes through `FUN_0097d740`: trunc((1 − p/100)·amount + 0.5) in chop mode.

| ID | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| `HERO_SKILL_LAST_STAND` | FIXED | 00a59490, 00a594f0, 00a61600 | a stack of 2+ creatures (vfunc 0x1c count > 1) hit for ≥ its total HP (vfunc 0x1dc) keeps 1 HP; no once-per-combat flag (re-triggers whenever the stack is back to 2+). Applies in the common damage pipeline (strikes, hero strike, damage spells, Corrupted Soil). Engine allowed it only once per stack | AuditSkillTests.LastStandHasNoOncePerCombatLimit |
| `HERO_SKILL_SOIL_BURN` | FIXED | 00a61a60, 00a66050 | once per move of an enemy stack (called at the start of the move routine); flying stacks (vfunc 0x190) are exempt; damage round(level · clamp(SoilBurnDamagePerHeroLevel, 0.1, 1000)) through the stack damage pipeline. Engine also hurt flyers | AuditSkillTests.CorruptedSoilSparesFlyers |
| `HERO_SKILL_DEATH_SCREAM` | FIXED | 00989c40, 00989fd0 | effect 0x3b: morale −= round(DeathScream_MoralePenalty · m), luck −= round(DeathScream_MoralePenalty · m), m = DarkEissary_DeathScreamMultiplier for the Dark Emissary (else 1) plus a set constant (RPGStats +0x1188 morale / +0x118c luck) with 2+ pieces of the necromancer set (FUN_004d8d60). Engine took a flat 1 luck point (Dark Emissary: should be 2). Set constant left to the artifacts audit | AuditSkillTests.DeathScreamLuckPenaltyScalesWithTheMultiplier |
| `HERO_SKILL_CHILLING_STEEL` | FIXED | 00a5c000 | strikes add cold damage 0.1 × the damage dealt (Last-Stand clamped, capped at the target's HP), dealt via FUN_0097d740 (rounded half up after protection). Engine truncated (9 damage → 0 instead of 1). The protection percent FUN_0097d2a0 for this call is not modelled | AuditSkillTests.ChillingPerkDamageRoundsHalfUp |
| `HERO_SKILL_CHILLING_BONES` | FIXED | 00a5bc50 | melee hit on an ABILITY_UNDEAD stack whose hero has the perk: the attacker takes 0.05 × the damage dealt as cold damage when ≥ 0.5, via FUN_0097d740 (rounded half up). Engine truncated | AuditSkillTests.ChillingPerkDamageRoundsHalfUp |
| `HERO_SKILL_ANCIENT_SMITHY` | FIXED | 00a5ed90 | Fiery Wrath: creature (unit vfunc 0 == 1, so not war machines) strikes add fire damage 0.1 × the damage dealt via FUN_0097d740 (rounded half up after protection). Engine truncated. Same function also handles Hellfire and Flaming Arrows | AuditSkillTests.ChillingPerkDamageRoundsHalfUp |
| `HERO_SKILL_DEADLY_COLD` | FIXED | 00977a80, 0097d600 | Cold Death: Ice Bolt (4), Circle of Winter (6), Deep Freeze (0x117) add one creature's HP (vfunc 0x1a8) to the raw spell damage BEFORE protection (FUN_0097d600 applies FUN_0097d2a0 to the sum). Engine added the HP after protection, pre-scaled by the protection share | AuditSkillTests.ColdDeathAndExorcismActBeforeProtection |
| `HERO_SKILL_ELEMENTAL_OVERKILL` | FIXED | 00977a80 | Exorcism: ×2 against summoned targets (vfunc 0x70 summoner, its vfunc 0x58), applied after Cold Death and before protection. Engine doubled after protection. The exe has no school check in this function (FUN_00977a80 serves every damage spell); the engine keeps its Destructive-only condition — not changed | AuditSkillTests.ColdDeathAndExorcismActBeforeProtection |
| `HERO_SKILL_ROAD_HOME` | FIXED | 008a0e30, 008a1e50 | Familiar Ground: speed += max(0, RoadHome/SpeedBonus) when the battlefield terrain (battle vfunc 0x60) equals RoadHome/NativeTerrain (TT_GRASS). Engine had no implementation | AuditSkillTests.FamiliarGroundAddsSpeedOnGrassOnly |
| `HERO_SKILL_WILDFIRE` | MATCH | 00a5ed90, 00a5a900 (00a5aa76), 00a75b00 | Flaming Arrows: the Ballista (war machine id 1) deals 50 extra fire damage (float 50.0 via FUN_0097d740) and the target's defence is halved (SAR 1 at 00a5aa7d) — the manual's "defence 0" is wrong; 00a75b00 only picks the FIRE_BALLISTA visual |  |
| `HERO_SKILL_DWARVEN_LUCK` | MATCH | 00977790, 0097d2a0 | a failed resistance roll by a stack whose hero has the perk is re-rolled once (Range(1,100) < chance) |  |
| `HERO_SKILL_MAGIC_MIRROR` | MATCH | 00983150 | chance round(level · MagicMirrorLevelMultiplier + 10), ability roll FUN_00a5a170 (Range(0,100) < chance); Spell Twister second roll round((Radiant_MirrorProb + level·Radiant_MirrorProbPerLevel)·100) |  |
| `HERO_SKILL_DEAD_LUCK` | MATCH | 00989c40 | luck −1 for stacks whose ENEMY hero (army vfunc 0x10) has the perk |  |
| `HERO_SKILL_ABSOLUTE_FEAR` | MATCH | 00989fd0 | under Death Scream (effect 0x3b) morale −6 more when the screaming (enemy) hero has the perk (constant 6 in code) |  |
| `HERO_SKILL_BATTLE_ELATION` | MATCH | 008a0db0 | on good morale, the stack's rage gains BattleElation_RPOnGoodMorale when its hero has the perk (rage vfunc 0x7c) |  |
| `HERO_SKILL_IMBUE_BALLISTA` | MATCH | 00a59be0 | the war machine with id 1 (Ballista) casts the imbued spell (FUN_0097ea20) on its target when its hero has the perk |  |

## Battle experience (adventure side)

| Item | Status | Evidence | Notes |
|---|---|---|---|
| Winner post-battle steps | traced | 00af5b80 → 00af38b0 (No Rest for the Wicked), 00af5460 (necromancy raise 00c26be0), 00af4010 (Spoils of War / Courtier), 00af2740 (kill tallies) | experience is not awarded in this chain; its call site is still unknown |
| +500 for the defeated hero | not traced in exe | manual p.298: only when the loser neither flees nor surrenders | engine gives the defender +500 even when the attacker fled; left unchanged pending the exe |
