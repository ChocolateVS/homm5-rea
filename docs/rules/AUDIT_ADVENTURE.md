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
