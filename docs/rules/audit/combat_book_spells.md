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
| `Artifact book spells (tomes, Band of Conjurer)` | MATCH | 00b5a410, 00b53b10, 00b526f0, 00b4bb40, 00bc1fa0, 00bc0030 | the combat hero casts the worn artifacts' book entries like learned spells (mana; no charge object) and, because 00b5a410 / 00b57ab0 run 00b53b10 after the entry goes in, also their mass / empowered versions when the perk is held — what `Battle.HeroSpells` derives for every book spell (the earlier GAP note "only for known spells" was wrong: 00b54770's Master-perk cases read the known list, but the 00b53b10 that follows covers every entry). The adventure book (`Adventure.BookSpells`) does not run 00b53b10 itself, so its own lists (spell book screens) lack those derived entries: adventure-side follow-up | AuditBookSpellTests.ArtifactBookSpellGetsItsMassVersion |
| `WAND_OF_X` | GAP | 00ca7e40 case 3, 00bc0030, 00bbf610, 00ca62e0, 00ca62f0, 00bc2010 (00ca6340) | the wand's per-instance spell (table 00e93128) is cast for a charge of its charge object, not for mana, and the charges are reset at combat start; artifacts carry no instance in the engine (no spell, no charges), so a wand gives nothing in combat |  |
| `ARTIFACT_RUNE_OF_FLAME` | GAP | 00ca7e40 case 0x56, 00ca6930, 00b541f0 | per-instance spell (table 00e933b8) in the book while worn; not modelled (no artifact instance), so not castable in combat |  |
| `SCROLL_OF_SPELL_X` | GAP | 00ca7e40 case 0x34, 00ca6930, 00b541f0 | per-instance spell in the book while worn, cast with mana (no charge object); the adventure side does not hand it to the combat hero (no artifact instance) |  |
| `SPELL_ABILITY_UNSUMMON` | FIXED | 00b54770 case 0x88, 00ca7e40 case 0x5d, 0097c320 case 0x42, 00984a00 case 0x42 (00985135..0098532d) | the Banish entry (perk Unsummon or Ring of Unsummoning) is the hero ability BANISH: offered while the opposing army has a spawn or a summoned stack, damage per HERO_SKILL_UNSUMMON; it is not a spell-book spell (school SPECIAL) | AuditBookSpellTests.RingOfUnsummoningEntryGivesBanishWithoutThePerk |
