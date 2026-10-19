# MMH55 5.5 as the target — work plan

Facts behind this plan: `INVENTORY.md`. Sizes: S ≈ ≤1 day of one helper, M ≈ 2–4 days, L ≈ a week+, XL = several helpers.
Rule as before: every behaviour from the binaries/data (now: stock exe **plus the 55u.dll patch it applies**), ledger per item.

## 0. Decisions needed from the user (blockers)

1. **Which exe do you play?** `MMH55_64.exe` (QAI "3.1j enhanced AI" engine + 55.dll) or `MMH55_Utility_64.exe`
   (stock engine + 55u.dll)? The GOG shortcut runs the stock `H5_Game.exe`, which ignores all DLL patches.
   If MMH55_64.exe: the adventure AI target becomes Quantomas AI (new RE effort, see W9) and combat/adventure patches are
   mapped from QAI addresses (`dll_patches_55_qai.txt`); otherwise the stock-address set (`dll_patches_55u.txt`) is exact.
2. Keep a "stock 3.1" mode? Proposal: yes — the loader already supports both (`H5_TARGET=stock`, `H5_GAME_DIR`), tests stay
   on stock until each workstream ports its tests; MMH55 behaviour is switched by "install has MMH55 + the enabled groups in
   MMH55Stats.xml", exactly like the DLL.

## 1. Done in this pass
- Loader: every `data/*.pak` + `UserMODs/*.h5u|*.zip`, per-file newest-timestamp override (exe FUN_009def50); 5.5 install is the
  default; tests pinned to stock; tolerant XML fallback for the two malformed MMH55 xdbs. 117/117 MMH55 maps and the rules DB load.

## 2. Workstreams (parallelisable)

### W1 Data-driven — should "just work" once loaded, needs verification + test ports (M, 1 helper)
Creature/spell/skill/artifact/hero/class stats, DefaultStats constants, town buildings/specialisations, banks, RMG templates,
texts, UI layouts/textures, HD textures, arenas/obstacles, campaign/SP maps.
- Port the 91 stock-value tests to a parallel MMH55 fixture (`H5_GAME_DIR` → 5.5) with MMH55 expected values; keep the stock set.
- Audit hard-coded counts/arrays that assume stock enum sizes: 180 creatures, 353 spells, 221 skills, 9 hero classes,
  97 artifacts, 6 hero skill slots, level cap, 12×10 battlefield (grep `EnumTables.cs`, save/net serialisers, UI grids).
- Placeholder creatures (820, `TOWN_NO_TYPE`, Snow Ape visual): exclude them where the DLL's CreatureFramework does
  (trace 0xba7d84 detour first — S) from random monsters/RMG/creature lists.
- New hero classes (16) and their skill tables / class icons in hero screen, tavern, level-up (S–M).
- Ledgers affected: AUDIT_COMBAT (values only), AUDIT_ADVENTURE, RMG.md, AUDIT_COMBAT_UI (texts/icons).

### W2 Lua runtime for MMH55 system scripts (M–L, 1–2 helpers) — highest leverage: ~140 adventure objects, skills, artifact sets
- Run `script-run all` on 5.5 maps and fix runtime errors; MMH55 relies on `sleep`-polling (`H55_ContinuesActivator`),
  `doFile` of 750 files at every script load (performance!), `consoleCmd` (`setvar`, `bind`, `game_writelog`), `SetGameVar`
  adventure→combat hand-over (`C55_*`), multiplayer combat scripts (DLL MultiplayerCombatScripts).
- Add the 15 missing natives (`missing_natives.txt`): EnableHeroAI, SetAIHeroAttractor, SetAIPlayerAttractor,
  DenyAIHeroFlee/DenyAIHeroesFlee, AllowHeroHiringByRace(ForAI/InTown), AllowPlayerTavernHero/Race, BlockTownGarrisonForAI,
  EnableAIHeroHiring, DoNotGiveTurnToPlayerAIIfNoTownsAndActiveHeroes, SetPlayerHeroesCountNotForHire,
  AllowHiringOfHeroInTown, EnableCinematicCamera — exe semantics from the stock natives table (S each, most AI flags).
- Verify the already-registered natives MMH55 leans on (list in INVENTORY §6) against the exe (M).
- Check town-startup.lua / combat-startup.lua hooks are invoked at the exe's points. Ledger: AUDIT_ADVENTURE (scripted objects
  replace engine visits where MMH55 sets OBJECT_TOUCH triggers — interaction with our native visit code must follow the exe:
  trigger + `SetObjectEnabled` disabling the default).

