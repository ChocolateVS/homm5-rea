# Campaign, save/load and single-scenario setup audit

Ground truth: H5_Game.exe 3.1 (headless Ghidra, private project `tools/ghidra-projects/camp`, scripts
`tools/camp/scripts`, helper `tools/camp/ghidra.sh`), stock data (`data.pak`, `a2p1-data.pak`, `texts.pak`)
and the MMH 5.5 campaign mod (`UserMODs/MMH55-Cam-*.h5u`: `Campaigns/CampaignsSets/Standart.xdb` lists 13
campaigns C1–C6, A1C1–A1C3, A2C0–A2C3, plus its own SelectCampaign MenuPanel).
Status: **MATCH** (already identical), **FIXED** (changed to match), **GAP** (not done / not traced).
Screenshots (off-screen, 1920×1080, MMH55 install): `tools/camp/out/campmenu3.png` (tabs, no list yet),
`campmenu4.png` (Tribes of the East tab), `pwl.png` (A2C1M1 bonus choice), `custom.png` (CustomGameCreate data).

## 1. Campaign flow

### Campaign list and unlocking (`CSelectCampaignSubmenu`, `CCampaignsSet` vtable 00e1a468)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Campaign set | `Campaigns/CampaignsSets` + `#xpointer(/CampaignsSet)` (009d2b50); user campaigns `UserCampaigns` (009d2ad0) | `CampaignLoader.LoadStandardSet` (sets ordered by `Order`, items in order) | MATCH |
| Panel used | 0069a600 → 00699dc0: standard set (mode 0) uses the single `SelectCampaign/MenuPanel`; `MenuPanel%02d` (count+1) only for the user-campaign list (mode 1) | was `MenuPanel<count+1>` (user-campaign variant with Load Campaign) | FIXED |
| Blocks | 0069a290 collects the panel's `CampaignBlock%02d` children from 01 until one is missing (stock 6, MMH55 13); block i = campaign i while i < set count, button `SelectCampaignBtn` text = name, `CampaignDescView` = NameComment | same, no fixed limit of 9 | FIXED |
| MMH55 tabs | MMH55 MenuPanel groups blocks under `Campaign_H5/H5A1/H5A2` lists with tab buttons (ButtonGroupID 1); visual state 0/1 `VisualOnEnterState` sends `UI_SHOW_WINDOW <list> 0/1` (00a51170 registers the message) | `VisualOnEnterState` parsed, run on every state change; `ShowWindow` command hides/shows the window | FIXED |
| MMH55 tabs at open | which visual-state commands the UI engine runs when the window is created was not traced; we apply state 0's (all lists hidden until a tab is clicked) | `CampaignMenu.ApplyVisualStates` | GAP (inferred) |
| Unlock rule | `CCampaignsSet` vf 0x10 = 0050a350: every `DependentCampaigns` entry must have a progress entry that is completed; cheat var `ignore_scenario_line` opens all | dependency missing from the set used to count as met; now locked. Cheat var not exposed | FIXED |
| Campaign completed | `CCampaign` vf 0x08 = 0045ea50: progress (+0x18) == mission count | `IsCampaignCompleted` (all missions completed) | MATCH |
| "New campaign" / "Campaign complete" messages | `T_NEW_CAMPAIGN_HEADER/TEXT`, `T_CAMPAIGN_COMPLETE_HEADER/TEXT` around 0069a9ab–0069aaa6 (`SELECT_CAMPAIGN`) | none | GAP |
| Load Campaign / user campaigns | `select_load_user_campaign` → 00699cc0 → load screen `ID_LOADCAMPAIGNSCREEN` (`CLoadScreen<SCampaignHeaderInfo>`), `UserCampaigns/*.h5c` mounted (00c8bd00) | not handled | GAP |
| Progress storage | per profile `Campaign.sav` / `CustomCampaign.sav` (00dbaad5) | `user://profiles/<name>/campaigns.json` (stock format not reproduced) | MATCH (function) |

