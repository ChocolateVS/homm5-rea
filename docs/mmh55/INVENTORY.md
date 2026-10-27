# MMH55 (Might & Magic: Heroes 5.5) — inventory of what it changes

Target install: `G:\HoMM 5 - Tribes of the East (5.5)` = GOG Tribes of the East 3.1 + MMH55 **RC 19e**
(`MMH55-Index.pak:scripts/H55-Version.lua` → `H55_Version = "19e"`; `UI/MainMenu2/Version.txt` "Version: 5.69k";
DLLs built 12 Oct 2025 (`55.dll`, `55u.dll`) and 20 Sep 2025 (`55e.dll`); pak entries dated 2–13 Oct 2025).
Analysed 2026-10-19 without running anything. Addresses are VAs in stock GOG 3.1 `H5_Game.exe` unless marked QAI.
Raw lists (scratch, regenerable by the scripts named): patch tables, pak classification, Lua call graphs.
Committed derived lists: `dll_patches_55u.txt` (every 55u.dll patch, stock addresses), `dll_patches_55_qai.txt`
(55.dll, MMH55.exe addresses), `missing_natives.txt` (Lua natives MMH55 calls that H5.Scripting lacks).

MMH55 is three layers: **(A) runtime code patches** from a hook DLL, configured by an XML; **(B) data** in 10 paks +
8 user-mod archives; **(C) Lua** (~23k lines) attached to every map through overridden startup scripts.

## 1. Executables — which one is played

| Binary | md5 | What it is | MMH55 code |
|---|---|---|---|
| `bin\H5_Game.exe` | 5a881b84… | **identical to stock 3.1** (build 0x48469f9f, May 2008) | none (imports Ubisoft `um.dll`) |
| `bin\MMH55.exe` / `MMH55_64.exe` | 841093c2… / a514c7b0… | a **different engine build: "3.1j enhanced AI" (Quantomas AI, QAI)**, 20 Oct 2011; spawns `H5_AIProcess_31j.exe` as the AI process. `_64` = same + Large-Address-Aware bit only | imports `55.dll` (`TrackingCount`) |
| `bin\MMH55_Utility.exe` / `_64` | 33799609… / 26ef2784… | stock 3.1 + 346 bytes in 17 ranges; `_64` = LAA bit | import `um.dll`→`55u.dll` |
| `bin\H5_Game_PEST.exe` | c44f05d5… | stock + 16 bytes (the PEST patch, §2) — also present in the stock install. **The user launches this exe (the target).** | none |
| `bin\MMH55_Editor(_64).exe` | 97ac…/0b4e… | patched map editor | `55e.dll` |
| `bin\MMH55_Mapmixer.exe` | caf1a951… | standalone x64 tool | — |
| `binDM\H5_Game.exe` | 030fff20… | older build (Mar 2008), duel-mode exe | — |
| `um.dll`, `SI.bin` (1 byte 0xF2) | same as stock | Ubisoft telemetry (`TrackingCount`, uts.ubi.com); SI.bin is its state | — |

**Answered (user, 2026-10-19): the target is `bin\H5_Game_PEST.exe` = stock engine + PEST bytes, no DLL; see PLAN.md §0.
The notes below about MMH55(_64).exe / MMH55_Utility.exe describe optional, out-of-scope engines.**
**The GOG shortcut / goggame `.info` launch `bin\H5_Game.exe`, which is stock: launched that way the MMH55 paks
run on a vanilla engine with none of the DLL patches.** MMH55 is fully active only via `MMH55.exe`/`MMH55_64.exe`
(QAI engine + `55.dll`) or `MMH55_Utility(_64).exe` (stock engine + `55u.dll`). **Open question for the user: which
exe do you start?** (MMH55's install notes normally point to `MMH55_64.exe`.) Consequence: if it is MMH55_64.exe, the
adventure AI is QAI, not the stock AI we reverse-engineered (`docs/rules/AI.md`), and MMH55.exe cannot be diffed
against its base (the vanilla 3.1j exe is not on disk); `55u.dll`'s patch set on stock addresses (124 groups, of
which 120 also exist in `55.dll`) is the reimplementation reference for everything except QAI.

