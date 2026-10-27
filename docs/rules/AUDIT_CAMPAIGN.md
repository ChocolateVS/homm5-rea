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
| Autosave (turn change) | 00607d50 (world tick 006082f0 when the current player changes): needs the `autosave` var (DAT_00fd9e82) and world settings +0x1c; +0x1c also lets the view switch to the new player when no local player is active, i.e. the hot-seat flag (inferred from that use) | per human turn: a single-human game saves once per turn (= the new-turn autosave), hot-seat at each human player | MATCH (+0x1c = hot-seat inferred, its writer not traced) |
| Autosave (new turn) | 006082f0: network games use the room setting (settings +0x38, `autosave` in 00712d40), others the `autosave` var | the option var | MATCH (single player) |
| Autosave name id | CSavesCreator vtable 00e462c4, vf 0x1c = 007f9e10 (text key, e.g. `autosavename` / `save_before_combat`): formats the template with ids 1..3, takes the first name without a save, else overwrites the oldest of the three (header time); network games send it through the host (+0x14 flag), others save locally (007f9090) | `SaveSlots.RotatingName` (test `RotatingNameTests`) | FIXED (was the day number) |
| Save before combat | 005a5d90 (battle start): when the two sides' players differ in vf 0x190 (human vs computer) and the `save_before_combat` var (DAT_00fd75ed, registered 005aadc0, default 0 in user_a2.cfg) is set → CSavesCreator vf 0x1c with `save_before_combat` ("Before Combat <id>") | `SaveManager.SaveBeforeCombat` from `AdventureScreen.StartBattle`, before the battle seed is drawn; the save holds the pending battle, which starts again on load; not in network games | FIXED |
| Save complete notice | 007ea770 (save job) ends with 007f84a0: `ID_SAVE_COMPLETE` (UI/ID_SAVE_COMPLETE.txt "Save completed") in a notice window (007fc080) for `save_ok_time` ms (var, 1000, 007ea1c0); `ID_SAVE_IN_PROGRESS` ("Saving…", 007f85f0) while a save is written | centred "Save completed" text over the adventure screen for `save_ok_time` ms after every save | FIXED; GAP: the notice window's template (007fc080) and the in-progress text (saves are synchronous here) |
| Save/load screens | `CSaveLoadScreen<SHeaderInfo>`: list, SaveInfo preview (screenshot, definition rows), `saveload_resave_header`, `save_wrong`, `not_in_save_*` rows | `SaveLoadScreen` (list, preview, overwrite/delete questions) | GAP (`not_in_save_*` rows not reproduced) |
| Script state ±infinity | MMH55 map scripts keep `1/0`, `-1/0`; the exe serialises the Lua state with the save | `LuaSnapshot.ToJson` threw on them (no MMH55 map could be saved); now named literals, NaN, and -0 kept | FIXED (test `InfiniteNaNAndNegativeZeroSurviveRoundTrip`) |
| New state in saves | moon calendar (`AdvState.Moon`, `MoonWeek`), Tower of Astrologer (`AstrologerPlayers`, `AstrologerChanged`), favoured enemies (`HeroState.FavoriteEnemies`), micro-artifacts WIP (`Stack.Artifact`, `MicroBag`) are public AdvState fields, serialised by `AdvState.Serialize` and covered by the state hash | — | MATCH |
| Simultaneous-turns state | the exe keeps the "still simultaneous" flag in the saved world settings (CWorld+0x90, 00999f60) | `SimultaneousTurns.Snapshot/Restore` in the network save: the flag plus the phase flags (ended / pending / requested check), since H5Reborn may save inside the phase | FIXED (test `NetSaveTests.ReloadResumes…(true)`) |
| Network game saving | any player orders a save (CSaveScreen<SLanSaveHeaderInfo>); `CAnnounceSaveGameOrderedCmd` (vtable 00e0ec6c, 00429300) shows every player `ID_SAVEGAMEORDERED_EVENTMESSAGE` = Text/Interface/FadedText/SaveGameOrdered "Save was ordered and will be done when every player will end the turn."; CSavesCreator vf 0x1c sends network saves through the host (007f9e10, settings +0 flag) | `{"$t":"save"}` command: the host announces the order to every side (`save` notices) and writes the save into its own profile (SaveKind.Multiplayer, part `netgame.json`: room settings, rules state, map scripts, human slots, simultaneous state, seed counters) once the day has passed with no battle/choice open and a human to move; clients show the order and the result | FIXED (tests `NetSaveTests`); GAP: the exact point in the exe's turn change, whether clients also keep a copy, the fade parameters of the message (4 s used), ghost-mode positions are not saved |
| Network game reload | `CMultiplayerHostLoadLogic<SLanSaveHeaderInfo>` (vtable 00e3ce10): 00754350 fills the wait screen from the saved slots — saved humans wait for a player (the host takes its own by name, FUN_00495410), computers and closed slots stay; 00754110 lets a player take a free saved human slot; 00754770 allows the start only when every saved human slot is taken | Load on UI/MPCreate (`loadbutton`, event `load`) opens the network save list (`CLoadScreen<SLanSaveHeaderInfo>` equivalent, multiplayer saves only); the room carries `load` + the saved human slots; the wait screen offers only those slots; the session resumes the saved state; each player keeps a chosen saved slot, else the slot saved under its name, else a free one; the start is refused unless every saved human slot is filled | FIXED (test `NetSaveTests`) |
| Save lists per header type | separate `CSaveLoadScreen<SHeaderInfo / SHotSeatHeaderInfo / SLanSaveHeaderInfo / SCampaignHeaderInfo …>` | network saves are listed only on the network load list and in network games; the other lists leave them out | FIXED (the hot-seat list is still shared with scenarios: GAP) |
| Generated (RMG) maps | the exe writes generated maps to `Maps/RMG/<name>.h5m`, so saves reopen them as files | saves of generated maps store MapSpec `rmg` with the generator options and regenerate the map on load (deterministic generator; test `GeneratedMapReopenTests`) | FIXED (function; storage differs) |

