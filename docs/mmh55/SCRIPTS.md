# MMH55 scripts on the stock engine (W2)

Target: the user's install runs `bin\H5_Game_PEST.exe` = stock GOG 3.1 `H5_Game.exe` + 16 bytes, **without** any MMH55 DLL.
MMH55's scripted gameplay (~23k lines of Lua in `MMH55-Index.pak` / `MMH55-Frame.pak`) therefore runs on the stock script
host, with the stock engine's quirks — including the checks the DLL would have removed. Addresses are VAs in `H5_Game.exe`
(Ghidra project `tools/ghidra-projects/scripting`). Status words: **MATCH** (ours already did what the exe does),
**FIXED** (changed to the exe), **GAP** (not traced or engine support missing — the note says what).

## 1. What runs, and when

### Adventure map (FUN_00992d00, called from 0x009941aa)
1. **No map script → no scripts at all.** `JZ` at 0x00992d2a returns when the map has no script resource. This is
   exactly the instruction MMH55's DLL group `RemoveMapScriptCheck` NOPs; without the DLL, MMH55's system scripts
   (`advmap-startup.lua` → H55-Core/H55-AdvMap) do **not** run on the 65 maps without a map script (all stock multiplayer
   maps, RMG maps). `AdventureScripting.RunWithoutMapScript` (off by default, `script-run --no-map-script-check`)
   reproduces the DLL behaviour. **FIXED** (we used to run the startup on every map).
2. Also skipped when both world flag bytes +0x88/+0x89 (CWorld vf5 → `this+0x88`) are 0; the same pair gates other
   host-side code (FUN_009902b0) — treated as "this machine runs the world" (our host/server runs the scripts). GAP: the
   exact meaning of the two bytes.
3. `DoString(scripts/advmap-startup.lua)`, `DoString("createAdvmapAliases();")` (unconditional), **one scheduler pass**
   (engine vf8 = FUN_004c0720), then the map script text if it is not empty. **FIXED** (the pass and the order).
   MMH55's `advmap-startup.lua` → `advmap-common` → `common.lua` (H55-Library + 750 `creature_N.lua` via `doFile`) →
   H55-Tables/Core/Version/Settings, `startThread` of 13 detector threads, `dofile(H55-AdvMap.lua)`.
4. `NEW_DAY` and the other triggers as before (AdventureScripting.Dispatch).

### Combat (combat screen constructor FUN_0054c2f0)
- At 0x0054ce31 the screen tests the multiplayer and hotseat flags (FUN_006b9de0; the same pair refuses flybys "in
  multiplayer or hotseat"). If either is set it logs "Warning: scripts are not allowed" and **no combat script runs**
  (no combat-startup, no DoPrepare/DoStart/UnitMove/UnitDeath). MMH55's DLL group `MultiplayerCombatScripts` NOPs that
  jump; **not active for the user**, so stock = combat scripts only in single-player (non-hotseat) battles shown on the
  combat screen. `CombatScripting.ScriptsAllowed(multiplayer, hotseat)`. Quick combat / AI-vs-AI battles have no combat
  screen, hence no scripts.
- Otherwise FUN_0053e810: `scripts/combat-startup.lua`, `createCombatAliases();` (the tutorial arena FUN_006a3fb0 also
  `createTutorialAliases();`), then the battle's combat script. Arena update FUN_0054ad10: `DoPrepare()` at arena state
  0x4000, `DoStart()` at 0x8000 (the arena waits for the call); `UnitMove("name")` from FUN_0053f400 when a new ready unit
  (heroes, war machines, towers included) appears outside preparation; `UnitDeath("name")` per CCombatEventDeath
  (FUN_0056fa90). MMH55's `H55-Combat.lua` hooks these (`C55_CombatPrepare/Start/Combat`).
- **Game variables are one shared object**: the adventure map passes its own table (IAdventureMap vf8) by reference into
  CCreateCombatArenaScreen (0x005a789b); the arena keeps it at +0x2e8. Nothing is copied either way.
  `AdventureScripting.CreateCombatScripts(battle)` shares `ScriptWorld.GameVars`. **FIXED** (combat scripts were never run).
- Wiring: `Adventure.BattleScripts` (factory) → `IBattleScriptSession`; `BattleRunner.RunToEnd` / `QuickCombat` route
  commands through it. The headless harness attaches it to battles with a human side. **FIXED**: the Godot client creates
  the session where the exe's screen does — `AdventureScreen.StartBattle` (a battle shown on the combat screen) passes
  `AdventureScripting.CreateCombatScripts` to `AdventureCombatSession` when the map's script host runs and the game is
  neither networked nor hotseat; commands go through the session (UnitMove/UnitDeath), script time runs at one segment
  per 200 ms, and battle events of script-issued commands are queued for the screen to animate
  (`CombatScripting.TakeScriptEvents`). Quick combat has no screen, hence no scripts (exe).
- FUN_0053e810 order: DoString(combat-startup.lua), DoString("createCombatAliases();"), **engine vf8 (one scheduler
  pass)**, then the combat script text from the combat game (arena +0x2a0 vf 0x28). **FIXED** (the pass was missing).
  GAP: which script the combat game carries (StartCombat's script, SetHeroCombatScript's per-hero script resolved by
  FUN_005d4a70, the map object's CombatScript) is not traced; our sessions run only combat-startup.lua.

### Town screen (FUN_006146f0)
CTownScreen has its own script engine (+0x390): DoString(`scripts/town-startup.lua`), then the text of the town
object's script resource (town vf 0xd0, when set). The screen's notify observers DoString `HeroHired('%s')` (0x00663ec0,
a hero hired in the tavern, string at 0x00e2e084) and `CreatureHired(%i,%i)` (0x00663f00, 0x00e2e094) on that engine.
MMH55's town-startup.lua loads `common.lua` and defines both as no-ops; the only towns with a script resource are
A2C1M3 (`A2C1M3_CombatScript`) and MMH55's A1C3M5 (`THcombatscript.xdb`). GAP: not hosted — the town engine's
registration table and which AdvMapTown field vf 0xd0 returns are not traced (observationally equivalent for MMH55).

## 2. Script VM (src/H5.Scripting/Lua)
- **Tables are not Lua 4.0's ltable.c** but an SGI-style chained hash map: find FUN_004c0900, set FUN_004c0b10, resize
  FUN_004c09c0 over the prime list at 0x00e0c020 (13, 29, 53, 97, …), new nodes at the head of their bucket, hash
  FUN_00463f90 (number: low 32 bits of the rounded int64; string: h·5+c, FUN_0040c470). Consequences reproduced: traversal
  order of `for k,v in t`; **nil is a valid key** (the exe has no "table index is nil"); storing nil keeps the node, so
  traversals yield nil-valued entries until a collection. **FIXED**. GAP: tables/functions as keys hash by heap address
  in the exe (we use object ids).
- **Collector timing**: luaC_checkGC FUN_00464a00 counts checks (+0x44) and collects on the 1001st; checks at
  CREATETABLE (before), CLOSURE, CONCAT, every function return / C-call completion (FUN_004c80a0 → FUN_009ac070) and
  chunk loads (FUN_009abcb0/FUN_009abb60). The mark pass FUN_00464640 erases nil-valued nodes (FUN_00464c40). **FIXED**.
- `a..b..c` compiles to one CONCAT n (luac 4.0 merge). **FIXED**.
- **Native argument parser** FUN_0087ad40 (formats generated into `Runtime/NativeFormats.cs` for all 300 natives):
  defaults, type checks, "Not enough arguments when calling function X.\n" (FUN_00879fb0), "Wrong type of argument K, when
  calling function X.\n" (FUN_0087a030); explicit nil counts as present. **FIXED**.
- **Errors**: the engine's error method (CLuaScriptEngine vf4 = FUN_00cab4e0 → FUN_009aca70 lua_error) sets the thread's
  stop flag (+0x30); the native returns and the thread ends after that call. Natives throw with the exe's text. MATCH.
  MMH55 relies on it: `H55_DetectPxAI` calls EnableHeroAI on every hero of player x and uses the "is not belong to AI
  player" error to stop the thread for humans (H55-Core prints a note about this).
- Scheduler, sleep, startThread, errorHook: prior RE (FUN_004c0720, FUN_004bfe80, FUN_004bfe20, FUN_004bff20). MATCH.

## 3. Time
- One script segment = 200 ms of game time (GetSoundTimeInSleeps divides by 200, FUN_006b5e60); the game client ticks by
  real time (ScriptUiBridge, AdventureNetSession). MATCH.
- The exe's world (and scheduler) keeps running while a computer player plays its animated turn; ours plays it in zero
  time, so MMH55's polling loop `H55_ContinuesActivator` (GetDate, `sleep(H55_CycleSpeed=2)`, then per-player
  `IsPlayerCurrent` events) never saw a computer turn. Each computer turn now starts with
  `SegmentsPerComputerTurn` = 5 segments (1 s). GAP: the exe's real duration of an AI turn is not reproducible (wall clock,
  animations); 5 ≥ one polling cycle is the guarantee MMH55 needs.
