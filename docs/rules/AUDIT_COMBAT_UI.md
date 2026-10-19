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
| Pattern attacks (several targets) | 00a63020 sums damage over all affected targets (target list FUN_00a62030), each with its share (truncated, 00a67bf3) | `PredictAttack` sums over the non-mutating `PredictionTargets` (commit e3a4975); RNG restored so hovering draws nothing | FIXED |
| Extra on-hit damage in prediction | Cold Steel 00a5c000 / Fiery Wrath 00a5ed90 called with the prediction flag, per target, clamped to the target's remaining HP | `PredictedElementalExtra` (e3a4975) | FIXED (other on-hit extras, e.g. Axe of Slaughter, not traced in 00a63020: UNVERIFIED) |
| Refused spell target | FUN_00982010 returns the COMBAT_CANT_CAST_* reason (only friendly/hostile, hero, war machine, immunity `<value=target> is immune for spell <value=spell>`, live/undead, …) shown in the box with the can't-cast cursor; immune stacks can't be picked | engine `SpellTargetRefusalKey` (commit e853590) → prediction box + CantCast cursor | FIXED |
| Area spell hover (cell-targeted) | grid materials ID_AFFECTED_TILE_MATERIAL(_2) from UIGameRoot / CombatSelectionDesc; which cells an area spell marks not traced | only the hovered cell is marked | GAP |
| Treant/Repair/Mana/Summon Balor/Fear My Roar/Call of Blood formats | Predictions_Formats/*.txt | not produced | GAP |

## Unit info

| Element | Original | Ours | Status |
|---|---|---|---|
| Right-click on a stack | small creature tooltip TOOLTIPS_HOLDER (UI/Tooltips/CreatureInfo/CommonInfo) next to the cursor (00575430 → 00573dd0 → 00572be0) | same window, built from the stock xdbs | FIXED |
| Repeated right-clicks | variant = click count mod 3; 2 = active-effects panel (00574790; fan manual p.304) | same cycle | FIXED |
| Tooltip rows | 00572880: current value coloured against base (Raised/LowedAttributeColor), optional second value after Separator1/Separator2, AbsentAttribute "-" | same | FIXED |
| WindowGathering layout | stacks visible children, ignores their own offsets | UiLayout | FIXED |
| Effect rows: turns "x/y" | maxval = original duration | `Effect.Duration` (additive engine field, set when the effect is applied) gives maxval | FIXED |
| Prediction appended under the tooltip | FUN_00575430: when a prediction text is current, FUN_005751b0 builds a stock text tooltip (SWindowTooltipShared) with it and FUN_00573dd0 gathers "CreatureInfo" + "Prediction" in TOOLTIPS_HOLDER | right-click on the hovered stack stacks a StockTooltip with the box prediction under the creature info (screenshot: Bless refused on an enemy Archangel) | FIXED (whether the text is the box or the cursor variant not decoded; box used) |
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
| Frame checks | — | walker (Nightmare), flyers (Griffins), teleporter (Arch Devil) captured at 10 fps: no snapping or sliding; a 2x2 walker (Hydra/Cyclops) move was not caught in the captures (same code path, StackCenter with size 2) | — |

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
| `<hotkey=…>` in tooltips | tag handler 0066a570 registers FUN_0066db50: when the action has bindings, `<hotkeys_prefix>` + per binding `<hotkey_prefix>` keys `<hotkey_postfix>` joined by `<hotkeys_delimiter>` + `<hotkeys_postfix>` (UI/Consts/mlTags: ". 'W'"), keys of a chord joined by UIGameRoot T_HOTKEY_CONTROLS_DELIMITER | UiMarkup "hotkey" + KeyBindings.HotkeyTexts (all input_a2.cfg sections): "Wait. 'W'", "Auto place creatures: . 'A', 'V'" | FIXED (key names are the cfg tokens; the exe's localised key-name lookup FUN_00666060 not traced) |
| Auto-combat feedback | no log line; OnJog-AutoCombat is a two-state button (Combat_AutoCombat in both states) | no log line; button state follows auto-combat | FIXED |
| Flee/Surrender box | exe 00570cf0 on the choice base 00676de0 (Box2p: Title, HeaderMain, MsgText via `<value=text>`, ButtonLeft/Right icon+text, OK; confirm/cancel/left/right messages): ID_RETREAT_FACE_TEXTURE, _TITLE_TEXT, _HEADER_TEXT, _DESCRIPTION (`<value=cost>`) when surrender is possible else _DESCRIPTION_FLEE; left ID_FLEE_TEXTURE/_TEXT; right ID_SURRENDER_TEXTURE with _SURRENDER_TEXT, or _NOGOLD_TEXT (disabled) without gold; ID_COMBAT_RETREAT T_HEADER/T_FLEE/T_SURRENDER tooltips; close input_Combat_Retreat_Close | RetreatDialog: same window, texts, textures and tooltips; OK submits `SurrenderCmd { PayRansom }` (engine EndReason "fled" / "surrender"); ransom from engine `Battle.SurrenderCost` (half the army gold value, exe FUN_004da9a0, heroes audit) | FIXED (enable indices 2/3 of 00675760 not fully decoded; OK always enabled) |
| Retreat button state | FUN_00540090: enabled only when FUN_0053feb0 returns 0; tooltip by code from OnJog-Flee-Tooltips: 5 tactics, 2/7 army not ready, 3 auto-combat, 4 duel mode ("Can't retreat. (Duel Mode)"), army reasons from CCombatArmy::CanRetreat (in town, no hero, Shackles of War) and no tavern town; 1/6/8 disabled with RETREAT_NORMAL | RetreatDialog.RefusalCode in that order; army reasons from engine `Battle.RetreatRefusal`; R key and Esc-menu Surrender open the box only when allowed | FIXED (no-tavern-town needs the adventure host's HasTavernTown) |
| Special ability / Spellbook jog buttons | FUN_005464a0 sets OnJog-Spellbook / OnJog-Special from FUN_0053f730 (actor → can-cast flag +0x2be, has-special flag +0x2bf; creature spell list FUN_00a345b0/00a34610/00a345e0 counts) | Special shown when the actor has a usable ability, Spellbook otherwise | UNVERIFIED (flag rules only partly decoded) |
| Tactics buttons | OnJog-StartCombat sends autoplace_army (tooltip StartCombat.txt "Auto place creatures"), handler 0053e510 → 00578510 re-runs the start placement; OnJog-NextCreature sends confirm_placement ("Confirm placement and start combat"), handler 00545260; keys A/V = input_autoplace_army, Enter/Space = input_start_combat | both buttons started the combat | FIXED (auto placement re-runs our start placement rule, which itself is not exe-traced) |
| Auto placement rule (battle start and autoplace_army) | FUN_00578510 → FUN_0085a190: console var combat_new_autoplacing (DAT_00f43ea4, default 1, FUN_0085aee0); 1 and 2+ stacks → iterative placer FUN_00859eb0 (FUN_00859cd0 setup, FUN_008588b0 / FUN_008599d0 / FUN_00859b40 passes), else FUN_0085a110 → even spread FUN_00857380 (rows by cumulative size, FUN_00856fa0 fit test) then FUN_00859930 | `BattleSetup.DefaultPlacement`: evenly spaced preferred rows in the first two columns (not from the exe) | GAP (engine rule; entry points traced, algorithm not ported) |
| Tactics: "not all creatures placed" confirm | 00545260 asks T_NOT_ALL_CREATURES_PLACED_QUESTION/_HEADER (Yes/No, 00679150) when FUN_00576170 finds an unplaced stack | the engine places every stack and has no "leave a stack out" command, so the case cannot arise | GAP (needs engine support for unplaced stacks) |
| Tactics: split stack | SplitStackHeader/Question: "Split Stack microartifacts - are you sure?" (asked when splitting a stack carrying micro-artifacts) | no stack splitting in tactics | GAP (needs engine support) |
| War machines: Ballista / Triple Ballista | one targeting action per turn fires every shot at the chosen target, stopping if it dies (engine 8b48870, exe 00a75bf0) | one ShootCmd, no follow-up shot commands; prediction shows "x N" from `Battle.WarMachineShotsPerAction` (NORMAL_ATTACK_MULTIPLE); each shot emits its own attack event, so N shot animations + projectiles play in sequence | MATCH |
| First Aid Tent / Catapult targeting | manual with the perks | prior agent (CATAPULT_SHOT targets) | UNVERIFIED |
| Esc menu | UI/EscMenu; esc_menu bindsection close_menu 'ESC'/'E'; Options opens the game options | stock window; ESC/E close it; Options opens the stock data-driven GameOptionsScreen (as on the adventure map) | FIXED |
| Keyboard shortcuts | input_a2.cfg combat_screen: options ESC/F4/F10, Defend ENTER/NUM_ENTER/SPACE/D, Wait W, AutoCombat A/V, Retreat R, Cast C/S, switch_atb_log TAB, camera lift INS/DEL; combat_screen_tactic: start combat ENTER/NUM_ENTER/SPACE, autoplace A/V | all mapped; tactics A/V corrected from "start combat" to auto place | FIXED (chat keys BACKSPACE/CTRL+ENTER/CTRL+A/T/R not implemented in combat: GAP) |
| Results screen text | FUN_005836a0: DescriptionHeader Victory/Defeat; DescriptionText_Format = outcome (type 1 defeated / 2 fled / 3 surrendered; Hero*/Enemy*/Ghost* texts) + `<value=no_army>` WINNER_HAS_NO_ARMY when the winner kept no troops; `<value=player_hint>` ResultForPlayer in multi-human games; DescriptionExp_Format HeroExp (own victory) / EnemyExp with the winner's experience | outcome text, no-army note, experience line (CombatSession.WinnerExperience: slain creatures' Exp + 500 for a defeated hero; the adventure host can override) | FIXED (player_hint and Ghost texts not produced: GAP) |
| Results lists | Lost / Destroyed units: icon list FUN_005814d0 / FUN_00580ee0 (icon i shows entry offset+i; Left enabled while offset > 0, Right while offset < entries − icons; UnitsButtons visible only if either is); Left/Right step one (FUN_00585920 / FUN_00585940); range set in FUN_005827f0 (00583630) | same rule in CombatHud.FillLosses, Left/Right messages per list (7-kind result checked: no buttons) | FIXED (two-row variant and the always-show flag +0x35 not used by the results screen; >7-kind scroll not captured) |
| Retaliation in predictions | engine rules: only Expert Blind stops retaliation, any Hypnotize does | prediction treated any Blind as "no retaliation" and Hypnotize only for the same controller | FIXED (UiPredictionTests) |
| Strikes in predictions (Rune of Berserking) | FUN_008a0f90: Double Attack 2, Berserking +1 | prediction doubled the strikes for Berserking | FIXED (UiPredictionTests) |
| ATB bar scrolling | FUN_0055dce0: atb_scroll_left/right set flags +0xc/+0xd while held, *_reverse or any other message clears; FUN_0055f880 slides the strip (+0x44) toward index × pitch + w/2 at dt[ms] × pitch × ATBBar_ScrollSpeed (DAT_00f32c28, default 4.0, FUN_00561f80) × 0.001 and steps the index (+0x48) again on arrival or within one step while a flag is set; right limited to count − visible (FUN_0055f750) | CombatHud.UpdateAtbScroll (commit 3b5f43a), same rule and constants; re-checked in pass 3 | FIXED (our scroll limit is the 20-turn prediction) |
| White glowing Griffin (reported by pass 1) | (no original counterpart) | not reproduced in four samples of the seed-7 AI battle; the white puff seen there is Raise Dead's own effect | UNVERIFIED (not reproduced) |
| "Playback can only happen when a node is inside the scene tree" log line | AudioStreamPlayer.Play on a node outside the tree | not reproduced in a full AI battle run | UNVERIFIED (not reproduced) |

