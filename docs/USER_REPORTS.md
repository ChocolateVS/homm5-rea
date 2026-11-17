# User reports (backlog)

Things the user noticed while playing. Each is only a pointer that something differs from the
original: the fix must come from the exe and the original data (see docs/AGENT_RULES.md), never
from the description. Move an item to "Done" with the commit when fixed.

## Open — reported 2026-11-10 (browser build 3c4ea8b)

3. **Moving troops:**
   - the town screen does not let troops be put into empty slots;
   - Shift while dragging should split a stack;
   - combining stacks within a hero's own army does not work.
4. **Adventure map creatures:** unsure whether creatures spawn properly on adventure maps
   (check weekly neutral growth / random monster resolution / week-of events).
5. **Town:** double-clicking to buy does not work.
6. **Browser — data picker page:** the first screen (choosing the game files) is very hard to read;
   make it readable and themed like the game.
7. **Browser — F11:** the game captures key input, so F11 full screen does not work in the browser.
8. **Cursor:** there may be a different cursor when hovering a castle's entrance.
13. **Fog of war:** unexplored areas are black, but tilting the camera down shows the outlines of
    things in the darkness. Earlier finding (docs/rules/ADVENTURE_RENDER.md, 7be9d21): the original
    also draws unexplored geometry black rather than hiding it, but our blending (linear vs gamma)
    makes the shapes ~2.5× more visible; re-check with the user's view (camera tilted down, not
    only through light beams) and match the original's look.
14. **Keyboard shortcuts:** some don't work, e.g. Space to move (continue the hero's route) on the
    adventure map. Check every shortcut against the stock input bindings (input_a2.cfg etc.) and the
    exe's handlers, in the desktop and the browser build (the browser may swallow keys).
15. **Duel setup screen:** the UI for starting a duel looks broken ("a bit cooked") — compare the
    duel preset / duel start screens with the exe's screen code and UI data.
19. **Combat graphics glitches:** "a little bit glitchy" in battle (browser build) — details not given;
    reproduce in a browser duel (smoke harness) and compare with the desktop build to find what
    looks wrong (z-fighting, overlay blend, shadows, flicker). Detail from the user: the blue squares
    under a stack flicker against the green movement grid — looks like z-fighting between overlapping
    grid-mark quads (select/target kinds vs the movement area, commits 9537a77 / 84b052b); check the
    exe's draw order / depth state for the CombatSelectionDesc layers (0055c480) and match it.
    *Progress:* the select marks now draw over the overlapping movement / target quads in the exe's order
    (material Priority → render layer 0x1f + priority, 00899720; Select.xdb / select_2.xdb = 1, others 0) and sit
    0.05 above the cell (0055bb60) — commit "combat grid: select marks drawn after the other marks". Still open: the
    cyan blobs under a Deflect Missile target are spell-effect particles sorted with the grid (their render layer in
    the exe not traced); the browser (Compatibility) renderer draws the grid fill brighter than the desktop one (not investigated).

## Done

1. **Victory/defeat screen** (combat results): the outcome and experience lines were one text block drawn over
   the container; now DescriptionText / DescriptionExp are filled separately and added as items of the HeaderCont
   scrollable container, which stacks them (exe 005836a0 → container slot 8; init 00581a70) — commit "combat
   results: stack DescriptionText/DescriptionExp in HeaderCont".
2. **Start-of-week message:** a week without a creature passed no picture, so the stock Box0p's Face stayed
   empty; the exe (009946e0) uses the RPGRoot texture TEXTURE_INFO (UI/MessageBox/Message) then. (MMH55's Box0p
   has no Face window, so there the box shows no picture at all, as in MMH55.) — commit "adventure: start-of-week
   box shows TEXTURE_INFO for non-creature weeks".
9. **Combat spell book:** looked greyed out because the book used Godot PBR lighting with a guessed sun; it is now
   lit by its own AmbientLight record (SpellBookDesc Light) with the original formula, in the desktop and browser
   renderers. The mana bookmark text is drawn into its own 128×128 texture at (6.4, 16) as in the exe (00685380), not
   the 512×256 spell outliner, so it is four times larger — commit "spellbook: original lighting from its
   AmbientLight record, mana mark in its own 128x128 texture".