## 3. Single-scenario setup (Custom Game, `UI/CustomGameCreate` → `UI/CustomGameWait`)

Screenshots (off-screen, MMH55 install): `tools/custom/out/cg1.png` (map list), `cw1.png` (player setup), `cw3.png` (started game).
Model and rules: `src/H5.AdvNet/CustomGameSetup.cs` (tests `tests/H5.AdvNet.Tests/CustomGameSetupTests.cs`); screens
`game/src/Adventure/CustomGameScreen.cs`, `CustomGameWaitScreen.cs`.

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Screens | `CCustomGameCreateScreen` (vtable 00e3e5c4, init 00774f80, messages 007756d0) then `CCustomGameWaitScreen` (00777260) with `CCustomGameWaitController` (00778640) over the game-setup model (00ca0240, vtable 00e92744) | create screen, then the new wait screen | FIXED (no wait screen before; Create started the map) |
| Map list folders | 00705de0: mask bit 1 `Maps/Multiplayer`, 2 `Maps/SingleMissions`, 4 generated maps; 007745e0: mask 2, `custom_game_show_mp_maps` → 0x30003, `custom_game_show_rmg_maps` → \|4; vars default 0 (00775a50) | `CustomMapList.Mask`, profile vars via `OptionsStore`; user maps count as multiplayer maps | FIXED (all maps were listed, MP filter was a session toggle) |
| Listed maps and names | 00705de0 scans only `Maps/Multiplayer`, `Maps/SingleMissions` and FUN_007c2cc0's `Maps/RMG` (all mounted sources: archives, UserMODs, user .h5m maps by their internal path); the name/description/goal are the map descriptor's own `NameFileRef` / `DescriptionFileRef` / `CustomGoal` texts (UTF-16LE) | folder from the map.xdb path (`CustomMapList.FolderOfPath`, test); campaign maps (`Maps/Scenario`) are no longer listed; texts read from the descriptor's root element; user maps generated by the RMG (internal `Maps/RMG/…`) count as RMG maps. Screenshots `tools/custom/out/cg55single.png`, `cg55.png` (5.5), `cgstock.png` (stock) | FIXED (user report: unreadable names came from the first object-level NameFileRef in the file and from listed campaign maps); GAP: the exe's fallback when a map has no name text (folder name used) |
| Filter check boxes | `MPFilterBtn` / `RMGFilterBtn` state = var (007745e0); `mp_filter_changed` / `rmg_filter_changed` flip the var and rebuild (00774de0 / 00774eb0) | same | FIXED (RMG filter was hidden) |
| Generated maps in the list | written to `Maps/RMG/<name>.h5m` and listed with the RMG filter | no .h5m writer: the generator options are kept in `user://maps/rmg/*.json` (deterministic generator) and listed with the RMG filter | MATCH (function; storage differs) |
| Sorting | 00705250: clicking the current column flips that column's direction flag, another column selects it with its own flag; `selection*` bar on the sort column, `butt*` buttons show the direction; comparators: name string (007062d0/00706290), goal key string (00706110), players = fewer teams then fewer players (00704d90), size (00704d00) | `CustomMapList.SortState`, `ComparePlayers` | FIXED (goal sorted by name, no direction) |
| Initial sort | list widget init (007060b0) not traced | name ascending | GAP (inferred) |
| Goal column | 00706380: `custom` (map CustomGoal text) else the `UI/MPCreate/Maps/Goals` key `goal%d` / `goal_default` / `goal_scenario` (00705800); index = OBJECTIVE_KIND enum (string table order SCENARIO_INFO … BUILD_TOWN_LEVEL) | `CustomMapList.GoalKey` from CustomGoal or the first primary objective kind | FIXED (was "Defeat all" for every map); GAP: which objective the exe picks and when `goal_scenario` is used |
| Players column | 00707330: team sizes joined by `players_separator` ("v"), all one-player teams → `ffa_format` "<count> (FFA)" | `CustomMapList.PlayersText` | FIXED (was the active player count) |
| Time limit | `TimeLimitBar` range 1..47 step 1, initial 46 (00774f80); header shows minutes below 46, else `T_UNLIMITED` (007747a0); Create passes 0 for values above 45 (007746c0) | stock scroll bar, `CustomGameOptions.Minutes` | FIXED (bar hidden); GAP: the single-player turn timer itself is not enforced (wait screen +0x270 = minutes × 60000) |
| Difficulty | `DifficultyBar` 4 positions (008478d0) initial 1 (00a33770); name from the difficulty table (`UIDifficultyInfo` texts, MMH55 renames them) | same; passed to the game setup | FIXED (game always started at difficulty 1) |
| Create | 007746c0: needs a selected map; Create button enabled only then (007747a0); lock flag = map list type 2 or 4 | same | FIXED |
| Load button | `load` → load screen 007f9480(1, …), back to this screen (00774a60) | `customload`: the load screen, closing returns here | FIXED; GAP: the save-type filter of load screen type 1 not traced (all saves listed) |
| Generate Map | `rmg` → RMG screen (00796be0) | RMG dialog; the generated map is kept and listed (not started at once) | FIXED |
| MMH55 options | `EE_lookahead`, `EE_mercenaries`, `EE_discount`, `EE_decisionhall`, `EE_artefacts`, `EE_taxes` bars/boxes (MMH55-Data.pak) | shown, inert | GAP (driven by the MMH55 exe patches, out of scope) |
| Player rows | per model slot an instance of `ListItem` (UI/CustomGameWait/Item → UI/MPWait/PlayersList/ItemElements, 007769d0); arrows `open_close_*`, `team_color_*`, `race_change_*`, `hero_change_*`, `bonus_change_*` set a pending command (00713820…00713940) processed by 00778500 | row instances with the same arrows; icons from the row's related textures (States, TeamColors, Races, Heroes, Bonuses) | FIXED |
| Row states | 00778640: per slot the list {human, closed if map `CanBeDisabled`, computer}; row 0 human with the profile name (008351a0), others computer; 00777db0: row 0 stays human, others cycle without the human entry; 00778000 names: human = profile, closed = empty, computer = colour name | `CustomGameSetup` | FIXED |
| Locks | 007782f0: open/close only for rows ≠ 0 with `CanBeDisabled`; with the lock flag (single missions, generated maps) every row but the first is locked | same | FIXED |
| Colour change | 00c9e660 swaps the slot's player with the slot of 00c9d5b0's colour: steps (team, colour 1..8) to the first other computer slot that can take the mover (human → `CanBeHumanPlayer`, computer → `CanBeComputerPlayer` and not human) | same; race kept if allowed in the new slot | FIXED; GAP (record copy details of 00c9e660 beyond state/name/race/hero/bonus) |
| Colours | 00ca0240: slot colour = player number unless the map gives one (+0x84) | `CustomGameSetup.ColourOf` | FIXED |
| Faction | 00c9e9f0: allowed races from the player's towns (random town = all), else its heroes (random hero = all), else reserve heroes, else all; exactly one → fixed; 00c9df40 cycles random (-1) .. last with wrap | same | FIXED; GAP: a random town/hero's race restriction (+0x16c / +0x168 compare) treated as all races |
| Starting hero | 00c9e140: only for players whose hero is created in town (`HeroInTown`, +0x30) and a chosen race; pool `HEROES_ANY` without heroes placed on the map (00c9f4a0) | hero pool = heroes of the race not on the map, not scenario heroes (or the map's AvailableHeroes), by id; random entry first; the engine gives the chosen hero to the HeroInTown player (`PlayerSetup.StartHero`) | FIXED; GAP: `HEROES_ANY` order and the hero cycle of 00c9dcc0/00c9da60 |
| Starting bonus | 00c9d860: 0 random, 1 random artifact, 2 gold, 3 resource with wrap, artifact skipped without a hero (+0x5e); default from map `DefaultBonus` (ARTIFACT only with a hero); `CanChangeBonus` (+0x5d) | same; passed as `PlayerSetup.Bonus` | GAP: where the engine applies the bonus (tooltips: 500–1000 gold, 3–6 resource, minor artifact) is not traced — the bonus has no effect yet |
| Start | 00776360: does nothing unless the players that are not closed form ≥ 2 teams (model vf 0x4c, 00c9d940) | Start enabled and acting only then; closed players are left out of the game | FIXED; GAP: what the exe does with a closed slot's objects |
| Wait screen panel | `T_CUSTOM_GAME`, `UI/MPWait/MapDescription` with Preferences (players/size/goal/difficulty) | description and preferences tabs | MATCH |
| Multiplayer create screen | `0070862c` (UI/MPCreate) uses the same bars: `timelimitscroll` 1..47 at 46, `difficultyscroll` 4 positions at 1 | stock scroll bars replace the click-to-cycle values (13 invented steps) | FIXED |
