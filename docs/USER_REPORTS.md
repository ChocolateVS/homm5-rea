# User reports (backlog)

Things the user noticed while playing. Each is only a pointer that something differs from the
original: the fix must come from the exe and the original data (see docs/AGENT_RULES.md), never
from the description. Move an item to "Done" with the commit when fixed.

## Open — reported 2026-11-10 (browser build 3c4ea8b)

1. **Victory/defeat screen:** the text under "Victory" overlaps and can't be read.
2. **Start-of-week message:** the left part of the dialog is empty, with the text on the right —
   probably a missing picture (week/creature image?).
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
9. **Combat spell book:** looks greyed out; mana is hard to read.
10. **Ownership / selection marks:** a captured mine (or other object) should show something on top
    (owner flag/banner); troops normally have something around them (ring/base marker).
11. **Minimap icons:** the icons for castles and other objects are wrong.
12. **Browser — bug reports:** add an in-game way to submit a report (text, optional screenshot,
    build/commit, map/save state) that is stored on the user's server, so issues found while playing
    are tracked. Server: apps/h5reborn-web on the LAN server (see memory / docs/WEB_BUILD.md).
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
16. **Start of combat:** the user's hero stood inside his war machines, and both heroes were shown
    (check hero model placement vs war machine positions per the exe's arena setup, and which heroes
    the exe shows at battle start / in duels).
17. **Combat hover highlight:** the user asks whether a hovered stack should be highlighted. Known so far
    (AUDIT_COMBAT_UI, commit 84b052b): hovering another stack draws its movement range as grid kind 7.
    Not yet traced: whether the hovered unit itself gets an outline / glow / base marker (likely related
    to item 10, "troops normally have something around them") — trace the exe's hover/selection
    rendering of units (CombatSelectionDesc, unit materials) and match it.
18. **Attacking war machines:** the user could not attack the enemy's war machines in combat. Check the
    exe's attack / shoot target rules for war-machine units (ballista, ammo cart, first aid tent;
    catapult) and our Battle legality + cursor/click handling for them.
19. **Combat graphics glitches:** "a little bit glitchy" in battle (browser build) — details not given;
    reproduce in a browser duel (smoke harness) and compare with the desktop build to find what
    looks wrong (z-fighting, overlay blend, shadows, flicker). Detail from the user: the blue squares
    under a stack flicker against the green movement grid — looks like z-fighting between overlapping
    grid-mark quads (select/target kinds vs the movement area, commits 9537a77 / 84b052b); check the
    exe's draw order / depth state for the CombatSelectionDesc layers (0055c480) and match it.
20. **Griffin Battle Dive:** "just doesn't work at all" in play. The rules were ported in sweeps 14–16
    (landing FUN_00979680, strike timing 00985474.., finish 0x9b); check the player-facing path —
    ability button / cursor / target click in the combat UI, and the AI's use — end to end in a duel.