Profiles: `profiles\default_profile\{input_a2,user_a2}.cfg` (stock format), `profiles\p2pdir.cfg` (Dec 2025).

## 2. Exe byte patches (stock addresses)

**PEST — "permanent endless simultaneous turns"** (16 bytes; full byte table in `docs/rules/SIMULTANEOUS.md` §7; also applied at runtime by the DLL group `/MMH55/Pest`):
CWorld vtable +0x1c (`0x9c2660`, the player-area conflict check) becomes `RET`; call sites
`if (world->vf1c()) { world->vf20(); FUN_009c22f0(); }` are skipped (JZ→JMP) at 0x42e072 (build boat,
FUN_0042de80), 0x8659c5 / 0x865ae3 (adventure spell cast FUN_008656e0, e.g. Summon Boat), 0x98f909, 0x9984f7,
0xb4b6d4; call to FUN_009c25c0 at 0xaf3664 NOPed; 0x8659c5 is a 6-byte JZ rel32 → JMP rel32 + NOP (Dimension Door, deferred break). → simultaneous turns, once on, never break on contact; network games with the option only.

**MMH55_Utility.exe (same patches found by signature in MMH55.exe):**
- 0x81eeb7 JZ→JMP skips the stock UbiStats call; entry stub 0xd11f3c → cave 0xd122e1 calls the `TrackingCount`
  thunk (0xd3e37e) = DLL initialisation.
- Spell table count (UndividedSpells.xdb) 0x161→0x179 at 0x847891, 0x84c9c0 (DLL raises it to 0x17b = 379).
- Artifact table count 97→196 at 0x8478a1, 0x84ca7f (cave 0x84cad8).
- HeroClass.xdb count 9→74 at 0xae29e0 (FUN_00ae2980).
- RMG FUN_00907150 (0x90760a–0x907646): upgrade-building multiplier default 1.0 {0.2,0.5,1,2,4} → 1.4 {1.0,1.2,1.4,1.6,2.0}.
- RMG guards FUN_00ba7690 (0xba77a9–0xba7866, table 0xba79d4): strength coefficients {0.4,0.9,1.7,4,12} → {1.0,1.9,2.7,3.5,7.0}.
- RMG places Dimension Doors instead of Cartographers: rdata 0xe577dc "Cartographer.(AdvMapCartographerShared)" →
  "RMGDimensionDoor.(AdvMapBuildingShared)", dynamic-cast target at 0x920cb2.

## 3. The hook DLLs (`55.dll` = MANTICORE_QAI, `55u.dll` = MANTICORE_TOE, `55e.dll` = editor)

Load: exe entry stub → `TrackingCount` → 0x10032f60 (55u; 0x10037450 in 55.dll): MinHook init, log `MMH55.log`,
config `MMH55Stats.xml` from the first existing of `../UserMODs/MMH55-CustomStats.h5u`, `../data/MMH55-Stats9.pak` …
`MMH55-Stats1.pak`, `../data/MMH55-Stats.pak`. Each patch group `/MMH55/<Name>` applies only when
`enabled="true"`; a group is a list of 0x88-byte records {type, addr, size, detour/value, …} applied by 0x100339f0
(55u) via VirtualProtect. Types: 0 JMP→detour, 5 CALL→detour, 1 byte, 2/3 dword (int/float from XML), 6 hex bytes,
7/8 Jcc→detour, 0xb NOP fill, 0xc memcpy. **No Lua functions are added** (every native MMH55 scripts call exists in
the stock exe). In the user's XML 80 groups are enabled; only `CreatureArtifacts/CanBeEquippedOnAllCreatures` is off.

