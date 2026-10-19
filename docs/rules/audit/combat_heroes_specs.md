# Combat audit — hero specialisations and artifact sets

Rows here override the computed status in `docs/rules/AUDIT_COMBAT.md` (merged by `tools/re/audit/ledger.py`).
Exe references are H5_Game.exe (GOG 3.1) addresses, analysed headless with Ghidra 12.1.4
(`tools/re/audit_specs/ghidra.sh`). RPGStats field offsets come from `tools/re/audit_specs/flatten.py` over the
combat reader chain (vectors take 12 bytes, so fields after an array shift by +0xc — e.g. Gating's fields follow
`PortionOfSummonedByGatingLevel`). Per-effect functions (004e4270, 00989790, 00989fd0) receive the effect's own
caster hero (effect vf 0xc), not the creature's owner.

## Core mechanics (not ID rows)

| Mechanic | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| Gating portion | FIXED | 0085fe60 | base = PortionOfSummonedByGatingLevel[mastery−1] (0.25 if the table does not have exactly 4 entries; engine used 0.3); bonuses are additive: Gating Mastery clamp(GatingMasteryPortionBonus,−1,1000); Critical Gating adds clamp(CriticalGatingPortionBonus,−1,1000) × base on an ability roll (00a5a170, no second roll) of trunc(100·clamp(clamp(Const,0,1) + Luck·clamp(PerLuck,0,1) [Luck > 0], 0, 1)) — engine doubled the whole portion on a continuous roll; Infernal Looms count × InfernalLoomPortionBonus (town-side, absent in duels); Gate Keeper level × clamp(per-level,−1,1000); the Demonic 2-piece set (artifact set 10, hero vf 0x328 > 1) multiplies only the base by 1 + pct·0.01 (engine multiplied the total); total clamped to [0, 1000]; n = floor(count·total). The exe then adds +1 on a random roll under a condition (00860524) not pinned down — engine keeps max(1, n). | AuditSpecSetTests.GatingPortionAddsBonusesAndScalesOnlyTheBase |
| Battle-context ×1.5 | FIXED | 00bcdf10 | the id is the week of the month (battle vf 0x3c): 0x49 WEEK_OF_MIGHT / 0x4b WEEK_OF_MIGHTNMAGIC — see `combat_heroes_skills_h.md` |  |
| HP percentage multiplier | GAP | 004bc470 (hero vf 0x330 struct +0x10) | source identified: the script battle bonus HERO_BATTLE_BONUS_HITPOINTS (percent) — see `combat_heroes_skills_h.md`; not wired |  |

## IDs

