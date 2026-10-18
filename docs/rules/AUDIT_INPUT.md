# Interaction audit (mouse & keyboard outside combat)

Ground truth, in priority order:
1. **H5_Game.exe** (Ghidra 12.1.4, project copy `tools/ghidra-projects/input`, helpers in `tools/inputre`).
   Screen message observers: adventure `SFLB_AddObservers @0059a210`, town `SFLB_AddObservers @0061bcf0`,
   hero/town list `@005ec7e0`, hero screen `@006c6790`.
2. **Original UI data**: every button's commands per trigger, dumped with `h5tool ui-commands <xdb|prefix>`
   (click = `CommandsOnEnterState`, right = `CommandsOnRightClick`, dbl = `commandsOnLDblKlick`).
   Across all 78 stock `(WindowScreen)` files there are 262 distinct trigger/command pairs; **no** button
   has right-click commands and **no** button carries a hotkey (only `DefaultSubState`) — keys come from
   the input config, right-click behaviour from code.
3. **Key bindings**: the install's `profiles/default_profile/input_a2.cfg` (the file the game loads),
   read at runtime by `game/src/Input/KeyBindings.cs` — sections `adventure_screen`, `town_screen`,
   `hero_screen`, `fort_screen`, `mage_guild_screen`, `tavern_screen`, `market_screen`, `town_build`,
   `buy_war_machine_screen`, `saveload`, `mpcreate`, global.
4. **Stock manual** (`Game Manual 3.0.pdf`): p.8 tooltips/right-click, p.10 creature info, p.17 hero
   management, p.20 town, p.46 control tables.

Status: **MATCH** (already right), **FIXED** (changed in this audit), **GAP** (not yet original).

## Double-click timing
| Item | Original | Ours | Status |
|---|---|---|---|
| Threshold | No `GetDoubleClickTime` import; the exe reacts to window-message double clicks (`win_left_button_dblclk`), i.e. the Windows setting | Godot's Windows backend flags `DoubleClick` from the OS double-click messages | MATCH |

## Adventure map — mouse
| Interaction | Original (evidence) | Status |
|---|---|---|
| Double click own hero | Opens hero screen (manual p.17; dblclk handler @00597640 → @00594e60) | FIXED (also selects it) |
| Click own hero, none selected | Select + focus | MATCH |
| Click another own hero while one is selected | Destination: approach & open exchange (manual p.46 "Click on friendly hero"; MeetHeroes cursor) | FIXED (was: select) |
| Click selected hero | Nothing | MATCH |
| Double click own town | Town screen (manual p.20) | MATCH |
| Single click own town, no hero selected | Focus only (was: opened the town) | FIXED |
| Click location/object | Set destination, route shown; click again or double click → move (manual p.46) | MATCH |
| Aimed adventure spell | Next click is the target; Esc cancels | FIXED (Esc) |
| Hover object → tooltip | TooltipContext AppearDelay 300 ms, full window after 2000 ms; windows ADV_OBJ_TOOLTIP / MONSTER_TOOLTIP (UIGameRoot) | FIXED (new `AdvTooltip`: stock background + text templates; monsters use `roughAmounts.xdb` Few…Legion and courage texts) |
| Right click object | Full description window (manual p.8) | FIXED (shows the full tooltip at once; right-drag still rotates the camera) |
| Tooltip layout details | Hero face, army as creature icons with counts (CreatureFace slots), Attributes rows, threat level (`ARMY_DANGER_*`, enum @00ad3e50, computed elsewhere) | GAP — text rows instead of icon grid; threat level not shown (formula not traced) |
| Wheel / right-drag / middle-drag / Ctrl+drag | Camera zoom/rotate/strafe (input_a2.cfg `+camera_*_mouse`) | MATCH (AdvCamera) |

