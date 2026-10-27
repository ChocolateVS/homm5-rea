# Combat audit — the hero's spell book in combat (artifact and perk book entries)

Per-ID rows merged into `docs/rules/AUDIT_COMBAT.md` by `tools/re/audit/ledger.py`. Exe references are H5_Game.exe
(GOG 3.1) addresses, analysed headless with Ghidra 12.1.4 (private project copy). Code: `src/H5.Combat/Battle.Spells.cs`
(HeroSpells), `src/H5.Combat/Battle.HeroPerks.cs` (Banish); tests: `tests/H5.Combat.Tests/AuditBookSpellTests.cs`.

How the combat hero gets its spells: CCombatHero (vtable 00e8499c, ctor 00bc2010) keeps the adventure CHero (+0xb0);
its spell list vf 0x248 = FUN_00bc1fa0 returns that hero's vf 0xcc = FUN_00b5aa20 = the book (hero +0x1e0, 0x14-byte
entries {artifact, spell, sources}) — every entry, not the known list (+0x204). Per entry: can cast vf 0x220 =
FUN_00bc0030 (spell set, not a rune FUN_00a345b0, CHero vf 0x158 = FUN_00b526f0; an entry whose artifact has a charge
object (FUN_00a34490, artifact +0x1c) needs a charge FUN_00ca62e0, any other the mana cost 009781e0); pay vf 0x224 =
FUN_00bbf610 (charge FUN_00ca62f0, else mana). The ctor resets every entry's charge object (FUN_00ca6340). Entries of
school SPECIAL (FUN_00a34610: spell 0x43 or school 7) are hero abilities, offered through 00982010 → 0097c320.
The book is re-derived by FUN_00b53b10 after each skill / perk learn (vf 0x1c8 = FUN_00b57ab0: 00b54770 then 00b53b10),
artifact on / off (FUN_00b5a410: 00b541f0 / 00b53730 then 00b53b10) and hero set-up (FUN_00b56cd0): each entry that is
not mass / empowered (FUN_00a33f60 / FUN_00a34100), or has an artifact, and passes vf 0x158 (true for artifact and
sourced entries) gets FUN_00b4bb40 (empowered version with 0x46, mass version with its Master perk); a mass / empowered
entry without artifact whose base (FUN_00a34110) is neither known nor in the book loses its perk source and goes.

## IDs

| ID | Status | Exe | Original behaviour / note | Test |
|---|---|---|---|---|
| `ARTIFACT_RING_OF_UNSUMMONING` | FIXED | 00ca7e40 case 0x5d, 00b541f0, 00bc1fa0, 00b5aa20, 0097c320 case 0x42, 00984a00 case 0x42 | the ring's book entry SPELL_ABILITY_UNSUMMON (0x42, the same entry the Unsummon perk adds, 00b54770 case 0x88) gives the hero Banish — offered while the opposing army has a spawn or a summoned stack, damage as HERO_SKILL_UNSUMMON. The engine offered Banish only for the perk and dropped the entry (school SPECIAL is no spell school) | AuditBookSpellTests.RingOfUnsummoningEntryGivesBanishWithoutThePerk |
| `Artifact book spells (tomes, Band of Conjurer)` | MATCH | 00b5a410, 00b53b10, 00b526f0, 00b4bb40, 00bc1fa0, 00bc0030 | the combat hero casts the worn artifacts' book entries like learned spells (mana; no charge object) and, because 00b5a410 / 00b57ab0 run 00b53b10 after the entry goes in, also their mass / empowered versions when the perk is held — what `Battle.HeroSpells` derives for every book spell (the earlier GAP note "only for known spells" was wrong: 00b54770's Master-perk cases read the known list, but the 00b53b10 that follows covers every entry). The adventure book now runs 00b53b10 itself (`Adventure.RebuildBook` on every worn-list change), so its lists (spell book screens) hold the same versions | AuditBookSpellTests.ArtifactBookSpellGetsItsMassVersion |
| `WAND_OF_X` | FIXED | 00ca7e40 case 3, 00ca6360, 00ca7ce0, 00bc0030, 00bbf610, 00ca62e0, 00ca62f0, 00b4db10 (from 00cced80) | the wand object carries its spell (map spellID through 00ca8300, else table 00e93128 drawn over 0..9) and a charge object (00ca7ce0: max = current = 00ca6360(spell), table 00e93150, 6 otherwise); its book entry is cast for a charge (vf 0x220 needs +0x20 > 0, vf 0x224 spends one), not mana, and the guild visit refills it (00b4db10 → 00ca6310). The engine kept no artifact object, so a wand gave nothing. Now `HeroState.Wands` (from the adventure instance, `ArtifactInstance`), `Battle.WandFor`; charges return through `ResolveBattleCmd.WandCharges` | AuditBookSpellTests.WandEntrySpendsAChargeInsteadOfMana, ArtifactInstanceTests.WandsKeepTheMapSpellAndGetTheirTableCharges, ArtifactInstanceTests.WandEntriesAreCastForChargesAndRechargedAtTheGuild |
| `ARTIFACT_RUNE_OF_FLAME` | FIXED | 00ca7e40 case 0x56, 00ca8300, 00ca6930, 00b541f0 | the rune object always draws its spell from table 00e933b8 over 0..3 (Rune of Charge / Berserking / Magic Control / Exorcism; 00ca8300 clears a map spell); the spell is a mana book entry while worn. Was not modelled (no artifact instance) | ArtifactInstanceTests.WandsKeepTheMapSpellAndGetTheirTableCharges |
| `SCROLL_OF_SPELL_X` | FIXED | 00ca7e40 case 0x34, 004ec660 / 00ca8300, 00ca6930, 00b541f0 | the scroll object keeps the map's spellID (else circles 3 + 4 of the pool); its spell is a book entry while worn, cast with mana (no charge object). The engine learned a picked-up scroll for good and handed nothing to the combat hero | ArtifactInstanceTests.AScrollIsAWornBookEntryNotALearnedSpell |
| `SPELL_ABILITY_UNSUMMON` | FIXED | 00b54770 case 0x88, 00ca7e40 case 0x5d, 0097c320 case 0x42, 00984a00 case 0x42 (00985135..0098532d) | the Banish entry (perk Unsummon or Ring of Unsummoning) is the hero ability BANISH: offered while the opposing army has a spawn or a summoned stack, damage per HERO_SKILL_UNSUMMON; it is not a spell-book spell (school SPECIAL) | AuditBookSpellTests.RingOfUnsummoningEntryGivesBanishWithoutThePerk |
| `Hero set-up artifact entries (00b56cd0)` | GAP | 00b56cd0 (00b53b10 at 00b56de9, 00b541f0 at 00b56e53) | the set-up rebuilds the book before it puts the worn artifacts' entries in, so a hero's starting artifacts have no mass / empowered versions until the next rebuild (perk learn, artifact on / off); the adventure book follows this (`SetUpHeroBook`), but `Battle.HeroSpells` derives the versions for every entry it is given, so in combat they are there from the start |  |
| `Wand charges at combat start / restore` | GAP | 00bc2010 → 00ca6340, CCombatHero vtable 00e8499c +0x348 = 00bc0740 → 00ca6320 | the combat hero ctor copies each wand's current charges to +0x24 and slot 0x348 puts them back (+0x20 = +0x24); the caller of slot 0x348 is not traced, so the restore is not ported |  |
| `Wand and mana entry of one spell` | GAP | 00bc0030, 00bbf610 | the exe casts a book entry; a hero with a wand of a spell it also knows has two entries. The engine's cast names only the spell and uses the mana entry first (`Battle.WandFor`) |  |
