# Combat UI end-to-end check (abilities and spells)

Every active creature / war-machine / hero ability and every combat-school spell (hero book, and creature casters'
own lists) driven through the real combat UI of the game, stock 3.1 data (`--target=stock`), 2026-11-19; re-run after G1–G3 the same day (results below; before: PASS 178, WARN 16, FAIL 8, SKIP 1).

Runner: `game/src/Combat/CombatE2E.cs`, started with
`-- --target=stock --combat-speed=4 --combat-e2e=abilities,heroabilities,spells,creaturespells --combat-e2e-out=DIR`
(`--combat-e2e-only=ID|NAME,…`, `--combat-e2e-range=FROM,COUNT`). Run it through tools/hidden-desktop/run.sh like any
Godot run. Per case:

1. a local duel is built from the duel presets (a preset of the case's town) with the case's armies, spells, perks and
   skills; both sides are local and every other turn is passed (Defend / Wait) until the user acts;
2. the ability is armed by clicking the **OnJog-Special** button, a spell by clicking **OnJog-Spellbook** and then the
   bookmark / page / spell parts of the 3D book (synthetic mouse events through `Input.ParseInputEvent`, the same path
   as the player's mouse);
3. targeting scan: for every hoverable cell, the command a click would submit (the screen's own `Resolve`) is compared
   with the engine's verdict (a dry run on a copy of the battle) on the natural command for that cell;
4. the preferred accepted target is hovered (`_pre` screenshot) and clicked; the click must submit exactly the planned
   command, the engine must accept it, the played events must equal the dry run, something must happen, and playback
   must finish within 45 s (`_post` screenshot).

PASS = all of that; WARN = passed, with a note (targeting mismatch, or the book opened with the S key because the
button is hidden); FAIL = see the detail; SKIP = the set-up does not make the engine offer it.
Screenshots (half size) and results.tsv stay outside the repo (the agent's scratch folder).

## Findings

| # | Finding | Status | Evidence | Test |
|---|---|---|---|---|
| F1 | The spellbook's right page (the turning sheet's three slots) could not be clicked: picking used the bind-pose part bounds, where the sheet stands upright (zero width on screen). Now the part's screen rectangle is taken from the skinned pose as drawn (idle animation). Every spell on the right half of a spread was unpickable by mouse before (e.g. the Matriarch's Forgetfulness). | FIXED | SpellBook3D.ScreenRect; 00685270 (sheet parts at rest on the right) | E2E `creaturespell:SPELL_FORGETFULNESS@CREATURE_MATRIARCH` FAIL → PASS |
| F2 | Creature spell lists hold their ability entries (SPELL_ABILITY_*, school SPECIAL; e.g. the Royal Griffin's SPELL_ABILITY_BATTLE_DIVE). The engine offered them as castable spells, so the creature's book (S key) and the AI could "cast" Battle Dive, Lay Hands, Dash, Harm Touch … as a plain spell that only put a dummy effect on a target. FUN_00a34610 classes school SPECIAL entries as abilities (combat_book_spells.md); they are now left out of the castable list and refused as creature casts (script casts unchanged). Determinism goldens updated (AI decisions changed). | FIXED | Battle.Legal (IsAbilityEntry), Battle.Spells DoCast; FUN_00a34610 | UiE2eFindingsTests.CreatureAbilityEntriesAreNotCastableSpells |
| F3 | An armed ability showed the ability cursor (and accepted the click) on targets its own checks refuse: Harm Touch / Ammo Steal out of reach, Sabotage on a non-mechanical stack, Defile Magic on a non-caster, Leap out of range, Balor Summoning on a non-demonic corpse, Treant Union on a non-treant, Set Snares on an occupied cell; the click then only printed the engine's error. The cursor now runs the target through the ability's checks (dry run) and shows can't-cast, as the exe's cast test FUN_00548270 → FUN_00982010 does (Gating already did, 00860ce0). | FIXED | CombatScreen.Resolve / CommandAccepted | E2E scan: 0 cells "cursor accepts, engine rejects" for abilities |
| F4 | Same for cell spells: Arcane Crystal / Summon Hive showed the cast cursor on occupied cells the cast refuses. | FIXED | CombatScreen.Resolve (SpellTargeting.Cell) | E2E `spell:SPELL_ARCANE_CRYSTAL`, `spell:SPELL_SUMMON_HIVE` WARN → PASS |
| G1 | A hero with any active hero ability (most duel presets) gets the Special button instead of the Spellbook button (the book still opens with C / S). Exe FUN_005464a0: a hero gets Spellbook; hero abilities are book entries (school SPECIAL). Was: needed hero abilities in the 3D book. Now heroes get the Spellbook and the hero ability entries (perk entries, CHero 00b54770: Encourage 0x34 … Unsummon 0x42) are listed in the book (Combat tab, bookmark 6; orc book bookmark 1) and armed when picked; Imbue Arrow asks for its spell in a second book (GAP: the exe's flow not traced). | FIXED | AbilityBook, SpellBook3D (Entry.Ability), CombatHud.Refresh; AUDIT_COMBAT_UI.md "Special ability / Spellbook jog buttons" | E2E `heroability:*` armed from the book: all PASS |
| G2 | A creature with spells and an ability (Druid Elder + Mana Feed, Shadow Mistress, Shaman Hag/Witch) shows Special, not Spellbook; a stack with two abilities (Cyclops: Swallow Goblin + Goblin Thrower; First Aid Tent with Plague Tent) can only use the first through Special. Exe: Spellbook with the abilities in the book. Now the button follows 0053f730 (`AbilityBook.ButtonFor`: Special for a lone ability, Spellbook for spells or several abilities, neither for none) and the book lists the ability entries (creature SPELL_ABILITY_* records). Goblin Thrower is no entry: it is the cyclops' shot (008a4b70 → 00b3bfc0), now aimed with the shoot cursor on a non-adjacent enemy (GAP: an adjacent one is struck). GAP: the tent's entry ids (the two tent effect records shown). | FIXED | AbilityBook, CombatScreen.Resolve (Goblin Thrower) | E2E `ABILITY_GOBLIN_THROWER`, `PLAGUE_TENT` FAIL → PASS; creature-spell WARNs → PASS |
| G3 | The orc (warcry) book showed a rolled-up scroll in combat (its parts ~5 px tall on screen), so no warcry could be picked by mouse. The idle is right (0067d950 +0x1b8 indexes the anim set: 1 = idle02, looped, 00ac2b70); the scroll hangs upright in every pose, so the top-down view saw its rod edge-on. 0067d950 / 0067d4a0 fill PartSpellsFirstPage (4 slots) from the adventure list and PartSpellsSecondPage (8 slots, the side idle02 shows) from the warcry / ability list (0067d4a0's param 5 = 0 / 4 is the slot-texture offset, not a list offset; ours had split one list over both). Now the scroll is viewed facing the shown side's slots (`SpellBook3D.FrameScroll`) and only that side's parts are picked. GAP: the exe's book camera (both descs share SpellbookCenterPosition (32,32,2), placed by 00cf8620) is not traced. | FIXED | SpellBook3D (FrameScroll, RefreshPage, SpellParts, OnBookmark 0067cd10) | E2E `spell:SPELL_WARCRY_*` FAIL → PASS (stock 6, MMH55 4) |
| G4 | Resurrect / Animate Dead: on a cell where a corpse lies under a living stack the cursor takes the living stack (refused) while the engine accepts the corpse; which one the exe picks is not traced. | GAP | CombatScreen.Resolve (State.At) | E2E WARN on Resurrect / Animate Dead |
| G5 | Engine accepts Lay Hands / Wheel of Fortune / Random Blessing on the user itself; the legal target list (and so the cursor) excludes it. Which side is right is not traced. | GAP | Battle.AbilityTargets | detail notes in the table |
| G6 | Catapult Shot (sieges) and runes on MMH55 are not covered by these duels; MMH55 data not run yet. | GAP | — | — |

## Results (stock)

| Case | User | Result | Detail | Screenshots |
|---|---|---|---|---|
| `ability:ABILITY_LAY_HANDS` | CREATURE_PALADIN | PASS | target CREATURE_PEASANT; events AbilityTriggered,Healed,AtbChanged,TurnStarted; (engine also accepts the user itself as target, the cursor does not) | 001_ability-ABILITY_LAY_HANDS_*.png |
| `ability:ABILITY_SEDUCE` | CREATURE_SUCCUBUS_SEDUCER | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,EffectAdded,AtbChanged,TurnStarted | 002_ability-ABILITY_SEDUCE_*.png |
| `ability:ABILITY_RESURRECT_ALLIES` | CREATURE_ARCHANGEL | PASS | target CREATURE_PEASANT; events AbilityTriggered,Healed,AtbChanged,TurnStarted | 003_ability-ABILITY_RESURRECT_ALLIES_*.png |
| `ability:ABILITY_DASH` | CREATURE_RAKSHASA_KSHATRI | PASS | no target; events AbilityTriggered,EffectAdded,AtbChanged,TurnStarted | 004_ability-ABILITY_DASH_*.png |
| `ability:ABILITY_EXPLOSION` | CREATURE_HORNED_DEMON | PASS | no target; events AbilityTriggered,AtbChanged,TurnStarted | 005_ability-ABILITY_EXPLOSION_*.png |
| `ability:ABILITY_HARM_TOUCH` | CREATURE_WRAITH | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,Damaged,AtbChanged,TurnStarted | 006_ability-ABILITY_HARM_TOUCH_*.png |
| `ability:ABILITY_DEATH_WAIL` | CREATURE_BANSHEE | PASS | no target; events AbilityTriggered,AtbChanged,TurnStarted | 007_ability-ABILITY_DEATH_WAIL_*.png |
| `ability:ABILITY_CALL_LIGHTNING` | CREATURE_TITAN | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,Damaged,AtbChanged,TurnStarted | 008_ability-ABILITY_CALL_LIGHTNING_*.png |
| `ability:ABILITY_RAGE_OF_THE_FOREST` | CREATURE_ANGER_TREANT | PASS | no target; events AbilityTriggered,EffectAdded,AtbChanged,TurnStarted | 009_ability-ABILITY_RAGE_OF_THE_FOREST_*.png |
| `ability:ABILITY_BERSERKER_RAGE` | CREATURE_BERSERKER | PASS | no target; events AbilityTriggered,EffectAdded,Moved,AttackStarted,Damaged,AtbChanged,TurnStarted | 010_ability-ABILITY_BERSERKER_RAGE_*.png |
| `ability:ABILITY_ORDER_OF_THE_CHIEF` | CREATURE_ORCCHIEF_CHIEFTAIN | PASS | target CREATURE_GOBLIN; events AbilityTriggered,Damaged,AtbChanged,TurnStarted | 011_ability-ABILITY_ORDER_OF_THE_CHIEF_*.png |
| `ability:ABILITY_REPAIR` | CREATURE_MASTER_GREMLIN | PASS | target CREATURE_IRON_GOLEM; events AbilityTriggered,Healed,AtbChanged,TurnStarted | 012_ability-ABILITY_REPAIR_*.png |
| `ability:ABILITY_MANA_FEED` | CREATURE_DRUID_ELDER | PASS | no target; events AbilityTriggered,ManaChanged,AtbChanged,TurnStarted | 013_ability-ABILITY_MANA_FEED_*.png |
| `ability:ABILITY_SCATTER_SHOT` | CREATURE_ARCHER | PASS | cell 10,7; events AbilityTriggered,Damaged,MoraleProc,AtbChanged,TurnStarted | 014_ability-ABILITY_SCATTER_SHOT_*.png |
| `ability:ABILITY_INVISIBILITY` | CREATURE_SHADOW_MISTRESS | PASS | no target; events AbilityTriggered,EffectAdded,AtbChanged,TurnStarted | 015_ability-ABILITY_INVISIBILITY_*.png |
| `ability:ABILITY_LEAP` | CREATURE_HORNED_LEAPER | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,Teleported,AttackStarted,Damaged,LuckProc,UnitDied,TurnStarted | 016_ability-ABILITY_LEAP_*.png |
| `ability:ABILITY_SUMMON_OTHER` | CREATURE_ARCH_DEMON | PASS | target CREATURE_PEASANT; events AbilityTriggered,Teleported,AtbChanged,TurnStarted | 017_ability-ABILITY_SUMMON_OTHER_*.png |
| `ability:ABILITY_BALOR_SUMMONIG` | CREATURE_ARCHDEVIL | PASS | target CREATURE_FAMILIAR; events AbilityTriggered,UnitSummoned,AtbChanged,TurnStarted | 018_ability-ABILITY_BALOR_SUMMONIG_*.png |
| `ability:ABILITY_FLAMESTRIKE` | CREATURE_WARLORD | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,Damaged,EffectAdded,MoraleProc,AtbChanged,TurnStarted | 019_ability-ABILITY_FLAMESTRIKE_*.png |
| `ability:ABILITY_STORMBOLT` | CREATURE_THUNDER_THANE | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,Damaged,MoraleProc,AtbChanged,TurnStarted | 020_ability-ABILITY_STORMBOLT_*.png |
| `ability:ABILITY_CALL_STORM` | CREATURE_STORM_LORD | PASS | cell 9,7; events AbilityTriggered,FieldChanged,AtbChanged,TurnStarted | 021_ability-ABILITY_CALL_STORM_*.png |
| `ability:ABILITY_HARPOON_STRIKE` | CREATURE_HARPOONER | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,AttackStarted,Damaged,Teleported,MoraleProc,AtbChanged,TurnStarted | 022_ability-ABILITY_HARPOON_STRIKE_*.png |
| `ability:ABILITY_SABOTAGE` | CREATURE_GREMLIN_SABOTEUR | PASS | target WAR_MACHINE_BALLISTA; events AbilityTriggered,EffectAdded,AtbChanged,TurnStarted | 023_ability-ABILITY_SABOTAGE_*.png |
| `ability:ABILITY_SWALLOW_GOBLIN` | CREATURE_CYCLOP | PASS | target CREATURE_GOBLIN; events AbilityTriggered,Damaged,MoraleProc,AtbChanged,TurnStarted | 024_ability-ABILITY_SWALLOW_GOBLIN_*.png |
| `ability:ABILITY_SACRIFICE_GOBLIN` | CREATURE_SHAMAN_HAG | PASS | target CREATURE_GOBLIN; events AbilityTriggered,Damaged,EffectAdded,AtbChanged,TurnStarted | 025_ability-ABILITY_SACRIFICE_GOBLIN_*.png |
| `ability:ABILITY_LUCK_GAMBLER` | CREATURE_DJINN_VIZIER | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,EffectAdded,AtbChanged,TurnStarted; (engine also accepts the user itself as target, the cursor does not) | 026_ability-ABILITY_LUCK_GAMBLER_*.png |
| `ability:ABILITY_RANDOM_CASTER` | CREATURE_GENIE | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,EffectAdded,MoraleProc,AtbChanged,TurnStarted | 027_ability-ABILITY_RANDOM_CASTER_*.png |
| `ability:ABILITY_RANDOM_CASTER_BLESS` | CREATURE_MASTER_GENIE | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,EffectAdded,MoraleProc,AtbChanged,TurnStarted; (engine also accepts the user itself as target, the cursor does not) | 028_ability-ABILITY_RANDOM_CASTER_BLESS_*.png |
| `ability:ABILITY_INCINERATE` | CREATURE_RED_DRAGON | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,AttackStarted,Damaged,AtbChanged,TurnStarted | 029_ability-ABILITY_INCINERATE_*.png |
| `ability:ABILITY_AMMO_STEAL` | CREATURE_POLTERGEIST | PASS | target CREATURE_ARCHER; events AbilityTriggered,AtbChanged,TurnStarted | 030_ability-ABILITY_AMMO_STEAL_*.png |
| `ability:ABILITY_SYPHON_MANA` | CREATURE_QUASIT | PASS | no target; events ManaChanged,AbilityTriggered,AtbChanged,TurnStarted | 031_ability-ABILITY_SYPHON_MANA_*.png |
| `ability:ABILITY_SCAVENGER` | CREATURE_WYVERN_PAOKAI | PASS | target CREATURE_PEASANT; events Moved,AbilityTriggered,AtbChanged,TurnStarted | 032_ability-ABILITY_SCAVENGER_*.png |
| `ability:ABILITY_GOBLIN_THROWER` | CREATURE_CYCLOP_BLOODEYED | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,Damaged,AttackStarted,LuckProc,MoraleProc,AtbChanged,TurnStarted | 033_ability-ABILITY_GOBLIN_THROWER_*.png |
| `ability:ABILITY_POWER_FEED` | CREATURE_HIGH_DRUID | PASS | no target; events AbilityTriggered,AtbChanged,TurnStarted | 034_ability-ABILITY_POWER_FEED_*.png |
| `ability:ABILITY_SET_SNARES` | CREATURE_GOBLIN_TRAPPER | PASS | cell 8,7; events AbilityTriggered,FieldChanged,AtbChanged,TurnStarted | 035_ability-ABILITY_SET_SNARES_*.png |
| `ability:ABILITY_DEFILE_MAGIC` | CREATURE_GOBLIN_DEFILER | PASS | target CREATURE_COMBAT_MAGE; events AbilityTriggered,EffectAdded,AtbChanged,TurnStarted | 036_ability-ABILITY_DEFILE_MAGIC_*.png |
| `ability:ABILITY_TREEANT_UNION` | CREATURE_DRYAD | PASS | target CREATURE_TREANT; events AbilityTriggered,Healed,AtbChanged,TurnStarted | 037_ability-ABILITY_TREEANT_UNION_*.png |
| `ability:ABILITY_PACK_DIVE` | CREATURE_BATTLE_GRIFFIN | PASS | cell 10,7; events EffectAdded,AbilityTriggered,FieldChanged,AtbChanged,TurnStarted | 038_ability-ABILITY_PACK_DIVE_*.png |
| `ability:ABILITY_BATTLE_DIVE` | CREATURE_ROYAL_GRIFFIN | PASS | cell 10,7; events EffectAdded,AbilityTriggered,FieldChanged,AtbChanged,TurnStarted | 039_ability-ABILITY_BATTLE_DIVE_*.png |
| `ability:FIRST_AID` | WAR_MACHINE_FIRST_AID_TENT | PASS | target CREATURE_PEASANT; events AbilityTriggered,Healed,AtbChanged,TurnStarted | 040_ability-FIRST_AID_*.png |
| `ability:PLAGUE_TENT` | WAR_MACHINE_FIRST_AID_TENT | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,Damaged,AtbChanged,TurnStarted | 041_ability-PLAGUE_TENT_*.png |
| `ability:GATING` | CREATURE_FAMILIAR | PASS | cell 6,5; events AbilityTriggered,UnitSummoned,EffectAdded,AtbChanged,TurnStarted | 042_ability-GATING_*.png |
| `heroability:DIVINE_GUIDANCE` | hero | PASS | target CREATURE_PEASANT; events AbilityTriggered,AtbChanged,TurnStarted | 043_heroability-DIVINE_GUIDANCE_*.png |
| `heroability:RETALIATION_STRIKE` | hero | PASS | target CREATURE_PEASANT; events AbilityTriggered,AtbChanged,TurnStarted | 044_heroability-RETALIATION_STRIKE_*.png |
| `heroability:BENEDICTION` | hero | PASS | no target; events AbilityTriggered,EffectAdded,AtbChanged,TurnStarted | 045_heroability-BENEDICTION_*.png |
| `heroability:MARK_OF_DAMNED` | hero | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,AtbChanged,TurnStarted | 046_heroability-MARK_OF_DAMNED_*.png |
| `heroability:MARK_OF_NECROMANCER` | hero | PASS | target CREATURE_PEASANT; events AbilityTriggered,AtbChanged,TurnStarted | 047_heroability-MARK_OF_NECROMANCER_*.png |
| `heroability:BANSHEE_HOWL` | hero | PASS | no target; events AbilityTriggered,EffectAdded,AtbChanged,TurnStarted | 048_heroability-BANSHEE_HOWL_*.png |
| `heroability:RAIN_OF_ARROWS` | hero | PASS | no target; events AbilityTriggered,AttackStarted,Damaged,AtbChanged,TurnStarted | 049_heroability-RAIN_OF_ARROWS_*.png |
| `heroability:POWERFUL_BLOW` | hero | PASS | target CREATURE_PEASANT; events AbilityTriggered,AtbChanged,TurnStarted | 050_heroability-POWERFUL_BLOW_*.png |
| `heroability:CONSUME_CORPSE` | hero | PASS | target CREATURE_PEASANT; events AbilityTriggered,ManaChanged,AtbChanged,TurnStarted | 051_heroability-CONSUME_CORPSE_*.png |
| `heroability:COUNTERSPELL` | hero | PASS | no target; events AbilityTriggered,AtbChanged,TurnStarted | 052_heroability-COUNTERSPELL_*.png |
| `heroability:MARK_OF_WIZARD` | hero | PASS | target CREATURE_FOOTMAN; events AbilityTriggered,AtbChanged,TurnStarted | 053_heroability-MARK_OF_WIZARD_*.png |
| `heroability:BANISH` | hero | PASS | no target; events AbilityTriggered,Damaged,AtbChanged,TurnStarted | 054_heroability-BANISH_*.png |
| `heroability:IMBUE_ARROW:SPELL_MAGIC_ARROW` | hero | PASS | no target; events AbilityTriggered,AtbChanged,TurnStarted | 055_heroability-IMBUE_ARROW-SPELL_MAGIC_ARROW_*.png |
| `spell:SPELL_CURSE` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 056_spell-SPELL_CURSE_*.png |
| `spell:SPELL_MASS_CURSE` | hero | PASS | no target; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 057_spell-SPELL_MASS_CURSE_*.png |
| `spell:SPELL_MASS_SLOW` | hero | PASS | no target; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 058_spell-SPELL_MASS_SLOW_*.png |
| `spell:SPELL_SLOW` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 059_spell-SPELL_SLOW_*.png |
| `spell:SPELL_SORROW` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 060_spell-SPELL_SORROW_*.png |
| `spell:SPELL_DISRUPTING_RAY` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 061_spell-SPELL_DISRUPTING_RAY_*.png |
| `spell:SPELL_MASS_DISRUPTING_RAY` | hero | PASS | cell 10,7; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 062_spell-SPELL_MASS_DISRUPTING_RAY_*.png |
| `spell:SPELL_MASS_PLAGUE` | hero | PASS | cell 10,7; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 063_spell-SPELL_MASS_PLAGUE_*.png |
| `spell:SPELL_PLAGUE` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 064_spell-SPELL_PLAGUE_*.png |
| `spell:SPELL_FORGETFULNESS` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 065_spell-SPELL_FORGETFULNESS_*.png |
| `spell:SPELL_MASS_FORGETFULNESS` | hero | PASS | no target; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 066_spell-SPELL_MASS_FORGETFULNESS_*.png |
| `spell:SPELL_MASS_WEAKNESS` | hero | PASS | no target; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 067_spell-SPELL_MASS_WEAKNESS_*.png |
| `spell:SPELL_WEAKNESS` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 068_spell-SPELL_WEAKNESS_*.png |
| `spell:SPELL_BERSERK` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 069_spell-SPELL_BERSERK_*.png |
| `spell:SPELL_BLIND` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 070_spell-SPELL_BLIND_*.png |
| `spell:SPELL_HYPNOTIZE` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 071_spell-SPELL_HYPNOTIZE_*.png |
| `spell:SPELL_UNHOLY_WORD` | hero | PASS | no target; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 072_spell-SPELL_UNHOLY_WORD_*.png |
| `spell:SPELL_VAMPIRISM` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 073_spell-SPELL_VAMPIRISM_*.png |
| `spell:SPELL_EMPOWERED_MAGIC_ARROW` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 074_spell-SPELL_EMPOWERED_MAGIC_ARROW_*.png |
| `spell:SPELL_EMPOWERED_STONE_SPIKES` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 075_spell-SPELL_EMPOWERED_STONE_SPIKES_*.png |
| `spell:SPELL_MAGIC_ARROW` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 076_spell-SPELL_MAGIC_ARROW_*.png |
| `spell:SPELL_STONE_SPIKES` | hero | PASS | cell 10,7; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 077_spell-SPELL_STONE_SPIKES_*.png |
| `spell:SPELL_EMPOWERED_ICE_BOLT` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 078_spell-SPELL_EMPOWERED_ICE_BOLT_*.png |
| `spell:SPELL_EMPOWERED_LIGHTNING_BOLT` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 079_spell-SPELL_EMPOWERED_LIGHTNING_BOLT_*.png |
| `spell:SPELL_ICE_BOLT` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 080_spell-SPELL_ICE_BOLT_*.png |
| `spell:SPELL_LIGHTNING_BOLT` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 081_spell-SPELL_LIGHTNING_BOLT_*.png |
| `spell:SPELL_EMPOWERED_FIREBALL` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 082_spell-SPELL_EMPOWERED_FIREBALL_*.png |
| `spell:SPELL_EMPOWERED_FROST_RING` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 083_spell-SPELL_EMPOWERED_FROST_RING_*.png |
| `spell:SPELL_FIREBALL` | hero | PASS | cell 10,7; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 084_spell-SPELL_FIREBALL_*.png |
| `spell:SPELL_FIREWALL` | hero | PASS | cell 10,7; events ManaChanged,SpellCast,FieldChanged,Damaged,AtbChanged,TurnStarted | 085_spell-SPELL_FIREWALL_*.png |
| `spell:SPELL_FROST_RING` | hero | PASS | cell 10,7; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 086_spell-SPELL_FROST_RING_*.png |
| `spell:SPELL_CHAIN_LIGHTNING` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 087_spell-SPELL_CHAIN_LIGHTNING_*.png |
| `spell:SPELL_EMPOWERED_CHAIN_LIGHTNING` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 088_spell-SPELL_EMPOWERED_CHAIN_LIGHTNING_*.png |
| `spell:SPELL_EMPOWERED_METEOR_SHOWER` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 089_spell-SPELL_EMPOWERED_METEOR_SHOWER_*.png |
| `spell:SPELL_METEOR_SHOWER` | hero | PASS | cell 10,7; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 090_spell-SPELL_METEOR_SHOWER_*.png |
| `spell:SPELL_ARMAGEDDON` | hero | PASS | no target; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 091_spell-SPELL_ARMAGEDDON_*.png |
| `spell:SPELL_DEEP_FREEZE` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,EffectAdded,AtbChanged,TurnStarted | 092_spell-SPELL_DEEP_FREEZE_*.png |
| `spell:SPELL_EMPOWERED_ARMAGEDDON` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 093_spell-SPELL_EMPOWERED_ARMAGEDDON_*.png |
| `spell:SPELL_EMPOWERED_DEEP_FREEZE` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,EffectAdded,AtbChanged,TurnStarted | 094_spell-SPELL_EMPOWERED_DEEP_FREEZE_*.png |
| `spell:SPELL_EMPOWERED_IMPLOSION` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 095_spell-SPELL_EMPOWERED_IMPLOSION_*.png |
| `spell:SPELL_IMPLOSION` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 096_spell-SPELL_IMPLOSION_*.png |
| `spell:SPELL_BLESS` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 097_spell-SPELL_BLESS_*.png |
| `spell:SPELL_HASTE` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 098_spell-SPELL_HASTE_*.png |
| `spell:SPELL_MASS_BLESS` | hero | PASS | no target; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 099_spell-SPELL_MASS_BLESS_*.png |
| `spell:SPELL_MASS_HASTE` | hero | PASS | no target; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 100_spell-SPELL_MASS_HASTE_*.png |
| `spell:SPELL_DISPEL` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 101_spell-SPELL_DISPEL_*.png |
| `spell:SPELL_MASS_DISPEL` | hero | PASS | cell 10,7; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 102_spell-SPELL_MASS_DISPEL_*.png |
| `spell:SPELL_MASS_STONESKIN` | hero | PASS | no target; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 103_spell-SPELL_MASS_STONESKIN_*.png |
| `spell:SPELL_REGENERATION` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 104_spell-SPELL_REGENERATION_*.png |
| `spell:SPELL_STONESKIN` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 105_spell-SPELL_STONESKIN_*.png |
| `spell:SPELL_BLOODLUST` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 106_spell-SPELL_BLOODLUST_*.png |
| `spell:SPELL_DEFLECT_ARROWS` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 107_spell-SPELL_DEFLECT_ARROWS_*.png |
| `spell:SPELL_MASS_BLOODLUST` | hero | PASS | no target; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 108_spell-SPELL_MASS_BLOODLUST_*.png |
| `spell:SPELL_MASS_DEFLECT_ARROWS` | hero | PASS | no target; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 109_spell-SPELL_MASS_DEFLECT_ARROWS_*.png |
| `spell:SPELL_ANTI_MAGIC` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 110_spell-SPELL_ANTI_MAGIC_*.png |
| `spell:SPELL_DIVINE_VENGEANCE` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 111_spell-SPELL_DIVINE_VENGEANCE_*.png |
| `spell:SPELL_TELEPORT` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,Teleported,AtbChanged,TurnStarted | 112_spell-SPELL_TELEPORT_*.png |
| `spell:SPELL_HOLY_WORD` | hero | PASS | no target; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 113_spell-SPELL_HOLY_WORD_*.png |
| `spell:SPELL_RESURRECT` | hero | WARN | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Healed,EffectAdded,AtbChanged,TurnStarted; cursor refuses 1 cells the engine accepts (2,7) | 114_spell-SPELL_RESURRECT_*.png |
| `spell:SPELL_RUNE_OF_BERSERKING` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 115_spell-SPELL_RUNE_OF_BERSERKING_*.png |
| `spell:SPELL_RUNE_OF_CHARGE` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 116_spell-SPELL_RUNE_OF_CHARGE_*.png |
| `spell:SPELL_RUNE_OF_EXORCISM` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 117_spell-SPELL_RUNE_OF_EXORCISM_*.png |
| `spell:SPELL_RUNE_OF_MAGIC_CONTROL` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 118_spell-SPELL_RUNE_OF_MAGIC_CONTROL_*.png |
| `spell:SPELL_RUNE_OF_ELEMENTAL_IMMUNITY` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 119_spell-SPELL_RUNE_OF_ELEMENTAL_IMMUNITY_*.png |
| `spell:SPELL_RUNE_OF_ETHEREALNESS` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 120_spell-SPELL_RUNE_OF_ETHEREALNESS_*.png |
| `spell:SPELL_RUNE_OF_REVIVE` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 121_spell-SPELL_RUNE_OF_REVIVE_*.png |
| `spell:SPELL_RUNE_OF_STUNNING` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 122_spell-SPELL_RUNE_OF_STUNNING_*.png |
| `spell:SPELL_RUNE_OF_BATTLERAGE` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 123_spell-SPELL_RUNE_OF_BATTLERAGE_*.png |
| `spell:SPELL_RUNE_OF_DRAGONFORM` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 124_spell-SPELL_RUNE_OF_DRAGONFORM_*.png |
| `spell:SPELL_EMPOWERED_MAGIC_FIST` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 125_spell-SPELL_EMPOWERED_MAGIC_FIST_*.png |
| `spell:SPELL_LAND_MINE` | hero | PASS | cell 10,7; events ManaChanged,SpellCast,FieldChanged,AtbChanged,TurnStarted | 126_spell-SPELL_LAND_MINE_*.png |
| `spell:SPELL_MAGIC_FIST` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 127_spell-SPELL_MAGIC_FIST_*.png |
| `spell:SPELL_ANIMATE_DEAD` | hero | WARN | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Healed,EffectAdded,AtbChanged,TurnStarted; cursor refuses 1 cells the engine accepts (2,7) | 128_spell-SPELL_ANIMATE_DEAD_*.png |
| `spell:SPELL_ARCANE_CRYSTAL` | hero | PASS | cell 9,7; events ManaChanged,SpellCast,UnitSummoned,AtbChanged,TurnStarted | 129_spell-SPELL_ARCANE_CRYSTAL_*.png |
| `spell:SPELL_WASP_SWARM` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 130_spell-SPELL_WASP_SWARM_*.png |
| `spell:SPELL_BLADE_BARRIER` | hero | PASS | cell 10,7; events ManaChanged,SpellCast,UnitSummoned,AtbChanged,TurnStarted | 131_spell-SPELL_BLADE_BARRIER_*.png |
| `spell:SPELL_EARTHQUAKE` | hero | PASS | no target; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 132_spell-SPELL_EARTHQUAKE_*.png |
| `spell:SPELL_PHANTOM` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,UnitSummoned,AtbChanged,TurnStarted | 133_spell-SPELL_PHANTOM_*.png |
| `spell:SPELL_SUMMON_ELEMENTALS` | hero | PASS | no target; events ManaChanged,SpellCast,UnitSummoned,AtbChanged,TurnStarted | 134_spell-SPELL_SUMMON_ELEMENTALS_*.png |
| `spell:SPELL_SUMMON_HIVE` | hero | PASS | cell 9,7; events ManaChanged,SpellCast,UnitSummoned,AtbChanged,TurnStarted | 135_spell-SPELL_SUMMON_HIVE_*.png |
| `spell:SPELL_CELESTIAL_SHIELD` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 136_spell-SPELL_CELESTIAL_SHIELD_*.png |
| `spell:SPELL_CONJURE_PHOENIX` | hero | PASS | no target; events ManaChanged,SpellCast,UnitSummoned,AtbChanged,TurnStarted | 137_spell-SPELL_CONJURE_PHOENIX_*.png |
| `spell:SPELL_WARCRY_CALL_OF_BLOOD` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 138_spell-SPELL_WARCRY_CALL_OF_BLOOD_*.png |
| `spell:SPELL_WARCRY_RALLING_CRY` | hero | PASS | no target; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 139_spell-SPELL_WARCRY_RALLING_CRY_*.png |
| `spell:SPELL_WARCRY_FEAR_MY_ROAR` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,AbilityTriggered,AtbChanged,TurnStarted | 140_spell-SPELL_WARCRY_FEAR_MY_ROAR_*.png |
| `spell:SPELL_WARCRY_WORD_OF_THE_CHIEF` | hero | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,AttackStarted,Damaged,AtbChanged,TurnStarted | 141_spell-SPELL_WARCRY_WORD_OF_THE_CHIEF_*.png |
| `spell:SPELL_WARCRY_BATTLECRY` | hero | PASS | no target; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 142_spell-SPELL_WARCRY_BATTLECRY_*.png |
| `spell:SPELL_WARCRY_SHOUT_OF_MANY` | hero | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 143_spell-SPELL_WARCRY_SHOUT_OF_MANY_*.png |
| `creaturespell:SPELL_MAGIC_FIST@CREATURE_ARCH_MAGI` | CREATURE_ARCH_MAGI | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 144_creaturespell-SPELL_MAGIC_FIST-CREATURE_ARCH_MAGI_*.png |
| `creaturespell:SPELL_FIREBALL@CREATURE_ARCH_MAGI` | CREATURE_ARCH_MAGI | PASS | cell 9,7; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 145_creaturespell-SPELL_FIREBALL-CREATURE_ARCH_MAGI_*.png |
| `creaturespell:SPELL_BLOODLUST@CREATURE_ARCH_MAGI` | CREATURE_ARCH_MAGI | PASS | target CREATURE_ARCH_MAGI; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 146_creaturespell-SPELL_BLOODLUST-CREATURE_ARCH_MAGI_*.png |
| `creaturespell:SPELL_DISPEL@CREATURE_ARCH_MAGI` | CREATURE_ARCH_MAGI | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 147_creaturespell-SPELL_DISPEL-CREATURE_ARCH_MAGI_*.png |
| `creaturespell:SPELL_FIREBALL@CREATURE_BALOR` | CREATURE_BALOR | PASS | cell 9,7; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 148_creaturespell-SPELL_FIREBALL-CREATURE_BALOR_*.png |
| `creaturespell:SPELL_METEOR_SHOWER@CREATURE_BALOR` | CREATURE_BALOR | PASS | cell 9,7; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 149_creaturespell-SPELL_METEOR_SHOWER-CREATURE_BALOR_*.png |
| `creaturespell:SPELL_DISRUPTING_RAY@CREATURE_BALOR` | CREATURE_BALOR | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 150_creaturespell-SPELL_DISRUPTING_RAY-CREATURE_BALOR_*.png |
| `creaturespell:SPELL_HASTE@CREATURE_CLERIC` | CREATURE_CLERIC | PASS | target CREATURE_CLERIC; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 151_creaturespell-SPELL_HASTE-CREATURE_CLERIC_*.png |
| `creaturespell:SPELL_BLESS@CREATURE_CLERIC` | CREATURE_CLERIC | PASS | target CREATURE_CLERIC; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 152_creaturespell-SPELL_BLESS-CREATURE_CLERIC_*.png |
| `creaturespell:SPELL_STONESKIN@CREATURE_CLERIC` | CREATURE_CLERIC | PASS | target CREATURE_CLERIC; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 153_creaturespell-SPELL_STONESKIN-CREATURE_CLERIC_*.png |
| `creaturespell:SPELL_MAGIC_FIST@CREATURE_COMBAT_MAGE` | CREATURE_COMBAT_MAGE | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 154_creaturespell-SPELL_MAGIC_FIST-CREATURE_COMBAT_MAGE_*.png |
| `creaturespell:SPELL_DISPEL@CREATURE_COMBAT_MAGE` | CREATURE_COMBAT_MAGE | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 155_creaturespell-SPELL_DISPEL-CREATURE_COMBAT_MAGE_*.png |
| `creaturespell:SPELL_CURSE@CREATURE_DEMILICH` | CREATURE_DEMILICH | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 156_creaturespell-SPELL_CURSE-CREATURE_DEMILICH_*.png |
| `creaturespell:SPELL_PLAGUE@CREATURE_DEMILICH` | CREATURE_DEMILICH | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 157_creaturespell-SPELL_PLAGUE-CREATURE_DEMILICH_*.png |
| `creaturespell:SPELL_WEAKNESS@CREATURE_DEMILICH` | CREATURE_DEMILICH | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 158_creaturespell-SPELL_WEAKNESS-CREATURE_DEMILICH_*.png |
| `creaturespell:SPELL_LIGHTNING_BOLT@CREATURE_DRUID` | CREATURE_DRUID | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 159_creaturespell-SPELL_LIGHTNING_BOLT-CREATURE_DRUID_*.png |
| `creaturespell:SPELL_STONESKIN@CREATURE_DRUID` | CREATURE_DRUID | PASS | target CREATURE_DRUID; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 160_creaturespell-SPELL_STONESKIN-CREATURE_DRUID_*.png |
| `creaturespell:SPELL_STONE_SPIKES@CREATURE_DRUID_ELDER` | CREATURE_DRUID_ELDER | PASS | cell 10,7; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 161_creaturespell-SPELL_STONE_SPIKES-CREATURE_DRUID_ELDER_*.png |
| `creaturespell:SPELL_LIGHTNING_BOLT@CREATURE_DRUID_ELDER` | CREATURE_DRUID_ELDER | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 162_creaturespell-SPELL_LIGHTNING_BOLT-CREATURE_DRUID_ELDER_*.png |
| `creaturespell:SPELL_STONESKIN@CREATURE_DRUID_ELDER` | CREATURE_DRUID_ELDER | PASS | target CREATURE_DRUID_ELDER; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 163_creaturespell-SPELL_STONESKIN-CREATURE_DRUID_ELDER_*.png |
| `creaturespell:SPELL_DEFLECT_ARROWS@CREATURE_FLAME_KEEPER` | CREATURE_FLAME_KEEPER | PASS | target CREATURE_FLAME_KEEPER; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 164_creaturespell-SPELL_DEFLECT_ARROWS-CREATURE_FLAME_KEEPER_*.png |
| `creaturespell:SPELL_FIREBALL@CREATURE_FLAME_KEEPER` | CREATURE_FLAME_KEEPER | PASS | cell 10,7; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 165_creaturespell-SPELL_FIREBALL-CREATURE_FLAME_KEEPER_*.png |
| `creaturespell:SPELL_DEFLECT_ARROWS@CREATURE_FLAME_MAGE` | CREATURE_FLAME_MAGE | PASS | target CREATURE_FLAME_MAGE; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 166_creaturespell-SPELL_DEFLECT_ARROWS-CREATURE_FLAME_MAGE_*.png |
| `creaturespell:SPELL_FIREWALL@CREATURE_FLAME_MAGE` | CREATURE_FLAME_MAGE | PASS | cell 10,7; events ManaChanged,SpellCast,FieldChanged,Damaged,AtbChanged,TurnStarted | 167_creaturespell-SPELL_FIREWALL-CREATURE_FLAME_MAGE_*.png |
| `creaturespell:SPELL_STONESKIN@CREATURE_HIGH_DRUID` | CREATURE_HIGH_DRUID | PASS | target CREATURE_HIGH_DRUID; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 168_creaturespell-SPELL_STONESKIN-CREATURE_HIGH_DRUID_*.png |
| `creaturespell:SPELL_WEAKNESS@CREATURE_LICH_MASTER` | CREATURE_LICH_MASTER | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 169_creaturespell-SPELL_WEAKNESS-CREATURE_LICH_MASTER_*.png |
| `creaturespell:SPELL_ANIMATE_DEAD@CREATURE_LICH_MASTER` | CREATURE_LICH_MASTER | WARN | target CREATURE_PEASANT; events ManaChanged,SpellCast,Healed,EffectAdded,AtbChanged,MoraleProc,TurnStarted; cursor refuses 1 cells the engine accepts (1,5) | 170_creaturespell-SPELL_ANIMATE_DEAD-CREATURE_LICH_MASTER_*.png |
| `creaturespell:SPELL_MAGIC_FIST@CREATURE_MAGI` | CREATURE_MAGI | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 171_creaturespell-SPELL_MAGIC_FIST-CREATURE_MAGI_*.png |
| `creaturespell:SPELL_DISPEL@CREATURE_MAGI` | CREATURE_MAGI | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 172_creaturespell-SPELL_DISPEL-CREATURE_MAGI_*.png |
| `creaturespell:SPELL_SLOW@CREATURE_MATRIARCH` | CREATURE_MATRIARCH | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 173_creaturespell-SPELL_SLOW-CREATURE_MATRIARCH_*.png |
| `creaturespell:SPELL_DISRUPTING_RAY@CREATURE_MATRIARCH` | CREATURE_MATRIARCH | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 174_creaturespell-SPELL_DISRUPTING_RAY-CREATURE_MATRIARCH_*.png |
| `creaturespell:SPELL_FORGETFULNESS@CREATURE_MATRIARCH` | CREATURE_MATRIARCH | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 175_creaturespell-SPELL_FORGETFULNESS-CREATURE_MATRIARCH_*.png |
| `creaturespell:SPELL_BLOODLUST@CREATURE_MATRIARCH` | CREATURE_MATRIARCH | PASS | target CREATURE_MATRIARCH; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 176_creaturespell-SPELL_BLOODLUST-CREATURE_MATRIARCH_*.png |
| `creaturespell:SPELL_SLOW@CREATURE_MATRON` | CREATURE_MATRON | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 177_creaturespell-SPELL_SLOW-CREATURE_MATRON_*.png |
| `creaturespell:SPELL_DISRUPTING_RAY@CREATURE_MATRON` | CREATURE_MATRON | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 178_creaturespell-SPELL_DISRUPTING_RAY-CREATURE_MATRON_*.png |
| `creaturespell:SPELL_BLOODLUST@CREATURE_MATRON` | CREATURE_MATRON | PASS | target CREATURE_MATRON; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 179_creaturespell-SPELL_BLOODLUST-CREATURE_MATRON_*.png |
| `creaturespell:SPELL_FORGETFULNESS@CREATURE_MUMMY` | CREATURE_MUMMY | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 180_creaturespell-SPELL_FORGETFULNESS-CREATURE_MUMMY_*.png |
| `creaturespell:SPELL_ANIMATE_DEAD@CREATURE_MUMMY` | CREATURE_MUMMY | WARN | target CREATURE_PEASANT; events ManaChanged,SpellCast,Healed,EffectAdded,AtbChanged,TurnStarted; cursor refuses 1 cells the engine accepts (2,9) | 181_creaturespell-SPELL_ANIMATE_DEAD-CREATURE_MUMMY_*.png |
| `creaturespell:SPELL_FIREBALL@CREATURE_PIT_FIEND` | CREATURE_PIT_FIEND | PASS | cell 9,7; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 182_creaturespell-SPELL_FIREBALL-CREATURE_PIT_FIEND_*.png |
| `creaturespell:SPELL_DISRUPTING_RAY@CREATURE_PIT_FIEND` | CREATURE_PIT_FIEND | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 183_creaturespell-SPELL_DISRUPTING_RAY-CREATURE_PIT_FIEND_*.png |
| `creaturespell:SPELL_DEFLECT_ARROWS@CREATURE_RUNE_MAGE` | CREATURE_RUNE_MAGE | PASS | target CREATURE_RUNE_MAGE; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 184_creaturespell-SPELL_DEFLECT_ARROWS-CREATURE_RUNE_MAGE_*.png |
| `creaturespell:SPELL_BLOODLUST@CREATURE_SERAPH` | CREATURE_SERAPH | PASS | target CREATURE_SERAPH; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 185_creaturespell-SPELL_BLOODLUST-CREATURE_SERAPH_*.png |
| `creaturespell:SPELL_DIVINE_VENGEANCE@CREATURE_SERAPH` | CREATURE_SERAPH | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 186_creaturespell-SPELL_DIVINE_VENGEANCE-CREATURE_SERAPH_*.png |
| `creaturespell:SPELL_SLOW@CREATURE_SHADOW_MISTRESS` | CREATURE_SHADOW_MISTRESS | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 187_creaturespell-SPELL_SLOW-CREATURE_SHADOW_MISTRESS_*.png |
| `creaturespell:SPELL_DISRUPTING_RAY@CREATURE_SHADOW_MISTRESS` | CREATURE_SHADOW_MISTRESS | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 188_creaturespell-SPELL_DISRUPTING_RAY-CREATURE_SHADOW_MISTRESS_*.png |
| `creaturespell:SPELL_FORGETFULNESS@CREATURE_SHADOW_MISTRESS` | CREATURE_SHADOW_MISTRESS | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 189_creaturespell-SPELL_FORGETFULNESS-CREATURE_SHADOW_MISTRESS_*.png |
| `creaturespell:SPELL_BLOODLUST@CREATURE_SHADOW_MISTRESS` | CREATURE_SHADOW_MISTRESS | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 190_creaturespell-SPELL_BLOODLUST-CREATURE_SHADOW_MISTRESS_*.png |
| `creaturespell:SPELL_SLOW@CREATURE_SHAMAN` | CREATURE_SHAMAN | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 191_creaturespell-SPELL_SLOW-CREATURE_SHAMAN_*.png |
| `creaturespell:SPELL_HASTE@CREATURE_SHAMAN` | CREATURE_SHAMAN | PASS | target CREATURE_SHAMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 192_creaturespell-SPELL_HASTE-CREATURE_SHAMAN_*.png |
| `creaturespell:SPELL_SLOW@CREATURE_SHAMAN_HAG` | CREATURE_SHAMAN_HAG | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 193_creaturespell-SPELL_SLOW-CREATURE_SHAMAN_HAG_*.png |
| `creaturespell:SPELL_HASTE@CREATURE_SHAMAN_HAG` | CREATURE_SHAMAN_HAG | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 194_creaturespell-SPELL_HASTE-CREATURE_SHAMAN_HAG_*.png |
| `creaturespell:SPELL_SLOW@CREATURE_SHAMAN_WITCH` | CREATURE_SHAMAN_WITCH | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 195_creaturespell-SPELL_SLOW-CREATURE_SHAMAN_WITCH_*.png |
| `creaturespell:SPELL_HASTE@CREATURE_SHAMAN_WITCH` | CREATURE_SHAMAN_WITCH | PASS | target CREATURE_PEASANT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 196_creaturespell-SPELL_HASTE-CREATURE_SHAMAN_WITCH_*.png |
| `creaturespell:SPELL_CHAIN_LIGHTNING@CREATURE_SHAMAN_WITCH` | CREATURE_SHAMAN_WITCH | SKIP | engine does not offer it in this set-up; offered: SPELL_SLOW SPELL_HASTE | 197_creaturespell-SPELL_CHAIN_LIGHTNING-CREATURE_SHAMAN_WITCH_*.png |
| `creaturespell:SPELL_WASP_SWARM@CREATURE_SPRITE` | CREATURE_SPRITE | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 198_creaturespell-SPELL_WASP_SWARM-CREATURE_SPRITE_*.png |
| `creaturespell:SPELL_DISPEL@CREATURE_SPRITE` | CREATURE_SPRITE | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 199_creaturespell-SPELL_DISPEL-CREATURE_SPRITE_*.png |
| `creaturespell:SPELL_ICE_BOLT@CREATURE_WATER_ELEMENTAL` | CREATURE_WATER_ELEMENTAL | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 200_creaturespell-SPELL_ICE_BOLT-CREATURE_WATER_ELEMENTAL_*.png |
| `creaturespell:SPELL_FROST_RING@CREATURE_WATER_ELEMENTAL` | CREATURE_WATER_ELEMENTAL | PASS | cell 10,7; events ManaChanged,SpellCast,Damaged,AtbChanged,TurnStarted | 201_creaturespell-SPELL_FROST_RING-CREATURE_WATER_ELEMENTAL_*.png |
| `creaturespell:SPELL_DISPEL@CREATURE_ZEALOT` | CREATURE_ZEALOT | PASS | target CREATURE_FOOTMAN; events ManaChanged,SpellCast,AtbChanged,TurnStarted | 202_creaturespell-SPELL_DISPEL-CREATURE_ZEALOT_*.png |
| `creaturespell:SPELL_BLOODLUST@CREATURE_ZEALOT` | CREATURE_ZEALOT | PASS | target CREATURE_ZEALOT; events ManaChanged,SpellCast,EffectAdded,AtbChanged,TurnStarted | 203_creaturespell-SPELL_BLOODLUST-CREATURE_ZEALOT_*.png |

Counts: PASS 198, SKIP 1, WARN 4

## Results (MMH55, `--target=mmh55`, same build)

Counts: PASS 214, WARN 3, FAIL 1, SKIP 6 (before G1–G3: PASS 196, WARN 15, FAIL 7, SKIP 6). Differences from stock:

| Case | Result | Detail |
|---|---|---|
| `ability:ABILITY_LEAP` | FAIL | no target in range in this set-up (cursor and engine agree: no target accepted); set-up, not a UI fault |
| `ability:ABILITY_DEFILE_MAGIC` | SKIP | no MMH55 creature has the ability |
| `heroability:IMBUE_ARROW:SPELL_MAGIC_ARROW` | SKIP | not offered with Magic Arrow in MMH55 data |
| `spell:SPELL_WARCRY_FEAR_MY_ROAR`, `spell:SPELL_WARCRY_RALLING_CRY` | SKIP | not offered to the preset hero (level) |
| Resurrect / Animate Dead | WARN | G4 |
