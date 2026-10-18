# H5Reborn — status report

_Last updated: 2026-10-19 (overnight run). Demo build: `G:\H5Reborn-play` (launchers: `Play H5Reborn.bat`, `Duel vs Computer.bat`, `Host Network Duel (h5server).bat`)._

## Verified for this build
- Full test suite: **355 tests pass** (15 test projects).
- Client builds from a clean checkout; main menu, a vs-AI duel and the A2M1 adventure map start and render (off-screen smoke test, 1920×1080).

## Fixed since your last session (your reports)
| Your report | Status |
|---|---|
| Main menu should be animated, not a broken picture | **Fixed** — the original's live 3D scene (Cyclops on a burning battlefield) with its 30 s camera loop, troop animations and 13 effects, pre-rolled 10 s like the exe. Not a video in the original. |
| Credits button did nothing | **Fixed** — original credits roll over the Magic Guild scene. |
| Hover attack predictions missing | **Fixed** — damage/kill predictions for attacks, shots, hero strikes and abilities. |
| Right-click should be a small window, not a big panel | **Fixed** — original right-click creature tooltip. |
| Troop movement "cooked" | **Fixed per exe** — anim blends, turn-in-place, exe move speeds; walker/flyer frame sequences checked. |
| Triple Ballista usable 3 times | In progress (combat mechanics audit). |
| Hero movement on the map | In progress (adventure workstream). |
| Animated sword slash trail | **Fixed** — effect models now play their original transparency tracks (Footman slash appears at 0.63 s, flashes at 0.8 s, gone by 1.5 s). |
| Minimap view box outside the minimap | In progress (adventure workstream). |
| Hero/town bar full of empty slots | **Fixed** — the bar now shows only your heroes/towns. |
| Adventure lighting / no fog | **Fixed per exe** — gamma-space lighting and the original distance+height fog, for adventure, combat, towns and the menu. One output constant inferred (see rendering notes). A grey-object regression on A2M1 found during verification is being fixed. |
| Zoom out further on the adventure map | In progress — "Extended zoom" option, on by default. |
| All double-click / click interactions | **Audited** — 61 interactions: 20 matched, 32 fixed, 7 gaps (`docs/rules/AUDIT_INPUT.md`). Double-click hero/town (map and lists), stacks → creature info, building double-click, object tooltips (300 ms / 2 s expand), all `input_a2.cfg` hotkeys. Gaps: dig / kingdom overview / puzzle map screens not built yet. |

## New features since your last session
- **Event sounds & positional audio** (audio pass 2): new week, level-up, objectives, join/flee, grail themes, recruit/hire/buy, chat, credits music, dialog-scene and mission win/lose music, 3D looping sounds on 37 map object types (heard per floor), hero walk sounds, town building sounds, object visit sounds.
- **Music & ambience** from the original systems (menu, adventure per hero/terrain/campaign, towns incl. tavern/recruit, combat incl. sieges, win/lose stings, ambient sets, UI click sounds).
- **Boats & water**: shipyards, ships, boarding/landing, sea movement bonuses, whirlpools, shipwrecks, Vessel of Shalassa, sea battles, AI boat use.
- **Network adventure battles** (human vs human on the combat screen), **Ghost Mode** rules + HUD, hot-seat hand-over screen.
- Adventure: hero meetings, adventure spells (Instant Travel, Town Portal, Summon Creatures), "Generate Map" (random maps) in Custom Game, Hall of Fame entries.
- Save/Load (F5–F8, autosave), profiles, original data-driven Options, Hall of Fame screen.

## Audit ledgers (evidence per item: exe address / data path, MATCH / FIXED / GAP)
- `docs/rules/AUDIT_COMBAT.md` — every combat mechanic
- `docs/rules/AUDIT_COMBAT_UI.md` — every combat UI element
- `docs/rules/AUDIT_INPUT.md` — every click / double-click / hotkey
- `docs/rules/AUDIT_FRONTEND.md` — main menu and front end
- Plus exe findings: `docs/rules/EXE_FINDINGS.md`, `CAMERA.md`, `AI.md`, `AUDIO.md`, `BOATS.md`, `SAVE.md`, `RMG.md`

## Known gaps (not yet like the original)
- Market exchange rates, kingdom overview, town info, artifact merchant, faction special buildings, Witch Hut / Hill Fort / Seer Huts / Sphinx / grail.
- Coastline is jagged; no shore foam. Some arenas too dark; lava glow; underground lighting.
- Ghost Mode: a few effect details not traced in the exe; possessed stacks are AI-played.
- Map editor not rebuilt (separate project — ask if wanted). Web port: after parity.

## Housekeeping for you
- `G:\H5Reborn-input-test` is a leftover scratch folder (copies of committed scripts only, no links). A safety check stopped me deleting it automatically — you can delete it.
- `G:\H5Reborn-audio2` is another leftover. **Careful:** it contains a junction `tools\godot` pointing at the real Godot install. Remove the junction first, in PowerShell: `(Get-Item 'G:\H5Reborn-audio2\tools\godot').Delete()` — then delete the folder. (Deleting the folder recursively before removing the junction could delete the real Godot files.)

## Interruptions
The overnight run hit the account usage limit once (all helpers paused until it reset at 4:10 am); they were resumed from where they stopped, with no work lost.