### Mission select (`CSelectMissionSubmenu`)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Blocks | 0069bbb0: `SelectMission/MenuPanel` `MissionBlock%02d` (`SelectMissionBtn`, `MissionDescView`), `CampaignNameView` | same | MATCH |
| Available missions | `CCampaign` vf 0x1c = 0045ec40: index ≤ progress (progress = missions completed in order); vf 0x18 = 0045ea90 completed: index < progress | completed ones + the first uncompleted | MATCH |
| Progress update on a win | vf 0x24 = 0045fab0: winning mission == progress → progress+1; replaying an earlier one keeps it | `MarkCompleted` set of indices, same availability result | MATCH |
| `mission_selected` / back | 0069b800: `mission_selected` → 0069b770 (stores index, opens PWL), `select_mission_back` | same | MATCH |

### Difficulty (SetupPanel, 00698600)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Combo | `SetupPanel/DifficultyCombo` filled with the `UIGameDifficultyInfo` names, count 4 (008478d0); MMH55 renames them Normal/Hard/Heroic/Impossible | was not filled | FIXED |
| Stored value | `difficulty_changed` → 006984b0 writes setvar `select_campaign_mission_difficulty` (00665f80); default profile value 1 | same setvar in the profile's options.cfg; campaign maps start with it | FIXED (was a separate campaigns.json field, no UI) |
| Shown value | 00698410: combo selection = var, or 0 when the var ≥ item count | same clamp | FIXED |
| ProfileNameView / ChangeProfileBtn | 00698600 sets the profile name; ChangeProfile enabled only where the caller passes 1 (mission panel passes 0) | name set; ChangeProfile enable state not reproduced | GAP |

### Pre-mission screen (`CPWLScreen`, 006b38d0 / 006b30c0)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Offered bonuses | 006b38d0 keeps only bonuses valid for 006b25e0: ARTIFACT/CREATURE/SPELL whose id exists, RESOURCE with a positive amount, BUILDING via 006b2270; E_BONUS_NONE never; packed into `Bonus_0..` | NONE bonuses were shown when a mission had only NONE (A2C0M0 showed 3 empty slots); slots kept data positions | FIXED |
| No valid bonus | 006b38d0 returns without the selection panel | `BonusSelection` hidden, Start enabled | MATCH |
| Preselection | none: 006b1eb0(0) disables Start with tooltip `NO_BONUS`; choosing a bonus enables it (`CAN_START`) | single bonus was auto-selected; NO_BONUS shown as progress text | FIXED |
| `select_bonus_and_start` | 006b2ad0: selects the bonus and starts | not handled | FIXED |
| BUILDING validity | 006b2270 (town/building check) not traced | always valid | GAP |
| Resource text | 006b2540 is called with the first positive amount only | one line per non-zero resource; no stock or MMH55 bonus has two resources | MATCH (for shipped data) |
| Bonus index for scripts | `GetPlayerSelectedCampaignBonusIndex` (005d2830) | original data index of the chosen bonus | MATCH (no shipped mission mixes NONE and valid bonuses) |
| Picture | `CDBID_TEXTURE_PWL` (0066c070), map `PWLPicture` (00a0b4b0) | not shown (grey background in `pwl.png`) | GAP |
| Press any key | `pwl_press_any_key_enabled/period/fade` (006b4090) | period 1.0 / fade 0.5 | MATCH |

