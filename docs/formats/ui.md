# UI format (`UI/**.xdb`)

Decoder: `src/H5.Core/Ui/` (`UiLoader`, `UiLayout`, `UiMarkup`, `UiFont`, `UiPreviewRenderer`).
Tool: `h5tool ui-dump <screen.xdb> [out.png] [w h] [--all] [--hide=A,B] [--outlines]`, `h5tool font-dump [dir]`,
`h5tool glyph-dump <face> <size> <chars>`.

Status: main menu, duel lobby (`UI/MPDMLobby.(WindowScreen).xdb`) and combat HUD
(`UI/CombatScreen-Heavy.(WindowScreen).xdb`) load, lay out and render with pixel positions,
textures, fonts and text colours matching the original (the 3D backgrounds are separate, see below).

## Object model

Every window is two files:

* **instance** (`WindowSimple`, `WindowMSButton`, `WindowTextView`, `WindowStaticText`,
  `WindowScreen`, `WindowScrollBar`, `WindowEditLine`, `WindowListCtrl`, `WindowComboBox`, ...):
  `Name`, `Visible`, `Enabled`, `Priority`, `TooltipFileRef`, `Placement`, `TextString`/`TextFileRef`,
  `relatedTexts`/`relatedTextures`, and for buttons `ButtonStates`, `ButtonGroupID`, `State`.
* **shared** (`<Type>Shared`, e.g. `WindowSimpleShared`): `Children` (ordered hrefs to child
  *instances*), `Background`, `Foreground`, default `Placement`, `Flags/Transparent`, `ActiveArea`,
  `ActiveMask`, for buttons `VisualStates` and `TriggerMode` (`BCST_ON_RELEASE`/`BCST_ON_PUSH`).

Hrefs are relative to the referencing file unless they start with `/` (pak root). `#n:inline(T)`
means the object is the element's child. Root screens live directly in `UI/` as
`<Name>.(WindowScreen).xdb` (96 of them: `MainMenu2`, `MPDMLobby`, `CombatScreen-Heavy`,
`AdventureScreen-Heavy`, `TownScreen`, `HeroScreen3`, `SpellBook`, ...).

## Placement

Each field is `<First>value</First><Second>bool</Second>`; `Second=true` means "set here".
Instance fields that are set override the shared ones.

| field | meaning |
|---|---|
| `Position` | offset (px) |
| `Size` | size (px) |
| `HorAllign` / `VerAllign` | `EPA_LOW_END` (from left/top), `EPA_HIGH_END` (right/bottom edge at parent edge + Position), `EPA_CENTER`/`ERA_CENTER` (centred + Position), `EPA_MARGIN` (stretch) |
| `LowerMargin` / `UpperMargin` | for `EPA_MARGIN`: left/top and right/bottom insets |

Screens are authored for **1024×768**; `WindowScreen` with `HorAlign/VerAlign = ALIGN_SCREEN` fills
the real screen and children re-anchor through their alignments (so higher resolutions keep panels
in the corners, as in the original).

Verified example: `MainMenu2/Menu` = HIGH_END/HIGH_END, pos (-8,-24), size 327×289 → `[689,455]`.

## Z-order (verified)

Higher `Priority` draws on top. Among equal priorities **the first child in `Children` is
topmost** (Windows-style). Evidence: on the combat jog dial, buttons (priority 1) must cover the
`Jogdial` plate (0); in `MainMenu2`, `Main` precedes the visible-by-default `HotSeatSubMenu` and
correctly covers it. `UiLayout.PaintOrder` implements this; hit-testing uses the reverse.

Note that screens keep alternative panels `Visible=true` and switch them from code/reactions
(e.g. `MainMenu2` children Main / SinglePlayer / MultiPlayer / HotSeatSubMenu...).

## Backgrounds / foregrounds

| type | semantics |
|---|---|
| `BackgroundSimpleTexture` | texture drawn unscaled, aligned by `TextureX/TextureY` (all stock data: LOW_END), clipped to window |
| `BackgroundSimpleScallingTexture` | region `(0,0)-Size` of the texture stretched over the window (`Size` 0 = whole texture) |
| `BackgroundTiledTexture` | 9-slice: `rLT rRT rLB rRB` corners, `rT rL rR rB` edges, `rF` fill; each has `Size`, `Maps` (x1,y1,x2,y2 texture px), `Rect`, `Rotate`. Edges/fill tile the source region |
| `BackgroundFrameSequence` | sprite-sheet animation: `FrameSize`, `FrameCountX/Y`, `FrameCount`, `Time` (ms), `TimeFadeIn/Out`, `RandomStartFrame` |

`Color` is ARGB tint as a signed int (-1 = white). Texture xdbs give the logical `Width/Height` and
`DestName` → `.dds` (formats in use: A8R8G8B8, DXT1/3/5, R5G6B5; `src/H5.Core/Ui/Imaging/DdsImage.cs`).

## Buttons

`ButtonStates/Item` (instance) holds logic: `CommandsOnEnterState`, `CommandsOnRightClick`,
`commandsOnLDblKlick`, `Name`. `VisualStates/Item` (shared) holds per-substate visuals: `Normal`,
`MouseOver`, `Pushed`, `Disabled`, `RightButtonDown`, each with `Background`, `Foreground`,
`TextString`, `TextFormat`, `OnEnterSubState`. Multi-state buttons (toggles, checkboxes) have
several items; `ButtonGroupID` makes radio groups.

### Commands (reactions)

Command lists contain hrefs to reaction objects, flattened by `UiLoader` into `UiCommand`:

* `UISDirectRunReaction` → `ReactionForward` → `ARSendGameMessage { EventName, IntParam }` —
  a game message such as `single`, `multi`, `options`, `credits`, `main_menu_exit`,
  `Combat_Defend`, `Combat_SpellBook`, `Combat_Retreat`, `autoplace_army`...
* `UISRunReaction { szReactionForward/First }` — a named UI reaction handled by screen code
  (`campaign`, `hot_seat`, `lan`, `back`, `hero_click`...).
* `UISSendGameMessage`, `UISSendUIMessage`, `UISPlaySound`, `UISMoveTo`, `UIConsoleCommand`.

These names are the contract between the UI data and game code: the Godot client should dispatch
on them rather than hard-wiring buttons.

## Text

* `TextFileRef` / `ForegroundTextString.TextStringFileRef` → UTF-16LE `.txt` with markup.
* Format prefix: button visual state `TextFormat` → `TextFormat.FormatStringFileRef`, or
  `ForegroundTextStringShared.FormatStringFileRef` (+ its `Position` placement for the text area).
  The format string is prepended to the text (e.g. `UI/Consts/Tags/menu.txt` = `<menu>`).
* `WindowTextViewShared` also has `Color`, `Format`, `FontName`, `RedLineSpace` (rarely set).
* `relatedTexts` → `WindowRelatedTexts { TextName → text href }` for code-selected strings;
  `relatedTextures` likewise for textures (e.g. faction icons by `TOWN_*`).
* `<value=name>` placeholders are filled by code (`Version: <value=version>`).

### Markup (`UiMarkup`)

Built-in tags: `<font face= size= outlinecolor= outlinesize= forcefontsize=>`, `<color=AARRGGBB>`,
`<left> <center> <middle> <right> <justify>`, `<br>`, `<tab>`, `<push>`/`<pop>` (style stack),
`<value=x>`, wrap hints `<nowrap> <forcewrap> <wrapdots=...> <wrapright>`.

All other tags are macros defined by files in `UI/Consts/mlTags/` and its `Fonts/`, `Colors/`,
`Styles/`, `Special/` subfolders, expanded recursively. E.g. `menu` → `<h2><color_menu>`,
`h2` → `<font face=Header size=28><color_header>`, `color_menu` → `<color=FF294761>`,
`outline` → `<font outlinecolor=black outlinesize=1 forcefontsize=true>`.
**`UI/Consts/Tags/*.txt` are format strings, not macros** (`Tags/menu.txt` is literally `<menu>`).

## Fonts

`UI/Fonts/FontsTable.(FontsTable).xdb` lists 14 `Font` xdbs: logical `Name` (Default, Header,
System, Button), `Height`, source `FaceName` (Monotype Corsiva, Century Gothic, Tahoma),
`Texture` (A8R8G8B8 atlas: white glyphs, coverage in alpha) and `uid`. Glyph metrics are in
`bin/fonts/<uid>` (texts.pak — localised), Nival chunk format (see terrain.md for the container):

```
[4] version  [1]{[1] type id}  [0] ...  [2]{[1]{[0] type id, [1] fields}}  [5]
fields: [10] cell height  [11] ?  [12] descent  [13] line spacing  [14] ?  [15] default char (31)
        [16] ?  [17] ?(≈ ascent-ish)
        [1] hash_map<u16 char, Glyph>: [3] count, [4] buckets, then ALL keys (id 1, 2 bytes)
            followed by ALL values (id 2, 28 bytes) in the same order
        [2] kerning hash_map (empty in stock fonts)
Glyph = 7 × i32: x1, y1, x2, y2 (atlas px), A, B, rectWidth
```

Drawing (verified visually): blit the atlas rect at `pen.x + A`, top at line top; advance the pen
by `A + B`. The rect is wider than B on italic faces (overhang into the next cell).

Markup sizes without an atlas (h2 = Header 28) pick the nearest atlas (`UiFontTable.Find`);
confirm the exact rule against the exe if text metrics differ in screenshots.

## Screens of interest

* **Main menu** `UI/MainMenu2.(WindowScreen).xdb`: 2D layer only. The animated 3D background is
  `Arenas/Interface/MainMenu/MainMenu.(ArenaDesc).xdb` (see arena.md) — the original renders the
  arena behind the UI.
* **Duel lobby** `UI/MPDMLobby.(WindowScreen).xdb`, presets `UI/MPDMLobby/presets.(DuelPresets).xdb`,
  related: `MPDMCreate`, `MPDMWait`, `MPDMChoosePreset`, `MPDMCustomize`.
* **Combat HUD** `UI/CombatScreen-Heavy.(WindowScreen).xdb`: `ATBBar` (LargeIcon, IconView,
  ScrollLeft/Right), jog dial (`OnJog-Wait/Defend/Spellbook/Special/AutoCombat/Flee/Menu`,
  `OnJog-StartCombat`/`NextCreature` during tactics), `PredictionBox` (`<value=action_sort>...`),
  `LogWindow` (combat log, priority 5), `PlacementBar` (tactics phase slots), `RunesBar` (Fortress
  runes), `cursor_prediction` (priority 200), `dndicon` (100). The spellbook is its own screen
  `UI/SpellBook.(WindowScreen).xdb` (+ `SpellBookDesc`).

## Unknowns

* Exact meaning of font header fields 11, 14, 16, 17; nearest-size font rule.
* `ActiveArea`/`ActiveMask` (hit masks), `WindowScrollableContainer` scrolling metrics,
  `WindowListCtrl` column specs — loaded raw (`UiWindow.RawInstance/RawShared`), not modelled.
* `BackgroundTiledTexture` `Rect` and `Rotate` are parsed but unused (all stock `Rect` = 0).
* Text vertical metrics are approximate (line height = font field 13).
