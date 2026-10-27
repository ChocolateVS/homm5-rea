# Simultaneous turns (network adventure games)

Reverse-engineered from `H5_Game.exe` (TotE 3.1, Ghidra 12.1.4; project copy
`tools/ghidra-projects/simul`, scripts and helpers in `tools/simul`). Texts from `data/texts.pak`.
Implementation: `src/H5.Adventure/Engine/Adventure.Simultaneous.cs` (areas, turn order hook),
`src/H5.AdvNet/SimultaneousTurns.cs` + `AdventureNetSession.cs` (phase, conflicts, approvals, concurrent battles §8),
client `game/src/AdvNet/*` and `game/src/Adventure/AdvHud.cs`.

## 1. The option

| Item | Exe evidence | Notes |
|---|---|---|
| Lobby checkbox `simultaneus` on UI/MPCreate (text `UI/MPCreate/Options/simultanious.txt` "Simultaneous Turns", tooltip `SimultaneousTurns_tooltip.txt` "Selecting this option will enable simultaneous turns") | reader `0070862c` binds the `simultaneus` button (`+0x3c`) | Only the network create screen has it; `UI/HotSeat` has no such control (pak search), so hot-seat games never use simultaneous turns. |
| Room setting key `simultaneus_turns` = `on`/`off` | `00712d40` (room settings writer, next to `ghost_mode`, `quick_combat`, `dynamic_battles`, `animation_speed`, `autosave`) | No day range or end date exists: it is a plain on/off flag. |
| Lobby game info "SimultaneousTurns: On/Off" | `007022d0`; tooltip `UI/Tooltips/Lobby/SimultaneousTurns.txt`; MPWait `TEXTS/simultaneus_turns.txt` "Simultaneous Turns:" | |
| World settings block at `CWorld+0x88` (copied by `00999f60`, applied by `CWorld` vtable slot 6 `0098f740`) | `+0x88` network-game flag, `+0x89` second mode flag, `+0x8a` ghost mode, **`+0x90` simultaneous turns active** | `0098f740`: if `+0x88 == 0` or `+0x89 != 0` the simultaneous flag is cleared. `CWorld::GetSettings` (slot 5 `0098c470`) returns `this+0x88`, so every `settings+8` test in the code reads `CWorld+0x90`. |
| Never re-enabled | writes to `CWorld+0x90`: only `0098f782` (settings init) and `0098fa6f` (break) | Once broken, the rest of the game is sequential. The flag is part of the saved world settings (`00999f60`), so a loaded game keeps it. |

## 2. Turn order (`GiveTurnToNextPlayers` `00998060`, `StartTurns` `0098f7a0`, world tick `00998520`)

Player turn states (`CPlayer` vf `+0x44/+0x48`, field `+0x634`, debug letters in `0098f950`):
0 = current (`C`), 1 = waiting (`W`), 2 = ghost (`G`).
`0098e410` = player alive (state 1 and has a hero or town) of a given kind (vf `+0x190` IsHuman; `+0x184` = controller field `+0x74 == 0`).

* Order is **all human players first, then the computer players one by one, then a new day**
  (`00998060`: next human after the current one; none left → first AI; next AI; none left → new day and
  the first human). In simultaneous mode (`+0x88 && +0x90`) there is no single current human: every
  alive human gets state 0 at once (`0098f7a0`: `cVar4` stays 0 while `+0x90` is set), and the next
  player after the human phase is the first AI.
* A human ending the turn (`CWorld` slot 52 `00998760`) only sets its "end requested" flag (vf `+0x50(1)`).
  The tick `00998520` then moves every current player whose flag is set to state 1 (or 2 with ghost mode,
  recording the time) while others still play (`FUN_0098d570` = "a current player has not ended").
  When all current players have ended, `GiveTurnToNextPlayers` runs.
* Ended players see the enemy-turn box with `T_WAIT_FOR_PLAYERS`
  (`UI/AdventureScreen/EnemyTurnBox/WaitForPlayers.txt` "Wait for other players, please").