### Carry-over between missions (`CCampaign` 0045f460 save, 0045f920 → 0045f080 apply)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Target mission index | HeroesPool `TargetMission` is a 0-based index: data (C1M5 → C2 mission 0 = C2M1; A2C1M1 Ornella → 2 = A2C1M3); 0045f460 keeps a target only if 0 ≤ target < missions of the target campaign | saved under the data value but looked up with mission index + 1 — carried heroes were restored one mission late or never | FIXED (`CampaignDef.CarryTarget`, test `HeroesPoolTargetsAreZeroBasedMissions`) |
| Empty TargetCampaign | the mission's own campaign | same | MATCH |
| Cross-campaign | 0045f920: records of other campaigns targeting this campaign/mission are merged (0045f3d0) | one profile-wide table keyed campaign:mission | MATCH |
| When saved | vf 0x24 0045fab0 (mission won) → 0045f460 for the local player | `OnMissionEnded(won)` | MATCH |
| Hero lookup | world hero registry by script name (vf 0x140 → 0x14), must be alive/valid | `HeroByNameAnywhere` | MATCH |
| Saved data | 0045edc0: attributes (hero vf 0x10–0x1c), skills with mastery (vf 0x17c / 0x174), vf 0x188 list, vf 0x1a4, 0x300, two lists (vf 0xd4/0xd8), 5 flags + list (vf 0x358); army object (vf 0x250) stored with the record | level, experience, attributes, skills, perks, spells, artifacts, backpack, army, war machines | GAP (vf 0x188/0x1a4/0x300/0xd4/0xd8/0x358 not identified; field list unverified) |
| `Count` extra heroes | 0045f460: extra = Count − named entries + empty entries; when > 0 the player's heroes are sorted (00460e80, comparator 0045fbc0) and the last `extra` are stored in the mission record (+0x18); 0045f080 hands record[mission−1]+0x18 to the next mission's start info | ignored | GAP (sort key and the start-info consumer not traced) |
| Per-mission record +0x3c/+0x40 | world vf 0xa8 / 0x120 at the win (results/scores, getters 0045eb80/0045ebe0) | not kept | GAP |
| Bonus application | E_BONUS_* given to the main hero / town / treasury (`Target/Hero` empty in all stock and MMH55 bonuses) | `CampaignFlow.ApplyBonus` | GAP (exe application routine not traced) |
| MMH55 scripts | MMH55 campaign map scripts call the same natives (scripts handle extra carry-over themselves) | scripts run by ScriptUiBridge | MATCH (per scripting workstream) |

### Objectives, results, scenes, end

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Results screen | `ID_MISSION_RESULTS_SCREEN` (007a0e50); statistics `T_*_DURING_MISSION` / `AT_END_OF_MISSION`, `T_MISSION_TIME` (0079fcf0) | header + mission name only | GAP (statistics) |
| Win/lose titles | `T_MISSION_END_TITLE_WIN/LOOSE`, `TX_MISSION_WIN/LOSE` (005a5d90) | `MissionEndWin/Loose.txt` | GAP (key → text file mapping not traced) |
| Restart mission | `restart_mission`, `T_RESTART_MISSION_TITLE/QUESTION` (007581b0) | Restart restarts with the same bonus | GAP (exe restart path not traced) |
| Next mission / campaign end | after a win the next mission's PWL, after the last the campaign menu | same | GAP (exe flow after results not traced) |
| Objectives screen, win/lose conditions, dialog scenes, cutscenes | handled by the adventure/scripting/cinematics workstreams | — | not in this ledger |