12. **Browser — bug reports:** "Report a problem" in the page (faded button bottom-right, F9, `?report`;
   `wwwroot/h5report.js`): text + optional canvas screenshot, newest save and console log tail, plus build,
   browser and current scene/map (`globalThis.h5game` from WebProbe). Stored by `tools/web/report-server`
   (one folder per report; read-only list at `/api/reports/view`); deploy steps in docs/WEB_BUILD.md,
   "Bug reports" — commits "tools/web/report-server: bug-report receiver …" (5aebbf7) and
   "web: Report a problem dialog …".
16. **Start of combat:** both heroes and the war machines now stand where the exe's setup puts them
   (hero FUN_00c56b90 for each army: the far-row border square behind its own army — attacker left, defender
   right; machines FUN_00c551f0: border columns, rows from table 00e8e010, 2×2 Ballista / Tent / Catapult by
   CombatSize). Before, both heroes were at the left border and the attacker's stood inside its Ballista.
   Both heroes are shown in duels too, as in the exe — commit b985569 "combat: war machine / hero stands …".
18. **Attacking war machines:** the engine allowed it, but the combat screen only picked cells inside the 12×10
   grid while the machines stand on the border cells; the pick now covers the bordered field like the exe's
   world → cell (FUN_00af15a0, no bounds test) — commit b985569.
20. **Griffin Battle Dive:** the take-off left the griffins standing on the field (nothing seemed to happen until
   their next turn); now specability1 plays and they leave the field, and the finish shows them landing with
   specability2 and the strike (exe FUN_008a9220 cases 0x9a / 0x9b). The ability button, target click and the
   AI's use were checked in duels — commit b985569. Not traced: the exe's target rule for 0x9a (ours: any cell).
10. **Troops' marks in combat** (combat half of 10) **and 17. combat hover highlight:** no ring or base marker
    under every stack turned up in the exe's selection code (0055c480 / 0055c6a0); its per-stack marks are the grid's select quad under the acting stack and
    aimable quads, plus a glow on the stacks the hovered action affects: the selection state's unit list (filled by
    the attack / shot / spell builders) is drawn each frame with CombatSelectionDesc SelectedCreature
    (0.396, 0.220, 0) — the model's visible surface with the colour added, then eight passes pushed out by
    k × ui_combat_hilight_radius (0.03), k = 2..9, adding colour × 0.08 around the silhouette (0055c6a0, 008f71b0).
    Hovering a stack with no action on it highlights nothing beyond its movement range (kind 7). Now the attacked
    / shot / spell-targeted stacks get that tint and glow — commit "combat: hovered action's targets glow in
    SelectedCreature". (The browser renderer adds the glow in sRGB, so it looks brighter there, closer to the
    exe's gamma-space add.)
11. **Minimap icons:** objects were coloured into the terrain tile by tile; now the exe's icon layer (005acd80:
    172×172 layer, transform 005ac8d0, blit 00c1cae0) draws the MinimapTextures icons — Town_/Mine_/Object_ in the
    owner's colour (0 = neutral) for flagged objects, UnderworldExitEnter, Town_1 for the citadels, caravans, Hero_ — and
    the terrain shows the map without its border. Open: the flagged-object set is the engine's capture set, icons are
    masked by the explored tiles (fog order not traced), and the world list drawn as Object_%d (vf 0x84) is not
    identified — commit "adventure: minimap icon layer".
10. **Ownership marks on the adventure map** (adventure half of 10): flagged objects now carry the possession marker
    of CAdvMapFlagged 00452320 — the PossesionMarker effect of the owner's colour scheme (UI/RefTables/
    PlayerColourSchemes) for the owner's race (TownType − 3, else 8; no owner: the neutral _BuildingFree_S glow) at the
    object's PossessionMarkerTile turned by its rotation; it is replaced when the owner changes. Open: the
    FLAGGED_CHANGEOWNER sound of the same function; the flagged set is the engine's capture set — commit
    "adventure: possession markers on flagged objects".