## Combat log

| Element | Original (evidence) | Ours | Status |
|---|---|---|---|
| Entry classes and texts | NWorld::CCombatLog* (20 RTTI classes); GameMechanics/RefTables/CombatLog.xdb COMBAT_LOG_* text lists; spells and creature abilities carry their own CombatLogTexts (Spell xdbs, RefTables/CombatAbilities.xdb) | CombatLogText builds every entry from those texts | FIXED (pass 3) |
| Self/enemy colours | FUN_00559df0: `<value=source_color>` / `<value=target_color>` = UIGameRoot LOG_PLAYER_SELF (Text/CombatLog/PlayerSelf.txt `<body><color_positive>`) when the actor's owner is the viewing player, else LOG_PLAYER_ENEMY (`<body><color_negative>`) | was fixed red/blue per side; now self/enemy via `CombatSession.ViewSide` | FIXED (viewing-player fallback when no local player, vtable+0x84(1) in FUN_0054ad10, not decoded) |
| Values filled | FUN_00559df0 sets source_color, source, target_color, target, damage, dead, spell (FUN_0055a710 adds element for bonus elemental damage); `<value=font>` is never set | `font` was filled with `<body>` (recoloured the tail); now left empty | FIXED |
| Attack / retaliation | COMBAT_LOG_ATTACK / _RETALIATE, index luck + 1, +3 for Favored (FUN_00c99f40) | luck variants; Favored (favoured enemy) variants not produced | FIXED (Favored: GAP) |
| Spell cast entry | CCombatLogCastSpell selector FUN_00c9a440: no target CastSpell, target without damage flag CastSpellTargeted, with flag damage > 0 TargetedDamage, 0 NoDamage, < 0 DamageInverted | same for casts; plain entry dropped when the spell then damages | FIXED |
| Spell damage | FUN_004e6600 (single target) and FUN_0097efd0 (per affected unit) create CCombatLogCastSpell with the damage flag → "X casts S on T: D damage (K died)." per target; spells with own CombatLogTexts use CCombatLogSpell (FUN_00c99fd0, index 0 for damage, clamped) | was an invented "T: D damage (K died)."; now CastSpellTargetedDamage per target or the spell's text (Firewall, Plague, Sun Fire, …) | FIXED (DamageInverted heal entries and whether area spells also write a plain cast entry: not traced) |
| Ability entries | CCombatLogCombatAbility FUN_00c99fa0: ability CombatLogTexts[0]; COMBAT_LOG_APPLY_ABILITY FUN_00c9a240: ApplyAbility / ApplyAbilityTargeted | own text when the ability has one (Cleave, Bash, Deadly Strike, Acid Blood, …; damage texts from the damage event); texts needing a spell name (Purge, Hexing, Whip) and abilities without texts use ApplyAbility | FIXED (spell-name values: GAP) |
| First Aid Tent | SPELL_EFFECT_FIRST_AID_TENT_HEAL CombatLogTexts [LastAid, Heal]; CCombatLogSpell index 1 for the non-damage flag (FUN_00c9a6a0) | "First Aid Tent: D hitpoints of T healed (K resurrected)." (was "uses ability FIRST_AID") | FIXED |
| War machine names | CreatureVisual CreatureNameFileRef via RefTables/WarMachines.xdb Visual | same (was "BALLISTA"); also in the creature tooltip and info panel | FIXED |
| Remaining invented lines | summon "X (N) appear.", "Actor: reason" for skipped turns, draw end text, hero skills (COMBAT_LOG_HERO_USES_SKILL / _SPECIALIZATION_WORKS have no texts; e.g. "uses ability BENEDICTION"), bonus elemental damage (BonusFire/Cold/Elemental texts; Hellfire perk falls back), Resurrect/Resurrect Allies/Regeneration heal texts | not built from the exe's entries | GAP |
| EndCombat | COMBAT_LOG_END_COMBAT [PVP, Win, Defeat]; selector not traced | EndCombatPVP always | UNVERIFIED |

## Not yet audited

ATB bar hover/click (IconContainer is used by 0055df40 / 007af660 / 007afe50; handlers not traced),
spellbook targeting flow (mass spells, cast on cell, invalid-target messages: the SpellStrings texts exist
but are not shown), creature ability activation flows, combat log behaviour (scrolling, hover): **UNVERIFIED**.
