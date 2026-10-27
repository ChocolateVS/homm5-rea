# MMH 5.5 player options: inventory and what H5Reborn exposes (2026-10-28)

User request: choose 3.1 or 5.5 at startup (INVENTORY.md §8), simultaneous turns in both (SIMULTANEOUS.md §7.4), and the
5.5 extras optionally enabled. This lists every way MMH55 lets the player switch its features, where each lives, and
whether H5Reborn offers it. Nothing here is invented: names, defaults and texts come from the 5.5 install's data.

## 1. Where MMH55 keeps its switches

| Source | Who reads it | Edited by | H5Reborn |
|---|---|---|---|
| `scripts/H55-Settings.lua` — default copy in MMH55-Index.pak, the player's copy in MMH55-Settings.pak (newer timestamp wins, INVENTORY.md §4) | H55-Core / H55-AdvMap (`dofile("/scripts/H55-Settings.lua")` in H55-Core) | by hand, or `bin/MMH55_Utility.exe` (= the game exe with `55u.dll`, which writes MMH55-Settings.pak) | **available**: Options → "MMH 5.5" (§2) |
| Lobby options `EE_artefacts` (Hidden Artifacts), `EE_eightskills` (8 Secondary Skills), `EE_taxes` (Taxes), `EE_decisionhall` (Hall of Decision / Length of Month), `EE_discount` (AI rush discount), `EE_lookahead` (Lookahead Depth), `EE_mercenaries` (Neutral Champions) — UI in MMH55-Data.pak `UI/CustomGameCreate/EE_*`, `UI/HotSeat/EE_*`, texts MMH55-Texts-EN.pak `UI/CustomGameCreate/*EE_*`, `UI/MPWait/MapDescription/TEXTS/EE_*` | `55.dll` (no script reads `EE_`) | custom-game / hot-seat lobby | **not available** (DLL-only) |
| Lobby player-list modes: Casual Mode (`EE_tooltip_equal_resources`), Aggressive / Defensive AI (`EE_tooltip_ai_aggressive` / `_defensive`) | `55.dll` | lobby | **not available** (DLL-only) |
| `MMH55Stats.xml` (MMH55-Stats.pak; `55.dll`/`55u.dll` also probe MMH55-Stats1..9.pak): 55 feature blocks — AbilitiesAtbReset, AvatarOfDeathNoMana, Avenger, BattlefieldSize, CastersLuckDamage_base, CombatCameraLimits, CreatureArtifacts, DestructionSkill, DisableMagicOpposites, DjinDarkLightAbility, DwarfFormationsForAll, EarthquakeTremors, EmpoweredArmageddonFix, EnlightenmentForBarbsFix, EmpoweredSpellsDamage_base, ExtendedElementalArtifacts, ChainShotImmunityForCrystalScales, ColdDeathImmunityForCrystalScalesAndMechanical, ConsumeCorpseSqrtHp, CreatureFramework, CreatureStatsModifiers, DisableMagicResistForNonDamagingSpells, FlamingArrowsDamageAndDefense, ImbueBalistaATB, Income, InstantTravelMovementRequirements, MassSpells, MultiplayerCombatScripts, Pest, ProtectorSpecialization, SpellCost, SpiritLink, HavenTraining, HellfireTriggerChance, CreatureAbilitiesChance, CreatureAbilities, Modifiers, OneStackSplit, PawStrikeDivisor, RuneOfBerserkingChange, PyromancyBonus, QaiFixes, PowerfeedCapped, RangedRetaliationStrike, RuneOfTheDragonForm, SacrificialPitHealth, ShowPlayerTurnTimerInCombat, SnareFix, SpellShrineExperience, SwapBarbarianSpellbook, TowerDamage, WandArtifact, WarMachines, Weeks, System | `55.dll` / `55u.dll` | by hand | **not available** (DLL-only; out of scope, PLAN.md). Its `Pest` block is the DLL's own variant; the user's setup uses the exe patch (SIMULTANEOUS.md §7) |
| Map-script-only globals `H55_DimensionDoorsOnThisMap`, `H55_MoonDiscQuest`, `H55_ForceMixedStacks` (the file says to copy them into a map script) | scripts | map authors | the last two are also global switches and are listed; `H55_DimensionDoorsOnThisMap` is not ("DO NOT activate this here") |
| In-game question `H55_AskForMixedStacks` (first human asked on multiplayer maps) | H55-Core | — | the switch is listed; the question itself is a script message box |

## 2. H55-Settings.lua in H5Reborn

