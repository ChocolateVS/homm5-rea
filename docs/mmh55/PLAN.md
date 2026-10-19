# MMH55 5.5 as the target — work plan

Facts behind this plan: `INVENTORY.md`. Sizes: S ≈ ≤1 day of one helper, M ≈ 2–4 days, L ≈ a week+, XL = several helpers.
Rule as before: every behaviour from the binaries/data (now: stock exe + PEST bytes + MMH55 data/scripts; §0), ledger per item.

## 0. Target (decided by the user, 2026-10-19)

**The experience to copy = the user's install `HoMM 5 - Tribes of the East (5.5)` launched with
`bin\H5_Game_PEST.exe`.** That exe is the stock GOG 3.1 `H5_Game.exe` with exactly 16 bytes changed (PEST, mapped in
`docs/rules/SIMULTANEOUS.md` §7) and it loads **no MMH55 DLL**. So the target is: stock 3.1 engine + MMH55 data paks
+ MMH55 Lua scripts + the PEST byte patches.

Consequences for this plan:
- **Out of scope / optional:** every `55u.dll` / `55.dll` patch group (`dll_patches_55u.txt`, `dll_patches_55_qai.txt`),
  the `MMH55_Utility.exe` byte patches (spell/artifact/class table counts, RMG multipliers, Dimension Door instead of
  Cartographer) and the Quantomas AI engine of `MMH55.exe`. They are not active for the user. W3, W4 (except PEST),
  W5's exe/DLL patches, W6's DLL hooks, W7, W8's DLL groups and W9 become optional modes, never the default.
- **In scope:** W1 (data on the stock engine, including what the stock engine does with MMH55's larger tables), W2 (Lua),
  PEST (done: `PestSimultaneousPolicy`), the UI data, RMG templates as data.
- Stock 3.1 mode stays (`H5_TARGET=stock` for tests, `H5_EXE=stock|pest` for the exe behaviour).

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
(Pest: done from the exe bytes, SIMULTANEOUS.md §7), Modifiers/Movement (pathfinding costs, diagonal 1.41,
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
