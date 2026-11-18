# User reports (backlog)

Things the user noticed while playing. Each is only a pointer that something differs from the
original: the fix must come from the exe and the original data (see docs/AGENT_RULES.md), never
from the description. Move an item to "Done" with the commit when fixed.

## Open — reported 2026-11-10 (browser build 3c4ea8b)

5. **Town:** double-clicking to buy does not work.
   Checked (rep-input): a double click on a hire row buys in off-screen desktop runs (both clicks in one frame and
   frames apart) and in headless Chromium with real mouse events (Playwright dblclick); the rows follow the exe's
   CHireItem double-click flag (006ddb60 +0x84) and every data-defined dbl command is handled. Not reproduced — need
   the screen / row the user double-clicked.
6. **Browser — data picker page:** the first screen (choosing the game files) is very hard to read;
   make it readable and themed like the game.
7. **Browser — F11:** the game captures key input, so F11 full screen does not work in the browser.
## Done

1. **Victory/defeat screen** (combat results): the outcome and experience lines were one text block drawn over
   the container; now DescriptionText / DescriptionExp are filled separately and added as items of the HeaderCont
   scrollable container, which stacks them (exe 005836a0 → container slot 8; init 00581a70) — commit "combat
   results: stack DescriptionText/DescriptionExp in HeaderCont".
2. **Start-of-week message:** a week without a creature passed no picture, so the stock Box0p's Face stayed
   empty; the exe (009946e0) uses the RPGRoot texture TEXTURE_INFO (UI/MessageBox/Message) then. (MMH55's Box0p
   has no Face window, so there the box shows no picture at all, as in MMH55.) — commit "adventure: start-of-week
   box shows TEXTURE_INFO for non-creature weeks".
8. **Cursor over a castle's entrance:** we tested a hero standing on the tile before the town and could not pick hero
   models, so a town gate with a hero in it showed the finger. Now as the exe: heroes are model-picked like objects
   (005913b0); 00600e70 tests an allied town in the pick first (OWN_TOWN, with or without a selected hero), then a
   picked hero (the selected one → finger, else meet / attack); the tile fallback follows 00cea350 (object standing on
   the tile or with it as an entrance, then heroes, then the object covering it). Clicks use the same pick — commit
   "adventure: hero model picking and the exe's pick order for the cursor (user report 8)".
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

4. **Adventure map creatures:** neutral stacks now follow the exe — the stack-count generator 004ee750 (Respawn
    MonstersMin/Max, √tier × growth, random-upgrade sub, downgrade ×2, custom Amount..Amount2, x87 chop), the
    CountCoefs difficulty scaling when a monster enters the world (004ef500, also for custom amounts, not with
    DoesNotDependOnDifficulty) and the weekly growth with its hundredths remainder (004eec20) — commit "adventure:
    neutral stack sizes, difficulty scaling and weekly growth from the exe". Open: creature-week monster spawns
    (009919a0, traced in AUDIT_ADVENTURE, not implemented); script-created monsters are not difficulty-scaled
    (unverified); the RNG stream is ours.
13. **Fog of war outlines:** the sunbeam/cloud cards over the unexplored black blended in linear space and were ~2×
    brighter than the exe's gamma-space blend, outlining the black scenery behind them; they now blend in gamma
    space (A2M1: 48–64 → 22–31 levels over black) — commit "render: sunbeam / cloud cards blend in gamma space like
    the exe". Unexplored geometry itself stays black as in the exe. Open: distance fog on these cards (FOGENABLE
    state untraced), overlapping cards add in linear space.
15. **Duel setup screen:** the hot-seat duel screen now works as the exe's (00733680 / 00732930 / 007447f0): "Hot-Seat
    Chess Mode", one page per player with Next/Start and Back/Previous, the arena selector with the arena picture and
    town background (the pink boxes were the additive highlights over bare parchment), the chosen arena used in the
    battle — commit "ui: hot-seat duel screen as the exe's". Open: the network duel screen has no arena selector.