## Adventure map — keyboard (`adventure_screen`)
| Key | Action | Status |
|---|---|---|
| Space | MB_Move_Hero | MATCH |
| N, Tab | MB_Next_Hero (skips sleeping heroes) | FIXED (sleep) |
| 1–8 / Ctrl+1–8 | select hero / town | MATCH |
| Enter, NumEnter, E | JogDialButton (end turn) | MATCH |
| Esc, F10 | input_esc_menu — clears aimed spell / route first (manual p.46) | FIXED |
| F4 | input_game_options | FIXED |
| C | SpellBookWindow | FIXED |
| A / I / S / V / H | hero screen pages (Army, Inventory, Skills, Abilities, Biography) | FIXED |
| O, Q / L | objectives / objectives log | FIXED (opens scenario info; the objectives screen itself is a GAP) |
| T | adv_show_town (town of the selected hero, @0058c320) **and** flip_floor (both bound in the file) | FIXED (both dispatched; exact precedence not traced — GAP) |
| U | flip_floor | MATCH |
| M | show_heroes_mana (toggle mana bars in the hero list, @0058deb0) | FIXED |
| Z | sleep_wake_hero (@0058c3a0) | FIXED — not saved with the game; Zzz marker on the list not drawn (list owner) — GAP |
| G | adv_dig | GAP (no dig rule in the engine yet; key dispatched by name) |
| K | input_adv_show_kingdom_overview | GAP (screen not built; key dispatched by name) |
| P | input_open_puzzle_map | GAP (screen not built; key dispatched by name) |
| F5–F8 | quicksave/save/load/quickload | MATCH (Save.SaveHotkeys) |
| Home, Insert/Delete, arrows, PgUp/PgDn | camera | MATCH (AdvCamera) |
| Backspace, Ctrl+Enter, Ctrl+A/T/R | chat (network only) | GAP (no chat line on the map) |
| Ctrl+Shift+T, Ctrl+D | editor cheats in the shipped file | disabled (retail) |
| Shift+Tab, Ctrl+E (manual only) | not bound in the 3.1 input file | not implemented (input file wins) |

## Hero / town list (TownHeroPanel)
| Interaction | Original | Status |
|---|---|---|
| Click portrait / town icon | hero_click / town_click → select & focus | MATCH |
| Double click portrait | hero_dbl_click → hero screen (manual p.17) | FIXED (was unhandled) |
| Double click town icon | town_dbl_click → town screen | FIXED (was unhandled) |

## Town screen
| Interaction | Original | Status |
|---|---|---|
| Click building | Nothing — `click_pointer` handler @00613130 returns 0; hover highlights | FIXED (was: opened it) |
| Double click building | Opens it — `dblclick_pointer` @00619a30 → @00616bf0 (manual p.20) | FIXED |
| Click visiting/garrison hero portrait | Nothing (click_pointer) | FIXED (was: opened hero screen on any top-row slot click) |
| Double click hero portrait | Hero screen (@00619a30 → @00619320; manual p.10 "heroes exchange screen … double-clicking") | FIXED |
| Double click a stack | Creature info (manual p.10) | FIXED (`CreatureInfoWindow`, stock UI/CreatureInfo) |
| Drag / Shift-drag stacks | Move/merge / Split Army | MATCH |
| Keys B R G T M Q X Y Z F, N/P, U, Space, A/I/S/V/H, Esc/Enter/E | town_screen section | FIXED (were missing) — Space swaps only when one hero is present (engine has no two-hero swap: GAP) |
| Right click building | Building tooltip (ID_TOWN_HALL_TOOLTIP) | GAP |
| Sub-screens (fort, guild, tavern, market, blacksmith, hall) | Close with their section keys / Esc (jd_button_0); right click does **not** close | FIXED (right click used to close them) |

## Hero screen
| Interaction | Original | Status |
|---|---|---|
| Esc | close_window | MATCH |
| Right click | Description (never closes) | FIXED (right click used to close the screen) |
| A / I / V / H | page keys (hero_screen) | FIXED |
| W / S, F | input_hero_page_up/down, hero_unlearn_skill | GAP |
| Double click a stack | Creature info | FIXED |
| Artifact slot double click (`slot_dbl_click`) | Handled @006c6790 (behaviour not decoded) | GAP |

## Dialogs and lists
| Item | Original | Status |
|---|---|---|
| Message / question boxes | Enter = OK, Esc = cancel | MATCH (AdvDialogs) |
| Game options, Esc menu, Save/Load, Custom game, Hall of fame | Esc closes (global close_window / saveload / mpcreate), Enter saves/loads in Save/Load | FIXED (`UiScreen.Input.cs`; options and Esc menu ignored Esc) |
| Creature info | Any click / Esc / Enter closes | MATCH |
| Map list, save list, lobby lists | Double click accepts | MATCH |
| Campaign briefing bonus | Double click = select bonus and start (`select_bonus_and_start`) | FIXED (was unhandled) |
| Tavern face | Double click hires | MATCH |
| Main menu keyboard | No input_a2.cfg section | MATCH (none) |

## Verification
Off-screen runs with `--input-test` (`game/src/Input/InputTestDriver.cs`) on A2M1 at 1920×1080:
double click town → town screen; single click on City Hall → nothing, double click → hall; Esc → hall
closes, Esc → town closes; double click hero portrait → hero screen, I → inventory, right click keeps it
open, Esc closes; F4 → options; Z/M; route plotted then cleared by Esc; tooltips for a monster
("Steel Golem / Horde / Courage: Unpredictable") and a hero (name, level, owner, army).
Screenshots: `%TEMP%/claude/…/scratchpad/inp/shots/`.