## 2. Save / load

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Quicksave / quickload | keys registered in 007f7190; quickload 007e7500 uses `defaultsavename` when no name is given | F5/F8, save named `defaultsavename.txt` | MATCH |
| Load dispatch | 007e7500: save type 0 single (007ed730), 4 campaign (007eab10), 1/2 multiplayer (008238c0 + 00838c10); missing file "Error: save doesn't exist" | `SaveKind` enum, one load path | MATCH (kinds) |
| Hot-seat save kind | separate `CSaveScreen/CLoadScreen<SHotSeatHeaderInfo>` | hot-seat games were written as Scenario; now `SaveKind.HotSeat` | FIXED (separate hot-seat load list: GAP) |
| File names | 007ea770: `save_%d.sav`, first unused number | `SaveSlots.NextFileName` | MATCH |
| Autosave (turn change) | 00607d50 (called by the world tick 006082f0 when the current player changes): autosave var `DAT_00fd9e82` and world settings +0x1c → save named by the text key `autosavename` | on every human turn start when `autosave` is on | GAP (settings +0x1c meaning not identified) |
| Autosave (new turn) | 006082f0: network games use the room setting (settings +0x38, `autosave` in 00712d40), others the `autosave` var | the option var | MATCH (single player) |
| Autosave name id | `autosavename` = "Autosave <value=id>"; the id is formed by the save manager (vf +0x1c), not traced | day number | GAP |
| Save before combat | `save_before_combat` (005aadc0) | not reproduced (SAVE.md) | GAP |
| Save complete notice | `ID_SAVE_IN_PROGRESS` / `ID_SAVE_COMPLETE` (007f85f0 / 007f84a0), `save_ok_time` (007ea1c0) | none | GAP |
| Save/load screens | `CSaveLoadScreen<SHeaderInfo>`: list, SaveInfo preview (screenshot, definition rows), `saveload_resave_header`, `save_wrong`, `not_in_save_*` rows | `SaveLoadScreen` (list, preview, overwrite/delete questions) | GAP (`not_in_save_*` rows not reproduced) |
| Script state ±infinity | MMH55 map scripts keep `1/0`, `-1/0`; the exe serialises the Lua state with the save | `LuaSnapshot.ToJson` threw on them (no MMH55 map could be saved); now named literals, NaN, and -0 kept | FIXED (test `InfiniteNaNAndNegativeZeroSurviveRoundTrip`) |
| New state in saves | moon calendar (`AdvState.Moon`, `MoonWeek`), Tower of Astrologer (`AstrologerPlayers`, `AstrologerChanged`), favoured enemies (`HeroState.FavoriteEnemies`), micro-artifacts WIP (`Stack.Artifact`, `MicroBag`) are public AdvState fields, serialised by `AdvState.Serialize` and covered by the state hash | — | MATCH |
| Simultaneous-turns state | lives in the network session (`H5.AdvNet.SimultaneousTurns`); the exe keeps the flag in the saved world settings (CWorld+0x90, 00999f60) | network games are not saved | GAP |
| Network game saving | the exe saves network games: `CSaveScreen<SLanSaveHeaderInfo>` / `<SUbicomSaveHeaderInfo>`, host reload via `CMultiplayerHostLoadLogic<…>`, `Async_save_created` (007c9410), adjourn (`IAdjournSaveNotify`) | refused with "network games cannot be saved yet" (it used to write a single-player save of the client's replica) | GAP |
| Generated (RMG) maps | — | `AdventureSave.SpecFor` always writes a file map spec; generated maps cannot be reopened | GAP |

## 3. Single-scenario setup (Custom Game, `UI/CustomGameCreate`)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Map list | Name / Goal / Players / Map Size columns (row template `UI/MPCreate/Maps/SingleMap`) | `CustomGameScreen` | MATCH (columns) |
| Filters | `MPFilter` (show multiplayer maps), `RMGFilter` (show RMG maps); `Maps/SingleMissions` folder (0068e5f0, 00705de0) | MP filter only; RMG filter hidden | GAP |
| Sorting | sort by name / goal / players / size | goal sorts by name | GAP |
| Options | `TimeLimitBar`, `DifficultyBar` (stock); MMH55 adds Lookahead Depth, Neutral Champions, AI rush discount, Hidden Artifacts, Taxes | hidden; game starts at difficulty 1 | GAP |
| Players / teams / factions / heroes / bonuses | `bonus_change_left/right` (007148e0, 00714550), bonus tooltips (00714f00), `bonus0..3` (00715cd0), `difficultyscroll`/`difficultyvalue` (0070862c, 0077a010) — the MPCreate player-setup code; whether the single-player flow uses it was not traced | first playable colour is human, map defaults otherwise | GAP |
| Load button | `Load` opens the scenario load screen | hidden | GAP |
