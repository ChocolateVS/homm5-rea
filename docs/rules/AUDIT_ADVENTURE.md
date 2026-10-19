# Adventure audit ledger

Every adventure mechanic / screen element checked against `H5_Game.exe` (Ghidra 12.1.4, addresses are image VAs) and
the original data. Status: **MATCH** (already as the exe), **FIXED** (changed to match), **GAP** (known difference or
not built), **UNVERIFIED** (implemented from data/editor semantics, exe path not traced).

Rows at 2026-10-20: 46 FIXED, 5 MATCH, 19 GAP, 20 UNVERIFIED (rows carrying two statuses, e.g. rules FIXED / visual GAP, count once for each).

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
| Random maps | RMG `GraalOnMap` / `Grail` (RmgData) | the generator places a Graal | H5.Rmg does not place one yet, so generated maps have no Tear of Asha | GAP (RMG workstream) |
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
| Objectives / Kingdom Overview tabs, town and hero templates, resources with daily income, moon week, date | UI/MissionObjectives (townview1 / heroview1 templates) | — | MissionBoxScreen | UNVERIFIED (exe screen code not traced in this pass) |

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
| Sylvan Avenger's Guild (favoured enemies) | UI/FavoredEnemy | choose favoured enemies | not built | GAP |
| Necropolis transform: rules | FUN_00cd36c0 mode 3; CTurnCreatureToUndeadCmd (vtable 0xe0e468, execute 00428110) → FUN_00cd07f0 mode 3 | needs ability 0x12 (Necropolis TB_SPECIAL_2) else 3; undead, mechanical or elemental (FUN_00b61ba0 abilities 0xa, 9, 0xc) → 4; target = {Skeleton, Walking Dead, Manes, Vampire, Lich, Wight, Bone Dragon}[tier clamped 1..7], its TB_DWELLING_tier built else 5; free; same count; garrison or hero army, no hero needed | `TransformQuote`, `DoTransformCreature` | FIXED |
| Necropolis transform: screen | UI/NecropolisTransformCreature (CNecropolisTransformCreature@NUI, vtable 0xe2cd24); enter_special → 00619e80 → FUN_0064ec40; status switch FUN_0064e860; click FUN_0064e630 | 0 → state 0 enabled; 4 → state 2 disabled + DENY_REASON_IMPOSSIBLE; 5 → state 4 disabled + DENY_REASON_NO_DWELLING_TEXT; others state 0 disabled; click → confirmation → command (no sound) | `NecropolisTransformScreen` (shared `CreatureTransformScreen` with the Haven screen); building double-click (ability 0x12) opens it | FIXED / confirmation box UNVERIFIED |
| Inferno Sacrificial Pit: rules | FUN_00cd36c0 mode 1; CSacrificeCreatureForExperienceCmd (vtable 0xe0e420, execute 00428030) → FUN_00cd07f0 mode 1 | hero required (2), ability 9 (Inferno TB_SPECIAL_5) else 3; experience = hit points per creature with the hero's bonuses (FUN_004bc470) × count; the hero gains it (+0x198), the stack is removed; the hero's own army only | `SacrificeQuote`, `DoSacrifice` (`CreatureHitPoints` mirrors BattleSetup's FUN_004bc470 terms) | FIXED / final HP percentage (hero +0x330) and experience modifiers UNVERIFIED |
| Dungeon Ritual Pit: rules | FUN_00cd36c0 mode 2 (ability 0x1c, Dungeon TB_SPECIAL_3, else 3; no hero needed); FUN_00cd07f0 mode 2 | town total += hit points (FUN_004bc470 with the command's hero) × count, stack removed; bonuses: witches = largest n ≤ 30 with total ≥ 150·2ⁿ (getter +0x94, transformer +0x64), minotaurs = largest n ≤ 20 with total ≥ 400·3ⁿ (+0x98, +0x68) | `RitualPitBonus`, `TownState.SacrificedHitPoints` | FIXED |
| Dungeon Ritual Pit: growth | growth list field +0x24 T_GROWTH_SACRIFICE (006dc714, after castle/horde/dwelling/specialisation/grail, before the moon week); building tooltip 0061a5d0 ritual_pit_info_witches / _minotaurs; Ritual Pit description names the Blood Maiden and Minotaur lines | bonus added to the tier 2 / tier 3 weekly growth | added after the castle multiplier, before the moon week | UNVERIFIED (the filler of +0x24 not traced) |
| Sacrifice screens | UI/InfernoSacrifice, UI/DungeonSacrifice on CInfernoDungeonSacrificeScreenBase (layout 00653ca0: Hero%d Levelup/ExpView, Creature%d_%02d Icon/CountView/SacrificeBtn/Highlight); BUTTON_TOOLTIP with creature_name / creature_exp (00656b80); GrowthPanel_%d (0064fa30); close inferno_dungeon_sacrifice_close | as listed | `SacrificeScreen`: rows garrison hero (or garrison for the Ritual Pit) / visiting hero, button → command directly | FIXED (layout, tooltips, panels) / caption source, drag-and-drop, Levelup/ExpView preview and SacrificeFX GAP |
| Sacrificial altar (map building) | BUILDING_SACRIFICIAL_ALTAR; UI/SacrificeAltar; CSacrificeCreatureOnAltar (0xe0edd4, 00429cb0) / CSacrificeArtifactOnAltar (0xe0edf8, 00429d20); DefaultStats SacrificialAltar_CreatureHitsToExpCoef 3, _ArtifactCostOfGoldToExpCoef 0.5 | creatures / artifacts for experience | not built | GAP |
| Stronghold slave market | UI/StrongholdSlaveMarket | trade creatures | not built | GAP |
| Academy creature artifacts / Tower of Astrologer | UI/AcademyCreatureArtifacts, UI/ArtefactMagic, UI/TowerOfAstrologer | creature artifacts; astrologer | not built | GAP |
| Academy Artifact Merchant | TB_SPECIAL_3 | buy artifacts | built earlier (ArtifactMerchantScreen) | UNVERIFIED |
| Town "enter_special" for unbuilt screens | — | each opens its screen | our notice "This building's screen is not available yet." (placeholder text, not from the game) | GAP |