- consoleCmd (FUN_00cabca0 → console FUN_00bcf150): MMH55 issues `setvar dev_console_password …`, `setvar ai_time_limit
  180|1800|180000` (int at 0x00f440d0, ms, AI turn wall-clock limit compared at 0x0086de8e), `bindsection`, `bind
  enter_pressed 'Z'`, `game_writelog 1`, `console_size 999`. `setvar` is stored (`BaseLibrary.ConsoleVars`); the others are
  input/log/UI. GAP: our AI ignores `ai_time_limit` (exe 0x0086de82: wall clock since the AI turn started > limit → end turn; not reproducible in lockstep play, AUDIT_AI.md); the console tokenizer FUN_00c10f00 is not traced.

## 4. Performance
Loading the ~770 files of an MMH55 start (750 one-line `creature_N.lua`) takes ~90 ms per adventure start and the same per
scripted battle (file read + compile, no caching needed). A scripted map runs 7 days in 0.2–18 s headless (bots, 8 players).

## 5. Harness
`h5tool script-run all 7 --include-user` (H5_GAME_DIR = the 5.5 install): every one of the 117 maps, bots for all players,
7 days, combat scripts on battles with a human side, script errors split into ours and **those the stock exe raises too**:
- `Hero "X" is not belong to AI player` — MMH55's deliberate AI probe (above).
- `Player %d doesn't taking part in the current game` — `H55_UpdateGameMode` calls GetPlayerResource(i, 6) for i = 1..8;
  on maps with fewer than 8 players the exe stops that thread the same way (GetPlayerResource 0x005c19a0, state 0).
- `H55_GetUnusualSchools` — heroes missing from MMH55's race phonebooks (campaign variants like `Caldwell_DSU`) index
  `H55_UnusualSchools[0]` = nil.
- The two broken files in the original data: `Maps/Scenario/A1C3M1/a1c3m1.lua` (a lone UTF-16 BOM: the chunk fails to
  compile, nothing of it runs) and `A1C3M5/script_combat.lua` (`elseif diff == 3;` syntax error), failing like the exe.