Groups (full sites in `dll_patches_55u.txt`), by area:
- **Tables / limits:** Tables (spells 379; hooks 0x9f4af6, 0x9f2416), Tables_Skills (skills 221→251 at 0x84cb40,
  0x8478b1, 0xd8981f; hooks 0x9f7166, 0x9f58f6), CreatureFramework (creature count; 0xacb381, 0xae2620, hook 0xba7d84),
  DwellingFramework (0x5c97c6/ca = 7, hooks 0x41bb00, 0x91ed49), HeroModifiers/Levels (level table at 0xb4c6b0..,
  cap 99 at 0x997ac0), HeroModifiers/Hero8Skills (8 skill slots: 0xb50de5, 0xb4d399 = 8), BattlefieldSize
  (0xaf14b6/0xaf14bc = **14×12** from XML), CombatCameraLimits.
- **Combat rules (modify audited mechanics):** CreatureStatsModifiers/{Attack, Defense, Health 0x4bc4f4, Initiative
  0x4bc1f0, Luck 0x98576e, Morale 0x98a175/0xa6ad35/0x4ba9de, Resistance 0x97ce0d, Speed 0x8a203b, Immunity,
  Spellproof, SpellsAndAbilities, ArtifactSets/SarIssus}, CreatureAbilitiesChance (0xa599c0–de), CreatureAbilities/Fear,
  AbilitiesAtbReset, CalculateRangePenalty, CalculateSpellDuration, CanCastSpell, SpellCost (0xa347a2, 0xa348da),
  MassSpells (16 sites), CastersLuckDamage_base (25 + hero level), EmpoweredSpellsDamage_base, DisableMagicOpposites,
  DisableMagicResistForNonDamagingSpells, MagicMirror, SpiritLink, SummonElementalsPerks, IgniteChanges,
  ArcaneRenewalFix, EmpoweredArmageddonFix, FlamingArrowsDamageAndDefense, BattleDiveDamage 0xa5fe81, PawStrikeDivisor,
  ChainShot/ColdDeath immunities, ConsumeCorpseSqrtHp, VampirismFix, SnareFix, EncourageFix, EarthquakeTremors,
  SoilBurn, HellfireTriggerChance (0xa5eea0 = 66), RangedRetaliationStrike, DwarfFormationsForAll, DjinDarkLightAbility,
  EnableRootsForSavageTreants, DisruptorMerge, Avenger, AvatarOfDeathNoMana, PowerfeedCapped, ProtectorSpecialization,
  PyromancyBonus, DestructionSkill, OccultismPenetration, ExpireSpelleffectsThatShouldEndAfterCreatureTurn,
  EffectsCleansedByHeal, Modifiers/* (AbsoluteCharge, ChillingBones/Steel, EnergyChannel, Enlightenment, ErraticMana,
  ExpertFrenzy, HeroAlignmentMoraleBonus, LastStandTrigger, MatronSalvo, PerkStats, PowerOfStone, ResurrectAllies,
  RuneOfBattleRageDamageModifier, SorrowStrikeDuration, SpellProof, StaffOfTheNetherworld, WhipStrike),
  RuneOfBerserkingChange, RuneOfTheDragonForm, SufferingStrikeToMasterOfCurses, HeroSpecFireballAttackFix,
  SkillsAddingBuffEffecsToCreatures, ImbueBalistaATB, TowerDamage (hook 0xcd221d; 200/140), WarMachines/{Health,
  Tent, Tier, BalistaAndCatapultShots 0xa75cb4..}, CreatureMaxDamage, CreatureArtifacts (creature-equipped artifacts),
  ExtendedElementalArtifacts, WandArtifact, PendantMasteryAndGating, HavenTraining, SacrificialPitHealth,
  SwapBarbarianSpellbook, CombatAI (8 sites 0x8d0924–0x8d3b3f, 0xbfbd90).
- **Adventure / economy:** Modifiers/Movement (pathfinding/logistics/navigation/boots factors, diagonal 1.41),
  InstantTravelMovementRequirements (50 %→20 %), Income (artifact/hero income), Weeks (week effects, 0xbcdcad..),
  Adventure/ResourcePiles, SpellShrineExperience, BookOfPowerLevelUpFix, EnlightenmentForBarbsFix,
  MasterOfElementsDisableSummon, SpellpowerBonuses, OneStackSplit (0x7866a6), Pest.
- **RMG (55u only):** IRandomMap (0x90c4a5, NOP 0x90eaab), RMG/DeployMines (0x91fb45), RMG/DisableSingleStacks (0xba7891).
- **UI / system:** GUI (0xc202f7, 0x56392b), HeroUI (11 byte patches in 0x6c1c24–0x6ca46f, 0x623a9a–b23),
  TooltipCreatureArrayUpdate + add_creature_active_spelleffects_to_its_SCreatureInfoTooltip_array, ShowPlayerTurnTimerInCombat
  (NOP 0x58d91b), MultiplayerCombatScripts (NOP 0x54ce31 — combat scripts run in multiplayer), RemoveMapScriptCheck
  (0x992d2a..0x992e6f), System/Networking (TransmissionInterval 0.02; 0x47e553), Borderless (windowed mode).
- 55.dll only (QAI addresses): QaiFixes, QaiWideScreen, StormWind. 55e.dll: CreatureFramework, DwellingFramework,
  IRandomMap, RMG/DeployMines, RMG/DisableSingleStacks.

## 4. Pak mounting (stock rule; MMH55 does not change it)

FUN_00c8bd00 → FUN_009e0b40 → FUN_009e0560 registers `data\*.pak` (priority 0), then `Maps/*.h5m`, `DuelPresets/*.h5p`,
`UserCampaigns/*.h5c`, `UserMODs/*.h5u`, `UserMODs/*.zip` (priority −1; `*.zip` absent in the QAI build).
FUN_009e01b0 enumerates each with FindFirstFile/FindNextFile; FUN_009df790 adds every zip entry.
**On a path clash FUN_009def50 keeps the entry with the strictly newer central-directory DOS date/time**
(`(date<<16)|time`, FUN_0047bc50 reads +0xc/+0xe); ties keep the earlier-registered entry; priority only matters
when both differ and neither is −1. So overriding is **per file, newest timestamp**, not per archive.
Implemented: `src/H5.Core/IO/PakLoadOrder.cs`, `PakFileSystem.Mount`, `PakArchive.Entry.DosTime`
(tests `PakLoadOrderTests`, `PakTimestampTests`). All stock tests still pass under the new rule. Behaviour change:
`UserMODs/*.pak` is no longer mounted (the exe only takes `*.h5u` and `*.zip` there).

Where the two rules disagree in MMH55 data (so the timestamp rule matters): `_(Model)/Artefacts/tank-geom.xdb`
(Data beats Frame by date), 1283 `bin/` files Cam-Dialogs vs Cam-Maps (Cam-Dialogs by date), 4 texts
SPMaps-Texts.h5u vs Texts-EN.pak (Texts-EN by date). Intended overrides that both rules agree on: Index over Data
(281 TownSpecialization xdbs), Texts-EN over Data (31 CombatLog texts), Skillwheel over Data (7 HeroScreen3 layouts),
**Settings over Index (`scripts/H55-Settings.lua`, rewritten by the Utility with a newer date)**.

`data\index.bin` and the `index.bin` inside data.pak / a2p1-data.pak / MMH55-Frame.pak are the engine's serialised
`NDb::COldTypes` cache (0x499f90/0x4998c0/0x499b80), regenerated by the game — not a pak index; ignored.

## 5. Data (paks)

The six stock paks in the 5.5 install are byte-identical to stock. Per MMH55 archive (OVR = path exists in stock with
different content, SAME = identical copy):

| Archive | Files | NEW | OVR | SAME | Content |
|---|---|---|---|---|---|
| MMH55-Data.pak | 3795 | 1910 | 1403 | 482 | UI 805 (combat log, ATB bar, JogDial, rune bar, hero screen), textures 754, hero visuals 330, town specialisations/buildings 332, arena objects 265 + 105 arenas, CreatureVisual 102, 82 spells (22 new Hero_Skills), music, models |
| MMH55-Texts-EN.pak | 5840 | 3825 | 1310 | 705 | Text/Game/* (heroes, skills, artifacts, 360 Text/Game/Scripts), Creaturepedia/SkillWheel docs, UIDifficultyInfo |
| MMH55-Index.pak | 3467 | 2041 | 852 | 574 | **core rules**: 179 creature overrides, 82 Combat_Spells, 202 heroes (117 OVR + 85 NEW), 1295 hero visuals, 846 RMG CustomArmyTemplates/Tiles/Params, TownBuildingSharedStats, all RefTables + RPGStats, **all system scripts** |
| MMH55-Settings.pak | 1 | 1 | – | – | `scripts/H55-Settings.lua` (user settings, §7) |
| MMH55-Stats.pak | 1 | 1 | – | – | `MMH55Stats.xml` (DLL config, §3) |
| MMH55-Templates.pak | 94 | 72 | 22 | – | RMG templates: all 22 stock overridden + 72 new |
| MMH55-Skillwheel.pak | 3907 | 3890 | 17 | – | UI/Doc/SkillWheel; HeroScreen3, LevelUpBox, lobby overrides |
| MMH55-Creaturepedia.pak | 4015 | 3992 | 23 | – | UI/Doc/Creaturepedia; HireCreatures, CreatureInfo, FortHill, UpgradeCreature UI |
| MMH55-Frame.pak | 3815 | 3758 | 51 | 6 | **creature framework**: 820 placeholder creatures + 820 monster links + 806 cameras, 750 `scripts/creature_250..999.lua`, `scripts/common.lua`, **`types.xml`**, `index.bin`, RefTables/Creatures.xdb, 108 artifact objects, dwellings, UIGameRoot |
| MMH55-HDTex.pak | 653 | 122 | 249 | 282 | HD terrain/skydome textures and models |
| MMH55-ARMGMaps-Demo.h5u | 865 | 865 | – | – | ARMG demo maps |
| MMH55-Cam-Maps.h5u | 15984 | 15912 | 60 | 12 | campaign maps, 10920 dialog scenes, 3198 dialogs, 102 lua |
| MMH55-Cam-Cutscenes / Cam-Dialogs-EN / Cam-Texts-EN.h5u | 367 / 1283 / 5072 | | | | cutscenes, dialog audio/anim, texts |
| MMH55-SPMaps(-Dialogs-EN, -Texts-EN).h5u | 373 / 3706 / 423 | | | | single-mission maps (20 lua), dialogs, texts |

**Enum extensions** (`types.xml` from Frame; no new structs or fields — 96 enums / 643 structs as stock):
CreatureType 180→1000 (SANC_101..703 = 180..200, RESERVED_001..049, CREATURE_250..999), SpellID 353→379
(SPELL_SKILL_TRAINING1-3, MAGIC_MIRROR, RETRIBUTION, HOLD_GROUND, DEAD_LUCK, CHILLING_STEEL, SPELLPROOF_BONES,
FOREST_RAGE, RUNIC_MACHINES, STORM_WIND, FOG_VEIL, MARCH_OF_THE_MACHINES, ARTIFICIAL_GLORY, MAGIC_CUSHION,
FAST_AND_FURIOUS, ELITE_CASTERS, TRIPLE_BALLISTA, DEATH_TREAD_*, SPELL_SPEC_ARTILLERYMAN_CATAPULT,
SPELL_ABILITY_LUCK_GAMBLER_ENEMY/REGENERATION/SHIELDED, SPELL_EFFECT_SHOCKED), SkillID 221→251 (HERO_SKILL_221..250
placeholders), HeroClass 9→25 (HERETIC, RANGERA, WARDEN, ENCHANTER, SORCERER, FLAMEKEEPERA, REAVER, NECROMANCERA,
WARLOCKB, BEASTMASTER, GUILDMASTER, SEER, WIZARDA, ELEMENTALIST, RUNEMAGEA, SHAMAN), ArtifactEffect 97→196.

**RefTables:** Creatures 180→1000, Artifacts 97→196 (inline definitions), UndividedSpells 353→379, HeroClass 9→25;
Skills.xdb still 221 IDs (content changed; a 251-ID copy sits at `RefTables/Skills.xdb`, outside GameMechanics);
CombatLog, WarMachines, TownTypesInfo, MicroArtifactEffects, RMGPresetTable changed. Heroes 118→203.

**The 820 placeholder creatures** (Frame): 1/1/1 stats, `TOWN_NO_TYPE`, Snow Ape visual,
`SubjectOfRandomGeneration=true`; each `creature_N.lua` only sets `CREATURE_N = N`. Handled by the DLL's
CreatureFramework; our random-monster/RMG pools would currently pick them.

**DefaultStats.xdb:** 6420 leaf differences — Banks ~5500 (new guard variants), ArtifactSets (99 inline sets removed,
11 added), combat/HeroSkills 384 (288 Necromancy), combat/Creatures 157, AI/Adventure 68, combat/Spells 46,
HeroSpecializations 40. Highlights: Hero/SpellInitiative 10→11; BaseHeroMovement 2500→2800; LookRange 12→16;
Oasis 800→500, Stables 600→400 (weekly 600→100), Lighthouse 500→300; human start gold 50000/30000/20000 →
25000/20000/15000, AI start gold 20001/20001/30001/30001; neutral CountCoefs 0.5/1/1.12/1.4→1/1.1/1.2/1.3,
WeeklyGrow 1.05..1.11→1.10..1.15; DamageIncrease/Decrease 0.05→0.025, caps 3→4 / 0.1→0.25; obstacles ~doubled,
FreeLines 2→6; Scouting 4→8; DisguiseAndReckon 12→0; war machine HP per skill 100→5; Astrologer 6000→2500;
altar/market artifact coefficient 0.5→0.25; Warcry RP level 1 20→240; PreciseShotRange 3→5; LifeDrain 0.5→0.3.

**Data errors the engine tolerates** (our reader must too):
- `GameMechanics/Shot/Creatures/Orcs/Kujin.xdb` — XML declaration lacks `>`; `UI/AdventureScreen-Heavy.(WindowScreenShared).xdb`
  — `<-- !Item …/>` (a botched comment; breaks our adventure HUD). **Fixed:** `XdbLoader` falls back to
  `XdbRepair` (closes the declaration, drops the broken tag). The engine's own XML reader was not traced; dropping the
  tag is the evident intent (UNVERIFIED).
- Invalid enum strings: `CREATURE_BERZERKER`, `CREATURE_THUNDERBIRD` (DefaultStats), `BUILDING_NAGA_TEMPLE`
  (MapObjects/Cyclops_Stockpile), `HERO_SPEC_EXPEDITER` (OrlandoMP), `HERO_SPEC_TEMPTRESS` (DMessiah) — our loaders keep
  enums as strings, so these load; the engine presumably maps them to the default value (not traced).
- Two empty `.tga.xdb` files in HDTex; `Maps/Scenario/A1C3M1/a1c3m1.lua` is a lone UTF-16 BOM; `A1C3M5/script_combat.lua`
  has a syntax error (`elseif diff == 3;`) — engine would fail the same way.
- MMH55 duel presets (`UI/MPDMLobby/presets`) point to "…C" heroes (e.g. VingC, class RANGER, 2/2/10/8, "Chess"
  specialisation) — placeholders that the duel scripts configure; db-check output for them is genuine.

New XML element paths (no new root types): `AdvMapBuildingShared/passableTiles/Item/{x,y}`,
`AdvMapDesc/objects/Item/AdvMapSphinx/pointLights`, `AdvMapDesc/sRMGProps/RMGguid`, inline `BasicSkelAnim/Sound/Sound/*`,
`RMGTemplate/Editor`, `WindowMSButtonShared/.../OnEnterSubState/Commands/Item`. New lobby options in UI:
`EE_artefacts`, `EE_decisionhall`, `EE_discount`, `EE_lookahead`, `EE_mercenaries`, `EE_taxes` (handled by 55.dll, not traced).

## 6. Lua

Attached to every map without touching maps: the engine runs `scripts/advmap-startup.lua`, `combat-startup.lua`,
`town-startup.lua`; MMH55-Index overrides all three.
- Adventure: advmap-startup (`dofile = doFile`, new ARTIFACT_* constants) → advmap-common (difficulty resource table)
  → common.lua (Frame; `H55-Library.lua` then 750 `creature_N.lua`) → H55-Tables → H55-Core → H55-Version, H55-Settings,
  H55-AdvMap.
- Combat: combat-startup → combat-common → common → H55-Combat; `DoPrepare`/`DoStart`/`UnitMove` call
  `C55_CombatPrepare`/`C55_CombatStart`/`C55_Combat`.

Systems: **H55-Library** (pure-Lua `table/math/string`); **H55-Core** — `H55_ContinuesActivator`, a `sleep`-polling
loop that detects day/week/current player and runs Weekly/Daily/Continues events (per-player payouts), AI detection and
teams (Nightmare/Savage auto-teams, AI cheat-mode bonuses), duel mode, console commands, mixed neutral stacks and
power rating, Town Gate, town/dwelling conversion, governors, mine control, artifact sets (Legion, Dragonish, Sar-Issus,
Moon Disc, Tomes, Guardian, Cornucopia), script-side skills/perks (Insights, Secrets, Tribal, Enlightenment, Runelore,
Necro Leadership, Shouting, Catapult, Twisted Avenger, Mentoring, Dark Ritual, Eternal Servitude, Med Kits, Artificer
Wand, potions, boots), weekly reinforcements, resurrection, mana regeneration/limits, specialisation stat tweaks;
**H55-AdvMap** — ~140 adventure objects re-scripted through OBJECT_TOUCH (mines/guards, dwelling conversion,
mills/gardens/workshops, magi huts as dimension doors, seer/witch huts, sacrificial altar, trading posts, fountain/temple
summoning, sphinx/sanctuary, tavern casino, banks with timers, obelisk/Shantiri quests, wagons/skeletons, Grail, prisons);
**H55-Combat** — first-turn free spells/summons/mines for specialists, caster mana tweaks, ATB boosts, data from the
adventure map via `SetGameVar("C55_*")`; A2_Artifact_Sets / A2_Zehir updated. Uses `consoleCmd` (`setvar ai_time_limit`,
`bindsection`/`bind enter_pressed 'Z'`, `game_writelog`, `console_size`).
Triggers used by system scripts: OBJECT_TOUCH 123, REGION_EXIT 6, REGION_ENTER_AND_STOP 4,
REGION_ENTER_WITHOUT_STOP 3, CUSTOM_ABILITY 3, NEW_DAY 1 (maps add OBJECT_CAPTURE, PLAYER_ADD/REMOVE_HERO,
OBJECTIVE_STATE_CHANGE, COMBAT_RESULTS, HERO_LEVELUP).

**Natives missing in H5.Scripting** (`missing_natives.txt`): system scripts — `EnableHeroAI` ×10, `DenyAIHeroesFlee` ×2,
`SetAIPlayerAttractor` ×1 (combat: none). MMH55 campaign/SP maps add 12 more, all AI/hiring/camera: SetAIHeroAttractor 74,
AllowHeroHiringByRaceForAI 28, DenyAIHeroFlee 27, EnableCinematicCamera 25, AllowPlayerTavernHero 21,
BlockTownGarrisonForAI 16, AllowPlayerTavernRace 16, EnableAIHeroHiring 15,
DoNotGiveTurnToPlayerAIIfNoTownsAndActiveHeroes 9, AllowHeroHiringByRaceInTown 8, SetPlayerHeroesCountNotForHire 6,
AllowHiringOfHeroInTown 2. Already registered and newly exercised by MMH55: GetHeroArtifactsCount, IsTilePassable,
GetTerrainSize, GetMaxFloor, IsPlayerInGhostMode, IsPlayerCurrent, IsTeamCurrent, SetPlayerTeam, *ForPlayers,
OpenPuzzleMap, DestroyTownBuildingToLevel, GenerateMonsters, GetCurrentMoonWeek, GetObjectDwellingCreatures,
GiveHeroSkill, Give/HasHeroWarMachine, SetHeroLootable, UnitCastAimed/AreaSpell, Get/SetUnitManaPoints, setATB,
SetControlMode, SetTrigger — their behaviour under MMH55's heavy use is unverified.

## 7. Settings in the user's install

`MMH55-Settings.pak:scripts/H55-Settings.lua` (entry dated 2025-10-12, newer than the 2025-10-02 build → written by
MMH55_Utility) differs from Index's default copy in three values: **H55_TownConvEnabled=1, H55_GovernanceEnabled=1,
H55_MineControl=1** (default 0). Others: TownGateEnabled=1, AmountExtraTownsDwellings=1, GovernorExpCoef=1,
DimensionDoors=1, DimensionDoorsOnThisMap=0, BuildNeutralTowns=0, BuildPlayerTowns=0, CompetitiveARMGTowns=1,
IgnoreExpAdjustment=0, NoAutoTeamHumans=0, AICheatMode=4, LowResGame=0, NoMentoring=0, NoHillFortsOnARMG=0,
GenerateMixedStacks=1, MixStacksThreshold=200, AskForMixedStacks=0, ForceMixedStacks=0, BanksDifficulty=1,
NeutralStackSize=1, PrisonersAdjustToHeroRace=0, ManaObservatories=1, NoArtifactMerchantsInTowns=0, ForcedObelisk=0,
ForcedShantiri=0, NegativeArtifacts=1, MoonDiscQuest=0, MoonDiscOnARMGMaps=1, RPGPotions=0, Duel_WeeksGrowth=8,
Duel_HeroLevel=25, Duel_Relics=4, Duel_Majors=8, Duel_Minors=4, Duel_BankDifficulty=6.
`MMH55Stats.xml` (~60 feature blocks with values, e.g. BattlefieldSize 14×12, TowerDamage 200/140, Movement factors,
MassSpells cost factors, Weeks modifiers, Networking 0.02) is the DLL side. Simultaneous turns are not a setting:
PEST makes them permanent. `data\RMGTemp\CurrentMap` is a runtime leftover.

## 8. H5Reborn on the 5.5 install (2026-10-19)

- Default install order is now `G:\HoMM 5 - Tribes of the East (5.5)` then stock; `H5_GAME_DIR` still overrides;
  `H5_TARGET=stock` picks the stock install. **Startup choice (2026-10-28, user request):** when both a stock 3.1 and an
  MMH55 install are found (defaults, or `stock=` / `mmh55=` lines in game_dir.txt; an old single-path file still works
  and names one of the two), the game asks "Heroes V: Tribes of the East 3.1" or "MMH 5.5" in the stock two-choice box
  before anything else loads, the last answer preselected (user://launcher.cfg). Skipped by `H5_GAME_DIR`,
  `--target=stock|mmh55`, `H5_TARGET`, a single install, headless runs and dev/test switches (those keep the 5.5-first
  order). Logic: `H5.Core.IO.GameTargets` (tests `GameTargetTests`), UI `game/src/FrontEnd/TargetPrompt.cs`.
  Simultaneous turns: PEST applies on both targets (stock 3.1 through a default-on H5Reborn option, a user-requested
  deviation, SIMULTANEOUS.md §7.4). **Tests pin stock** (`tests/stock.runsettings` via
  `tests/Directory.Build.props`) because their assertions are stock 3.1 values.
- `h5tool db-check` on 5.5: creatures 999 (180 + 820 placeholders), abilities 175, spells 378, skills 221, artifacts 196,
  classes 25, heroes 202, duel presets 24, 0 warnings (stock: 179/175/352/221/97/9/116/24).
- `lua-compile`: 896 scripts, 4 failures — the 2 stock ones plus the 2 MMH55 data errors above.
  `adv-stats`: 117/117 maps load, 0 warnings. `terrain-stats`: 504 decoded, 0 failures. `geom-stats`: 3619/3619 geometries
  (11 material-quantity / 3 mesh-count descriptor mismatches, as in the stock set). `gr2-stats`: 2280/2280 skeletons,
  3538/3538 animations. `ui-dump UI/AdventureScreen-Heavy` loads after the XdbRepair fix (threw before).
- Full test suite run against 5.5 (before the timestamp rule): **91 of 355 tests fail** — all assert stock values (creature
  stats, spell/skill numbers, start resources, RMG template output, map contents, music lists). List: §PLAN "Test split".
