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
  (SCRIPTS.md, missing_natives.txt); the options only set the globals. Per-setting status: §4.
- EE_* lobby options, casual/aggressive/defensive AI modes and MMH55Stats.xml: DLL-only, not available.
- MMH55_Utility's own screen (inside `55u.dll`) is not reproduced; the H5Reborn options screen edits the same globals.

## 4. Does each setting's script path run in our engine? (2026-11-09)

Where MMH55 runs at all (SCRIPTS.md §1, corrected 2026-11-09): on the stock exe **single-player games run
advmap-startup.lua — and so H55-Core / H55-AdvMap — on every map**, multiplayer, custom and RMG maps included (the
adventure controller's script host, FUN_006089a0 → FUN_00603b20, has no MapScript check). Only **networked and hotseat**
games need a MapScript resource (the world host FUN_00992d00, `JZ` 0x00992d2a, the jump MMH55's DLL removes): stock
multiplayer maps and stock-RMG maps have none, so there MMH55 does not run without the DLL; MMH55's ARMG demo maps ship
an empty one, so it runs on them. (The earlier text here — "the stock engine runs no script on a map without a map
script" — read only the world path and was wrong for single player.) On the campaign / single-mission maps the map type
(H55_GetMapType: path contains "Scenario" → Campaign, "SingleMissions" → Scenario) makes H55-Core switch off Town
Conversion, Governance and Mine Control and force PrisonersAdjustToHeroRace = 1; on multiplayer / RMG maps played single
player the multiplayer / RMG-only features run. `AdventureScripting.RunWithoutMapScript` / `--no-map-script-check` is the
DLL behaviour for networked / hotseat games (harness only).

Evidence: **T** = a test runs the feature's script in our engine and checks its effect (tests/H5.Scripting.Tests/
Mmh55FeatureTests.cs, 5.5 install); **S** = settings sweep: the setting at a non-default value, 7 days of bot play on
A1SM1, A2S1 (scripted single missions), Maps/Multiplayer/A2M1 and the RMG map user:L_I4_RMG_Map_001 (single player,
which runs MMH55 there on the stock exe; re-run 2026-11-09 without --no-map-script-check, 32 settings x 2 maps),
`script-run … --h55-set=H55_X=v`: no script error of ours (only the stock ones of SCRIPTS.md §5; on A2M1 every run also
shows H55_MakeStartArmyNeutral's RemoveHeroCreatures(…, 0) "Quantity cannot be negative", the exe's own count check).
The sweep proves the path runs without errors where the bots trigger it, not that every branch was reached.

| Setting | Runs in our engine | Notes |
|---|---|---|
| TownGateEnabled | yes — T, S | needs the hero custom ability: ControlHeroCustomAbility / SPELL_ABILITY_CUSTOM4 cast → CUSTOM_ABILITY_TRIGGER → H55_TownManagement (was dead before 2026-11-09: the ability was stored only). Teleport H55_TeleportNow tested (entrance tile, move points, mana) |
| TownConvEnabled | yes on MP/RMG — T, S; off on scripted maps (MMH55's own rule) | H55_ConvertNow tested: TransformTown recreates the town (new 2026-11-09), resources paid, buildings rebuilt with UpgradeTownBuilding |
| AmountExtraTownsDwellings | yes — S | script arithmetic (H55_UpdateTownQtyDifficulty) feeding the conversion limits |
| GovernanceEnabled / GovernorExpCoef | S; off on scripted maps | reached through the same Town Management ability (T for the ability); the appointment (H55_StartGovernor) and the governor bonuses (H55_WeeklyEvents / H55_DailyEvents) were not exercised: the bots appoint no governor |
| MineControl | S; off on scripted maps | H55_MineVisit via SetObjectEnabled + OBJECT_TOUCH_TRIGGER |
| DimensionDoors | S (RMG) | sets H55_DimensionDoorsOnThisMap; magi huts become scripted |
| IgnoreExpAdjustment | S | H55_SetEasyExperience (script tables) |
| NoMentoring | S | H55_MemoryMentorRefuse on visit |
| NoHillFortsOnARMG | S (RMG) | H55_HillFortVisit |
| BuildNeutralTowns / BuildPlayerTowns | yes — S | UpgradeTownBuilding now as the exe (one level past the map limit, limit unchanged: T) |
| CompetitiveARMGTowns | yes — S (RMG: "Towns prepared for Competitive ARMG Map!") | |
| NoAutoTeamHumans | path runs (S) but effectively off | H55_PrepareAdvMap only teams players when H55_GameMode ≠ 0; H55_UpdateGameMode stops with the stock error "Player %d doesn't taking part" on maps with fewer than 8 players before setting the mode, so the mode stays 0 ("casual") there — same in the exe |
| AICheatMode | path runs (S) but effectively off on < 8-player maps | same H55_GameMode condition (H55_WeeklyEvents line 5614); also needs the AI start gold in H55_AIStartResources |
| LowResGame | yes — T, S | 10/10/5/5/5/5/10000 taken from every human |
| GenerateMixedStacks / MixStacksThreshold / AskForMixedStacks | path runs (S); the start-up mix is skipped | the decision in H55-Core's chunk runs after one sleep(1), before the H55_UpdateMapType thread (later in the scheduler list, FUN_004c0720 / FUN_00d0c8a0 append order), so H55_MapType is still "" and the multiplayer-only branch is not taken (H55_MixDecisionDelay = 1; T). Follows the traced scheduler; not observed on the exe |
| ForceMixedStacks | yes — T, S | the delayed branch mixes (H55_MixStacks) |
| BanksDifficulty | S | bank guard multipliers (H55_GetBankDifMultiplier) used when a bank is visited |
| NeutralStackSize | yes — T, S | H55_ModifyStackSize: counts × k |
| PrisonersAdjustToHeroRace | S | prisoner / temple rewards |
| ManaObservatories | S | observatories become scripted (SetObjectEnabled, OBJECT_TOUCH_TRIGGER); the overridden tooltip is not shown (UI) |
| NoArtifactMerchantsInTowns | yes — T, S | DestroyTownBuildingToLevel(…, 0, 0) on Academy / Dungeon merchants |
| ForcedObelisk / ForcedShantiri | S | reward choice when the scripted obelisk / Shantiri is won |
| NegativeArtifacts | S | H55_DocumentAward |
| MoonDiscQuest / MoonDiscOnARMGMaps | S | wagons / skeletons scripted in H55_PrepareAdvMap |
| RPGPotions | S | potions in H55_IndexArtifacts / trading posts |
| Duel_* | not run | only on ARMG duel templates (H55_DetectDuel → H55_ARMG_Duel = 1); no such map on this install |

Start-hero roll (fixed 2026-11-09): the random start hero could be the cut-scene actor `MapObjects/DialogScenes/Godrik_ds`
(InternalName Godric_ds), unknown to MMH55's phonebooks, after which H55_GetUnusualSchools crashed every day. The exe's
hero lists all iterate RPGRoot HEROES_ANY (MapObjects/_(AdvMapSharedGroup)/Heroes/Any.xdb, the "List of all heroes" of
00837590; custom-game candidates 00c9f330 / 00c9f4a0, which also skip ScenarioHero at shared +0x1fc; 00ce83c8), and
Godric_ds is neither in it nor a non-scenario hero. Our start roll now takes only HEROES_ANY non-ScenarioHero heroes
(AdventureDb.InHeroesAny); GameDatabase.Heroes still holds every AdvMapHeroShared for map lookups. GAP: the world-side
draw of the start hero itself (vs. the custom-game list) is not traced.
