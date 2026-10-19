# Simultaneous turns (network adventure games)

Reverse-engineered from `H5_Game.exe` (TotE 3.1, Ghidra 12.1.4; project copy
`tools/ghidra-projects/simul`, scripts and helpers in `tools/simul`). Texts from `data/texts.pak`.
Implementation: `src/H5.Adventure/Engine/Adventure.Simultaneous.cs` (areas, turn order hook),
`src/H5.AdvNet/SimultaneousTurns.cs` + `AdventureNetSession.cs` (phase, conflicts, approvals),
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
* Turn timer: the tick checks the world's time-limit methods (vf `+0x1c8/+0x1cc`) against one turn
  start time (`CWorld+0x104`), so the whole simultaneous phase shares one timer; when it runs out
  `GiveTurnToNextPlayers` runs for everybody (`00998520`, branch `0x1cc() == 0`).
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
* Break policy seam: `ISimultaneousPolicy.BreakOnIntersection`. `OriginalSimultaneousPolicy` (default)
  breaks as above. `AlwaysOnSimultaneousPolicy` = lobby option "Simultaneous turns: always on (MMH 5.5)"
  (`AdvNetSettings.SimultaneousAlwaysOn`): areas are never checked.

### MMH 5.5 "always on" — GAP
The fan mod Might & Magic: Heroes 5.5 keeps simultaneous turns on for the whole game. Its rules are
not in our data (mod files not provided yet). Until they are, the always-on policy only skips the
break; everything else falls back to the original engine behaviour, which the mod surely changes.
Open questions the mod files must answer:
1. Contact/attack: what happens when heroes of two humans meet or one attacks the other while both
   are moving (blocked? queued? battle immediately?).
2. Battles while others keep moving: are the other humans paused, and is the battle interactive for
   both sides during the simultaneous phase?
3. Town captures / visiting objects another human is also heading for (who wins a race).
4. Timers: one shared phase timer or per player; what happens to a player in battle when time runs out.
5. AI: do computer players still move after all humans, or concurrently?
6. Ghost mode while simultaneous turns are permanent.

## 6. Audit ledger

| # | Item | Status | Evidence / note |
|---|---|---|---|
| 1 | Lobby checkbox enabled on the network create screen, off by default | FIXED | `0070862c`, UI/MPCreate `simultaneus` (was disabled) |
| 2 | Not offered for hot-seat | MATCH | UI/HotSeat has no control |
| 3 | Room setting `simultaneus_turns` on/off, no day limit | FIXED | `00712d40` |
| 4 | Humans move concurrently; AIs afterwards in order; then new day | FIXED | `00998060`, `0098f7a0` |
| 5 | Ending the turn in the phase only marks the player; phase ends when all ended | FIXED | `00998760`, `00998520` |
| 6 | Ended player sees "Wait for other players, please" | FIXED | `T_WAIT_FOR_PLAYERS` |
| 7 | Shared phase timer; expiry ends every human's turn | FIXED | `00998520` |
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
| 32 | MMH 5.5 always-on policy | GAP | see §5 |
| 33 | Other humans keep moving while one human of the phase fights a battle | GAP | the session runs one interactive battle at a time and holds every other command until it ends |
| 34 | A pending adventure choice of one human (e.g. a chest) does not block the others | GAP | the engine has one shared choice slot; others get "another player is deciding" until it is answered |

Counts: MATCH 3, FIXED 18, GAP 13 (34 items).