Result (2026-10-20, HEAD d2567ec): **117 maps, 0 engine exceptions, 106 without any error of ours** (114 stock-matching
errors); 50 maps have a map script (MMH55 runs), 65 have none (nothing runs, as stock), 2 are not playable. The remaining
21 errors on 10 maps were each checked and are script/map logic the exe hits the same way under the same game state:
A1C2M4 `EnableHeroAI("Brem")` on a tavern-only hero (ours: "is not exists". The exe (0x005da500) raises "is not belong
to AI player" only for a hero whose owner is human (player vf 0x184 = 0x00500d60: AI controller +0x74 is null) and
posts the command for an ownerless hero, so the exe either stops with "is not exists" or raises nothing — GAP: whether
the name lookup IAdventureMap vf 0x14 (CAdventureController 0x006033c0 → +0x18 → vf 0x140 → vf 0x14) sees
tavern-pool heroes is not traced; our tavern pool holds hero ids only); SM2 `GetObjectOwner("Haven")`
(no such object in the map) → its `H55_TriggerDaily` is never defined → MMH55's crash protection calls nil; A2C2M1
`H55_TriggerDaily` indexes `targetsArray` after shrinking it inside the loop; A1C3M5 completes an objective that is still
in its initial state (exe's transition table); A2C2M3/A2C3M4/SL2 objects or players removed by the bots' play
(`crossbowman1`, Zehir not on the map, `Loose()` with no active human); MMH55's own `H55_AbandonedMineWin` passing nil to
Trigger, `H55_MakeStartArmy` removing 0 creatures ("Quantity cannot be negative", exe check), `CheckWizardCombat2` passing
nil to IsObjectExists.

## 6. Natives ledger
Every native MMH55's system scripts and campaign/SP map scripts call (213, from the inventory call graphs) was compared
with its exe wrapper (decompiles of all 300 natives). The 16 "missing" natives of `missing_natives.txt` were registered
before but as inert flags; they are now real (group AI/hiring). Totals: 24 MATCH, 182 FIXED, 11 GAP-only; many FIXED rows
note a residual gap.

| Native | Group | exe VA | Status | Note |
|---|---|---|---|---|
| BlockGame | core | 0x005deeb0 | MATCH | IAdventureMap vf0x58(arg); our block counter (arg unused, as no semantics traced for it) |
| GetCurrentMoonWeek | core | 0x005d2cc0 | MATCH | world vf0xb0 moon week id |
| GetCurrentPlayer | core | 0x005d73d0 | FIXED | returns -1 (not 0) when there is no current player (FUN_0098d480 null) |
| GetDate | core | 0x005d7170 | FIXED | WEEK = (d/7)%4+1 (week of month), MONTH = (d/28)%12+1 (were absolute week/month); DAY, DAY_OF_WEEK match; error text matches |
| GetDifficulty | core | 0x006b5730 | MATCH | engine difficulty mapped by FUN_00a33780 to script 0..3; S.Difficulty already script scale |
| GetGameVar | core | 0x005dea90 | FIXED | empty name -> "Game variable name is not specified"; missing var -> "" |
| GetLastSavedCombatIndex | core | 0x005e5c50 | MATCH | world vf0x1f4 = count-1 (-1 when none) |
| GetMapDataPath | core | 0x006b5c70 | MATCH | directory of map path (FUN_00bf6a40 formatting of the leading '/' not traced) |
| GetMaxFloor | core | 0x005e1570 | MATCH | 1 if floor 1 exists else 0 |
| GetObjectiveState | core | 0x005d8710 | FIXED | per-player lookup (player arg, default 1) with exe errors: name not specified / Incorrect player ID #%i / Objective "%s" is not exists |
| GetPlayerHeroes | core | 0x005c1540 | FIXED | errors "Player ID #%i is not valid", "Player #%i is not exists" (state 0); order = our hero list order (exe player list order not traced) |
| GetPlayerResource | core | 0x005c19a0 | FIXED | "Player ID must be 1 through 8", "Player %d doesn't taking part in the current game" (state 0), "Invalid resource specified: %d" |
| GetPlayerState | core | 0x005c1390 | FIXED | "Player ID must be 1 through 8"; returns player state 0/1/2/3 |
| GetSavedCombatArmyCreatureInfo | core | 0x005e6720 | FIXED | winner=army0/loser=army1 (side 0 -> loser); unknown index/no armies -> 0,0,0; bad index -> "Invalid creature index %d!"; data now recorded (engine hook) |
| GetSavedCombatArmyCreaturesCount | core | 0x005e64d0 | FIXED | side mapping fixed; unknown index -> 0 (no error); counts start-of-combat stacks |
| GetSavedCombatArmyHero | core | 0x005e6260 | FIXED | side mapping fixed; no hero / unknown index -> no return value (nil), no error |
| GetSavedCombatArmyPlayer | core | 0x005e6030 | FIXED | side mapping fixed; unknown index -> 0 (PLAYER_NONE), no error; was invented "Saved combat #%d not found" |
| GetSavedCombatResult | core | 0x005e5e40 | FIXED | returns COMBAT_RESULT_* (engine 1->1 win, 2->3 surrender, 3->2 retreat); unknown index -> default record 0; GAP: COMBAT_RESULT_NONE (script-cancelled combat) not produced |
| GetSoundTimeInSleeps | core | 0x006b5e60 | MATCH | ms/200, missing sound logs warning and returns 0 |
| GetTerrainSize | core | 0x005e0f90 | MATCH | ground terrain +0x14,+0x18 |
| GiveBorderguardKey | core | 0x005c2540 | FIXED | "Invalid player ID #%i", doesn't-taking-part, "Invalid borderguard key color #%i" (1..8) |
| HasBorderguardKey | core | 0x005c22a0 | FIXED | same validation as GiveBorderguardKey; returns bool |
| IsPlayerCurrent | core | 0x005d75c0 | FIXED | "Invalid player ID %d!"; returns turn status vf0x44==0 (= current player) |
| IsPlayerInGhostMode | core | 0x005d7810 | FIXED | "Invalid player ID %d!" (1..8), then "No player with ID %d!" (added), then player vf 0x44 (0x00500c10: turn state +0x634) == 2. State 2 (00998060): network game with Ghost Mode (+0x8a) and sequential turns, an alive human other than the next player when that player is human; simultaneous phase: humans that ended their turn (tick 00998520). Single player: +0x8a = the never-written global 0x00ffad3c (0x0098f76e), so always false. AdventureNetSession.InGhostMode feeds AdventureScripting.PlayerInGhostMode |
| IsTeamCurrent | core | 0x005d7cb0 | FIXED | true iff team has players and every active (state 1) member is on its turn (was: current player's team) |
| IsTilePassable | core | 0x005e11a0 | FIXED | errors: "Can`t run: cant get ground floor", "X/Y is out of range (must be in [0,%i], now is %i" (was silently false); passability via vf0x160 approximated by IsBlocked |
| IsTutorialEnabled | core | 0x007742f0 | MATCH | profile tutorial_show != 0 (context is CTutorialScriptContext, a base of CAdventureController) |
| IsTutorialItemEnabled | core | 0x00773c00 | GAP | tutorial object vf0xc(name) not traced; kept enabled && not shown yet |
| IsTutorialMessageBoxOpen | core | 0x00774160 | MATCH | tutorial vf0x1c |
| Loose | core | 0x005d6ed0 | FIXED | SP: arg ignored, first active human; MP: player by id; "No player!" / "Player not active!" (was "Incorrect player ID (-1)" for the default -1); GAP: SP/MP told apart by human count (exe: world+0x88 string) |
| Save | core | 0x006b58d0 | GAP | emits save_game; exe FUN_006b5660 error-string path (save failures) not reproduced |
| SetGameVar | core | 0x005decb0 | FIXED | empty name error; empty value removes the variable |
| SetObjectiveProgress | core | 0x005d9170 | FIXED | per-player lookup; requires OBJECTIVE_KIND_MANUAL ("Objective %s is not manualy controlled") and active state; step range errors |
| SetObjectiveState | core | 0x005d89e0 | FIXED | full transition table with exe messages (scenario info/initial/active/completed/failed, dependencies, CanUncomplete, manual); same-state set now errors; trigger fires only for name\|player; GAP: vf0x8c approximated as "no dependencies" |
| SetObjectiveVisible | core | 0x005d83f0 | FIXED | per-player lookup; IsHidden objectives -> "Objective \"%s\" is hidden" |
| SetPlayerResource | core | 0x005c1c30 | FIXED | exe messages ("Quantity cannot be negative", "Invalid resource specified: %d", doesn't-taking-part); 4th arg hero: warning log when not on map |
| SetPlayerStartResources | core | 0x005c1fb0 | FIXED | exe messages; any negative -> "All resource values must be >0"; sets all 7 at once |
| SetPlayerTeam | core | 0x005e2030 | FIXED | player/team 1..9 checks with exe messages; id 9 -> "Not found player 9 in world!" |
| SetTrigger | core | 0x005dd530 | FIXED | type/player args rounded (FUN_004c0d00) and lua_isnumber; string type checks; player trigger needs player; objective trigger = player 1 unless 4+ args (then id must be number 1..8) and objective must exist for that player; type 10 needs a town; GAP: types 4/5 "cannot be touched/captured" checks and hero lookup (vf0x14) scope not reproduced |
| Trigger | core | 0x005dd530 | FIXED | same function as SetTrigger |
| TutorialActivateHint | core | 0x00773da0 | MATCH | tutorial vf0x10(name); GAP (central): parser errors name it "ActivateTutorialHint" |
| TutorialMessageBox | core | 0x00774020 | MATCH | tutorial vf0x18(name) |
| TutorialSetBlink | core | 0x00773ee0 | FIXED | 2nd arg is a number, blink = n != 0 (c.Bool on a number was always true); GAP (central): parser errors name it "SetTutorialBlink" |
| UnblockGame | core | 0x005df000 | MATCH | IAdventureMap vf0x5c |
| Win | core | 0x005d69d0 | FIXED | SP: arg ignored, first active human; MP: "Invalid player ID(%d) in MP game Win() script command!"; "No player!"/"Player not active!"; default -1 no longer errors |
| random | core | 0x005dfc30 | FIXED | top < 1 -> "Random`s top must be positive" (was 0); GAP: synchronised RNG algorithm (IAdventureMap vf0xc) not traced |
| AddHeroCreatures | heroes | 0x005c6140 | FIXED | count 0 now errors "Quantity cannot be negative" (exe: count<=0); 4th arg slot used when empty/same type (FUN_0045a520) else default placement; returns true |
| CalcHeroMoveCost | heroes | 0x005ceaf0 | FIXED | hero looked up as map object ("Object \"%s\" is not exists" / "in not a hero"); floor -1..1 ("Incorrect floor number #%i", "Floor #%i is not exists"); X/Y range errors with exe text; -1 without path. GAP-detail: cost = sum of our StepCost (exe: last path node's accumulated cost) |
| CanMoveHero | heroes | 0x005ce790 | FIXED | errors in exe order: not exists / not belong to any player / human-owned ("Can`t use function for heroes, that belong to human player") / not on map / floor checks; returns path exists |
| ChangeHeroStat | heroes | 0x005c3620 | FIXED | exp: "Experience can`t be negative", no HeroesExpCoef (cmd -> hero vf0x198 adds raw, capped at max-level exp); attributes: plain add, no clamp (vf0x1bc); MP/mana = clamp(cur+d, 0, max) strictly (old code kept values above max); texts "Hero \"%s\" not exists", "Invalid stat ID #%i" |
| ControlHeroCustomAbility | heroes | 0x005d2560 | GAP | validation added: mode -1..1 ("Invalid hero custom ability mode %d!") first, then ability 1..4 ("Invalid her custom ability index %d!"), then hero. GAP: ability effect not modelled (stored only) |
| DeployReserveHero | heroes | 0x005c27f0 | FIXED | errors in exe order incl. empty name, "is not in reserve of player #%i, to which he belongs", floor 0..1 "Invalid floor number #%i", "Floor #%i is not exists", "Can`t find an empty cell to deploy hero" (was silently using the tile). GAP: player # printed as script id 1..8 (player vf0x2c unverified); 5th arg rotation ignored |
| GetHeroArtifactsCount | heroes | 0x005c4f70 | FIXED | id range check "Invalid artefact ID %d"; worn count + backpack count unless 3rd arg non-zero (was ignoring backpack and flag) |
| GetHeroCreatures | heroes | 0x005c5ed0 | FIXED | texts "Hero \"%s\" doesn`t exist", "Invalid combat unit ID %d [arg1]"; sum over all stacks |
| GetHeroCreaturesTypes | heroes | 0x005ccb70 | FIXED | returns the DISTINCT creature types in slot order padded with 0 to 7 values (was one value per slot); text "Hero \"%s\" doesn`t exist" |
| GetHeroLevel | heroes | 0x005c3190 | FIXED | text "Hero with name \"%s\" doesn't exist"; no empty-name check (as exe) |
| GetHeroSkillMastery | heroes | 0x005c4140 | FIXED | skill id must be in [0,count) else "Skill with id #%i not exists"; hero vf0x174: perk -> 1, Pendant of Mastery +1 to the hero race's racial skill, values outside 0..4 -> 0. GAP: MMH55 DLL patch PendantMasteryAndGating (hex@b4d190) not reproduced |
| GetHeroStat | heroes | 0x005c33a0 | FIXED | stat 9 (max move points, vf0xfc) added; attack/defence = base + worn artifact bonuses floored at 0, SP/knowledge floored at 1, luck/morale clamped -5..5 (exe getters 0x00b4cc50.. / 0x00b4cb40); texts "Hero \"%s\" not exists", "Incorrect stat id #%i". GAP: set bonuses, curse (+0x228) and Fortress SP bonus in the getters not reproduced |
| GetHeroTown | heroes | 0x005d0a90 | FIXED | returns the town name only when the hero is that town's garrison hero (town slot 1), else nothing (nil); text "Hero \"%s\" is not exists" |
| GiveArtefact | heroes | 0x005c5200 | FIXED | "Invalid artefact #%d" range check; worn when its slot has room else backpack (engine hook ScriptGiveArtifact, exe cmd 0x00431fe0); 3rd arg (n) tested !=0 (was Lua-truthy so 0 counted as bound). GAP: bound flag stored only |
| GiveHeroSkill | heroes | 0x005c43a0 | FIXED | returns whether given; perk: learnable (vf0x18c); skill: improve if known and < Expert (vf0x1c4) else needs a free slot (vf0x1b8: 5 minus non-racial skills); id range error. GAP: vf0x188 learnable-perk list approximated (not learned, basic skill known, prerequisites) |
| GiveHeroWarMachine | heroes | 0x005c6a50 | FIXED | "Invalid war machine ID #%i" outside 1..4; returns true iff the hero did not have it (catapult always had -> false) |
| HasArtefact | heroes | 0x005c4cd0 | FIXED | 3rd arg = equipped only: worn, else also backpack (flag was ignored); range check "Invalid artefact ID %d" |
| HasHeroSkill | heroes | 0x005c3ee0 | FIXED | vf0x174 > 0 (same mastery as GetHeroSkillMastery); id range error "Skill with id #%i not exists" |
| HasHeroWarMachine | heroes | 0x005c67f0 | FIXED | invalid id now errors "Invalid war machine ID #%i" (was false); text "Hero \"%s\" doesn`t exist" |
| IsHeroAlive | heroes | 0x005c2f80 | MATCH | no errors; true iff the hero is deployed in its owner's hero list (dropped an extra owner-player-alive test) |
| IsHeroInBoat | heroes | 0x005e3580 | FIXED | empty-name / "is not exists" errors; false for heroes not on the map |
| IsHeroInTown | heroes | 0x005c9c50 | FIXED | hero name not validated (only compared); "Town \"%s\" does not exist!"; 3rd arg checks the gate hero (slot 0), 4th the garrison hero (slot 1); flags tested !=0 (old code garbled them) |
| KnowHeroSpell | heroes | 0x005c4770 | FIXED | out-of-range id read as 0, then FUN_00a349f0 validity (runic/warcry ok; id 67 and SPECIAL/NONE schools invalid) -> "Invalid spell id #%i" with the original id; text "Hero \"%s\" is not exists" |
| LevelUpHero | heroes | 0x005c3c00 | FIXED | only below level 40 (hard-coded 0x28); gives the exp missing for the next level through the capped exp path; returns level<40 (was nothing) |
| MakeHeroInteractWithObject | heroes | 0x005d2220 | FIXED | cmd -> adventure hero vf0x74: visit WITHOUT moving (was walking the hero there); "Object name is not specified", "Object \"%s\" does not exist!"; no-op for heroes not on the map |
| MakeHeroNecromancer | heroes | 0x005e4d50 | FIXED | level 0..4 validated ("Invalid necromancy level %d!"); CMakeHeroNecromancerCmd (Execute 0x0042a140) → hero vf 0x364 (0x00b4c9a0) sets CHero +0x2a8 (HeroState.ScriptNecromancyLevel); read by hero vf 0x368 in FUN_00c25ff0/FUN_00c26040: necromancy level = Necromancy mastery if > 0, else this level if > 0 (Adventure.NecromancyLevel: raise percent and raising). GAP: the Execute sets it only when two bytes of an owner record (player vf 0x14 → +0x1cc) are 0 — not traced, ignored; third reader FUN_00c26360 (from 0x004fb920) not identified |
| MakeHeroReturnToTavernAfterDeath | heroes | 0x005e4fc0 | GAP | no hero validation; 2nd/3rd args (n) tested !=0 and both stored. Traced: CMakeHeroReturnToTavernAfterDeathCmd (Execute 0x0042a090) → hero vf 0x74 (0x00724940: CHero sub-object +0x18) vf 0x44(hero name, flag1, flag2). GAP: that object and the death-time reader not traced; our engine returns every dead hero to the pool |
| MoveHero | heroes | 0x005cef50 | FIXED | AI-owned heroes only (human -> error); not exists / not belong / not on map / floor errors; a missing path is only logged (exe text). GAP: exe sets an AI move target, we move at once using move points |
| MoveHeroRealTime | heroes | 0x005cf310 | FIXED | any owner; same checks without the human test; missing path logged and nothing moves; moves ignoring move points |
| RemoveArtefact | heroes | 0x005c54d0 | FIXED | "Invalid artefact #%d"; "Hero %s hasn't such artefact!" when neither worn nor in backpack; worn copy removed first else backpack; engine hook no longer subtracts artifact stats from the base attributes (it corrupted stats: bonuses are never added to the base) |
| RemoveHeroCreatures | heroes | 0x005c64a0 | FIXED | count<=0 errors "Quantity cannot be negative"; removing all of a type when the army is only that type keeps one; removal starts at the 4th-arg slot then last->first (FUN_0045a440); returns the number removed (exe value, -1 on an empty army) |
| ResetHeroCombatScript | heroes | 0x005c72e0 | FIXED | errors "Hero \"%s\" is not exists", "Hero \"%s\" not belongs to any player" |
| SetHeroCombatScript | heroes | 0x005c6fd0 | GAP | same hero/owner errors. GAP: script resource lookup ("Script \"%s\" is not exists", FUN_00bf6c30/FUN_005d4a70) not reproduced; the combat host does not run the stored script |
| SetHeroLootable | heroes | 0x005c5c80 | FIXED | text "Hero with name \"%s\" doesn't exist"; empty-name check. CSetHeroLootableCmd (Execute 0x00427a00) → hero vf 0xb0 (0x00b4b740) sets CHero +0x218 (HeroState.Lootable, default true); IsHeroLootable reads it (vf 0xac = 0x00b5aa00). GAP: our engine does not move a defeated hero's artifacts to the winner, so nothing consumes the flag; the vf 0xac readers (151 call sites of that slot) were not isolated |
| SetHeroRoleMode | heroes | 0x005e7260 | GAP | mode mapped like the exe (1->1, 2->2, other->0); text "Hero \"%s\" is not exists". GAP: role effect (AI) not modelled |
| SetHeroesExpCoef | heroes | 0x005e2c70 | FIXED | (int)(coef*100+0.5) posted as CSetHeroesExpCoefCmd (Execute 0x00428ef0) → world vf 0x1b0 (0x0099e5b0): AdvState.HeroesExpCoef = clamp 0..1000 (world +0x16c, 100 in the ctors 0x00997600/0x009978d0). The only caller of world vf 0x1b4 (0x0099e500: coef·exp/100 unless 100; no other reference) is CMonsterFleeCmd 0x0042ad40: the experience for letting monsters flee (Adventure.ApplyHeroesExpCoef). Battle experience is not scaled (exe) |
| SinkHero | heroes | 0x005e37f0 | FIXED | text "Hero \"%s\" is not exists"; only a hero on the map sinks, otherwise no-op |
| TakeAwayHeroExp | heroes | 0x005e5a00 | GAP | hero vf0x354 (0x00b56430): experience removed level by level and the level drops (was exp only). GAP: per-level skill loss and attribute loss (vf0x338/0x340/0x308) not reproduced |
| TeachHeroSpell | heroes | 0x005c49e0 | FIXED | same spell-id validation as KnowHeroSpell; always teaches; returns !vf0xdc (approximated as "hero did not know it"); text "Hero \"%s\" is not exists" |
| UnreserveHero | heroes | 0x005c2c90 | FIXED | errors not specified / not exists / not belong / not in reserve of player #%i; only removes the hero from the reserve (invented tavern-pool add removed). GAP: owner vf0x170 exact effect untraced |
| AddObjectCreatures | objects/towns | 0x005ccec0 | FIXED | order name/object("doesn't exist")/creature("Invalid creature ID #%i")/count<1("Quantity cannot be negative", 0 now errors)/army("has no army"); invented "monster can only get own type" error removed. GAP: slot arg and the bool the exe pushes not modelled |
| CreateArtifact | objects/towns | 0x005c8730 | FIXED | "Invalid artifact ID #%i"; floor 0..1 "Invalid floor ID #%i", "Floor #%i is not exists", "Cell (%i,%i) is not inside floor #%i borders", "Can`t find an empty cell to place artifact" |
| CreateCaravan | objects/towns | 0x005e32c0 | FIXED | empty name -> "Caravan name is not specified! ..."; player 1..8 else "Incorrect player ID #%i!". GAP: no caravan engine (stored in Settings) |
| CreateDwelling | objects/towns | 0x005c9530 | FIXED | was taking a shared path; now (name,townType,tier,owner,x,y,floor,rot): "Invalid town type %d!", "Invalid player %d!", floor/cell checks, RPGRoot DWELLINGS_<town> group link clamp(tier-1,0,3), tier 4..7 enables one post creature / 8 all, footprint "Cell is not empty: (%d, %d, %d)", group errors |
| CreateMonster | objects/towns | 0x005c8aa0 | FIXED | removed invented duplicate-name error and count clamp; checks type, floor, cell, empty cell ("Can`t find an empty cell to place monster"), mood 0..3, courage 0..2 in exe order; rotation truncated to int degrees. GAP: 10th arg unknown; FUN_005c0f00 nearest-cell search approximated by FreeTileNear |
| CreateStatic | objects/towns | 0x005d1810 | FIXED | "Not found static shared '%s'!", floor/cell checks, rotation arg now applied, no empty-cell check (as exe). GAP: args 7/8 (flag, 100) not modelled |
| DestroyTownBuildingToLevel | objects/towns | 0x005c8140 | FIXED | "Town name is not specified"/"Town \"%s\" is not specified"/"not containing building #%i" (building must exist in the town type), level<0 and >max errors; canRebuild==0 caps the limit (was ignored) |
| DisableAutoEnterTown | objects/towns | 0x005e6a00 | FIXED | "Object \"%s\" does not exist!"/"is not town!"; the exe ignores the flag argument and always disables. GAP: engine does not consume the setting |
| GenerateMonsters | objects/towns | 0x005cf8c0 | FIXED | exe validations (min groups<0, max<min, min count<1, max<min) instead of clamping; group count min+rng%n; groups spread to random existing floors (rng&1). GAP: per-floor placement algorithm (FUN_00ce9e40..) not reproduced |
| GetObjectCreatures | objects/towns | 0x005cc590 | FIXED | object check before creature id; "Object \"%s\" doesn`t exist", "Invalid creature ID #%i", "has no army" (objects without army now error) |
| GetObjectCreaturesTypes | objects/towns | 0x005cc820 | FIXED | distinct creature types in slot order (was one per slot with duplicates), padded to 7 returns; exe error texts |
| GetObjectDwellingCreatures | objects/towns | 0x005cd530 | FIXED | towns only (vf 0x3c), "Town name is not specified"/"Town \"%s\" doesn't exist"; arg is a creature id checked 0<=id<count ("Invalid creature ID #%i"), id 0 allowed |
| GetObjectNamesByType | objects/towns | 0x005e0660 | FIXED | floor 0 then 1 in object order; substring (std::search) match of the type name: Mine/Building/Dwelling <Type>, AbanMine/Prison/SeerHut fixed names, monster creature, town type (resolved for random ones), hero class; other shareds (Sphinx, Garrison, Tent...) never match; names unfiltered. Unnamed objects now carry the exe's auto name (FUN_00ce1020 "%i-%i-%i-%s-%i", AdventureScripting.NameUnnamedObjects). GAP: world object id in the auto name = map index |
| GetObjectOwner | objects/towns | 0x005cb6d0 | FIXED | empty-name error; "Object \"%s\" not exists" |
| GetObjectPosition | objects/towns | 0x005caf40 | FIXED | empty-name error "Object name is not specified" |
| GetObjectsInRegion | objects/towns | 0x005cbf60 | FIXED | only type 0 (OBJECT_HERO) valid, others "Invalid objects type #%i" (the type-1 objects branch was invented); named heroes in list order; region errors |
| GetTownBuildingLevel | objects/towns | 0x005c7590 | FIXED | "Town \"%s\" is not specified" and per-town building existence check |
| GetTownHero | objects/towns | 0x005d0840 | FIXED | garrison hero only (town vf 0x18(1)); no return value when none (was nil / visiting-hero fallback); empty-name error |
| IsObjectExists | objects/towns | 0x005c8520 | FIXED | empty name now errors "Object name is not specified" (was false) |
| IsObjectInRegion | objects/towns | 0x005cbcd0 | FIXED | object checked before region; empty region name -> "Region name is not specified" |
| IsObjectVisible | objects/towns | 0x005cc2a0 | FIXED | "Invalid player ID #%i (must be in [1,8] )", "Player #%i doesn`t taking part in the current game", empty-name error |
| MakeTownMovable | objects/towns | 0x005e5770 | FIXED | "does not exist!"/"is not town!" texts. GAP: engine does not consume the setting (SetObjectPosition moves towns regardless) |
| MarkObjectAsVisited | objects/towns | 0x005ce0f0 | FIXED | interactive object or hero; "is not exists or is not interactive", "is enabled, so can`t mark it as visited manualy", hero must be on map ("Hero \"%s\" is not on map"). GAP: object-specific visited state (interactive vf 0x1c, e.g. banks) approximated by hero.VisitedOnce |
| OpenCircleFog | objects/towns | 0x005da1e0 | FIXED | player 1..8 / taking-part errors, "Floor #%i is not exists", "Range is negative". GAP: exact circle shape of CModifyPlayerFogInCircleCmd not traced |
| OpenRegionFog | objects/towns | 0x005d9df0 | FIXED | reveals exactly the region cells (new engine hook Adventure.ScriptRevealTiles) instead of a circle; floor/border errors. GAP: border margin (floor vf 0x68) assumed 0 |
| OverrideObjectTooltipNameAndDescription | objects/towns | 0x005e74e0 | FIXED | empty name silently ignored (exe passes a null object); "Object \"%s\" does not exist!". GAP: UI does not consume the override |
| RazeBuilding | objects/towns | 0x005ca830 | FIXED | was disabling the object; now removes it (towns through the RazeTown path) and places its (RazedStatic) with the same name; "Building \"%s\" does not exists"/"is not razeable" |
| RazeTown | objects/towns | 0x005ca3f0 | FIXED | needs (RazedStatic) ("Town \"%s\" is not razeable"); places the razed static named like the town |
| RegionToPoint | objects/towns | 0x005d9540 | FIXED | region must be one cell ("Region \"%s\" is not a point") and returns it (was the centre of any region) |
| RemoveObject | objects/towns | 0x005cacb0 | FIXED | empty-name error. GAP: failure reasons "Can`t remove object \"%s\": %s" of the object's remove method not traced |
| RemoveObjectCreatures | objects/towns | 0x005cd7b0 | FIXED | count<1 errors "Quantity cannot be negative"; exe texts/order; hero keeps one creature, emptied monster removed (unchanged). GAP: returned number not modelled |
| ReplaceDwelling | objects/towns | 0x005d1c50 | FIXED | "Dwelling name was not specified!", "Dwelling \"%s\" does not exist!", "Invalid town type %d!"; the dwelling now takes the same link of the new town's DWELLINGS group (shared swap, restored on load). GAP: creature overrides and the exe's availability handling not traced (availability reset to weekly growth) |
| SetDisabledObjectMode | objects/towns | 0x005e54b0 | FIXED | no longer disables the object; stores the cursor mode only (CSetDisabledObjectModeCmd -> interactive vf 0x3c); "does not exist!", "is interactive!" (non-interactive), "Invalid disabled object mode #%d!"; InterceptVisit no longer lets DISABLED_ATTACK monsters fight (invented) |
| SetMonsterCourageAndMood | objects/towns | 0x005c9240 | FIXED | args are (monster, player, courage, mood): courage/mood were swapped and player ignored; "Monster \"%s\" does not exist!", "Invalid player %d!", range errors. GAP: per-player attitude (player != -1) only stored; engine has one mood/courage per monster |
| SetMonsterSelectionType | objects/towns | 0x005ce490 | FIXED | monster check "Object \"%s\" is not exists or is not monster"; invalid type only logged ("Script error (SetMonsterSelectionType): ..."), not a script error |
| SetObjectDwellingCreatures | objects/towns | 0x005cd270 | FIXED | towns only; creature id range check; negative count errors (was clamped) |
| SetObjectEnabled | objects/towns | 0x005cde30 | FIXED | non-interactive objects (statics, stands) now error unless the name is a hero on the map. GAP: hero interaction flag only stored for map-placed hero objects; empty-name lookup ("" may hit an object in the exe's registry) not modelled |
| SetObjectOwner | objects/towns | 0x005cb910 | FIXED | player 0..8 ("Invalid player ID #%i"; negative was treated as neutral); flagged-object check ("is not a flagged object or hero" for monsters, statics...); "Can`t make hero \"%s\" neutral" for 0. GAP: players->vf18 silent refusal for heroes not traced |
| SetObjectPosition | objects/towns | 0x005cb1b0 | FIXED | "Object \"%s\" does not exist", empty-name error. GAP: 5th arg and the movability/occupancy failure reasons ("Can`t move object \"%s\": %s") not traced |
| SetObjectRotation | objects/towns | 0x005cb440 | FIXED | "does not exist" text, empty-name error. GAP: failure reasons of the rotate method |
| SetRegionAutoObjectEnable | objects/towns | 0x005e6ca0 | FIXED | all 7 args validated (mode 1..2, hero town type, player -1/1..8, enable -1..1) with exe texts. GAP: auto enable on enter/leave not implemented by the engine (stored only) |
| SetRegionBlocked | objects/towns | 0x005d9a50 | FIXED | player -1 = all players (stored per player so unblocking one after block-all works); 0 or out of range -> "Invalid player number #%i" (0 used to mean all) |
| SetStandState | objects/towns | 0x005d0570 | FIXED | "Stand name is not specified", "Stand \"%s\" is not exists", state index bounded by the shared's <States> count |
| SetTownBuildingLimitLevel | objects/towns | 0x005c7dd0 | FIXED | removed invented "Level (%d) is lesser than 0"; added "Limit (%i) is greater then maximum ..." check; building existence check |
| SetWarfogBehaviour | objects/towns | 0x005df9d0 | FIXED | 3rd arg player (-1 = local player, approximated by the current player); "No player!", "Specified player is not human player!"; stored per player. GAP: fog engine does not consume it |
| SiegeTown | objects/towns | 0x005d4460 | FIXED | hero must be on the map ("Hero \"%s\" is not exists", "is not belongs to any player"), "Can`t siege allied town", arena existence ("Arena \"%s\" is not exists"). GAP: arena not passed to the battle |
| StartCombat | objects/towns | 0x005d3b90 | FIXED | native-side parsing with all exe messages in order (string-or-nil types, count>0, creature type 0 allowed, amount message formatted with the amount as the exe does, script/arena existence); enemy hero must be a reserved hero of a player; allowQuickCombat stored. GAP: a reserved enemy hero is not resolvable by the battle engine |
| TransformTown | objects/towns | 0x005c9ee0 | FIXED | "Invalid town type #%i"; format "sn" added to NativeFormats. GAP: the exe recreates the town (only name/owner kept, TOWN_TRANSFORM effect); the engine only changes the type |
| UpgradeTownBuilding | objects/towns | 0x005e7a30 | FIXED | format "sn" (literal 0x00e1ed94) added; "Town name is not specified", "Town \"%s\" is not registered!", building existence. GAP: CUpgradeTownBuildingCmd internals (cost/limit handling) not traced |
| DisableCameraFollowHeroes | presentation/base | 0x005df710 | FIXED | n-format flags are numbers tested !=0 (truncated); we used Lua truthiness so 0 counted as true (ForPlayers form fixed too); passed to the map's camera-follow setting |
| MessageBox | presentation/base | 0x005db320 | FIXED | text now built in the native like FUN_006b5010 ({path; k=v}: <value=k> replaced recursively, number -> %d of trunc, string -> another text path, nil -> ""; missing '>' fails); explicit nil "No message passed!", unbuildable/absent "Cannot construct message text!", empty (missing/empty file) "Empty message text!"; callback = arg if string/number; UiRequest.Text carries the text. GAP: exe runs the callback as DoString("%s(%d,%d)", cb, X, -1) (X = 0 or screen player; MP path via CRunScriptCallbackCmd) - AnswerUi still runs cb.."()" (AdventureScripting.cs, not changed) |
| MessageBoxForPlayers | presentation/base | 0x005db910 | FIXED | player filter = truncated number else 0; same text checks/callback as MessageBox |
| MoveCamera | presentation/base | 0x005df150 | FIXED | x,y are truncated tile ints (were floats); noZoom/noRotate/fast are numbers !=0 (were always true); floor [0,1] "Invalid floor number #%i" then "Floor #%i is not exists"; zoom/pitch/yaw floats |
| MoveCameraForPlayers | presentation/base | 0x005df430 | FIXED | same as MoveCamera with the filter first |
| OpenPuzzleMap | presentation/base | 0x005e29f0 | FIXED | added "Invalid player id #%i" (p not in [1,8]) and "Player #%i is not exists"; arg1 passed on |
| OverrideAdvMapDialogPos | presentation/base | 0x005e3090 | FIXED | args are (dialog int, a1 int, x, y, z floats) -> +0x68(dialog, a1, {((float)(x-0.5)+0.5)*2, same for y, z}); we sent x=arg1,y=arg2. GAP: exe acts only when world vf+0x14 string is empty (likely single-player) - not modelled |
| Play2DSound | presentation/base | 0x005dc970 | FIXED | sound must resolve to an xdb whose root is Sound, else only the log line "Play2DSound: Sound \"%s\" was not found!" (no error); 2nd argument is never read (exe passes arg0's number slot = 0) |
| Play2DSoundForPlayers | presentation/base | 0x005dcf40 | FIXED | as Play2DSound (log text "Play2DSoundForPlayers: ..."); flag = arg2 != 0 passed |
| Play3DSound | presentation/base | 0x005dcb90 | FIXED | sound check (log only), floor checks, then exe quirk: "X is out of range (must be in [0,W-1], now is Y" when x<0 or y>=width, "Y is out of range ... now is Y" when y>=height; ints |
| Play3DSoundForPlayers | presentation/base | 0x005dd170 | FIXED | as Play3DSound with the filter first |
| PlayVisualEffect | presentation/base | 0x005e1710 | FIXED | effect must resolve (root Effect) else "Effect \"%s\" does not exist!"; named object must exist ("Object \"%s\" does not exist!") and supplies the floor; floor must exist else "Invalid floor (%d)!" (exe formats the floor-as-double with %d: low dword); 9th arg (a8) passed. GAP: world position/terrain height math left to the client |
| QuestionBox | presentation/base | 0x005db5f0 | FIXED | as MessageBox; yes/no callback = args 2/3 if string/number |
| QuestionBoxForPlayers | presentation/base | 0x005dbc30 | FIXED | as QuestionBox with truncated filter first |
| SetAmbientLight | presentation/base | 0x005dab00 | FIXED | added "Invalid floor ID #%i", "Floor #%i is not exists", "Light name is not specified"; fade time passed as trunc(time*1000f) ms when fading else 0 |
| SetObjectFlashlight | presentation/base | 0x005db020 | FIXED | object must exist ("Object \"%s\" is not exists"); "" removes the flashlight. GAP: "Light \"%s\" is not exists" needs the map's named-light list (not loaded by MapScriptData) |
| ShowFlyingSign | presentation/base | 0x005dc450 | FIXED | text checks as MessageBox (text resolved in native); object must exist "Object with name \"%s\" doesn't exist on adventure map"; player truncated, -1 = all, else [1,8] "Invalid player id #%i" and active "Player #%i is not active"; time taken only when > 0 (default 1.0). GAP: FUN_0054dfa0 transform of the text not traced |
| StartAdvMapDialog | presentation/base | 0x005e2e80 | FIXED | removed our invented "Adventure map dialog #%d is not exists" error: exe passes (index, callback) unchecked. GAP: same single-player condition as OverrideAdvMapDialogPos |
| StartCutScene | presentation/base | 0x005de340 | FIXED | scene must resolve to an AnimScene xdb else "CutScene %s not found". GAP: save under arg3 (FUN_006b5660) still left to the UI via SaveName |
| StartDialogScene | presentation/base | 0x005de0d0 | FIXED | scene must resolve to a DialogScene xdb else "Dialog \"%s\" is not exists"; save name as above |
| StopVisualEffects | presentation/base | 0x005e1bb0 | MATCH | queues the stop-by-tag command |
| _ERRORMESSAGE | presentation/base | 0x00caba10 | MATCH | logs "(Script) ERROR: " + msg (channel 9). Note: table 0x00f2def0 variant 0x0049c9b0 logs "Script error: " - not registered in our contexts |
| consoleCmd | presentation/base | 0x00cabca0 | FIXED | was a no-op (no ConsoleCommand handler set anywhere). Exe: FUN_00bcf150(text,3,-1,0) dispatches to console handlers immediately. Now "setvar NAME [=] VALUE" stores into process-wide BaseLibrary.ConsoleVars (defaults ai_time_limit=180000 ms, dev_console_password=""); MMH55 also issues bind/bindsection/unbind (key bindings), game_writelog, console_size (UI/logging only). GAP: ai_time_limit (exe 0x0086de82: GetTickCount − AI turn start > limit → phase 9 end turn) not consumed by our AI (lockstep determinism); exact console tokenizer (FUN_00c10f00) not traced |
| doFile | presentation/base | 0x00cabda0 | FIXED | missing file logs "Couldn't open file PATH", empty "File PATH is empty." (was "Can`t find script file: "); runs via DoString in a new thread at once, error stops only that thread (already so). Chunk name kept = path (exe: "DoString script") so saves keep re-linking |
| errorHook | presentation/base | 0x004bff20 | MATCH | exactly one arg; function sets / nil clears; texts match |
| mod | presentation/base | 0x00cabf00 | FIXED | error text "Divisor can`t be zero" (was invented); y==0 only (NaN computes); math matches |
| parse | presentation/base | 0x00cabb60 | FIXED | syntax error is reported via _ERRORMESSAGE and stops the calling thread with no result (was silent nil); collector check before load |
| print | presentation/base | 0x00cac850 | FIXED | formatter FUN_0087a180 reproduced: "[C-function]"/"[function]"/"[userdata]"/"nil", lua_isnumber values (numeric strings too) as %.16g, table pretty-print ("\n{\n", list part, [%.16g] keys, ",\n", ", ;\n", "}") |
| sleep | presentation/base | 0x004bfe80 | MATCH | already reproduced |
| sqrt | presentation/base | 0x00cac090 | FIXED | error text "Value under root can`t be negative" (was invented); NaN passes |
| startThread | presentation/base | 0x004bfe20 | MATCH | texts and behaviour match |
| AddCreature | combat | 0x00568d90 | FIXED | args side,type,count,x=-1,y=-1,summoned(b, default false),name; exe checks/texts "Invalid side #%i","Invalid creature type #%i","Invalid creature number #%i","Invalid cell (%i,%i)" (x/y -1 or in arena); now honours bool+name args (were ignored); invented duplicate-name error dropped. Exe posts the add as a command (applies later); ours applies immediately; placement for -1/occupied cells is ours (engine pick not traced) |
| Break | combat | 0x00567660 | MATCH | ICombatArena vf4 (0x00552980): posts a break command for the combat world and releases the arena's world ref; we emit break_combat (engine has no break-combat hook) |
| EnableAutoFinish | combat | 0x00567210 | MATCH | posts auto-finish(bool) command; sets AutoFinish |
| EnableDynamicBattleMode | combat | 0x00566b60 | MATCH | posts dynamic-battle(bool) command on the combat world; presentation event emitted |
| Finish | combat | 0x005673f0 | FIXED | side must be 0/1 else "Invalid side #%i" (was unchecked); posts finish command for that side's army; we end the battle with that side as winner |
| GetBuildingType | combat | 0x00568b60 | FIXED | error "Building \"%s\" is not exists" for non-buildings (was Creature/returned 0/1); returns BUILDING_* id (WALL1 GATE2 LEFT_TOWER3 BIG_TOWER4 MOAT5 RIGHT_TOWER6 MAGIC_WALL7). GAP: only towers are units in H5.Combat; walls/gate/moat are not named units |
| GetCreatureNumber | combat | 0x005686e0 | FIXED | only creature units (vf6c), else "Creature \"%s\" is not exists" (missing names gave "Unit ..." before) |
| GetCreatureType | combat | 0x005684b0 | FIXED | same creature-only lookup/error; returns creature id number |
| GetHeroName | combat | 0x00568290 | FIXED | non-hero now errors "Hero \"%s\" is not exists" (returned nil before); returns hero script name |
| GetHost | combat | 0x00567000 | FIXED | side check "Invalid side #%i" (returned 0 before); HUMAN 0 / COMPUTER 1 (no player → 1) |
| GetRageLevel | combat | 0x0056e710 | FIXED | any unit ("Unit ... is not exists"); no-rage unit → logs "Unit <n> has no rage points", returns 0 (no error). GAP: FUN_008a16b0 also checks army flag vf5c and hero vf290(0xac); we use creature + Stronghold hero |
| GetRagePoints | combat | 0x0056e4a0 | FIXED | as GetRageLevel (rage points vf2d0) |
| GetUnitManaPoints | combat | 0x00569570 | FIXED | any unit incl. heroes (vf234); heroes errored before (MMH55 reads hero mana) |
| GetUnitMaxManaPoints | combat | 0x00569790 | FIXED | any unit incl. heroes (vf238) |
| GetUnitPosition | combat | 0x00568060 | GAP | any unit, returns x,y of unit vf 0x40; for CCombatHero that is the unit-base cell getter 0x00a6a040 (via thunk 0x00bc2a80), the same field pair creatures use. GAP: where the arena places the hero (the writer of that cell) is not located (COMBAT.md "hero / war machine cells"); we return -1/width, height/2 |
| GetUnitSide | combat | 0x005677a0 | FIXED | unchanged logic; dead stacks now resolvable (exe keeps dead units in the world; UnitDeath handlers call GetUnitSide on them) |
| GetUnitType | combat | 0x005679f0 | FIXED | HERO0 CREATURE1 WAR_MACHINE2 BUILDING3 SPELL_SPAWN4: siege buildings 3, spell-spawned objects 4 (were: all obstacles 3, phantom clones 4) |
| GetUnits | combat | 0x00567c70 | FIXED | "Invalid side id #%i" / "Invalid type id #%i" (0..4) checks added; type 0 = army hero; order = army unit list (we use unit order, alive only — dead-stack membership in army list not traced) |
| GetWarMachineType | combat | 0x00568930 | FIXED | war-machine-only lookup, error "War machine \"%s\" is not exists" |
| SetControlMode | combat | 0x00566d70 | FIXED | texts "Invalid side #%i", "Invalid control mode #%i" (0..2), "Side #%i player is not a human" (AI side) added; mode≠0 → arena vf8(player, mode==2), always vfc(player, mode≠0) |
| SetUnitManaPoints | combat | 0x005699b0 | FIXED | any unit incl. heroes; mana<0 → "Invalid mana points number %i"; exe posts a set-mana command (async, value unclamped) — we set immediately |
| SummonCreature | combat | 0x00569180 | FIXED | same body as AddCreature with summoned default true |
| UnitCastAimedSpell | combat | 0x0056a080 | FIXED | caster/target any unit incl. hero ("Unit \"%s\" is not exists"); refused cast → "Unit \"%s\" can`t cast spell #%i on unit \"%s\"" (was only logged). GAP: exe validates via FUN_00982010 and posts a free cast; we route through Battle.Apply, which may refuse out-of-turn casts |
| UnitCastAreaSpell | combat | 0x00569e60 | FIXED | same; refused → "Unit \"%s\" can`t cast spell #%i at (%i,%i)" |
| UnitCastGlobalSpell | combat | 0x00569c30 | FIXED | same; refused → "Unit \"%s\" can`t cast spell #%i" |
| combatReadyPerson | combat | 0x0056cd60 | FIXED | returns nothing while finished (vf98) or not started (vf124); else ready unit name (vfbc(0)) |
| combatSetPause | combat | 0x0056d3c0 | MATCH | posts pause(bool) command; presentation/flow flag |
| combatStarted | combat | 0x0056cbd0 | MATCH | !world vf124 (preparation/tactics) ~ our Started (set at DoStart) |
| commandDefend | combat | 0x0056b950 | FIXED | arena lookup error "There is no unit on combat arena with name \"%s\""; then arena vf 0x2c (0x00566090). The bool (b[false]) of all command* natives (arena vf 0x2c..0x3c: 0x00566090, 0x005661f0, 0x00566300, 0x00566580, 0x00566460): false first posts CSetATBValue(unit, 1.0) (Execute 0x004e0970 → unit vf 0x18c), then the action command — now done for commandDefend/Move/MoveAttack/Shot/DoSpecial/DoSpell. GAP: the exe's action commands (CDefendCreatureCmd 0x004e0280) act on the unit without a turn check; ours go through Battle.Apply (legal on the unit's turn only) |
| playAnimation | combat | 0x0056d170 | FIXED | unknown unit → "There is no unit on combat arena with name \"%s\""; action must be 0..5 else "Non valid action type %d"; posts animation command (event) |
| removeUnit | combat | 0x0056b1d0 | FIXED | unknown name only logged "There is no unit on combat arena with name <n>" (no error; we threw before); else posts remove command. GAP: removing a hero not modelled (event only) |
| setATB | combat | 0x0056cf60 | FIXED | arena lookup error text "There is no unit on combat arena with name \"%s\""; posts ATB(number) command (we set immediately) |
| EnableCinematicCamera | combat | 0x0056e370 | FIXED | added (was missing): ICombatArena vf0 = 0x00552870 sets CCombatArenaScreen+0x4cc (ctor default 1), read only at 0x0054b2f0 as a flag to the combat-event presentation sequencer → camera only; emits cinematic_camera event |
| GetGameVar | combat | 0x0056a3f0 | FIXED | empty name → "Game variable name is not specified"; missing → ""; table = arena +0x2e8 (ICombatArenaCommon vf40) = adventure table shared by reference |
| SetGameVar | combat | 0x0056a660 | FIXED | empty name → "Game variable name is not specified"; empty value removes the variable (table vfc), else set (vf8) |
| EnableHeroAI | AI/hiring | 0x005da500 | FIXED | errors "Hero name is not specified"/"Hero \"%s\" is not exists"/"…is not belong to AI player" (owner human); HeroState.AiEnabled; AI skips disabled heroes (GAP-detail: the gate at 0x008698ba is world vf 0x100(hero) → vf 0x44 in the hero picker FUN_00869770, not proven to be this flag; docs/rules/AUDIT_AI.md), turn rule FUN_0098e410 reads it; garrison in/out readers 0x00868768/0x008699f8 n/a (our AI never garrisons heroes); end-of-turn gate 0x0098e0c8 untraced |
| SetAIHeroAttractor | AI/hiring | 0x005dfe10 | FIXED | errors in exe order (priority range, Hero '%s' not found, Object not found); FUN_00b4e880 list semantics (overwrite/remove/append) on HeroState.AiAttractors; read in AI destination scoring (FUN_00bc5740: -1 −SearchNegativeAttractorValue, 1 +SearchPositiveAttractorValue, both now loaded from DefaultStats); priority 2 sets result state 3, which outranks every lower-state destination in the choice FUN_00a7b630 (AUDIT_AI.md, AiAuditTests); GAP-detail: whether state 3 skips the danger checks is not traced |
| SetAIPlayerAttractor | AI/hiring | 0x005e00e0 | FIXED | errors priority/"Invalid player id #%i"/"Player #%i is not active"/object; PlayerState.AiAttractors, used when the hero has no non-zero entry |
| EnableAIHeroHiring | AI/hiring | 0x005da7a0 | FIXED | errors invalid id/not exists/"is human"/"Town name is not specified"/"Town \"%s\" is not exists on map"; PlayerState.AiHiringDisabledTowns read by AI hiring (FUN_0086aa50 vf 0x54) |
| DenyAIHeroFlee | AI/hiring | 0x005e22b0 | FIXED | errors hero empty/not exists; FUN_0042f660 add/remove enemy name ("" = any) in HeroState.AiNoFleeFrom; reader FUN_00c972d0 ported as Adventure.AiFleeDenied → BattleState.RetreatDenied for the AI side (our combat AI has no flee decision, so the gate is the enforcement point) |
| DenyAIHeroesFlee | AI/hiring | 0x005e2760 | FIXED | errors invalid id/not exists; PlayerState.AiNoFleeFrom, checked after the hero list when flee control is off (0x00c9741e) |
| SetAIHeroFleeControl | AI/hiring | 0x005e2510 | FIXED | errors hero empty/not exists; HeroState.AiFleeControl (settings +0x2c) makes the hero ignore the player's list |
| BlockTownGarrisonForAI | AI/hiring | 0x005e3aa0 | FIXED | errors "does not exist!"/"is not town!"; TownState.AiGarrisonBlocked stops the AI's garrison exchange (TakeGarrison; exe reader 0x008687a0); GAP: empty name null-derefs in exe, ignored here |
| AllowHeroHiringByRaceForAI | AI/hiring | 0x005e3d40 | FIXED | errors invalid id/not exists; humans silently ignored; FUN_00429ff0 (0..7 one race via FUN_00af10f0, else all: allow clears, deny appends all with duplicates); PlayerState.AiHireFilter read by AI hiring with FUN_00c069d0 |
| AllowHiringOfHeroForAI | AI/hiring | 0x005e4000 | FIXED | errors invalid id/not exists; humans ignored; FUN_0042a040: -1 clears both lists, 0 deny, other allow (overrides race) |
| AllowHeroHiringByRaceInTown | AI/hiring | 0x005e4270 | FIXED | errors "Object \"%s\" does not exist!"/"…is not a town!"; TownState.HireFilter; hides rejected heroes in that town's tavern for every player (FUN_00636a10: second offer shifts to slot 0), HireHeroCmd slot indexes the shown list, AI hiring checks it too |
| AllowHiringOfHeroInTown | AI/hiring | 0x005e4530 | FIXED | same errors/filter as above, hero mode -1/0/other |
| AllowPlayerTavernRace | AI/hiring | 0x005e47f0 | FIXED | errors invalid id/not exists/"Invalid town type %d!"; FUN_0042fb70 on PlayerState.TavernFilter; GAP: exe reader of player +0x644 untraced — applied when the player's tavern offers are rolled, and offers it now rejects are re-rolled at once |
| AllowPlayerTavernHero | AI/hiring | 0x005e4ac0 | FIXED | errors invalid id/not exists; FUN_0042fc40: 0 denied, 1 allowed despite race, other neither; same GAP as AllowPlayerTavernRace |
| SetPlayerHeroesCountNotForHire | AI/hiring | 0x005d11c0 | FIXED | errors "Player ID must be 1 through 8"/"Invalid heroes count, must be 0..%d" (max FUN_0098de60 = 8 = MaxHeroesPerPlayer); PlayerState.HeroesNotForHire, reader traced: hire check FUN_004f4570 / AI FUN_0086b0a0 allow hiring while heroes < max − count (DoHire); GAP: missing player crashes exe, ignored here; map-setting caps in FUN_0098de60 not modelled |
| DoNotGiveTurnToPlayerAIIfNoTownsAndActiveHeroes | AI/hiring | 0x005e7750 | FIXED | errors "Incorrect player ID #%i"/"Player #%i is not exists"; PlayerState flag; turn order now uses FUN_0098e410 (Adventure.GetsTurn): towns → turn, no towns & no heroes → no turn, flagged AI without towns → turn only with an AI-enabled hero (BL/vf 0x190 parameter untraced) |