Options screen (MMH 5.5 target only): categories "MMH 5.5 (1/2)" and "(2/2)" after the stock ones (the disabled
Replay row of the menu scroll is hidden to make room), built from the mounted
`scripts/H55-Settings.lua` at run time (so the player's MMH55-Settings.pak values are the defaults, as in MMH55): every
global in file order, label = name without `H55_`, tooltip = the line's comment plus the following comment block
(guidelines, warnings). Yes/no settings are check boxes; the others are combo boxes. Values: the file's own lists where
its comments give them (BuildNeutralTowns / BuildPlayerTowns 0–5; AICheatMode 0–6 as Off/40%/…/140%; ForcedObelisk /
ForcedShantiri 0–9 as random/sword/…/tear of asha); otherwise H5Reborn steps (UI choice, not MMH55 data): multipliers
GovernorExpCoef / BanksDifficulty / NeutralStackSize 0.5–2.0 by 0.1, 2.5, 3.0; AmountExtraTownsDwellings 0–10;
MixStacksThreshold 50–1000 by 50; Duel_HeroLevel 1–50 ("Max level = 50"); Duel_Relics/Majors/Minors 0–20;
Duel_WeeksGrowth / Duel_BankDifficulty 1–20; the file's current value is always in the list.
Stored per profile as `h55.<name>`; only values that differ from the file are sent to the scripts:
`AdventureScripting.SettingOverrides` appends `name = value;` lines to the file's text when a script loads it
(`H5.Scripting.Mmh55Settings`, tests `Mmh55SettingsTests`). Single player / campaign: `ScriptUiBridge`; network and
hot-seat: the host's values in `AdvNetSettings.ScriptSettings` (scripts run on the host). New games only (a save keeps the
Lua state it was saved with). Test switch: `--h55-set=H55_Name=value` (repeatable) overrides without the profile
(checked off-screen: `--adventure=Maps/SingleMissions/A1SM1 --h55-set=H55_TownGateEnabled=0` → the map's scripts see 0).
The scripts' own rules still hold (e.g. Town Conversion, Governance and Mine Control are
off on campaign and scenario maps).

| Global | 5.5 install value | Control | Read by |
|---|---|---|---|
| H55_TownGateEnabled | 1 | check | H55-Core |
| H55_TownConvEnabled | 1 (Index default 0) | check | H55-Core, H55-AdvMap |
| H55_AmountExtraTownsDwellings | 1 | combo | H55-Core |
| H55_GovernanceEnabled | 1 (Index default 0) | check | H55-Core |
| H55_GovernorExpCoef | 1 | combo | H55-Core |
| H55_MineControl | 1 (Index default 0) | check | H55-Core, H55-AdvMap |
| H55_DimensionDoors | 1 | check | H55-AdvMap |
| H55_DimensionDoorsOnThisMap | 0 | not listed (map scripts) | H55-AdvMap |
| H55_IgnoreExpAdjustment | 0 | check | H55-Core |
| H55_NoMentoring | 0 | check | H55-AdvMap |
| H55_NoHillFortsOnARMG | 0 | check | H55-AdvMap |
| H55_BuildNeutralTowns / H55_BuildPlayerTowns | 0 / 0 | combo 0–5 | H55-Core |
| H55_CompetitiveARMGTowns | 1 | check | H55-Core |
| H55_NoAutoTeamHumans | 0 | check | H55-Core |
| H55_AICheatMode | 4 | combo 0–6 | H55-Core |
| H55_LowResGame | 0 | check | H55-Core |
| H55_GenerateMixedStacks / H55_MixStacksThreshold / H55_AskForMixedStacks / H55_ForceMixedStacks | 1 / 200 / 0 / 0 | check / combo / check / check | H55-Core |
| H55_BanksDifficulty / H55_NeutralStackSize | 1 / 1 | combo | H55-Core, H55-AdvMap |
| H55_PrisonersAdjustToHeroRace | 0 | check | H55-AdvMap |
| H55_ManaObservatories | 1 | check | H55-AdvMap |
| H55_NoArtifactMerchantsInTowns | 0 | check | H55-Core |
| H55_ForcedObelisk / H55_ForcedShantiri | 0 / 0 | combo 0–9 | H55-AdvMap |
| H55_NegativeArtifacts | 1 | check | H55-Core |
| H55_MoonDiscQuest / H55_MoonDiscOnARMGMaps | 0 / 1 | check | H55-Core |
| H55_RPGPotions | 0 | check | H55-AdvMap |
| H55_Duel_WeeksGrowth / HeroLevel / Relics / Majors / Minors / BankDifficulty | 8 / 25 / 4 / 8 / 4 / 6 | combo | H55-Core |

## 3. Gaps

- How far each feature works depends on the script engine's coverage of what H55-Core / H55-AdvMap call
  (SCRIPTS.md, missing_natives.txt); the options only set the globals.
- EE_* lobby options, casual/aggressive/defensive AI modes and MMH55Stats.xml: DLL-only, not available.
- MMH55_Utility's own screen (inside `55u.dll`) is not reproduced; the H5Reborn options screen edits the same globals.