| ID | Status | Exe | Original behaviour / note | Test |
|---|---|---|---|---|
| `HERO_SPEC_HASTE` | FIXED | 004e4270 (case 0x18), 008a4330 | Haste factor 1 + max(0, v) + Expediter level × clamp(Expediter_HasteBonusPerLevel, 0, 100) — the Expediter is the effect's caster hero (effect vf 0xc); engine used the creature's own hero | AuditSpecSetTests.ExpediterBonusFollowsTheCasterOfHaste |
| `HERO_SPEC_WHITE_KNIGHT` | FIXED | 004e4270 (case 0x36) | Prayer initiative 1 + Prayer_InitiativeBonus × (WhiteKnight_PrayerMultiplier if the effect's caster is a White Knight); engine checked the creature's own hero | (same code path as Expediter) AuditSpecSetTests.ExpediterBonusFollowsTheCasterOfHaste |
| `HERO_SPEC_BOMBARDIER` | FIXED | 0085f0f0 | ballista shot carries Fireball (spell 5) cast at a fixed mastery 3 (Expert) with power (level−1)·PerLevel / Divisor + Base (integer, Base −1, /5, ×1, unclamped); engine used the hero's own mastery and Base + floor(level/Divisor)·PerLevel clamped at 0 | AuditSpecSetTests.BombardierPowerUsesLevelMinusOne |
| `HERO_SPEC_GATE_KEEPER` | FIXED | 0085fe60 | additive gating bonus level × clamp(GatekeeperHeroSpecPerLevelPortionBonus, −1, 1000); see the Gating core row for the other terms fixed alongside | AuditSpecSetTests.GatingPortionAddsBonusesAndScalesOnlyTheBase |
| `HERO_SPEC_FOREST_GUARDIAN` | MATCH | 00a59340 | Sylvan (town 4) attackers vs a favoured enemy of their hero (hero vf 0x25c contains target id): ×(1 + clamp(ForestGuardian_DamageBonus + level·PerHeroLevel, 0, 10)) — offsets 0x858/0x85c |  |
| `HERO_SPEC_MALEDICTOR` | MATCH | 00989790 (case 0xb) | Curse also lowers defence by (level + 2) / 3 (integer), skipped for ABILITY_ARMORED (0x55) — engine subtracts Value2 inside the not-Armored branch |  |
| `HERO_SPEC_DARK_EMISSARY` | FIXED | 00989fd0 (case 0x3b) | Death Scream morale −trunc(DeathScream_MoralePenalty · f), f = DarkEissary_DeathScreamMultiplier (else 1) + BansheeWhail_DeMorale with 2+ Necromancers pieces; Howl of Terror (0x6f) −6. Engine took max(multiplier, 2) for the set, so Dark Emissary + set gave 2 instead of 3. Dark Emissary does not affect initiative (004e4270) | AuditSpecSetTests.DeathScreamMoraleCombinesDarkEmissaryAndTheSet |
| `HERO_SPEC_FEATHER_MAGE` | FIXED | 00bc1eb0 | hero initiative ×(1 + clamp(FeatherMage_InitiativeBonusBase + level·PerLevel, 0, 5)); engine had no Base term and no clamp | AuditSpecSetTests.FeatherMageInitiativeIsClamped |
| `HERO_SPEC_POISONER` | FIXED | 009844e0 | per enemy stack (exe pre-filters valid targets with 00982010 — not ported), ability roll of trunc(100·clamp(Poisoner_PlagueProb + level·PerLevel, 0, 1)); hits get Plague (spell 0xe). Engine used a continuous unclamped chance | AuditSpecSetTests.PoisonerAndSacredHammerChancesAreClampedWholePercents |
| `HERO_SPEC_SACRED_HAMMER` | FIXED | 00bbebd0 | light-school (school 2) spell on a friend: ability roll of trunc(100·clamp(ChanceToCastBase + level·PerLevel, 0, 1)) casts SacredHammer_SpellToCast; engine used a continuous unclamped chance | AuditSpecSetTests.PoisonerAndSacredHammerChancesAreClampedWholePercents |
| `HERO_SPEC_BLOODDRINKER` | FIXED | 00a5c300 | each stack killed adds effect 0x13a (infinite) of ((level−1)/LevelDivisor)·PerDividedLevel + Base attack to every creature of the hero's army (accumulating, per the spec text); engine used Base + floor(level/Divisor)·Per (shared Specials.LevelFormula corrected) | AuditSpecSetTests.LevelFormulaUsesLevelMinusOne |
| `HERO_SPEC_MIND_CLEANER` | FIXED | 00983790 (0098431f-009843f2) | combat spec (was marked economy-only): after an enemy hero's spell resolves, ability roll (no second roll) of 100 / max(1, circle + 1) − caster level + Mind Cleaner level (integer); success blocks that spell in the caster's book for the rest of the combat (hero vf 0x2b8). Hero roles in the subtraction read from the call order; levels taken as hero level (vf 0x20) | AuditSpecSetTests.MindCleanerBlocksTheEnemySpell |
| `HERO_SPEC_IMPREGNABLE` | MATCH | 0097c9f0 | magic resistance + level + 5 percent (verified by the spells audit, Battle.SpellAudit) |  |
| `HERO_SPEC_GOBLIN_KING` | MATCH | 008a0b60 | Treachery threshold lowered by GoblinKing_TreacheryBonusPerLevel × level (EXE_FINDINGS Treachery) |  |
| `HERO_SPEC_INFERNAL_MACHINE` | N/A | 0085f0f0, 008cf270, 008cfbc0 | test hero only (Tank_Hero); shares the Bombardier fireball path in 0085f0f0 |  |
| `SPELL_ARTFSET_NECROMANCERS_DEBUFF` | FIXED | 008a8140, 008a6c40, 008a4800, 008a22f0 | was not implemented: a stack failing its morale check gets effect 0xce when the opposing hero has > 3 Necromancers pieces; removed at the stack's next turn start; while present attack and defence are trunc(value·(1 − pct·0.01)), pct = Necromancers_4_EnemyCreature_BadMoralePenaltyToAttackDefencePercents (RPGStats+0x1198). The 2-piece Death Scream initiative doubling (BansheeWhail_DeInitiativeMultiplier, +0x1190, 004e4270) was also missing | AuditSpecSetTests.NecromancersDebuffCutsAttackAndDefence, DeathScreamInitiativeDoublesWithTheNecromancersSet |
| `SPELL_ARTFSET_LIONS_DEMORALIZED` | FIXED | 008cf7ff-008cf86d, 00989fd0 (case 0x129) | was not implemented: a Knight (hero class 1) with 2+ Lions pieces (set 3) marks every hero-strike target with effect 0x129 (infinite, not re-applied if present); the mark lowers morale by Lions_2_HeroAttack_DeMorale (+0x1178) | AuditSpecSetTests.LionsSetDemoralisesTheKnightsTargets |
