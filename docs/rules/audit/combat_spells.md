# Combat audit — spells

Rows here override the computed status in `docs/rules/AUDIT_COMBAT.md` (merged by `tools/re/audit/ledger.py`).
Exe references are H5_Game.exe (GOG 3.1) addresses, analysed headless with Ghidra 12.1.4
(`tools/re/audit_spells/ghidra.sh`, private project copy). Tests are in `tests/H5.Combat.Tests/AuditSpellTests.cs`.
All float→int conversions in the spell code run with the x87 control word set to truncation
(`FNSTCW; OR AH,0x0C; FLDCW; FISTP` — 00976c0d, 00976c53, 0097d5c4, 0097d6ad, 00a34836, 00978290): the
decompiler's `ROUND` is a truncation toward zero there.

## Core mechanics (not ID rows)

| Mechanic | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| Spell base damage | FIXED | 00976b90 | trunc(max(1, Base + PerPower·SP)); empowered trunc(×1.5). Engine rounded to nearest. | SpellBaseDamageTruncates |
| Protection percent | FIXED | 0097d600, 0097d2a0, 0097af50 | share P = 1 − clamp(product of protections, 0, 1) in single precision (1 for immune / resisted), × (1 − {0,20,40,50,75}[Irresistible Magic mastery] %), truncated to a whole percent; damage = trunc((1 − P%)·dmg). Protection alone → 14 % (manual p.321). Engine multiplied by the raw factor. | ProtectionPercentIsTruncated |
| Irresistible Magic vs resisted | FIXED | 0097d2a0 | a damage spell resisted by the roll still deals the Irresistible Magic share (same as immune; manual p.322). Engine dealt 0. | ProtectionPercentIsTruncated |
| Magic resistance chance | FIXED | 0097c9f0 (0097cfab..0097d0bf) | integer %: +15 Resistance effect, Impregnable level+5, Plate Mail of Stability +20, Boots of Interference +10; Aura of Magic Resistance → trunc(clamp(0.3+0.0001,0,1)·100 + sum); clamp 0..100 (100 = always resists). Engine: continuous, capped at 95 %, no artifacts. | MagicResistanceChanceSumsLikeTheExe |
| Resistance roll | FIXED | 0097d2a0 | resisted iff rng.Range(1,100) < chance (so 15 % resists 14/100); Dwarven Luck second roll; Stormcaller (air) and caster's Staff of Magi skip the roll (0097cf5b, 0097cf89). | MagicResistanceChanceSumsLikeTheExe |
| Spell cost chain rounding | FIXED | 00a34836, 00a34968, 00a349b9, 00978290, 0097844f | every conversion truncates (Arcane Training 7 → 5, not 6). The earlier audit used round-half-even. | ArcaneTrainingCostTruncates |
| Initiative modifiers | FIXED | 008a4330, 004e4270 | base × enemy Prudent/Master of Initiative factor × Π per-effect factor (Slow, Haste, Hypnotize, Prayer, Dash, Wound, Death Scream) × Storm Wind × Fog Veil; flat bonuses added after. Engine added the factors. Gated creatures (effect 0x9f) return base × max(InitiativeFactor,0) before any other modifier — not applied (abilities partition). | InitiativeEffectFactorsMultiply |
| Elemental casters | FIXED | 00976db0 (FUN_008a08c0) | spell damage of the four elementals (creature ids 85..88) × Neutral/SpellDamageMultiplier (0.3), truncated. | (DealSpellDamage) |
| Elemental damage artifacts | FIXED | 00976db0 | caster's Titan's Trident / Phoenix Feather Cape / Evercold Icicle / Earthsliders: ×1.5 truncated for air / fire / water / earth spells. A second fire ×1.5 branch (hero field +0x20, 600() == 4) is not identified. | (ElementArtifactDamage) |
| Elemental protection artifacts | FIXED | 0097af50 | target hero's Ring of Lightning Protection (air ×0.5), Iceberg Shield (fire ×0.5), Dwarven Smithy Hammer (fire ×0.75), Dragon Flame Tongue (water ×0.5), Bearhide Wraps (water ×0.75), Rigid Mantle (earth ×0.5). | (ElementProtectionArtifacts) |
| Artifact spell immunities | FIXED | 0097c9f0 (0097cdff) | target hero's Dwarven Mithral Cuirass/Greaves/Helmet/Shield → immune to Implosion/Slow/Blind/Berserk; Ring of Death → Holy Word. | ArtifactsGrantSpellImmunities |
| Anti-Magic / opposite blessing resistance | MATCH | 004e41c0 | Magical Immunity: spell level ≤ trunc(value) → 100; an opposite blessing gives 50 (100 with the effect's +0x52 flag) — engine dispels the opposite with Eternal Light 50 %. | |

## IDs

| ID | Status | Exe | Original behaviour / note | Test |
|---|---|---|---|---|
| `SPELL_HASTE` | FIXED | 004e4270 (case 0x18), 008a4330 | initiative ×(1+max(v,0)) [+ level·Expediter_HasteBonusPerLevel for an Expediter hero], multiplied with the other factors | InitiativeEffectFactorsMultiply, ExpediterBoostsHaste |
| `SPELL_SLOW` | FIXED | 004e4270 (case 0xc), 0097c9f0 | initiative ×(1−clamp(v,0,1)); immune: ABILITY_IMMUNITY_TO_SLOW, Dwarven Mithral Greaves | InitiativeEffectFactorsMultiply, ArtifactsGrantSpellImmunities |
| `SPELL_HYPNOTIZE` | MATCH | 004e4270 (case 0x14), 0097c9f0 | initiative ×clamp(v,0,1); immune: mind immunity / IMMUNITY_TO_HYPNOTIZE; tier ≥ 7 needs Expert (caster mastery < 3 → 100) | |
| `SPELL_BLESS` | MATCH | 008a2630 | min += (max−min)·clamp(v,0,1) | |
| `SPELL_CURSE` | MATCH | 008a2630, 0097c9f0 | (only without Bless) max = min + (max−min)·clamp(v,0,1) if lower; immune: IMMUNITY_TO_CURSE | |
| `SPELL_BERSERK` | FIXED | 008a2630, 0097c9f0 | damage ×max(1+v,0); immune: mind immunity / IMMUNITY_TO_BERSERK / Dwarven Mithral Shield (added). Exe checks only the IMMUNITY_TO_MIND_CONTROL ability (008a34c0 never grants it to undead) while the engine also treats undead/elemental/mechanical as mind-immune — open question (another targeting layer may block them) | ArtifactsGrantSpellImmunities |
| `SPELL_BLIND` | GAP | 0097c9f0 (0x13 → ability 0x10) | immune only with ABILITY_IMMUNITY_TO_BLIND or Dwarven Mithral Helmet (helmet added). Engine also makes undead/elemental/mechanical (living-only + mind list) immune — not confirmed in the exe, left unchanged | ArtifactsGrantSpellImmunities |
| `SPELL_FORGETFULNESS` | GAP | 0097c9f0 (0x11 → ability 0x13) | exe immunity: IMMUNITY_TO_MIND_CONTROL only; engine adds undead/elemental/mechanical (see Berserk) | |
| `SPELL_DISRUPTING_RAY` | FIXED | 00976db0 (0xd + spec 0x2a), 0097c9f0 | Disrupter deals trunc((lvl−1)·PerLevel/trunc(Div) + Base) with it — the engine checked a non-existent SPELL_VULNERABILITY id, so the bonus never fired; war machines immune unless Disrupter | DisrupterDamagesWithDisruptingRay |
| `SPELL_MAGIC_ARROW` | FIXED | 00976b90, 0097d7b0, 0097d600 | single-target damage: truncated base, protection percent | SpellBaseDamageTruncates |
| `SPELL_MAGIC_FIST` | FIXED | 0097c9f0 (spell 2 → 0), 00976b90 | never resisted; truncated base | SpellBaseDamageTruncates |
| `SPELL_LIGHTNING_BOLT` | FIXED | 00976b90, 0097d7b0 (→ 004e6a10) | truncated base / protection; stun rider 004e6a10 not compared | ProtectionPercentIsTruncated |
| `SPELL_ICE_BOLT` | FIXED | 00976b90, 0097d7b0 (→ 004e6600) | truncated base / protection; freeze rider 004e6600 not compared | ProtectionPercentIsTruncated |
| `SPELL_IMPLOSION` | FIXED | 00976b90, 0097c9f0 | truncated base; Dwarven Mithral Cuirass immunity | ArtifactsGrantSpellImmunities |
| `SPELL_FIREBALL` | FIXED | 00976b90, 0085eaa0, 0097a530 (area cells by spell id; neighbour table 00f757c8 = the 8 offsets) | truncated base / protection; area = target cell + 8 neighbours (3×3), every stack in it, friend or foe (00b3d960 enumerates both sides) | SpellBaseDamageTruncates |
| `SPELL_FROST_RING` | FIXED | 00976b90, 0085eaa0, 0097a530 (area cells by spell id; neighbour table 00f757c8 = the 8 offsets) | as Fireball, area = the 8 neighbours only (centre excluded) | SpellBaseDamageTruncates |
| `SPELL_METEOR_SHOWER` | FIXED | 00976b90, 0085eaa0, 0097a530 (case 8 / 0x11d) | as Fireball, area 5×5 (dx, dy ∈ −2..2) | SpellBaseDamageTruncates |
| `SPELL_STONE_SPIKES` | FIXED | 00976b90, 0085eaa0, 0097a530 (case 0xed) | as Fireball, area = centre + 4 orthogonal cells | SpellBaseDamageTruncates |
| `SPELL_CHAIN_LIGHTNING` | FIXED | 0085edb0, 00a5dbb0, 0085e2f0, 0097d600, 00e0ab70 | the chain (first target + up to 3 hops) is chosen before any damage: each hop goes to the nearest living stack not yet chained, friend or foe, skipping stacks with protection > 99 %, ties broken by the battle RNG; per hop the float base is halved (×0.5f) and truncated; riders (stun) only on the first target. Engine re-picked after each hit, included immune stacks and broke ties by id. Distance metric (combat vf +0x118) not traced (footprint gap used). Uber Chain Lightning (0xee) does not halve — not implemented (campaign hero ability) | ChainLightningSkipsFullyProtectedStacks, ChainLightningBreaksTiesWithTheRng, ChainLightningHopBasesHalveAndTruncate |
| `SPELL_ARMAGEDDON` | FIXED | 00976b90, 0085f330 | truncated base / protection; 0085f330 target rule not compared | SpellBaseDamageTruncates |
| `SPELL_HOLY_WORD` | FIXED | 00977600, 0097c9f0, 0085f330 | affects Undead, Demonic and Demon-Raged creatures (engine missed Demon-Raged); Ring of Death holder immune | ArtifactsGrantSpellImmunities |
| `SPELL_UNHOLY_WORD` | MATCH | 00977600 | affects creatures that are neither Undead nor Demonic | |
| `SPELL_PLAGUE` | FIXED | 00976b90, 0097c9f0 | truncated damage; immune: Undead, Mechanical, Elemental | SpellBaseDamageTruncates |
| `SPELL_WASP_SWARM` | FIXED | 00976db0, 0097d7b0 (0x27), 0097c9f0 | Call of the Wasp damage trunc((lvl·per+1)·dmg); ATB −= duration·(1 + ((lvl+2)/3)·ATBReducePerLevel3) clamped 0..1 (engine added per3·lvl/3); immune: Undead, Elemental, Mechanical | CallOfTheWaspMultipliesTheAtbDrop |
| `SPELL_LAND_MINE` | MATCH | 0097d7b0 (0x26) | mine count = trunc(max(duration value, 2)) — data PerPower is 0 so engine's Base matches | |
| `SPELL_DEEP_FREEZE` | MATCH | 00a5a900 (0x117) | frozen target takes ×max(value·0.01, 1) | |
| `SPELL_DIVINE_VENGEANCE` | FIXED | 00976b90, 0097d7b0 | truncated base; the engine's ×sqrt(frags) (manual p.191) not located in the exe — extra damage term 00977e60 not decompiled | SpellBaseDamageTruncates |
| `SPELL_CELESTIAL_SHIELD` | MATCH | 00a595b0, 00a59660 | absorbs min(trunc(clamp(v,0,1)·damage), shield HP) | |
| `SPELL_ANTI_MAGIC` | MATCH | 004e41c0 | resists spells of level ≤ trunc(value) | |
| `SPELL_PHANTOM` | MATCH | 0097c9f0 (0x28), 0097d7b0 → 00860be0 | a phantom cannot be cloned | |
| `SPELL_RESURRECT` | MATCH | 0097c9f0 (0x30), 009789b0 | immune: Undead, Elemental, Mechanical | |
| `SPELL_ANIMATE_DEAD` | FIXED | 0097c9f0 (0x2a) | immune: Elemental, Mechanical (engine allowed them) | RaiseDeadSkipsElementalsAndMechanicals |
| `SPELL_REGENERATION` | MATCH | 0097c9f0 (0x118) | immune: Undead, Elemental, Mechanical | |
| `SPELL_SKILL_RESISTANCE` | MATCH | 0097c9f0 (effect 0x52) | +15 resistance | MagicResistanceChanceSumsLikeTheExe |
| `SPELL_BLADE_BARRIER` | MATCH | 0097d7b0 (0x11c) | three cells (y−1, y, y+1) | |
| `SPELL_WARCRY_RALLING_CRY` | FIXED | FUN_00946030 struct, 008a1e50 formula | morale +((lvl−1)/LevelDivisor)·PerDividedLevel + Base (engine 1 + lvl/8) | WarcryBonusesUseLevelMinusOne |
| `SPELL_WARCRY_BATTLECRY` | FIXED | 008a1e50 (0x126), FUN_00946030 | speed +((lvl−1)/div)·per + base (confirmed), offence by the same struct formula (engine 1 + lvl/2) | WarcryBonusesUseLevelMinusOne |
| `SPELL_DEFLECT_ARROWS` | UNVERIFIED | 00a5a900 (visual only), 00a5a750 | the ranged reduction site was not found (not in 00a5a900 / 00a58de0 / 00a5c5a0); engine ×(1 − clamp(v,0,0.95)) left as is | |
| `SPELL_FIREWALL` | UNVERIFIED | 0097a530 (case 0xec) | cells = (x, y−1), (x, y), (x, y+1) — matches; the field's entry/turn damage is not yet compared | |
| `SPELL_MASS_DISRUPTING_RAY` | MATCH | 0097a530 (default case), spell record +0xcc IsAimed, +0xd0 Target | aimed mass spell: 4×4 area x−1..x+2, y−1..y+2, TARGET_HOSTILE → enemy stacks only; effect as SPELL_DISRUPTING_RAY | |
| `SPELL_MASS_PLAGUE` | MATCH | 0097a530 (default case) | aimed: 4×4 area, enemy stacks only; effect as SPELL_PLAGUE | |
| `SPELL_MASS_DISPEL` | UNVERIFIED | 0097a530 (default case) | aimed: 4×4 area, TARGET_NEUTRAL → all stacks (matches); dispel rules not yet compared | |
| `SPELL_MASS_CURSE` | MATCH | 0097a530 (default case, IsAimed false) | whole enemy army (TARGET_HOSTILE → caster vf +0x10); effect as SPELL_CURSE | |
| `SPELL_MASS_SLOW` | MATCH | 0097a530 | whole enemy army; effect as SPELL_SLOW | |
| `SPELL_MASS_HASTE` | MATCH | 0097a530 | whole own army (TARGET_FRIEND → caster vf +0xc); effect as SPELL_HASTE | |
| `SPELL_MASS_BLESS` | MATCH | 0097a530 | whole own army; effect as SPELL_BLESS | |
| `SPELL_MASS_FORGETFULNESS` | GAP | 0097a530 | whole enemy army (matches); immunity as SPELL_FORGETFULNESS (GAP) | |
| `SPELL_MASS_WEAKNESS` | UNVERIFIED | 0097a530 | whole enemy army (matches); SPELL_WEAKNESS effect not yet compared | |
| `SPELL_MASS_STONESKIN` | UNVERIFIED | 0097a530 | whole own army (matches); SPELL_STONESKIN effect not yet compared | |
| `SPELL_MASS_BLOODLUST` | UNVERIFIED | 0097a530 | whole own army (matches); SPELL_BLOODLUST effect not yet compared | |
| `SPELL_MASS_DEFLECT_ARROWS` | UNVERIFIED | 0097a530 | whole own army (matches); Deflect Arrows reduction site not found | |
| `HERO_SKILL_ELEMENTAL_VISION` | FIXED | 00977e60, 004b12c0, 004b0ef0, 004b0d40, 004b0d80, 004b0e90 | Elemental Chains on damage spells: Warlock (class 6) with Elemental Vision; spell element opposite to the target's; m = 1 + Invocation·0.1 + altars·0.1 (float), ×2 Absolute Chains, ×Runic_2Warlock_ElementalVisionMultiplier with ≥ 2 Runic pieces; extra = trunc(D·m − D) of the damage after protection; target element re-rolled. Engine: any class, Destructive school only, bonus before protection, no altars/Absolute Chains/Runic. Creature-attack chaining (Altar of Elements, 004b13e0 path) not implemented; re-roll picker (004b0e90 → 00877220) not traced | ElementalChainsUseTheExeMultiplier |
| `HERO_SKILL_ABSOLUTE_CHAINS` | FIXED | 004b0d80 (skill 0x96) | doubles the Elemental Chains fraction x | ElementalChainsUseTheExeMultiplier |
| `HERO_SKILL_MASTER_OF_FIRE` | FIXED | 004e6770, 004e5b70, 00989790 (case 0xca), 00bbe760 | fire rider of Fireball / Firewall / Armageddon (not Magic Arrow: 0097d7b0 case 1 → plain 004e6b60): effect 0xca for one turn, value = (defence + existing crush) / 2 fixed at application, subtracted by the defence sum; Armored (0x55) and Fire Protection (effect 0x61) immune. Engine halved the defence at read time. With no casting hero the exe skips the skill test (creature casters) — engine still requires the hero's skill | MasterOfFireCrushIsAFixedHalfOfTheDefence |
| `SPELL_EFFECT_FIRE_DAMAGE` | FIXED | 00bbe760 (0xca → 004e5b70) | the armour crush applied as an effect (Fortress moat): same rule as Master of Fire | MasterOfFireCrushIsAFixedHalfOfTheDefence |
| `HERO_SKILL_SET_AFIRE` | FIXED | 004e6770 (004e6893..004e6960), 004e5790 | fire-rider spells only (Fireball, Firewall, Armageddon — engine also fired on Magic Arrow and skipped Armageddon), enemy targets, damage > 0; per-turn damage trunc(trunc(D / clamp(MarkOfFire_FireDamageMultiplier,0,1000) if marked) · SetAfire_DamageMultiplier) with D the damage dealt (engine: base damage / 3); duration SetAfire_DamageDurationInRounds (004e57d0 case 0x10e) | SetAfireBurnsAShareOfTheDamageDealt |
| `HERO_SKILL_MASTER_OF_ICE` | MATCH | 004e6600 (004e66b2..004e672a) | Ice Bolt / Frost Ring rider: effect 0xc9 (frozen) for trunc(time-per-turn · EmpoweredIceFreezeDuration / n) — n = 1 for Ice Bolt, targets hit for Frost Ring. Deep Freeze not seen calling this rider (engine applies it) — unverified | |
