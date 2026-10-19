# Adventure audit ledger

Every adventure mechanic / screen element checked against `H5_Game.exe` (Ghidra 12.1.4, addresses are image VAs) and
the original data. Status: **MATCH** (already as the exe), **FIXED** (changed to match), **GAP** (known difference or
not built), **UNVERIFIED** (implemented from data/editor semantics, exe path not traced).

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