3. **Moving troops:** the army screens now follow the exe's drop handlers (town 00615670, hero screen 006c5e60,
   garrison 00652b70, meeting 00649e70): a stack dropped on the same creature merges whole into it — inside one
   army too (it used to exchange the two stacks) —, anything else moves or exchanges; Shift opens the stock Split
   Army window with the exe's split controller (opens only for an empty slot or the same creature; the value is the
   target's new size, starting at half rounded up; Edit / Cell / slider; a hero keeps one creature), now also on the
   garrison and caravan screens. Drops into empty town slots worked in off-screen runs (hero ↔ garrison, inside a
   row, click-click); no failing case was found — commit "army screens: merge, split window and keys per the exe".
14. **Keyboard shortcuts:** Space (MB_Move_Hero) had nothing to move once a route was partly walked: the hero now
   keeps its route (shown again when selected and at the start of its turn) and Space also wakes a sleeping hero
   (00599bd0 → 006049d0); L opens the mission log tab (0058c790), W / S page the hero screen (006c3160 / 006c31c0)
   and a page key on its own page closes the screen (006c1f70). Every binding of the adventure, town, hero and
   combat sections was checked against our handlers (AUDIT_INPUT). Browser: the game only gets keys while its
   canvas has the focus (Godot cancels Space / Tab defaults there); the data picker now hands the focus back to the
   canvas. Open: the route is kept per screen as a destination (not saved); Ctrl+1–8 / Ctrl+T may stay with the
   browser — same commit.
   Note (key bindings source): the exe has no built-in bindings and no cfg in its paks; a new profile gets copies of
   profiles\default_profile\user_a2.cfg / input_a2.cfg (00834a30) and start-up executes the profile's input_a2.cfg,
   or default_profile's when the profile has no user_a2.cfg (008352a0 → 00834200). We now do the same with
   user:// profiles (IndexedDB in the browser): the copy is made when a profile is created or first used, so a
   browser that once saw the game folder keeps its keys on later .pak-only visits. A browser session with only the
   .pak files and no earlier copy still has no keys (as the exe without the file); the .pak picker now also takes
   `input_a2.cfg` (placed as profiles/default_profile/) and says so — commit "input: key bindings per profile as the exe".
19. **Combat graphics glitches:** the select marks now draw over the overlapping movement / target quads in the exe's
   order (material Priority → render layer 0x1f + priority, 00899720; Select.xdb / select_2.xdb = 1, others 0) and sit
   0.05 above the cell (0055bb60) — commit "combat grid: select marks drawn after the other marks". The cyan blobs under a
   Deflect Missile target are the spell's effect particles: the scene frame FUN_00a91900 (the call at 00a91c00) draws the
   whole material queue first (00465040 → 009ba810 → 009ba6a0, the grid's layer 0x1f + priority among it) and only then
   the particle pass (0087ed90 → 0087e7e0 → 0087e220; 00a904d0 only fills the batches), so particles are always over the
   grid. Our particle material now has the top render priority — commit "combat sweep 19: particles after the material
   layers, Summon Other destination". (The browser renderer's brighter grid fill is not investigated.)
25. **Hero after attacking:** "sometimes after the hero attacks he freezes somewhere in the battlefield". Reproduced in
   off-screen duels (2,3 and 6,7): after a ranged strike ("rangeattack") or a spell ("cast") the hero character held
   the clip's last frame at its stand (the exe's border square, inside the bordered field) for the rest of the
   battle — the stacks' return to idle (FinishAttack) never covered the heroes. The melee strike character (shown on
   the target) was already hidden after its clip. Now a hero goes back to idle00 when any one-shot clip ends, with the
   any→idle combat_anim_blend cross-fade (exe 0054d700); `--hero-after-screenshot=N:PATH` captures 4 s after the Nth
   hero strike / cast — commit "combat: the hero returns to idle after a strike or cast".