* Turn timer: one turn start time (`CWorld+0x104`) for the whole phase, but each current player runs out
  on its own: the tick (`00998520`) ends the turn (slot 52) of a current player whose time left
  (`CWorld` vf `+0x1c4` = `0098e4e0`) is 0. Time left = limit (`CWorld+0x94`) - max(0, now - turn start -
  the player's combat credit (`CPlayer` vf `+0x5c`, section 8)); for a player that is not current it is 0 in
  sequential turns and uses the largest credit of the alive humans in simultaneous turns. A current player
  that holds a combat is skipped: its end flag is cleared instead (vf `+0x50(0)`), so the phase cannot
  pass while it fights. (vf `+0x1c8`/`+0x1cc` = `CWorld+0x184` and a 5000 ms countdown from `+0x180`
  (`0098c4c0`/`0098c4d0`): a separate path that passes the turn at once.)
* Loading a network save restarts turns (`00997f60` → `0098f7a0`) and adds 20000 ms to the turn timer.

## 3. Areas and the conflict check (`CWorld` vtable slot 7 `009c2660`)

Skipped while `CWorld+0x84` is set (slot 78 `00c61b10`; set to 1 around multi-step operations such as
`008719f0`/`00871b70` and restored afterwards). A per-floor grid of player ids is cleared, then:

1. **Heroes** (`CWorld+0x5c` list): for every hero whose owner is a playing human, a pathfinder flood
   (`FUN_00c08be0`; mode `FUN_00c076d0(6,0)`) from the hero's tile with limit
   **hero max daily movement (vf `+0xfc`) + max(0, `VisitFountainOfYouthMovementBonus`,
   `VisitRallyFlagMovementBonus`, `VisitOasisMovementBonus`, `VisitStablesMovementBonus`,
   `LighthouseMaxSeaMovementBonus`)** (RPGStats `adventure` +0x18/+0x1c/+0x20/+0x24/+0x2c; reader
   `0095e8f0`; `ForWeekStablesMaxLandMovementBonus` +0x28 is skipped). Flags: in a boat (vf `+0x24`),
   flying with `ANGEL_WINGS` (artifact 0x1e, equipped or carried), water walking with
   `BOOTS_OF_LEVITATION` (0x36) when not flying.
2. **Towns** (player list `+0x98` of each playing human): a flood from the town (vf `+0xc4`) with limit
   = the largest max movement among the heroes of the `CWorld+0x40` list (`FUN_00ce78c0`).
3. `FUN_009c20a0` marks every tile whose path cost ≤ limit with the player id; a tile already marked by
   **another player** is a conflict (debug log "Simultaneous Turns: area of player A was intersected at
   x y floor by player B"). Players are compared by id, not team: allies conflict too. AI players have
   no areas.
4. `FUN_009c2380`: for every map object owned by a playing human, any footprint tile marked by another
   player is a conflict; likewise for each object of the `CWorld+0x174` list (owned objects updated each
   turn by `00995f40`) on a tile marked by another player.

## 4. When it is checked and how it breaks

Two break paths:

* **Immediate**: `CWorld` slot 8 `0098fa60` (clear `+0x90`, restart turns sequentially with
  `0098f7a0`: the first human gets the turn, other humans wait, end-turn flags are cleared) followed by
  `FUN_009c22f0(approve=0, message=1)`.
* **Deferred** (`009c25c0`, also `CBreakSimultaneousTurnsCmd` slot 7 `00426970` and console command
  `break_simultaneous_turns` → `0058cc90`/`00602ed0`): every playing human gets the "approval pending" flag
  (`CPlayer` vf `+0x58(1)`, field `+0x21d`), then `FUN_009c22f0(1, 1)`. Each client answers with
  `CApproveSimultaneousConflict` (ctor `005aa0c0`, execute `004289a0`: clear own flag; if no playing human
  is still pending (`009c2270`) → slot 8 break + `FUN_009c22f0(0, 0)`).

`FUN_009c22f0(a, b)` sends `CEventSimultaneousConflict` (ctor `009c2dc0`, fields `+8 = a`, `+9 = b`) to
every playing human. Client handler (`005a5d90` case 0x21): if `a`, queue an approve command at once;
otherwise `FUN_00595cc0`; if `b`, show the message box (`FUN_00679150`) with
`T_SIMULTANEOUS_TURNS_FINISHED_HEADER` "Simultaneous turns finished" /
`T_SIMULTANEOUS_TURNS_FINISHED_TEXT` "Player’s areas of interests have intersected. Game will switch to a
standard turn based mode now." (`UI/AdventureScreen/EnemyTurnBox/Texts.(WindowRelatedTexts).xdb`).

| Trigger | Site | Path |
|---|---|---|
| Turns start (game start / load) | `0098f7a0` end | immediate |
| Humans' phase of a new day begins | `00998060` end (no current AI) | immediate |
| `SPELL_SUMMON_BOAT` (0x31) cast | `008656e0` → `00865af2` | immediate |
| `SPELL_DIMENSION_DOOR` (0x32) cast | `008656e0` (requires `!CWorld+0x84`) | deferred |
| Ship built at a shipyard | `CBuildShipCmd` slot 7 `0042de80` → `0042e083` | immediate |
| `ANGEL_WINGS` / `BOOTS_OF_LEVITATION` put on | `CHero` slot 33 `00b4b610` | immediate |
| Adventure battle finished | `CCombat` slot 55 `00af6680` → `00af35f0` (needs `!CWorld+0x84`, `!combat vf+0x108`, participant vf `+0x1c` or `+0x50` non-zero) | deferred |
| Town Portal, hero hire, ordinary movement | — | no check (areas already bound a whole day's movement) |

## 5. H5Reborn design

* The host session keeps the exe state: `Active` (`CWorld+0x90`), the humans' phase, per-human
  ended/pending flags. During the phase the engine's `CurrentPlayer` is switched to the human whose
  command is applied; each side's redacted view shows itself as current while it still plays
  (`ViewFilter.ViewFor(currentOverride)`), so the rest of the client works unchanged.
* `Adventure.TurnOrder` hook gives the exe order (humans, then AIs, then a new day) in games created
  with simultaneous turns.
* Break policy seam: `ISimultaneousPolicy.BreakOnIntersection`. `OriginalSimultaneousPolicy` (stock
  `H5_Game.exe`) breaks as above. `PestSimultaneousPolicy` = `H5_Game_PEST.exe` (§7): never breaks and never
  computes areas. Chosen by the host's target exe (`AdvNetSettings.Pest` from `H5.Core.IO.TargetExe.Pest`: PEST with
  an MMH55 install, since the user's 5.5 setup launches `bin/H5_Game_PEST.exe`; on the stock 3.1 target too unless the
  host turned off the H5Reborn option "Uninterrupted simultaneous turns" (§7.4, user-requested deviation); `H5_EXE=pest|stock`
  overrides). It is not a lobby control: the stock two-state checkbox is unchanged. `H5_ADVNET_SIMUL=1` ticks the
  checkbox for screenshots.
* Concurrent battles (§8): the session keeps a list of running battles. A battle's engine `PendingBattle` is parked
  in its entry while it runs (the engine stays free for the other humans) and put back, with the attacker made
  current, to apply the result when it ends. Pending choices are parked per player during the phase. Each side's
  view and events are built with its own battle and choice in the engine's slots (`AdventureNetSession.ViewFor`).
  The attack gate is the engine hook `Adventure.AttackRefused`, set by the session.

## 6. Audit ledger

| # | Item | Status | Evidence / note |
|---|---|---|---|
| 1 | Lobby checkbox enabled on the network create screen, off by default | FIXED | `0070862c`, UI/MPCreate `simultaneus` (was disabled) |
| 2 | Not offered for hot-seat | MATCH | UI/HotSeat has no control |
| 3 | Room setting `simultaneus_turns` on/off, no day limit | FIXED | `00712d40` |
| 4 | Humans move concurrently; AIs afterwards in order; then new day | FIXED | `00998060`, `0098f7a0` |
| 5 | Ending the turn in the phase only marks the player; phase ends when all ended | FIXED | `00998760`, `00998520` |
| 6 | Ended player sees "Wait for other players, please" | FIXED | `T_WAIT_FOR_PLAYERS` |
| 7 | One turn start for the phase; each current human times out on its own time (combat credit), a fighting one never | FIXED | `00998520`, `0098e4e0` (§2) |
| 8 | Hero area = max daily MP + max movement bonus, pathfinder flood | FIXED | `009c2660`, `0095e8f0` |
| 9 | Boat heroes flood on water | FIXED | vf `+0x24` flag |
| 10 | Angel Wings flight / Boots of Levitation water walk in the area flood | GAP | engine has no flying/levitation movement |
| 11 | Pathfinder mode 6 details (which objects block the flood, guards, fog) | GAP | `FUN_00c076d0(6,0)` not decoded; flood uses the engine's normal passability and does not pass guarded tiles |
| 12 | Town areas, limit = max MP of `CWorld+0x40` heroes | GAP | list identity unresolved; implemented with the hero pool (`AdvState.HeroPool`) and town entrance as start |
| 13 | Owned map objects' footprints inside another area | FIXED | `009c2380` (blocked + active tiles used as footprint) |
| 14 | `CWorld+0x174` owned objects | GAP | list identity unresolved; not checked |
| 15 | Allies conflict too (ids, not teams); AI players have no area | FIXED | `009c20a0`, `0x184` filter |
| 16 | Check at phase start (game start, every new day) → immediate break | FIXED | `0098f7a0`, `00998060` |
| 17 | Summon Boat → immediate | FIXED | `00865af2` |
| 18 | Dimension Door → deferred | FIXED | `008656e0` case 0x32 |
| 19 | Build ship → immediate | FIXED | `0042e083` |
| 20 | Equip Angel Wings / Boots of Levitation → immediate | FIXED | `00b4b610` |
| 21 | Battle end → deferred | GAP | applied after every adventure battle; the participant condition (vf `+0x1c`/`+0x50`) is not identified |
| 22 | Immediate break restarts turns: first human current, others wait, end flags cleared | FIXED | `0098fa60` → `0098f7a0` |
| 23 | Deferred break: pending flags, clients auto-approve, switch when none pending | FIXED | `009c25c0`, `004289a0`, `009c2270`, `005a5d90` |
| 24 | Message box texts | FIXED | `T_SIMULTANEOUS_TURNS_FINISHED_*` |
| 25 | Never resumes after a break | MATCH | only two writers of `+0x90` |
| 26 | Break during the computer phase restarts the humans' turns in the exe (`0098f7a0` unconditionally) | GAP | we only clear the flag then; restarting a finished human phase mid-AI turn is not reproduced |
| 27 | Ghost mode during the phase (ended humans become ghosts, state 2) | GAP | ghosts only act in sequential turns |
| 28 | Console command `break_simultaneous_turns` | GAP | dev console not built |
| 29 | Saves keep the broken state; load restarts turns + 20 s | GAP | network games cannot be saved yet |
| 30 | `CWorld+0x84` suppression during multi-step operations | MATCH | our commands are atomic; checks run after each command |
| 31 | General sequential turn order "humans first" in non-simultaneous games | GAP | engine still uses map player order there |
| 32 | PEST exe (5.5 target): simultaneous turns never break; no area check at any trigger | FIXED | §7, `PestSimultaneousPolicy` |
| 33 | Other humans keep moving while one human of the phase fights a battle; several battles at once | FIXED | each player holds its own combat (`CPlayer` vf `+0x8c` = `CPlayer+0x24`, set by vf `+0x88` `004fdb00`), §8. `ConcurrentBattleTests` |
| 34 | A pending adventure choice of one human (e.g. a chest) does not block the others | FIXED | offers are interface events sent to the visiting player only (join offers `008e1860`/`008e1a80` -> `00a7d1d0` -> `00a7d0e0(player, event)`), answered by that player's command (`CJoinCreaturesCmd`, `CRefuseJoinMonsterCmd`, `CTakeChestCmd`); no world-level slot. Choices are parked per player in the phase; the owner cannot end its turn before answering |
| 35 | PEST active only in network games with the option ticked; no effect in hot-seat / single player | MATCH | every patched site is behind `settings+8` (`CWorld+0x90`), which `0098f740` clears unless `+0x88` (network), §7.2 |
| 36 | PEST: `break_simultaneous_turns` / `CBreakSimultaneousTurnsCmd` still break (deferred `009c25c0`, approvals, slot 8: not patched) | GAP | dev console not built (#28) |
| 37 | Simultaneous turns: an attack is refused when the target's owner has a combat (vf `+0x8c`/`+0x90` non-null) or the target is the side-`+0x90` army of any player's running combat (`00426b30` from `00433440`) | FIXED | owner = hero owner / town owner / object owner (`00426aa0`); defender = `CCombat+0x90` (vf `+0x84(false)` `00af2140`). A refused attack does nothing: the hero stays where its move stopped (hero meeting, neutral encounter incl. guard zones, town, guarded object, declined join). Only while `CWorld+0x90` is set |
| 38 | Heroes of two humans meeting / attacking during the phase: ordinary attack, interactive battle for both humans (`00432d30`: interactive when either side is human and `CWorld` vf `+0x138` is false, else quick) | MATCH | `NetBattle` gives each human side to its player; the stock engine does not block or queue contact |
| 39 | Two humans heading for the same object / town: commands apply in the order the host receives them | MATCH | no extra rule in the exe's command path; the session applies commands in arrival order |
| 40 | The humans' phase does not pass while a human of it fights; the others may end and wait | FIXED | the tick `00998520` clears the end flag of every current player holding a combat (vf `+0x8c` -> vf `+0x50(0)`), so "all current ended" (`0098d570`) waits for it. (Earlier reading corrected: `0098e150` tests players, not combats, #50) |
| 41 | Both side owners hold the combat; each participant gets its combat screen | FIXED | `008545b0`: attacker-army and defender-army owners (`CCombatArmy` vf `+0x4`) get vf `+0x88(combat)` and a `CCreateCombatScreen` / `CCreateQuickCombatScreen` sent with `00a7d0e0`. Neutral defenders have no owner |
| 42 | A player in a combat (attacker or defender) gives no adventure commands meanwhile | FIXED | its client is on the combat screen; the host refuses ("a battle is in progress"). A human whose turn has ended may still be attacked and fights |
| 43 | Adventure state of the fighting hero and the defender is unchanged until the result | MATCH | results are applied only by `CFinishCombat` (execute `004e0520` -> `CCombat` slot 55 `00af6680`); others keep seeing the hero where it stopped and the defender in place |
| 44 | Results of concurrent battles apply in the order the battles end | MATCH | each end is its own `CFinishCombat` command in the command stream |
| 45 | Turn time left per player, the combat credit counted (§2) | FIXED | `0098e4e0`; `turn.msLeft` is per side in the phase |
| 46 | Combat credit: `CCloseCombat` (`004e0f10`) clears each owner's combat and, if the combat began in this turn (`CCombat+0xd0` == `CWorld+0x12c`), adds duration (`00af1c00`) minus the side's own acting time (`CCombatArmy+0x7c`, added each update for the active army by vf `+0x130`, `00af5930`) | FIXED | `NetBattle.ActingMs`; duration from the battle start to its end |
| 47 | Second combat reference `CPlayer` vf `+0x90` (`CPlayer+0x28`, set by vf `+0x94`) | GAP | identity not resolved (quick-combat result screen likely); the gate uses running battles only |
| 48 | `00423d00`: attack refused when the target's (or attacker's) object state vf `+0x6c` is 2 or 3 | GAP | state meaning not identified |
| 49 | Reconnect / resume mid-battle | FIXED | the replayed side updates carry the battle start and every battle update; the rebuilt replica matches the host (`ReconnectingClientRebuildsTheRunningBattle`) |
| 50 | The tick does nothing while `CWorld+0x54` (vf `+0xc` `0098c490`) is set or a player of the `CWorld+0xec` list has status vf `+0x6c` == 2 (`0098e150`; `0098e010` walks the same list's heroes) | GAP | neither the flag nor the status is identified (a paused / reconnecting player is likely); not reproduced |
| 51 | What other clients show of a hero in a combat (marker, animation) | GAP | not traced; the view shows it unchanged (#43) |
| 52 | Combat credit details: where the credit is reset, `CWorld+0x12c` identity, tactics phase, `CCloseCombat` timing | GAP | reset at each turn start; "this turn" = the current turn/phase; tactics time counts for nobody (no active army); credited at the battle end, the exe credits when each human closes its result screen (`CCloseCombat+0x10` player) |

| 53 | PEST behaviour on the stock 3.1 target (H5Reborn option "Uninterrupted simultaneous turns", default on) | EXT | user-requested deviation, not exe behaviour: the unpatched 3.1 exe breaks (#32 is the 5.5 target), §7.4 |

Counts: MATCH 8, FIXED 28, GAP 16, EXT 1 (53 items; EXT = user-requested extension, not original behaviour).

## 7. H5_Game_PEST.exe, "permanent endless simultaneous turns" (the 5.5 target)

The user's install `HoMM 5 - Tribes of the East (5.5)` is launched with `bin/H5_Game_PEST.exe` = stock GOG 3.1
`H5_Game.exe` with **16 bytes changed** (`cmp -l`) and no MMH55 DLL. Addresses: VAs in the stock exe (`.text` file
offset = VA - 0x400c00). Ghidra copy `tools/ghidra-projects/pest`, scripts `tools/pestre`.

### 7.1 The bytes

| File offset (cmp, 1-based) | VA | Stock -> PEST | Function | Effect |
|---|---|---|---|---|
| 185459 | `0042e072` | `74 14` JZ -> `EB 14` JMP | `CBuildShipCmd` execute `0042de80` | after a ship is built, `if (settings+8 && world->vf1c()) { vf20(); 009c22f0(0,1); }` is skipped: no area check, no immediate break |
| 4607430-33, 4607435 | `008659c5` | `0F 84 6F 05 00 00` JZ rel32 -> `E9 70 05 00 00 90` JMP rel32 + NOP (5 bytes differ) | adventure spell cast `008656e0`, case `SPELL_DIMENSION_DOOR` (0x32) | after the teleport, `if (settings+8 && !vf138() && vf1c()) { 009c25c0(); return; }` always jumps to `00865f3a` (the same epilogue): no deferred break |
| 4607716 | `00865ae3` | `74 7F` -> `EB 7F` | same function, case `SPELL_SUMMON_BOAT` (0x31) | immediate break after the boat is summoned skipped |
| 5827850 | `0098f909` | `74 34` -> `EB 34` | `StartTurns` `0098f7a0` | no check/break when turns (re)start with `+0x90` set (game start, load) |
| 5863672 | `009984f7` | `74 13` -> `EB 13` | `GiveTurnToNextPlayers` `00998060` | no check/break when the humans' phase of a new day begins |
| 6036065 | `009c2660` | `81` (`SUB ESP,0x174`) -> `C3` RET | `CWorld` vtable slot 7 (`00e62628`), the area check | returns at once with an undefined AL; harmless because every caller now ignores it. No areas are computed |
| 7285349-53 | `00af3664` | `E8 57 EF EC FF` CALL `009c25c0` -> 5 x `90` | `00af35f0` (from `CCombat` slot 55 `00af6680`, battle end) | no deferred break after an interactive adventure battle |
| 7645909 | `00b4b6d4` | `74 12` -> `EB 12` | `CHero` slot 33 `00b4b610` (artifact equipped) | no break when `ANGEL_WINGS` (0x1e) / `BOOTS_OF_LEVITATION` (0x36) are put on |

These are all callers of the check (scan of every `CALL [reg+0x1c]; TEST AL,AL` site and of every reference to
`009c22f0`, `009c25c0`, `0098fa60`); at `00af365b` the call stays but the break call after it is NOPed. Not patched:
`CWorld` slot 8 `0098fa60` (the break itself), `009c25c0`/`009c22f0`, and their remaining caller
`CBreakSimultaneousTurnsCmd` (`00426970`): the console command `break_simultaneous_turns` still ends simultaneous
turns (deferred, with approvals). Nothing writes `CWorld+0x90`: PEST does not switch simultaneous turns on, it only
keeps them on once the lobby option is ticked.

### 7.2 Where it applies
Only network games created with "Simultaneous Turns": `0098f740` clears `+0x90` when `+0x88` (network game) is 0 or
`+0x89` is set, and every patched site sits behind `settings+8` (= `CWorld+0x90`). Hot-seat (no checkbox, §1) and
single player are unaffected.

### 7.3 Contact during the endless phase: the stock engine's own rules
With the check gone nothing ends the humans' phase, so contact that the stock game avoids by breaking now happens
inside it. Traced in the stock exe:
* **Attack** (`CAttackHeroCmd` execute `004338f0` / `CAttackHeroExCmd` `00433990` -> `00433440(target, world)`):
  first `00426b30(world, target)`, only while `settings+8` is set, refuses the attack (returns 0, nothing happens)
  if the target's owner (`00426aa0`: hero owner, town owner via vf `+0xbc`, or `world->GetPlayer(obj->vf4())`) has a
  combat (`CPlayer` vf `+0x8c`) or a second combat reference (vf `+0x90`), or if the target is the army on side
  `vf+0x84(false)` (`CCombat+0x90`) of any player's running combat (`CWorld+0x24` player list). Then `00423d00`
  refuses targets whose state (vf `+0x6c`) is 2 or 3. Otherwise the ordinary battle starts (`00432d30`); it is
  interactive when either side is human and `CWorld` vf `+0x138` is false (`combat vf+0x10c`).
* So heroes of two humans meeting or attacking each other fight an ordinary human-versus-human battle; nothing is
  blocked or queued. The per-player combat reference and the gate above show that the stock engine lets battles run
  while the other humans keep moving (§8).
* Captures and races for objects: no extra rule; commands apply in arrival order.
* Timers: unchanged (§2): one turn start, per-player time with combat credit; the phase waits for running combats (#40).
* AI: unchanged, computer players move after every human ended (§2).
* Ghost mode: unchanged (#27).

### 7.4 PEST on the stock 3.1 target (user-requested deviation, not exe behaviour)
The user asked for full simultaneous turns in both games H5Reborn can run (startup choice "Heroes V: Tribes of the
East 3.1" / "MMH 5.5", `game/src/FrontEnd/TargetPrompt.cs`). The stock 3.1 exe breaks simultaneous turns on contact
(§3); H5Reborn instead applies the PEST policy there as well, through the option **"Uninterrupted simultaneous turns"**
(Options → Game, section "H5Reborn"; profile variable `h5r_simultaneous_uninterrupted`, **default on**). Off gives the
original 3.1 behaviour (`OriginalSimultaneousPolicy`). On the MMH 5.5 target the row is shown ticked and disabled: the
5.5 setup always runs `H5_Game_PEST.exe`. Scope is exactly PEST's (§7.2): network games created with the lobby's
"Simultaneous Turns" ticked; the lobby control itself is unchanged. The host's setting decides (`AdvNetSettings.Pest`,
`TargetExe.Pest(root, option)`); `H5_EXE=pest|stock` still overrides for tests.

## 8. Concurrent battles (the stock engine's, kept by PEST)

Traced in the stock exe (copy `tools/ghidra-projects/conc`, scripts `tools/conc`).

* **Who holds a combat.** `CPlayer` keeps a combat reference (vf `+0x8c` getter, field `CPlayer+0x24`; setter vf `+0x88`
  `004fdb00`) and a second one (vf `+0x90`/`+0x94`, `+0x28`, #47). At the start (`008545b0`) the owners of both
  combat armies (`CCombat` vf `+0x84(true/false)` -> `CCombat+0x8c`/`+0x90`, then the army's owner vf `+0x4`) get the
  combat and their combat screen. A human attacking a neutral holds one; human versus human both hold the same one.
* **While it runs.** The adventure map is not touched (#43). The fighting players are on their combat screens (#42).
  The others keep playing; their attacks pass the gate `00426b30` (#37): refused when the target's owner holds a
  combat or the target defends a running combat; a refused attack does nothing.
* **Turn end.** A current player holding a combat cannot end: every tick clears its end flag (#40). Others may end
  and wait; the phase passes once every current human ended, i.e. after the last battle of the phase. The fighting
  player is never timed out; its timer is credited afterwards (#46).
* **End.** `CFinishCombat` (`004e0520`) -> `CCombat` slot 55 (`00af6680`) applies the result; `CCloseCombat`
  (`004e0f10`) logs `last_combat_duration`, credits the timer and clears the owners' combat (vf `+0x88(0)`; a human
  side only by its own close command). Concurrent battles therefore apply in the order they end (#44).
* **H5Reborn.** `AdventureNetSession` keeps `RunningBattle` entries (battle, parked `PendingBattle`, owners, turn
  stamp). Battle commands go to the battle of the sender's side; dynamic-battle timeouts and leavers are handled per
  battle. The result is applied with the attacker made current, then the previous current player is restored.
  Determinism: the host is authoritative; each participant checks the battle hash after every update and each side
  checks its redacted view hash (its own battle and choice included).
