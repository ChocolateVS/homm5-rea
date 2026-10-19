# Adventure audit ledger

Every adventure mechanic / screen element checked against `H5_Game.exe` (Ghidra 12.1.4, addresses are image VAs) and
the original data. Status: **MATCH** (already as the exe), **FIXED** (changed to match), **GAP** (known difference or
not built), **UNVERIFIED** (implemented from data/editor semantics, exe path not traced).

Rows at 2026-10-20: 55 FIXED, 7 MATCH, 22 GAP, 30 UNVERIFIED (rows carrying two statuses, e.g. rules FIXED / visual GAP, count once for each).

## Seer Huts

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Quest data | `AdvMapSeerHut/Quest` reader 00adae90 / 00adb3f0; `types.xml` ObjectiveKind, AwardType | Quest = objective (Kind, Parameters, CaptionFileRef, DescriptionFileRef …, Award, TakeContribution, AllowMultipleActivations/Completions) | `MapLoader` → `SeerQuest` (objective, description, award fields, TakeContribution); parameters are enum indices resolved through `types.xml` (`AdventureDb.EnumName`) | FIXED |
| Visit opens the box | 005a5d90 case 0x1d (`CSeerHutDialogLogEntry`) → 006f0ee0 → 006f09b0 | quest state 1 → box mode 1; state 2 → mode 2 + (fulfilled ? 1 : 0); other states → no box | `VisitSeerHut`: `seerhut:offer` (not taken), `seerhut` (taken; mode 3 when `SeerQuestMet`), `seerhut:done` opens nothing | FIXED |
| Accept | 006ef2a0 `seer_hut_accept` → 006ef170 | callback only in mode 1 (takes the quest), always closes | `AcceptSeerQuestCmd` in mode 1, then close; per player (`ObjState.QuestTakenBy`) | FIXED |
| Buttons | 006f0000 tail: vtable +0x44 SetVisible; Accept and Cancel share 343,367 in `UI/SeerHut/*.(WindowMSButton).xdb` | CompleteQuestBtn and CancelBtn visible iff mode 3, AcceptBtn iff mode ≠ 3 | same | FIXED (was: Complete always shown, disabled; Cancel hidden) |
| Complete Quest | 006ef130 `seer_hut_complete_quest` | callback +4 (complete), close | `CompleteSeerQuestCmd` (requires taken + fulfilled) | MATCH (UI); rules UNVERIFIED below |
| Cancel / timeout | 006ef2a0 `seer_hut_cancel`, `timeout_forced_close` → 006ef0a0 | close only | close | MATCH |
| Texts | `UI/UIGameRoot` `ID_SEER_HUT` → `UI/SeerHut/Texts.(WindowRelatedTexts).xdb` | TITLE_TEXT, QUEST_FORMAT (`<value=caption>`, `<value=desc>`), *_FORMAT per item | looked up by the same keys (`UiGameRoot.RelatedText`) | FIXED (was: file names) |
| Quest item format | 006ef730 switch on objective kind | 6 ASSEMBLEARMY, 7 RESOURCES, 11 NEUTRALS, 14 ARTIFACT, 16 ADVANCEHERO, 17 KILLARMY, else UNKNOWN_FORMAT + `ID_RIDDLE_CHOICE_TEXTURE` | same, KILL_CREATURES shown as unknown (see gap) | FIXED |
| Quest item icon | 006ef730 (icon from the objective descriptor, decompile unclear) | per objective | artifact / creature / resource / AdvanceHeroesIcon; riddle texture otherwise | UNVERIFIED |
| Reward format | 006efb10 switch on AwardType | 1 EXP (`CHEST_EXP_TEXTURE`), 2 RESOURCES, 4 ARTIFACT, 6 ARMY (count), 10 SKILL; others UNKNOWN_FORMAT + riddle texture; `<value=number>` only when > 0 | same | FIXED (was: no exp icon, blank for others) |
| Reward resource shown | 006eefe0 | first positive of wood, ore, mercury, crystal, gems, gold — **sulfur is never shown** | `ShownRewardResource` | FIXED |
| Alternate quest window | 006f0ee0 param 6 → 006f0d20 / 006ef3f0 (message-box style with QUEST_FORMAT, title_tooltip) | used when the quest's vtable +0x1c returns 4 (meaning not identified) | not built | GAP |
| Objective checks | objective classes per kind (not traced) | — | OBTAIN_ARTIFACT (worn or backpack), ASSEMBLE_ARMY (count of the creature), COLLECT_RESOURCES (purse), ADVANCE_HEROES (named hero's level), DEFEAT_NEUTRALS (all monsters with that name removed) | UNVERIFIED |
| Award application | award reader 00ae3280; applier not traced | — | exp, resources, attribute, artifact, spell, creatures, spell points, morale, luck, skill | UNVERIFIED |
| TakeContribution | reader 00adae90 | — | removes artifact / creatures / resources when set (no shipped map sets it) | UNVERIFIED |
| AllowMultipleCompletions / CanUncomplete | reader 00adb3f0 | — | quest completes once | GAP |
| KILL_CREATURES quests | 006ef730 case 17 | KILLARMY_FORMAT | parameters untraced; no shipped map uses it | GAP |
| MANUAL quests (map scripts) | A2C2M4, A2S1 | driven by Lua objective calls | box shows UNKNOWN_FORMAT; never fulfilled by the engine | GAP |

## Sphinx

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Map data | AdvMapSphinx reader 00a02330 | Shared, Riddle (href to SphinxRiddle), RandomRiddle | `MapLoader`: `SphinxRandomRiddle`, own riddle parsed when not random (all shipped Sphinxes are random) | FIXED |
| Riddle data | NDb::SSphinxRiddle reader 00ad0430; group 00ad05c0; path string 00a94ce8 `/GameMechanics/SphinxRiddles/RandomGroup.xdb` | Question, answers (first is right), Experience, ArtifactID, Gold; 30 riddles in the group | `AdventureDb.RandomRiddles` / `SphinxRiddle.Parse` | FIXED |
| Riddle pool | CRiddlesPool 00ced5b0 (fill) / 00ced780 (draw) | one pool per game; index clamp(round(n·f), 0, n−1), f = rand[0,100]·0.01 (00e15a80 = 0.01), swap with last and drop; refilled when empty | `DrawRiddle` (round half to even like x87 FISTP) | FIXED |
| Answer order | 009cb070 (rand calls via 008e77e0, inclusive) | right answer at rand[0,2]; other slots draw rand[0, left−1] from the remaining answers | same, with our seeded RNG (stream differs from Nival's LCG) | FIXED |
| Used / no riddle | 009cb070, +0xd0 set by 009cb000 | used Sphinx, missing riddle or < 3 answers → messagesFileRef[3] (refuse.txt) | `sphinx:refuse` → message box with that text | FIXED |
| When the used flag is set | 009cb000 via the screen callback | — | set when the answer is sent (right or wrong); refusing leaves it unused (the drawn riddle is still consumed from the pool, as in the exe) | UNVERIFIED |
| Screen pages | 006ebfd0, 006e95c0 (Ok → next page), 006eb640, 006ebca0 | 1 PoseRiddle (box2p Accept/Refuse + Ok), 2 Riddle (3 answers), 3 ChooseReward; refuse closes; wrong answer → battle; page 4 → reward | `SphinxScreen` same flow; Ok enabled only with a selection (006eb6d0/006eb8c0/006ebab0); double click = select + Ok | FIXED |
| Pose page | 006e9880 | Face ID_SPHINX_ICON_TEXTURE, Title ID_SPHINX_HEADER_TEXT + T_HEADER_TOOLTIP, ID_POSE_RIDDLE_ACCEPT/REFUSE_TEXTURE and _TEXT, T_ACCEPT/T_REFUSE_TOOLTIP | same | FIXED |
| Riddle page | 006ea100 | ID_SPHINX_ICON_TEXTURE, ID_RIDDLE_QUESTION_TEXT (`<value=Question>`), Choice_n Text = answer, Icon = ID_RIDDLE_CHOICE_TEXTURE | same | FIXED |
| Reward page | 006ea940 | Choice_0 ID_CHOOSE_REWARD_GOLD_TEXT (Amount) + resource icon, Choice_1 artifact name + icon, Choice_2 ID_CHOOSE_REWARD_EXPERIENCE_TEXT + CHEST_EXP_TEXTURE; T_REWARD_TOOLTIP_* | same; gold icon is the HUD gold texture (exe resource-icon lookup 006710a0 not traced) | FIXED / icon UNVERIFIED |
| Reward | 006ebe20 | 0 → CAddPlayerMoneyCmd (gold), 1 → CGiveArtefactCmd, 2 → CGiveHeroExperienceCmd | `AnswerSphinxCmd` | FIXED |
| Wrong answer | 006ebd20 → CFightSphinxCmd (004331c0), CCombatHeroVsSphinxArenaAndOpponentsLoader | battle against the Sphinx's opponents | opponents not traced: no battle, the Sphinx is spent | GAP |
| Sphinx stores the riddle's artifact | 009cb070 `+0xd4 = riddle+0x64` | purpose not traced | not modelled | GAP |
| AI | "AI: Hero visited Sphinx and got " (00e168f4) | AI answers | AI visits pose a riddle nobody answers | GAP |

## Tear of Asha, digging, obelisks

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Where it is buried | map init 009960e0 | one of the map's Graal artifact objects (both floors) picked at random; all of them removed; no Graal → no Tear of Asha; warnings when obelisks and Graal don't match | same (`PlaceGrail`) | FIXED (was: random open land tile) |
| Random maps: option | UIGameRoot RMG option EP_GRAAL_ON_MAP (ECT_CHECKBOX, name TemplateInfo/Grail/Name.txt (shown as "Tears of Asha"), tooltip Graal/Tooltip.txt); InitialParams.Grail = generator +0xb5 (saved by 0090a980 as sRMGProps.InitialParams.Grail); the template's GraalOnMap (+0x74) is only read by its loaders and copy constructor | a check box on the RMG screen decides | `RmgOptions.Grail`, `RmgSettings.Grail`, a row in the RMG dialog | FIXED / the check box's default (0079e6f0 → option +0x2c) UNVERIFIED (ours: off) |
| Random maps: placement | 0x00907150 (Next(template zones) up front, else one discarded draw), 0x00921a50 (Graal: ≤ 100 attempts, random candidate and quarter turn, tile marked 4), 0x00921a10 → 0x009216f0 (obelisks: (zone tiles·k)/(W·H) + 2 per zone, k 10 / 18 / 26 by size index) | Graal artifact (RMG/Params Grail) in the drawn zone; "obelisks" = RMG/Params Obelisk, which the stock data points at Learning_Stone | `PlaceGrailAndObelisks` after the dwellings of every zone; the adventure map then buries the Tear of Asha there (`PlaceGrail`) | FIXED / candidate filters (0x0091bb10, 0x009216f0) and the radius-5 test 0x00914a60 UNVERIFIED |
| Random maps: grail objective | RMG/Params DefaultGrailObjective | — | not traced | GAP |
| Puzzle pieces | 00a7f8c0, obelisk visit 00a7f880 | each of N obelisks visited (capped at N) opens (36 − open) / (obelisks left) | `PuzzlePiecesFor` | FIXED (was 36·v/N; differs e.g. N = 8) |
| Pieces without a Tear of Asha | 009960e0 (puzzle object only created with a Graal) | none | none | FIXED |
| Dig checks | 0098ec30 | hole on the tile → GRAAL_HAS_HOLE (no cost, command succeeds); tile not clear → GRAAL_TRY_ON_CLEAR; movement < cost → GRAAL_NOT_ENOUGH_MOVEMENT (RPGRoot texts) | same messages | FIXED |
| Dig cost / "clear" test | hero slots +0x104/+0xf8; 00bcc2b0, 0098eb30 | not traced | whole day's movement; free land tile without objects/towns | UNVERIFIED |
| Hole | 009920a0 (FUN_00a80000 creates a hole object, sound 0x35) | every dig leaves a hole | `AdvState.Holes` (rules); the hole model is not drawn | FIXED / visual GAP |
| Found | 00a7fa50 | same floor, Chebyshev distance ≤ 0, or ≤ 2 with Tear of Asha Vision (skill 0x50 = HERO_SKILL_GRAIL_VISION) | same | FIXED (was reach 1) |
| Found message | 009920a0 | GRAAL_NAME header, GRAAL_CONGRATULATIONS text, music 0x36 | message box with those texts and the artifact icon | FIXED (music not checked) |
| Hint | 00a7f8f0 / 00a7f710 | within 5 tiles (|dx|, |dy| ≤ 5, same floor): direction by |dx|·26 vs |dy|·10 (8 GRAAL_HINT_* ids, north = +y); otherwise GRAAL_NOTHING_HERE | `GrailHint` | FIXED (was: no hints) |
| Dig confirmation | DIG_CONFIRMATION(_HEADER) 005a4860, adv_dig | Yes/No box | stock dig_graal texts | UNVERIFIED (text ids not cross-checked) |
| Puzzle map screen | UI/PuzzleMap, CPuzzleMap@NUI | 36 PuzzleDesc pieces over the terrain around the Tear of Asha | built earlier (c144024) | UNVERIFIED |

## Shipyard (town button and map shipyards)

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Town enter_shipyard | 0061bcf0 → 0061a1c0 → 00619af0 | only when TB_SHIPYARD (type 3) is built; otherwise nothing | same | FIXED (was an invented "not built yet" notice) |
| Shipyard box window | 006a7840, 00676290 | Box2p when gold, wood and a free water slipway; Box1p when resources are short; Box0p otherwise | `AdvDialogs.Shipyard` | FIXED (was a Yes/No box) |
| Box texts | UI/BuyShip/Texts.(WindowRelatedTexts) | ID_TITLE_TEXT, ID_HEADER_TEXT, ID_NORMAL/BLOCKED/DESERTED_DESCRIPTION, ID_ACCEPT_TEXT, ID_DECLINE/NORES/NOGOLD/NOWOOD_TEXT (both short / gold / wood) | same | FIXED |
| Box textures / tooltips | 006a7840; UIGameRoot | ID_SHIPYARD_FACE/ACCEPT/DECLINE_TEXTURE; T_HEADER, T_BUILD_BOAT + T_REFUSE when buildable, T_NOT_ENOUGH_RESOURCES otherwise | same | FIXED |
| Map shipyard visit | CBuildShipDialogLogEntry → 006a7840 | the box opens for every slipway state (description says deserted / blocked) | engine always offers the shipyard choice; the box decides | FIXED (was plain messages) |
| Initial selection / OK enabling | vtable +0xc(0) on both choices; 00675760(2, !canBuild) | both choices unpressed; second index of 00675760 not decoded | OK builds only with Build Boat picked | UNVERIFIED |
| Town building info | 0061a5d0 (building tooltip, type 3) | adds "shipyard_deserted" when the slipway is not water | not built | GAP |

## Marketplace (from the earlier market commit 2859866)

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Exchange rates | FUN_008e7b80, tables at 00dc64a0.. | three 9×7 tables (wood/ore, rare, gold) by marketplace count 1..9 | `RatesCommon/Rare/Gold` | MATCH |
| Exchange arithmetic | CExchangeResourcesCmd FUN_004f4c10 | non-gold: received = offer / rate, cost = received × rate; gold: received = offer × rate | same | MATCH |
| Marketplace count | CPlayer vtable +0xb0 = 00500cf0 returns field +0x610 (setter +0xac = 00500cd0) | who fills the field is not traced | one per town with a marketplace, two with TOWN_BONUS_MARKETPLACE (manual) | UNVERIFIED |
| Trading Post rate | — | manual: as with 3 marketplaces | same | UNVERIFIED |
| Artifact selling | FUN_00623160, RPGStats+0x1c8 | gold cost × Marketplace_ArtifactSellCostOfGoldCoef (0.5) | same | MATCH |

## Kingdom overview (MissionObjectives box, built in c144024)

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Screen messages | FUN_005bb180 (tab_objectives, tab_log, tab_kingdom, kingdom_overview_towns / _heroes, objectives_close, timeout_forced_close, show_hero_artifacts 005b4c10, show_hero_abilities 005b4c80, spell_book 005b3d00, show_town_info 005b3df0, show_town_plan 005b2710); input_adv_show_kingdom_overview / _objectives / _objectives_log | tabs, town/hero lists and the per-view buttons | tabs, lists, input keys (AdvHud) bound | MATCH (tabs, lists) |
| Templates | FUN_005baa00 (townview1 / heroview1, scroll_of_views) | two views per page | same templates | UNVERIFIED (paging/scroll not compared) |
| Resources and income | FUN_005b2e40: %s_count = player +0xd8 (purse), %s_income = player +0xe8 (stored income, CPlayer +0x58) with the breakdown +0xf0 (+0x230) for tooltips | purse and the player's stored daily income | purse and `ExpectedIncome` (computed) | UNVERIFIED (how the exe fills +0x58 not traced; breakdown tooltips not built) |
| Moon week and date | FUN_005b2e40: moon_week_name = the week's name with its description as tooltip (world +0xb0 → 00ad5ac0); month_week_day | as listed | name and date shown | UNVERIFIED (tooltip) |
| Town view | FUN_005b64c0: TownName, TownLevel, TownSlotHeader, gold_income / silo_income, garrison / visiting slots (TownSlotGarrison/Visiting/Hero, GuardingHero, VisitingHero, Garrison), AvailableCreatures (Creature%d, CreatureNumber, BlockCreatures), build state BuildDone / BuildNoResources / NothingBuild, KO_TownInfo, KO_TownPlan | as listed | all these elements are filled | UNVERIFIED (element logic not compared line by line) |
| Town view buttons | show_town_info 005b3df0 (needs a dwelling, FUN_0061a390, then the hire-creatures dialog 006db170); show_town_plan 005b2710 (town plan FUN_00659480) | hire creatures / town plan | KO_TownInfo opens the town on the fort (recruit) screen, KO_TownPlan on the hall | MATCH (destinations) / opening the whole town screen instead of the bare dialogs GAP |
| Hero view | FUN_005b8510: HeroName, HeroClass, HeroLevel, HeroOwnerPrefix, primary attributes, Attribute6 = luck (ICON_LUCK, Attribute_Luck), Attribute7 = mana (ICON_MANA, Attribute_Mana), Attribute8 experience, num_of_creatures, bio/spec tooltips (biosuffix, specsuffix) | as listed | name, face, class, level, attributes, luck, mana, experience, army, artifacts | UNVERIFIED (bio/spec tooltips missing) |
| Hero view buttons | show_hero_artifacts 005b4c10, show_hero_abilities 005b4c80, spell_book 005b3d00 | open the hero's artifacts / abilities / spell book | not bound (Cast hidden) | GAP |

## Faction special buildings (town "enter_special")

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| enter_special dispatch | 0061bcf0 binds enter_special → FUN_0061b570: switch on town type 3..10 (Haven 006184f0, Preserve 00618ff0, Academy 00618c10, Dungeon 006187c0, Necropolis 00619e80, Inferno 00618870, Fortress 00618d10, Stronghold 00618dc0) | each type opens its screen; nothing happens when the screen's conditions fail | Haven routed; other types still the placeholder | FIXED (Haven) / GAP (rest) |
| Building double-click | FUN_0061b760: building → town ability (CAdvMapTown +0x48, table 00f7c998: ability, town type, building, level) → handler: 0 training, 9 Inferno pit, 10/11 Avenger's Guild, 0x12 Necropolis shrine, 0x16 Arcane Forge, 0x17/0x1d merchant/trade guild (00618a90), 0x1c Ritual Pit, 0x27–0x29 Stronghold halls (guild handler), 0x2b Traveller's Shelter, 0x2d Slave Market; other abilities → the building's info box | Haven Training Grounds → training, info box when it cannot open | FIXED (Haven) / GAP (rest) |
| Town abilities | 00cce1c0 / 00cce170, table 00f7c998 | ability n = (town type, building, level), e.g. 0 Haven S1, 1 Haven S2, 9 Inferno S5, 0x12 Necropolis S2, 0x1c Dungeon S3, 0x20 Fortress S1, 0x2d Stronghold S5 | `Adventure.TrainingGrounds` / `HallOfHeroes` | FIXED (Haven) |
| Haven training: lines | quote FUN_00cd36c0 mode 0 (transformer = CAdvMapTown +0x120, vtable 0xe957e0; CTrainCreatureCmd 0xe0e3fc → 00427fb0 → slot +0x10 = FUN_00cd07f0) | groups {Peasant, Conscript, Brute}, {Archer, Marksman, Longbowman}, {Footman, Swordsman, Vindicator}, {Priest, Cleric, Zealot}, {Cavalier, Paladin, Champion}; a stack trains into the base creature of the next group (griffins are not in the table); needs that creature's dwelling (TB 8, 9, 0xb, 0xc) | `TrainLines`, `TrainDwellings` | FIXED |
| Haven training: checks | FUN_00cd36c0 status | 1 no stack, 2 no hero, 7 hero not HERO_CLASS_KNIGHT (+0x258), 3 no Training Grounds (ability 0), 4 not trainable, 6 top group, 5 dwelling missing, 9 training points < stack count (whole stack only), 8 purse ratio < 1 (FUN_009d0500) | `TrainingQuote` (same order) | FIXED |
| Haven training: cost | 00cd3a08..00cd3ac4; Training struct at RPGStats+0x564 | D = (Training mastery − 1)·SkillTraining_CostDiscountPerLevel + ExpertTrainer_CostDiscount (perk 0x39) + Suzerain (spec 1): Base + ((level − 1) / LevelDivisor)·PerDividedLevel; factor = max((100 − D)·BaseCostMultiplier·0.01, 0) (FUN_00465640); cost = target creature cost × (count·factor), each resource ROUND (FUN_009d0d10) | `TrainingDiscount`, `TrainingQuote` | FIXED |
| Haven training: apply | FUN_00cd07f0 mode 0 | pay, points −= count (floor 0), the slot becomes the target creature with the same count | `DoTrainCreature` | FIXED |
| Haven training: points | FUN_00cd1850; weekly 00cd2b90 (interface +0x108 slot 0) writes +0x2d0; build 00cd4a40 | limit TrainingPointsPerWeek_Upgraded with the Hall of Heroes (ability 1), TrainingPointsPerWeek with the Training Grounds, else 0; reset to the limit weekly; a new building adds the rise of the limit | `TrainingPointsMax`, `ResetTrainingPoints`, `TrainingPointsAfterBuild`; also set at game start | FIXED (start value UNVERIFIED) |
| Haven training: opening | 006184f0, transformer slot 0 FUN_00cceb70 | opens only when the garrison or visiting hero can use mode 0 (Training Grounds + Knight) | `HavenTrainingScreen.CanOpen` | FIXED |
| Haven training: screen | UI/HavenTraining; FUN_00657820 (status → button), 00657e50 (click), 00657580 | 0 → state 0 T_BUTTON_GREEN; 1/4 disabled T_BUTTON_DISABLED; 5/7 state 1 T_BUTTON_RED; 6 state 2 T_BUTTON_YELLOW; 8 state 1 T_BUTTON_RED_NO_MONEY; 9 state 1 T_BUTTON_RED_LIMIT_REACHED; others disabled; tooltip with training_cost; click on green → confirmation → command + FORTHILL_UPGRADE_CREATURES_SOUND, red → FORTHILL_NO_RESOURCES_SOUND; smallcaption `remain` | `HavenTrainingScreen` (rows: garrison hero or town garrison / visiting hero) | FIXED / confirmation box and row contents UNVERIFIED (exe confirmation is a choose-upgrade window 0065fb60 with training_cost) |
| Haven training (MMH 5.5) | docs/mmh55/INVENTORY.md "HavenTraining" DLL patch | 5.5 patches the training code | 3.1 rules | GAP (5.5) |
| Sylvan Avenger's Guild: favoured-enemy list | CSetFavoriteEnemiesCmd (0xe0dfc4, built by 00648ba0, execute 00427a30 → hero +0x25c IAvenger (CAvenger 0xe63054, created with the hero only when it has skill 0x10, FUN_00b4cf30) slot +8 = 009a8e70: copy, then 009a8d30 lenient); list check 009a8d30; allowed 009a8be0; max 009a8d20 | at most HERO_SKILL_AVENGER mastery entries; an entry is allowed when it is a base creature (creature +0x100 = 0) and the kills of the creature plus its upgrades (+0x104) reach 2 × WeeklyGrowth (+0xa8); strict: longer list, a disallowed entry before the trailing empties or a duplicate → false; lenient: cut to the maximum, disallowed / duplicate entries emptied in place; IsFavoured 009a8ba0 maps an upgrade to its base | `SetFavoriteEnemiesCmd`, `CheckFavoriteEnemies`, `FavoriteEnemyAllowed`, `IsFavoriteEnemy`; `HeroState.FavoriteEnemies` ("" = empty slot), loaded from the shared hero / map hero Editable FavoriteEnemies | FIXED |
| Avenger kill counters | 009a8be0 sums two counters per creature (CAvenger +0x34 = hero vtable +0x60 → hero +0x1fc counter; +0x38 = a counter of the avenger's own, FUN_00a84630 with no parent); counter object vtable 0xe717d8: get 00a84480 (hash map), add 00a844d0 (forwards to the parent) | kills recorded per creature id | `HeroState.Kills`; both heroes of a battle count the enemy's losses at the battle result | UNVERIFIED (the counters' writer is not traced) / the avenger's own counter GAP (not kept) |
| Favoured enemies in combat | BattleSetup takes the HeroInstance list | the adventure hero's list goes into combat | `ToInstance` passes `FavoriteEnemies` (empties dropped) | FIXED |
| Avenger's Guild screen | enter_special → 00618ff0 → 00647c80 (UI/FavoredEnemy, CFavoredEnemyScreen@NUI 0xe2c9a4); panel controller 0xe2c928 (00648c60); refresh 00647cb0; cycling 00647ae0 / 00648a30 over candidates 00648990; commit 00647bb0; ok / close / timeout_forced_close → 00647c40 | UpperPanel = garrison hero, LowerPanel = visiting hero; HeroIcon = face (hidden without hero); ud1..ud4 shown for the list's entries; Up/Down enabled when cycling changes the slot, else the icon is hidden too; empty slot hides its icon; creature icon with TTFavoredEnemy (creature + upgrades); cycling skips to the next candidate ("" + every creature, wrapping) passing the strict check; ok/close commit both panels (hero, non-empty list, strict check) then close | `FavoredEnemyScreen`; Preserve enter_special and TB_SPECIAL_0 double-click open it | FIXED / panel's initial list (screen +8), candidate sort order (comparator 006477e0) and which of up/down steps forward UNVERIFIED |
| Necropolis transform: rules | FUN_00cd36c0 mode 3; CTurnCreatureToUndeadCmd (vtable 0xe0e468, execute 00428110) → FUN_00cd07f0 mode 3 | needs ability 0x12 (Necropolis TB_SPECIAL_2) else 3; undead, mechanical or elemental (FUN_00b61ba0 abilities 0xa, 9, 0xc) → 4; target = {Skeleton, Walking Dead, Manes, Vampire, Lich, Wight, Bone Dragon}[tier clamped 1..7], its TB_DWELLING_tier built else 5; free; same count; garrison or hero army, no hero needed | `TransformQuote`, `DoTransformCreature` | FIXED |
| Necropolis transform: screen | UI/NecropolisTransformCreature (CNecropolisTransformCreature@NUI, vtable 0xe2cd24); enter_special → 00619e80 → FUN_0064ec40; status switch FUN_0064e860; click FUN_0064e630 | 0 → state 0 enabled; 4 → state 2 disabled + DENY_REASON_IMPOSSIBLE; 5 → state 4 disabled + DENY_REASON_NO_DWELLING_TEXT; others state 0 disabled; click → confirmation → command (no sound) | `NecropolisTransformScreen` (shared `CreatureTransformScreen` with the Haven screen); building double-click (ability 0x12) opens it | FIXED / confirmation box UNVERIFIED |
| Inferno Sacrificial Pit: rules | FUN_00cd36c0 mode 1; CSacrificeCreatureForExperienceCmd (vtable 0xe0e420, execute 00428030) → FUN_00cd07f0 mode 1 | hero required (2), ability 9 (Inferno TB_SPECIAL_5) else 3; experience = hit points per creature with the hero's bonuses (FUN_004bc470) × count; the hero gains it (+0x198), the stack is removed; the hero's own army only | `SacrificeQuote`, `DoSacrifice` (`CreatureHitPoints` mirrors BattleSetup's FUN_004bc470 terms) | FIXED / final HP percentage (hero +0x330) and experience modifiers UNVERIFIED |
| Dungeon Ritual Pit: rules | FUN_00cd36c0 mode 2 (ability 0x1c, Dungeon TB_SPECIAL_3, else 3; no hero needed); FUN_00cd07f0 mode 2 | town total += hit points (FUN_004bc470 with the command's hero) × count, stack removed; bonuses: witches = largest n ≤ 30 with total ≥ 150·2ⁿ (getter +0x94, transformer +0x64), minotaurs = largest n ≤ 20 with total ≥ 400·3ⁿ (+0x98, +0x68) | `RitualPitBonus`, `TownState.SacrificedHitPoints` | FIXED |
| Dungeon Ritual Pit: growth | growth list field +0x24 T_GROWTH_SACRIFICE (006dc714, after castle/horde/dwelling/specialisation/grail, before the moon week); building tooltip 0061a5d0 ritual_pit_info_witches / _minotaurs; Ritual Pit description names the Blood Maiden and Minotaur lines | bonus added to the tier 2 / tier 3 weekly growth | added after the castle multiplier, before the moon week | UNVERIFIED (the filler of +0x24 not traced) |
| Sacrifice screens | UI/InfernoSacrifice, UI/DungeonSacrifice on CInfernoDungeonSacrificeScreenBase (layout 00653ca0: Hero%d Levelup/ExpView, Creature%d_%02d Icon/CountView/SacrificeBtn/Highlight); BUTTON_TOOLTIP with creature_name / creature_exp (00656b80); GrowthPanel_%d (0064fa30); close inferno_dungeon_sacrifice_close | as listed | `SacrificeScreen`: rows garrison hero (or garrison for the Ritual Pit) / visiting hero, button → command directly | FIXED (layout, tooltips, panels) / caption source, drag-and-drop, Levelup/ExpView preview and SacrificeFX GAP |
| Sacrificial altar (map building) | BUILDING_SACRIFICIAL_ALTAR; UI/SacrificeAltar; CSacrificeCreatureOnAltar (0xe0edd4, 00429cb0) / CSacrificeArtifactOnAltar (0xe0edf8, 00429d20); DefaultStats SacrificialAltar_CreatureHitsToExpCoef 3, _ArtifactCostOfGoldToExpCoef 0.5 | creatures / artifacts for experience | not built | GAP |
| Stronghold Slave Market: price | CAdvMapTown +0x120 = FUN_00cd0bc0; SlaveMarketConsts at RPGStats +0x11cc/+0x11d0/+0x11d4 | ability 0x2d (TB_SPECIAL_5); stack in the town's garrison or a hero's army there (FUN_00cd0340); cost × count; p = BasePercents + BonusPercentsPerMarketplace × marketplaces (CPlayer +0xf8 = FUN_004fbd80: per town 1 with a marketplace + 1 with TOWN_BONUS_MARKETPLACE); AffectResources → all × p / 100, else gold only (integer division) and the other resources in full | `SlaveMarketQuoteFor`, `SlaveMarketMarketplaces` | FIXED |
| Stronghold Slave Market: sale | CSellCreatureAtSlaveMarketAtTownCmd (vtable 0xe0ed68, execute 004298b0) | stack must be removable (army +0x18), price computed, stack removed, price added to the purse | `DoSellCreature`; "removable" = not a hero's last stack | FIXED / army +0x18 UNVERIFIED |
| Stronghold Slave Market: screen | UI/StrongholdSlaveMarket (CStrongholdSlaveMarket@NUI 0xe43174); enter_special → FUN_00618dc0 (needs TB_SPECIAL_5) → FUN_007b9fc0; button switch FUN_007b99c0 | not removable → state 2 T_BUTTON_RED_UNREMOVEABLE; not sellable → state 2 T_BUTTON_RED_UNSELLABLE; else state 0 T_BUTTON_GREEN; enabled; sell_gain tooltip | `SlaveMarketScreen`; building double-click (ability 0x2d) | FIXED / click confirmation (FUN_007b9d30) not traced, sells directly UNVERIFIED |
| Academy Arcane Forge (creature artifacts) | enter_special → 00618c10 → FUN_00640850 (UI/AcademyCreatureArtifacts, N_ACADEMY_CREATURE_ARTIFACTS_SCREEN_ID 00640ee0; UI/ArtefactMagic ID_ARTEFACT_MAGIC_SCREEN 007c0c10); building ability 0x16 (Academy TB_SPECIAL_2); CAddMicroArtifactToHeroBagCmd (0xe0e588, 004284e0 → hero +0xbc), CEquipMicroArtifactCmd (0xe0e564, 004283b0: HERO_SKILL_ARTIFICIER mastery > 0, slot in range, …), Move/Remove micro-artifact commands; MicroArtifactEffects / Prefixes / Shells tables | craft and equip creature artifacts | not built (combat has creature-artifact effects from another workstream) | GAP |
| Tower of Astrologer (map building) | BUILDING_ASTROLOGER_TOWER, CAstrologerDialogLogEntry; ID_TOWER_OF_ASTROLOGER_SCREEN (007b88c0); refusals ASTROLOGER_CANT_CHANGE_NO_MONEY / _TWICE_ON_WEEK / _ON_SUNDAY / _NO_ELEMENTS (007b7970); CChangeMoonWeekCmd (0xe0edb0, 00429bb0); cost AstrologerCallWeekCost (6000 gold; MMH 5.5: 2500) | buy the next week's moon week | not built | GAP |
| Fortress special (enter_special) | 00618d10: ability 0x20 (Fortress TB_SPECIAL_1, Runic Shrine) → FUN_00630450 (controller vtable 0xe2b6e8, open 006323f0 → FUN_00630f00 / 00631110: N_MAGIC_GUILD_SCREEN_ID, initial button "Button07") | the magic guild screen opened on a rune bookmark; RunesPanel_%02d / Button06–07 show the town's runes (+0x194 list, FUN_006309d0) | Fortress enter_special and the TB_SPECIAL_1 double-click open `MageGuildScreen` | FIXED / rune panel and bookmarks GAP (no town rune list; the guild screen's circle caption is still ours, not T_CIRCLE_%d) |
| Academy Artifact Merchant | TB_SPECIAL_3 | buy artifacts | built earlier (ArtifactMerchantScreen) | UNVERIFIED |
| Town "enter_special" for unbuilt screens | — | each opens its screen | Academy still shows our notice "This building's screen is not available yet." (placeholder text, not from the game) | GAP |

## Map object interaction (hover, cursor, click)

The hover handler is 0059e1f0: pick (005913b0) → action (00600e70 → 00600060 → 005ff8f0) → cursor (switch at
0059e594, jump tables 0059e694 / 0059e6bc; day families 0058c500 over the tables 00e21c78..00e21dd8). Cursor ids are the
enum named at 00516900 and are resolved through `UI/RefTables/Cursors.xdb` (ID → hwcursors FileName). Code:
`Adventure.Target` (src/H5.Adventure/Engine/Adventure.Interaction.cs), `AdvInteraction.cs`, `AdvCursors.cs`;
tests `InteractionTests`; off-screen check `--adventure-hover=Mine:body|entrance[:click]`.

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Object under the mouse | 005913b0: ray pick of the visual object (param_2[4]); when nothing or a static is hit, 00cea350 (object on / entered from the tile) then 00cea010 (cell type 1, or object whose cell list covers the tile) | any part of the model, or any of its cells, points at the object | `AdvWorld.PickObject` (ray vs model triangles, bind pose) then `Adventure.ObjectAtTile` (position / active cell, then blocked cell); statics never picked | FIXED (was: only the entrance cell counted, the body gave the default cursor and no route) |
| Route target | 00cebd50: the object's active cells (+0xb8, offsets from +0xa4) sorted by squared distance to the pointed tile (00cecb10), first with a route; heroes on their tile; no active cells → the pointed tile | route ends at the entrance nearest to the pointed cell | same (`Adventure.Approach`) | FIXED |
| Action per object | 005ff8f0 | own/allied army-holding building (mine, dwelling, garrison) → 10 own building; guarded → 7 attack; ship without hero → 13 board; hero: ally → 15 meet, enemy → 7; town (00600e70): ally → 11 own town, enemy with hero/garrison → 7, empty → 6; anything else → 5/6 visit | same mapping on our object kinds; a monster's guard zone → attack | FIXED (the interface getters behind the switch are identified by behaviour, not by vtable) |
| Cursor per action | 0059e594 switch | 0/5/6/7/10/11/13/14 → 0058c500 families (MOVE, USE, ATTACK_RED, OWN_BUILDING, OWN_TOWN, BOARD/UNBOARD/SHIP_MOVE); 1/2 → WAIT; 3, 0x15–0x18 → DEFAULT; 4 (no route) and 8 (no hero) → CANT_CAST; 9 → CAST; 0xf → HERO_MEET; 0x10/0x11 → FINGER; pick failed → CANT_CAST | `AdventureScreen.CursorFor` | FIXED (was: default over unreachable tiles, the selected hero and while locked; no CAST/WAIT/FINGER) |
| Attack cursor | 0058c500 case 7 reads only 00e21d68 | always CURSOR_ATTACK_RED…_RED_6 (Attack_a.cur …) | same | FIXED (was Attack.cur) |
| Day digit | 0058c500 clamps action+0x34 to 0..5; value from 00b4f930 (day roll-overs along the route; −1 when a step exceeds a full day → first entry) | 1–6 days | `Adventure.DaysFor` | MATCH |
| Red (`_a`) variants | 00600060 sets action+0x31 when the last route node's cost tuple +0x20..+0x2c is negative; 00c09d90 adds (−1, −penalty) to that tuple for every step onto a cell the pathfinder's evaluator (object at pathfinder+0xc) scores > 0 | red horse/boat/town/building cursors when the route crosses such cells | evaluator not identified; never red (except attack, above) | GAP |
| Cursor files | `UI/RefTables/Cursors.xdb` (00e1bbdc, loader 00524cb0) | ID → `..\hwcursors\*.cur` | loaded from the table | FIXED (was a hand-written file list) |
| Click | 00597640: stores the action; a second click with the same pick (tile and object) moves | plot, then go | same (`ClickTarget`) | FIXED (was: same destination tile) |
| Path preview | — | markers to the entrance, red end marker for fights | ends at the entrance; Contested end marker also for guarded objects | FIXED |
| Disembark on a visit route | 0059e5a7: action 5 with route flag +0x39 or water → UNBOARD family | — | not modelled | GAP |
| Hover highlight of objects | not traced | — | none | GAP |
| Tooltip target | tooltips follow the pick | — | `AdvTooltip` still resolves by tile (blocked cells included) | GAP |
| Unexplored tiles | not traced | — | CANT_CAST | GAP |

## Map object visits by category

Census of non-static objects (`count/maps`: stock 75 maps; MMH 5.5 117 maps — the same object types; 5.5 adds
ARTF_RANDOM_ANY and BUILDING_LEAN_TO and has no TREANT_THICKET, LAKE_OF_SCARLET_SWAN or MAGIC_SPRING on its maps).
Objects a 5.5 (or campaign) script re-scripts go through `ScriptHooks.InterceptVisit` (script runtime workstream). Texts
are the object's own `messagesFileRef` (index in brackets). The exe has one visit dialog per kind (RTTI
`C*DialogLogEntry`: Astrologer, BlackMarket, BuildShip, Chest, EnterSanctuary, Garrison, HeroVisitDwelling, HillFort,
Interactive, Market, MemoryMentor, SacrificialAltar, SearchGuarded, SeerHut, SpellShop, Sphinx, Tavern, ThievesInfo,
Treasure, WarMachineFactory, WitchHut; dispatcher 005a5d90).

| Category | Count (stock; 5.5) | Original | Ours | Status |
|---|---|---|---|---|
| Mines (sawmill, ore pit, alchemist lab, crystal cavern, gem pond, sulfur dune, gold mine) | 1877/68; 3055/105 | capture: flag + [2] FLYOFF_Captured flyoff; army holder (00b08560 / 00b086b0 put stacks in slots 0..6), so the owner can leave guards (own-building cursor); [3] AlreadyCaptured | capture, ownership, [2] flyoff; neutral guards fought on entry | FIXED (flyoff; was the name) / GAP (guard garrison, [3], fight prompt `DoYouWantToFightGuards.txt` not traced) |
| Abandoned mine | 66/18; 111/38 | guarded; [2] FirstVisit | battle, capture | GAP ([2] unused) |
| Dwarven Warren | 18/14; 34/23 | capture + [2] FLYOFF_Captured, [3] AlreadyCaptured, [4] DescriptionCaptured | capture + [2] flyoff | FIXED (flyoff) / GAP ([3], [4]) |
| External dwellings (faction dwellings, military posts, refugee camp …) | ~900/72; ~1300/110 | capture, then every visit 005a5d90 case 3 (CHeroVisitDwellingDialogLogEntry) → deferred 006db170 / 006e7e50 → 006dd290: the terrain's dwelling flyby scene (UIGameRoot ID_DWELLING_MAP_*, `Scenes/Dwelling/*`) under HIRE_CREATURES (`UI/HireCreatures`, set up by 006e23a0): name_header, one hire_window row per creature (006ddb60: dwelling icon, creature button with count, `HireWindow/Item/definition.txt` count / +growth), creature tab → creature_info, the hire jog dial 00782b70 → 007826c0 (EditJogDial: 0 Exit, 1 Minus, 2 Buy, 3 Plus, 4 HireAll; TownJogDial tooltips) | same screen and dial (`DwellingHireScreen`), RecruitCmd on the dwelling into the visitor; test `VisitScreenTests.DwellingVisitOpensTheHireScreenEveryTime` | FIXED (was capture only) / GAP (flyby scene: the adventure map stays behind; caravan tabs hidden; HireAll confirmation reuses `UI/HireCreatures/buy_all_*` — the dial's action not traced; the free-join branch of case 3 (006aef50 → 006ae7b0, `UI/FreeHire` texts, single-creature dwellings, condition = first vfunc of the dwelling interface) not identified; texts [2]–[5] FirstVisit/SecondVisit/…NoHire not referenced on this path) |
| Resource piles (wood … gold, random) | 9687/70; 15340/109 | pick up | resources + flyoff | UNVERIFIED |
| Treasure chest | 4105/73; 6000/114 | gold or experience (CChestDialogLogEntry), minor artifact chance | Choose2 with the stock Chest texts | UNVERIFIED |
| Campfire, flotsam, sea chest, shipwreck, soulstone | 768/53; 1707/88 | per type | rules per manual; soulstone not special-cased | UNVERIFIED |
| Artifacts (specific and random) | 2899/59; 4170/97 | pick up | artifact given; box "You found the …" (invented wording) | GAP (message) |
| Shrines (levels 1–3, runic) | 542/54; 975/91 | [2] SpellLearned, [3] AlreadyLearned, [4] SpellCantBeLearned (`<value=spell>`, `<value=skill>`) | outcome per visit, box with that text | FIXED (was: no message) |
| Creature banks (crypt, cyclops stockpile, dwarven treasure, naga bank, gargoyle stonevault, dragon utopia, treant thicket, pyramid, blood temple, sunken temple, Biara citadel) | ~640/49; ~800/70 | [2] Search prompt (CSearchGuardedDialogLogEntry) before the fight; empty → [3] SearchedEmpty with the morale/luck loss | Yes/No with [2]; empty → [3] | FIXED (was: battle at once, invented "The place is empty.") / UNVERIFIED (loot) |
| Keymaster tent | 19/10; 48/26 | [2] first_visit, [3] second_visit | key + box with [2] / [3] | FIXED (was: no message) |
| Border guard | 24/10; 54/26 | without key [2] nopass; with key [3] pass "Do you wish to pass at this time?" (also `Text/Interface/BorderGuard`) | [2] box; key → Yes/No with [3], Yes removes the guard | FIXED (was: invented text, opened without asking) / GAP (dedicated BorderGuard window) |
| Seer hut, Sphinx, obelisk / Tear of Asha, shipyard, marketplace | see the sections above | — | — | see above |
| Garrisons (garrison, anti-magic garrison, outpost) | 60/18; 120/35 | garrison screen (CGarrisonDialogLogEntry), [2] WayThruBlockedByHero | event only | GAP |
| Prison | 85/28; 99/38 | free the hero; [2] PrisonEmpty, [3] PrisonCantHire (too many heroes), [4] (file named NoExit) the freeing text | frees the hero; box with [2] / [3] / [4] | FIXED (was: invented "You cannot have more heroes.", no message on freeing) / UNVERIFIED (prison removed after freeing; hero cap 8) |
| Towns | ~320/30; ~520/50 | enter / capture / siege | town screen, capture, siege hand-off | UNVERIFIED (town audits elsewhere) |
| Monsters | 7417/72; 11946/114 | fight / join / flee (CJoinFleeDialogLogEntry, CFleeFightDialogLogEntry) | join/flee/fight by mood | UNVERIFIED (mood coefficients MATCH, see ADVENTURE.md) |
| Heroes | 86/26; 152/56 | meet (exchange) / attack | exchange screen / battle | UNVERIFIED |
| Teleports (monoliths one/two way, subterranean gate, whirlpool) | 448/46; 818/72 | teleport | `Teleport` | UNVERIFIED |
| Boats | — | board / land | see BOATS.md | UNVERIFIED |
| Stat buildings (mercenary camp, Marletto tower, star axis, garden of revelation, learning stone, arena, war academy, school of magic, library) | ~830/41; ~1400/70 | once per hero | once per hero; revisit box `BuildingsCommon/AlreadyVisited.txt`; arena/school choice box shows the internal kind name | GAP (choice box) / UNVERIFIED (rules) |
| Morale / luck / movement places (buoy, faerie ring, mermaids, fountains, idol, rally flag, stables, temple, oasis, scarlet swan, sanctuary, sirens) | ~1100/64; ~1700/103 | until the next battle / weekly | per manual; revisit box `BuildingsCommon/AlreadyVisited.txt` / `AlreadyVisitedThisWeek.txt` | FIXED (was the engine's placeholder wording) / UNVERIFIED (rules) |
| Weekly producers (windmill, water wheel, mystical garden), magic well / spring | ~790/59; ~1440/96 | weekly; `BuildingsCommon/AlreadyVisitedThisWeek.txt` | weekly; that text on a revisit | FIXED (was "Come back next week.") |
| Loot sites (lean-to, wagon, skeleton, tomb of the warrior) | 100/20; 230/36 | one-off loot | per manual | UNVERIFIED |
| Observatory, hut of magi, eye of magi, lighthouse, cartographer | ~500/58; ~800/93 | reveal / ownership | reveal rules; cartographer screen | UNVERIFIED |
| Witch hut, tree of knowledge, trading post, hill fort, war machine factory, black market | ~370/48; ~520/71 | screens (CWitchHutDialogLogEntry, CHillFortDialogLogEntry, CMarketDialogLogEntry, CWarMachineFactoryDialogLogEntry, CBlackMarketDialogLogEntry) | client screens (MapBuildingScreens) | UNVERIFIED |
| Signs | 31/12; 69/29 | sign text box | event only | GAP |
| Spell shop, map tavern, den of thieves, Memory Mentor, sacrificial altar, astrologer tower, nomad shaman, fortuitous sanctuary, demolish, unkempt | ~520/48; ~620/66 | screens (CSpellShop / CTavern / CThievesInfo / CMemoryMentor / CSacrificialAltar / CAstrologer DialogLogEntry) | event only | GAP |

Rows in these two sections: 18 FIXED, 1 MATCH, 15 GAP, 15 UNVERIFIED (rows with two statuses count once for each).