26. **Triple Shot ballista prediction** ("only shows the damage of the first shot"): the exe's attack prediction
   (00a63020) multiplies each target's min/max damage by the shots of a Ballista (attacker vf 0x7c war machine, type
   +0x1c == 1; shots = vf 0x1c0 = 00a75bf0) before counting kills (00a635ba..00a635d8) and passes attacks_count 1, so the
   box is the plain "Attack. Damage: a-b (Kills: c-d)" with the summed damage, never "x N" (00a637c7). We showed one
   shot's damage with "x N"; now as the exe. The ballista already fires 00a75bf0's count of shots in one action (1;
   Triple Ballista 2; Ballista perk +1; Ring of Machine Affinity +1; WarMachineAuditTests) — commit "combat prediction:
   a Ballista's shots summed into one attack per H5_Game.exe 00a63020".
27. **Auto-combat** ("the auto combat seems to jump around and skip"): reproduced in off-screen duels with auto-combat
   on (`--autoplay-turns`, the same path as the HUD toggle): the event order was right (one command per turn, played
   in full before the next), but every turn's AI decision (the original AI, 50–950 ms per decision natively, far more
   in the browser) ran on the render thread, so the picture froze and then caught up — the camera's return flight
   after a fight camera jumped ~20 units in one frame, moves started a cell ahead. Auto-combat makes this happen on
   every turn instead of only the computer's. The exe's toggle (0054b930) only hands the side to the AI controller,
   whose command goes through the normal command path at turn start (0054b650 → FUN_00559c80), as ours does. The
   AI now thinks on a copy of the battle on a worker thread (`PendingDecision`, sync in the browser build, which has no
   threads) and its command is used only for the position it was decided for; frames over 40 ms in the same duel went
   11 → 1 (PendingDecisionTests) — commit "combat: AI turns decided off the render thread (user report 27)".
   The adventure battle's computer side (AdventureCombatSession.Tick) still decides in place.
21. **Moving 2×2 troops by one square** ("hovering either of the squares one up would move the troop one up"): the exe
   (00548270 move branch → FUN_00576630 / FUN_00c53160) snaps the cursor cell to the first anchor (the stack's high
   corner) cursor + (dx, dy), dx outer, dy inner, the stack has a path to — over empty ground, its own cells or a
   friend's. We took the destination whose top-left was the hovered cell (the row above moved a 2×2 by two or
   diagonally) and only over empty cells. Now as the exe: the cell above the high corner, and at the left edge either
   cell above, moves it up by one, as does its own high corner (captures mv_upR / mv_upL / mv_own) — commit "combat:
   move destination and attack position from the cursor per H5_Game.exe". Not ported: the exe's cursor point over a
   stack is the model hit (it then takes that stack's nearest cell); ours is the ground under the cursor.
23. **Attack direction from the cursor:** the exe (FUN_00547e50) takes the ring of attack positions round the target
   (00b3d010, counter-clockwise from +x), the sector of the cursor's angle round the target's centre (00544230,
   0053e570), that position if the attack from it is valid, else the nearest valid one round the ring; the sword cursor
   is that direction less the camera yaw in 8 sectors (00540ef0). We matched screen directions instead. Now as the exe
   for every size pair (captures at_W / at_N / at_NW; CursorTargetingTests) — same commit.
24. **2×2 spell target half green, half blue:** the exe marks a unit spell target only with the aimable double quad all
   valid targets get (0055c480 / 0055bf10); affected cells (kind 4) come only from area spells (00548270 → 0097a530).
   We also put affected marks on the target's cells, which the double quad does not hide. Now one quad (capture
   sp_target); the same for shot and melee targets — same commit.
22. **Imp gating** ("I should be able to choose where to gate and the imps should go there after some turns"): the
   engine gated at once onto the free cell nearest the caster. Now as the exe (combat_sweep_19g.md): the Gating
   button arms a cell target (cast test 00860ce0: the caster's footprint free, own cells allowed, not on a stack still
   gating in); the cast 00861840 creates the stack at that cell off the field (placement state 1) with the gating mark
   GATING_CIRCLE1x1/2x2; it arrives at its own first turn start (00bbb390: that cell, else the nearest free one) and
   does not act that turn. Eligibility tier ≤ 2 × Gating mastery (00c54f50), one charge (SpellPoints2), none for the
   gated stack; the AI picks ring cells round the field's units (008d28e0). Checked in off-screen duels (Veyer vs
   Wing Commander): target cursor, mark, arrival — commit "combat: Gating …".