### W3 DLL combat patches (XL, 3 helpers split by group family) — rewrites parts of the combat audit
Implement each `/MMH55/*` combat group from `dll_patches_55u.txt`: decompile the detour in `55u.dll`
(Ghidra project `tools/ghidra-projects/mmh55`), read its XML parameters, implement behind an "MMH55 group enabled" switch,
add a ledger row (stock behaviour → MMH55 behaviour, detour address). Families:
- W3a stats/modifiers: CreatureStatsModifiers/* (8 groups), Modifiers/* (20), CreatureMaxDamage, CreatureAbilitiesChance,
  AbilitiesAtbReset, HellfireTriggerChance, BattleDiveDamage, PawStrikeDivisor, etc. (L)
- W3b spells: SpellCost, MassSpells, CalculateSpellDuration, CanCastSpell, MagicMirror, SpiritLink, DisableMagicOpposites,
  DisableMagicResistForNonDamagingSpells, Empowered*, Ignite, Armageddon, SummonElementals, Occultism, Pyromancy,
  Destruction, Expire…/EffectsCleansedByHeal, new spells (26 SpellIDs incl. 22 SKILL spells). (L)
- W3c war machines, siege, field: WarMachines/*, TowerDamage, BattlefieldSize 14×12 (arena layout, deployment zones,
  obstacles, camera), CombatCameraLimits, RangedRetaliationStrike, CalculateRangePenalty, creature artifacts. (L)
- Ledgers: AUDIT_COMBAT (all families; every MMH55 group gets a row), docs/rules/audit/combat_* fragments, COMBAT.md.

### W4 DLL adventure/economy patches (L, 1–2 helpers)
Pest (simultaneous turns never break — AdvNet/ghost-mode/turn logic), Modifiers/Movement (pathfinding costs, diagonal 1.41,
logistics/navigation/boots), InstantTravelMovementRequirements, Income, Weeks, ResourcePiles, SpellShrineExperience,
HeroModifiers/Levels (cap 99, level table), Hero8Skills, BookOfPowerLevelUpFix, OneStackSplit, DwellingFramework,
EnlightenmentForBarbsFix, RemoveMapScriptCheck. Ledgers: AUDIT_ADVENTURE, ADVENTURE.md, BOATS.md (PEST site in build-boat),
SAVE.md (hero level/skill-slot sizes), MULTIPLAYER.md.

### W5 RMG (M) — exe byte patches (upgrade multiplier, guard coefficients, Dimension Door for Cartographer) + 55u groups
IRandomMap, RMG/DeployMines, RMG/DisableSingleStacks + 94 templates, CustomArmyTemplates, new `RMGTemplate/Editor` element.
Ledger: RMG.md; Rmg tests (8 fail on 5.5).

### W6 UI (L, 1–2 helpers) — mostly data (805 UI files in Data, Skillwheel, Creaturepedia) + DLL
HeroUI byte patches (0x6c1c24–0x6ca46f, 0x623a9a…), GUI hooks, tooltip creature effect array, ShowPlayerTurnTimerInCombat,
8-skill hero screen (HeroScreen3), new lobby options (`EE_*`), Skillwheel/Creaturepedia docs screens, Borderless (n/a for us).
Ledgers: AUDIT_COMBAT_UI, AUDIT_FRONTEND, AUDIT_INPUT (`bind enter_pressed 'Z'` from scripts).

### W7 Combat AI (M) — CombatAI group (8 sites) on top of the stock combat AI (docs/rules/AI.md).

### W8 Networking (S) — System/Networking TransmissionInterval 0.02, MultiplayerCombatScripts; data fingerprint now
includes MMH55 paks (peers must match).

### W9 Adventure AI = QAI (XL, only if the user plays MMH55(_64).exe) — RE of `MMH55.exe` + `H5_AIProcess_31j.exe`
(separate AI process, Quantomas). Needs its own Ghidra analysis (project `tools/ghidra-projects/mmh55_qai`). Ledger: AI.md.

## 3. Order
W1 + W2 first (largest visible effect, mostly verification), then W3/W4 in parallel by family, W5/W6 alongside, W7/W8 small,
W9 after the user's answer.

## 4. Test split
Tests run on stock by default (`tests/stock.runsettings`). Against 5.5 (`H5_GAME_DIR=…(5.5)`) 91 tests fail on stock values:
Combat 55 (AuditSpellTests 8, RulesTests 5, AuditAbilityTests(A) 10, SpecialsTests 4, DataTests 4, AuditSpecSetTests 4, …),
Adventure 23 (MovementTests 8, SeerHut, Necromancy, Boats, AdventureSpells, OriginalAdventureAi 2 each, …), Rmg 8, Audio 2,
Scene 1, Cinematics 1, Save 1. Each workstream ports the tests of its area to an MMH55 fixture with exe/DLL-derived values.
