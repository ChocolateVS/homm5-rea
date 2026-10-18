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
| Animated sword slash trail | In progress (rendering workstream). |
| Minimap view box outside the minimap; empty hero/town slots | In progress (adventure workstream). |
| Adventure lighting/fog | In progress (rendering workstream). |
| Zoom out further on the adventure map | In progress — "Extended zoom" option, on by default. |
| All double-click / click interactions | In progress — interaction audit (ledger `docs/rules/AUDIT_INPUT.md`). |

## New features since your last session
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

## Interruptions
The overnight run hit the account usage limit once (all helpers paused until it reset at 4:10 am); they were resumed from where they stopped, with no work lost.
