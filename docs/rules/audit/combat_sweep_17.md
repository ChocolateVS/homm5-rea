# Combat audit — sweep 17: cast cost chain, clock leftovers

Per-ID rows merged into `docs/rules/AUDIT_COMBAT.md` by `tools/re/audit/ledger.py` (fragments are read in natural
order, so rows here override sweep 1–16). Exe references are H5_Game.exe (GOG 3.1) addresses, analysed headless with
Ghidra 12.1.4 (private project copy), disassembly read directly where the decompiler loses registers. Tests are in
`tests/H5.Combat.Tests/AuditSweep17Tests.cs`; code in `src/H5.Combat/Battle.Sweep17.cs`.

## Core mechanics (not ID rows)

The ledger's core row "Spell mana cost chain" (ledger.py MECH) carries the status; details:

| Mechanic | Status | Exe | Original behaviour | Test |
|---|---|---|---|---|
| Spell mana cost chain: base record, Avatar of Death, warcry floor | FIXED | 00a34740 (00a3474a..00a347b1, 00a34785..00a3479b, 00a34886, 00a349d4), 00a33e20, 00a33f70, 009781e0 (00978200..0097822f) | 00a34740(ECX = spell, EDX = hero or 0, week): the record is the base of a mass (00a33e20) or empowered (00a33f70) version, else the spell (no record → 0); cost = record +0x90; spell 0xc8 (SPELL_ABILITY_AVATAR_OF_DEATH) with a hero: level (vf 0x20) · RPGStats +0x888 (= combat/HeroSpecializations/Necromancer/AvatarOfDeath_ManaCostPerLevel: the Warlock block is 9 dwords from +0x8f4 (00a34906 copy, header + 8 floats, PowerBargain at +0x900 / +0x904), the Runemage block's Courtier / MountainKing / Researcher at +0x928..+0x930 and WarLeader's {Base, LevelDivisor, PerDividedLevel} at +0x9a0..+0x9a8 fix the layout: one 4-byte header per class block and per {Base, LevelDivisor, PerDividedLevel} record, so the Wizard block is +0x88c..+0x8f3 and the Necromancer's last field +0x888); × 2 (ADD ESI,ESI) for a version; week 00bcddf0; warcries (the spell's own record +0x88 = 6) with a hero: Voice Training (vf 0x174(0xbc) > 0), War Leader (vf 0x190(0x4b)) — then ≤ 0 → 0 (SETLE); other spells with a hero: Teleport + Beater, Ring of Magi, Power Bargain, Arcane Training — then < 0 → 0. The combat cost 009781e0 calls it with EDX = the caster's vf 0x78 interface (0 for a creature caster: no hero modifiers, the ×2 still applies). `Battle.ExeSpellBaseCost` is now that chain for `HeroBaseManaCost` (hero spells, the hive) and `CreatureManaCost`, as `Adventure.AdventureSpellCost(hero, spell)` is on the adventure map (the engine took the version record's own cost — stock Mass Weakness 14 instead of 2 · 5 = 10, Empowered Deep Freeze 11 instead of 22; MMH 5.5's data differs from 2 · base for many versions, e.g. Mass Bloodlust 20 vs 2 · 7, Empowered Implosion 50 vs 2 · 32 — the exe still charges 2 · base, the MMH55 DLL patches being out of scope; Avatar of Death took its record cost; a warcry was at least 1). Combat still has no Avatar of Death cast, so only the cost is ported | AuditSweep17Tests.AVersionCostsTwiceItsBaseSpell, AvatarOfDeathCostsLevelTimesTheStat, AWarcryCostCanReachZero |

## IDs

| ID | Status | Exe | Original behaviour / note | Test |
|---|---|---|---|---|
| `SPELL_ABILITY_AVATAR_OF_DEATH` | GAP | 00a34740 (00a34785..00a3479b), 00b56cd0 | the cost is ported (level · AvatarOfDeath_ManaCostPerLevel, see the cost-chain row; HERO_SPEC_AVATAR_OF_DEATH learns the spell at set-up, AUDIT_TOWN "Starting perks"); the combat cast itself (the AvatarOfDeath_Creature summon) is not traced or built | AuditSweep17Tests.AvatarOfDeathCostsLevelTimesTheStat |
