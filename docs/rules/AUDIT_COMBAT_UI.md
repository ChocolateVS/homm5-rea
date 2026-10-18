# Combat UI audit ledger

Every combat-screen element and interaction compared with H5_Game.exe and the stock UI data.
Status: **MATCH** (already identical, evidence given), **FIXED** (was wrong, now follows the
exe/data), **GAP** (known difference, not fixed yet), **UNVERIFIED** (not audited against the
exe yet). Addresses are H5_Game.exe virtual addresses.

## Hover predictions

| Element | Original (evidence) | Ours | Status |
|---|---|---|---|
| Attack prediction line (PredictionBox) | exe 00a63020 builds `COMBAT_PREDICTION_NORMAL_ATTACK` "Attack. Damage: a-b (Kills: c-d). <retaliate>", `_MULTIPLE` "x N" for multi-strike; shown by 00545ae0 in PredictionText wrapped in UI/CombatArena-Heavy/PredictionFormat.txt | Same strings (RPGRoot COMBAT_* ids), engine `Battle.PredictAttack` | FIXED |
| Damage / kills range format | 00a5b2d0 / 00a5b160: SIMPLE when min = max, SEPARATED otherwise | Same | FIXED |
| Damage roll used | 00a63020: min and max roll, luck factor 1.0 | `ComputeDamage` with fixed min/max roll, no luck | FIXED |
| Kills formula | 00a6bd90: dmg / hp + (top ≤ dmg % hp), clamped to count | `Battle.KillsFor` | FIXED |
| Vorpal Sword +1 kill | 00a63020 (ability 0x25 unless target has Crystal Scales 0x42) | Same (target hero-owner condition not traced) | FIXED |
| Retaliation text | 00a5fee0 reason order: Rune of Battlerage, Hypnotize, Gating, shooter, expert Blind, No Enemy Retaliation, Battle Dive, invisible attacker, Swift Attack vs Slow, cannot retaliate, Cowardice, "Will retaliate"; dead target → "No retaliation" | `RetaliationReason`, same order and RetaliateStrings | FIXED |
| Cursor prediction label | UI/CombatArena-Heavy/CursorPrediction (200 px, grows), cursor-prediction.txt; kills-cursor-prediction-1 "Kills: <red>N<white>", retaliation-cursor-prediction "Retaliate: Yes/No" | Same texts beside the cursor | FIXED (position relative to cursor inferred) |
| Named colours in markup (`<color=red>`) | exe 00cc3610 table (white, red, green, blue, yellow, cyan, orange, pink, brown, grey, black, darkyellow, beige, lightblue) | UiMarkup.NamedColors | FIXED |
| Spell prediction | `COMBAT_PREDICTION_DIRECT_DAMAGE_SPELL`, `COMBAT_CAST_SPELL[_TARGETED]`, `COMBAT_PREDICTION_FORMAT_RESURRECT` | Same strings; damage from resolving the cast on a battle copy | FIXED (values from simulation, not the exe's analytic estimate; resist rolls can differ) |
| Ability prediction | `COMBAT_PREDICTION_DIRECT_DAMAGE_ABILITY`, `COMBAT_APPLY_ABILITY[_TARGETED]` | Same, simulated | FIXED (same caveat) |
| Hero strike prediction | 00a62e50: NORMAL_ATTACK format, single value, no retaliation | `HeroStrikeDamage` | FIXED |
| Move hover | no prediction text | none (box shows the last log line) | FIXED |
| Box when nothing hovered | 00545ae0 falls back to the log | last log line | MATCH |
| Pattern attacks (several targets) | 00a63020 sums damage over all affected targets | main target only | GAP |
| Extra on-hit damage in prediction (elemental chains, Axe of Slaughter, …) | 00a5c000 / 00a5ed90 added per target | not added | GAP |
| Treant/Repair/Mana/Summon Balor/Fear My Roar/Call of Blood formats | Predictions_Formats/*.txt | not produced | GAP |

## Unit info

| Element | Original | Ours | Status |
|---|---|---|---|
| Right-click on a stack | small creature tooltip TOOLTIPS_HOLDER (UI/Tooltips/CreatureInfo/CommonInfo) next to the cursor (00575430 → 00573dd0 → 00572be0) | same window, built from the stock xdbs | FIXED |
| Repeated right-clicks | variant = click count mod 3; 2 = active-effects panel (00574790; fan manual p.304) | same cycle | FIXED |
| Tooltip rows | 00572880: current value coloured against base (Raised/LowedAttributeColor), optional second value after Separator1/Separator2, AbsentAttribute "-" | same | FIXED |
| WindowGathering layout | stacks visible children, ignores their own offsets | UiLayout | FIXED |
| Effect rows: turns "x/y" | maxval = original duration | engine keeps only remaining time, so x/x | GAP |
| Prediction appended under the tooltip | 00573dd0 adds the "Prediction" child | not appended on right-click | GAP |
| Tooltip anchor | placed by the tooltip system | beside the cursor, kept on screen | GAP (not traced) |
| Full CreatureInfo screen in combat | opened from elsewhere (not right-click) | still built, no entry point | UNVERIFIED |

## Movement and animation

| Element | Original | Ours | Status |
|---|---|---|---|
| Clip cross-fade | 0054d700: combat_anim_blend 300 ms idle→stir/move and any→idle; other changes cut | `UnitView.BlendSeconds` | FIXED |
| Turn before moving | 008b8a70 → 008b88b0: turn in place to the first segment playing the move clip | `UnitView.TurnTo` | FIXED |
| Turn duration | 00a44030: (1/speed[units/ms]) × angle/π, or 300 ms × angle/π | same | FIXED |
| Ground speed | 008b85e0: MovementSpeed × SpeedFactor × 0.001 units/ms, default 0.003; × combat_time_factor | same (time factor 1.0) | FIXED |
| Facing after a move | 00a733a0: direction of the last path segment | same | FIXED |
| Flyers | same routine on a straight path; no take-off arc | straight line, arc removed | FIXED |
| Corners along the path | path track (008ad190 type 4) corner handling not traced | turns at the in-place rate while walking | GAP (inferred) |
| Attack facing | turn routine | attacker and defender turn instead of snapping | FIXED (inferred from the same routine) |
| Teleporting movers | moveStart / moveEnd clips | as before | MATCH (prior agent) |
| Frame checks | — | walker (Nightmare), flyers (Griffins), teleporter (Arch Devil) captured at 10 fps: no snapping or sliding | — |

## Cursors and highlights

| Element | Original | Ours | Status |
|---|---|---|---|
| Broken-arrow (range penalty) cursor | same test as damage, 00a5c5a0 (nearest-cell distance² > (w/2)²) | engine `NearestCellDistanceSq` | FIXED |
| Cursor set (move, fly, attack ×8, shoot, cast, can't cast, wait) | hwcursors/*.cur | decoded originals (prior agent) | MATCH |
| Movement area / selection marks | UI/CombatArena/Grid materials | prior agent | UNVERIFIED |

## HUD, dialogs and flows

| Element | Original | Ours | Status |
|---|---|---|---|
| Tooltips on HUD buttons | UI/Tooltips/Main.(TooltipContext).xdb: TextTooltip window, 300 ms delay, 223 px line, text.txt markup | StockTooltip (shrinks one-line tooltips to their text) | FIXED |
| `<hotkey=…>` in tooltips ("Wait (W)") | rendered by the markup parser from the key bindings | tag dropped | GAP |
| Auto-combat feedback | no log line; jog button state | invented "Auto combat on/off" log line removed; button state not toggled | FIXED / GAP (button state) |
| Flee/Surrender | choice dialog built in code (exe 00570cf0) from UIGameRoot ID_RETREAT_FACE_TEXTURE, _TITLE_TEXT, _HEADER_TEXT, _DESCRIPTION[_FLEE], _NOGOLD_TOOLTIP/_TEXT, ID_SURRENDER_TEXTURE, _SURRENDER_TOOLTIP/_TEXT, ID_FLEE_TEXT/_TEXTURE, ID_COMBAT_RETREAT T_HEADER/T_FLEE/T_SURRENDER tooltips; close key input_Combat_Retreat_Close; CANT_RETREAT_* refusals | submits SurrenderCmd at once | GAP |
| Tactics: "not all creatures placed" confirm | NotAllCreaturesPlacedHeader/Question (UI/CombatScreen-Heavy) | none | GAP |
| Tactics: split stack | SplitStackHeader/Question | none | GAP |
| War machines: Ballista / Triple Ballista | one targeting action fires every shot (engine 8b48870, exe 00a75bf0) | one ShootCmd; prediction shows "x N" (NORMAL_ATTACK_MULTIPLE) | MATCH |
| First Aid Tent / Catapult targeting | manual with the perks | prior agent (CATAPULT_SHOT targets) | UNVERIFIED |
| Esc menu | UI/EscMenu | stock window (prior agent) | MATCH (prior agent) |
| Keyboard shortcuts | profiles/default_profile/input_a2.cfg combat sections | mapped by the prior agent | MATCH (prior agent; not re-checked key by key) |

## Not yet audited

ATB bar (hover, click, scroll, markers), spellbook targeting flow (mass spells, cast on cell,
invalid-target messages: the SpellStrings texts exist but are not shown), creature ability
activation flows, combat log behaviour (scrolling, hover), results screen details: **UNVERIFIED**.
