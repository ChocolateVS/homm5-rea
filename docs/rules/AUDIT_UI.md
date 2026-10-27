# General UI audit (text layout, placement, shared widgets)

Ground truth: stock data (`UI/**`, `bin/fonts/*`) and H5_Game.exe (headless Ghidra, private project
`tools/ghidra-projects/uui`, helper `tools/uui/ghidra.sh`, scripts in `tools/uui/scripts`).
Status: **MATCH** (already identical), **FIXED** (changed to match), **GAP** (not yet done), **EXT** (our extension).
Code: `src/H5.Core/Ui/UiTextLayout.cs` (layout), `UiMarkup.Tokenize` (stream), `UiFont.cs` (fonts),
`UiLayout.Axis/PlaceText` (placement), `game/src/Ui/BitmapText.cs` (drawing), `UiNode.DrawText`.

## Markup stream (CMLStream 00cc0820)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Token categories | outside tags: ' ' = space run, '\n'/end = break, '\r', '<', '>'; everything else (letters, digits, punctuation, tab, NBSP) is a word character; a token ends where the category changes | `UiMarkup.Tokenize` | FIXED (words split at spaces only, with the space glued to the word) |
| Space runs | one space element per run (`vt20(2, …)`), measured with the space glyphs | same | FIXED |
| Line breaks | a run of '\n' is ONE break; '\r' is dropped; a '\r' after a tag, at the start or after a '\r' is dropped together with the next character ("&lt;br&gt;\r\n" breaks once) | same | FIXED (every '\n' broke) |
| End of text | always one closing break (a trailing "\n" adds nothing, a trailing &lt;br&gt; adds an empty line) | same | FIXED |
| Tags inside words | tags end the current token, so a tag splits a word into two elements (they wrap independently) | same | FIXED |
| Macros, &lt;value&gt;, &lt;hotkey&gt; | text spliced into the stream at the tag (00cbf0d0 inserts at the read position) | sources stack | FIXED |
| Default state | left, vertical middle, `System` 16pt (ml_text_default_font_name, 00cc41e0), white (builder ctor 00cc0530) | `UiMlState` defaults | FIXED (was Default 18, colour C5BEA1) |
| Alignment/wrap modes | one field: left 1, right 2, center 3, nowrap 4, justify 5, wrapleft 6, wrapright 7, forcewrap 8, wrapdots 9 (handlers 00cc2b60..00cc2d40, 00cbf580); nowrap therefore left-aligns | `UiMlMode` | FIXED |
| &lt;top&gt;/&lt;bottom&gt;/&lt;middle&gt; | per-element vertical alignment inside its line (0/1/2, 00cc2d90..), default middle | per element | FIXED (was a block-level override) |
| &lt;font&gt; | size=N (no unit = pt, scaled with the screen; px/fx absolute), face, outlinesize, outlinecolor, forcefontsize; any argument first resets the outline (size 0, colour = text colour) (00cc3a80) | same | FIXED |
| &lt;color&gt; | exactly "color = value", names table 00cc3610 | same | MATCH |
| &lt;push&gt;/&lt;pop&gt; | state stack (CStateStack, builder 00cc0380/00cc01e0) | same | MATCH |
| &lt;tab&gt; | element to the next multiple of 4 average widths: (round(pen/u)+1)·u − pen (00cc0130) | same | FIXED |
| &lt;wrapdots=…&gt; | dots text from the tag; overflow cuts after the last element the dots still fit behind, appends dots + break, re-lays out (00cc0f92, 00cc1f90) | same | FIXED |
| &lt;forcewrap&gt; | words wider than the line are split per character (00cbff00) | split at the last fitting character | FIXED (split rule approximated: count via re-measure) |
| &lt;wrapleft&gt;/&lt;wrapright&gt; | elements float at the margins from the next line (00cc03d0) | same | FIXED |
| &lt;image&gt; / &lt;minfontsize&gt; | inline image (00cc4c00); minimum size | minfontsize parsed; images not drawn | GAP (no stock text uses either) |
| ml_force_split_words | CJK per-character splitting (widechar.chk) | — | GAP (not used by the English data) |

## Fonts (00cbf650, 00a54310, 00cbf8b0)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Glyph header fields | serializer 005126e0: 10 height, 11, 12 avg char width, 13 max char width, 14 (byte), 15 default char, 16 internal leading, 17 ascent | `UiFont` | FIXED (13 was read as the line advance) |
| Font choice | same logical name (exact), smallest nominal (height − internal leading) ≥ size, else the largest; unknown face → System 16 | `UiFontTable.Find` | FIXED (was nearest size) |
| Glyph scale | every metric × size / nominal (Header 28 = 32 atlas × 0.875) | same | FIXED (atlas drawn unscaled) |
| pt sizes and UI scale | pt size × min(screen/virtual) truncated (virtual screen 1024×768, wider aspect = 768·aspect wide, 00b295d0) | pt size × our UI scale | FIXED / EXT (our UI scale stands in for the screen ratio) |
| Glyph placement | x = pen + (forcefontsize ? 0 : o) + (kern + A)·s, quad (w·s + o) × (h·s), pen += B·s (+ o without forcefontsize); pen starts at o | same | FIXED |
| Element heights | height (cell − internal leading)·s (+2o without forcefontsize), visual height cell·s + 2o | same | FIXED |
| Outline | 8 copies (0,±o), (±o,0), (±o,±o), drawn before the glyph, o = outlinesize × screen ratio | same | FIXED (was 4-way) |
| Tab character | pen = (⌊round(pen/u)/4⌋·4 + 4)·u, u = avg width·s (+2o) | same | FIXED (was half the area) |
| Kerning | table at +0x28 (empty in stock fonts) | 0 | MATCH |
| Single-line measure (StockTooltip) | — | uses the widest line, not the alignment-offset block width | FIXED (follow-up to the layout change) |
| Filtering | — (D3D) | bilinear when a glyph is drawn at another size than its atlas, else nearest | EXT |

## Line layout (CMLLayout 00cc0c20, line finish 00cbea10, newline 00cc06b0)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Wrap test | element width + pen > line width and pen > 0 → new line first; the width is the advance (not the visual extent) | same | FIXED (measured word + trailing space) |
| Spaces at line ends | a space at a line start is dropped; a line keeps its trailing space (it counts in centring / right alignment) | same | FIXED |
| Horizontal alignment | by the line's first element: right = right − visual width; centre = round-half-even((width − visual)/2); justify gaps = free / (n − 1), not on a paragraph's last line | same | FIXED |
| Block width | max over elements of x + advance (visual extent for the last element of a line) | same | FIXED |
| First line | elements at the pen y; y advances by the max visual height (cell) | same | FIXED |
| Later lines | elements internal-leading higher; y advances by the max height (minus ceil(outline × ratio) when forcefontsize) | same | FIXED (pitch was max(field 13, cell): Default 18 pitched 25 instead of 18, Button 37) |
| Empty lines | advance by the previous line's height (the first, empty, advances 0) | same | FIXED |
| In-line vertical alignment | top: y0; bottom: y0 + line h − h; middle: y0 + (line h − h)/2 | same | FIXED (baseline by field 17) |
| Reported size | GetSize 00cbe970: round-half-even(value + 0.5) per axis, converted to virtual px | `UiTextBlock.RoundedWidth/Height` | FIXED |

## Text block placement (ForegroundTextString 00b8fb40, text view 00b8f5e0, 00a4d7b0)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Wrap width | the full window width (00b8f700 / 00b8f7b0: rect width) | `UiNode.DrawText` | FIXED (was the text placement's Size) |
| Block position | per axis only when the text placement's HorAllign / VerAllign is set (Second = true): placed like a window with the block's size; otherwise the window's left/top edge, Position ignored | `UiLayout.PlaceText` | FIXED |
| Placement struct | Position +4, VerAllign +0x10/+0x14, HorAllign +0x18/+0x1c, Size +0x20, LowerMargin +0x2c, UpperMargin +0x38 (reader 00bdf970) | — | MATCH (doc) |
| Enum | EPA_LOW_END 0, ERA_CENTER 1, EPA_HIGH_END 2, EPA_MARGIN 3 (00bda180); "ERA_CENTER" is the real name | same | MATCH |
| Centre rounding (all windows) | round-half-even((parent − ⌊size⌋)/2) (00a4d630) | `UiLayout.Axis` | FIXED (integer division: 1 px off for odd differences ≡ 3 mod 4) |
| Draw position | glyph quads at block origin + element pos, float coordinates | snapped to whole screen pixels | EXT (rasterizer detail) |

## Buttons (CWindowMSButton, button code 00b91700..00b96800)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| AutoChangeState | instance field +0xfc (reader 00be4a60); on the trigger (push 00b924d0 / release 00b929f0 by TriggerMode) a left click first advances the state when set (IButton slot 2 = 00b92e00: (state + 1) mod count), then runs CommandsOnEnterState of the state the button is in; right click runs CommandsOnRightClick | `UiWindow.AutoChangeState`, `UiNode.Activate` | FIXED (every multi-state button advanced, so AutoChangeState=false buttons such as the marketplace rows ran the wrong state's commands) |
| Radio groups | in a ButtonGroupID group a selected button (state 1) does not advance; otherwise the group's previous button is reset silently (slot 3, no commands) (00b910d0); the commands still run | same (siblings form the group) | FIXED (a selected group button ran nothing) |
| Caption placement and format | 00b91fb0 (button text update): when the visual sub-state has a TextFormat (accessor 00b93ba0), its Placement (+0x48) places the caption (FUN_00a4dc00) and its format string replaces the text string's format when not empty (+0x8c ≠ +0x90); otherwise the text string's own placement / format | same (`UiVisualState.HasTextFormat / TextPlacement`, `UiNode.TextPlacementOf`) | FIXED (sub-state TextFormat placement was ignored and the text string's format won: MenuButton / Common Button / Tavern button captions, whose TextFormats set VerAllign ERA_CENTER, sat low in the button) |
| Pressed caption offset | — (no offset found in 00b91fb0; sub-state TextFormats in the data carry no Position) | none | MATCH (as far as traced) |

## Common boxes (NUI::CCommonBox, 00676290 / 00676de0)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Box types | 0 N_BOX2P, 1 N_BOX1P, 2 N_BOX0P, 3 N_BOX2P2, 4..7 N_BOX0P2; type 4 OK/Cancel, 5 OK_NoBind/Cancel_EnterEsc, 6 Yes/No, 7 Yes/No_NoBind | — | MATCH (doc) |
| Question box | generic helpers 00677d80 / 00677e40 open type 2 (message) or 4 (question): OK / Cancel at the foot of `box0p2` (650×681 in the data, the size of the original box in 1024×768 virtual px) | `AdvDialogs.YesNo` | FIXED (showed the type-6 Yes/No buttons, which sit mid-box at y 233, leaving an empty half box) |

## Level-up box skill info (006a8f50)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Tooltip on offered skills/perks | each Skill%d button gets NWorld::SHeroSkillTooltip (00a33960) through a NUI::CClickingTooltipCreator (006b7a90, window vfunc +0x74): shown while the right button is held | `HeroSkillTooltip.Attach` in `AdvDialogs.LevelUp` | FIXED (nothing was shown) |
| Tooltip window | UI/Tooltips/HeroSkills (005fdd50): Name (value name), Description (value description), Allows_Provided = related text SkillText "Allows to learn abilities:" / AbilityText "Provided by:", SkillList rows (icon + name, Learned / NotLearned format) | same | FIXED |
| List content | perk: class prerequisites (SkillPrerequisites for the hero class) or the basic skill; skill: list built by 00876150 | perk as traced; skill: its standard perks | GAP (00876150 not traced) |
| Tooltip size | tooltip manager (UI/Tooltips/Main TooltipContext) | width from the Name row (230 + 2·13) | GAP |

## Spellbook pages (NUI::CSpellBook 00682000..0068b000, SpellBookDesc)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Spread | six spells per spread from the school's first-spell index (+0x190[school]); Next (MB_Button_Next / input_next_page, 00686020) +6 while index + 6 < count, Prev (00686080) −6 while > 0 | `SpellBook3D` | MATCH |
| Page parts during a turn | 00685270: forward First ← old spread, Second ← new; backward First ← new, Second ← old; without a turn First ← spread, Second empty. Model check: First = static left page + turning sheet front, Second = sheet back + the page under the sheet | same | FIXED (pages alternated First/Second by parity, so after a turn the book showed the old spread again: "pages don't flip") |
| School change | 00685f60: index reset to 0, the page turns forward when the new bookmark lies after the old one, else backward; same school toggles a flag only | same | FIXED (no turn / parity hack) |
| Keyboard | input_a2.cfg spellbook_screen: A/LEFT previous, D/RIGHT next page; W/S/UP/DOWN bookmark ∓1 ((school ± 1) mod 7, 006860c0/006860e0) | pages | FIXED; bookmarks GAP |

## Hero screen layout (UI/HeroScreen3, setup 006ca4e0)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Pages | 006ca4e0: left page `HeroPage_Self2` when 006d0940 (screen +0x720 set) or a second hero is shown (006cbde0, +0x1b4), else `HeroPage_Self`; right page `HeroPage_Meet` (shown only with the second hero, 006c2710) | `HeroScreen._Ready` | MATCH |
| Bookmarks | one group drives the screen: `Bookmarks_HeroMeet` with a second hero, else `Bookmarks_HeroSingle` (its buttons collected by 006c8070); the other group is hidden (vfunc 0x44(0)) | same | FIXED (both groups were shown in a meeting, each switching only its own half) |
| Current page | 006c2ea0 → 006c1f70 stores one page index (+0x708) for the whole screen; 006c2710 shows Army/Inventory/Skills/Abilities/Biography of both halves for it | `ShowPageByName` on every bookmark click | FIXED |
| Backdrop | 006c8330: `Back_Single2` when 006d0940, else `Back_Meet` with a second hero, else `Back_Single`, made visible; its Layer1..Layer5 pushed to +0x4bc; 006c2710 shows Layer n only while page n is current | `_backdrop` + layers in `ShowPage` (test `InstallTests.HeroMeetingBackdropHasOneLayerPerPage`) | FIXED (no backdrop was shown: black screen behind the meeting pages) |
| `Back_Single2` / `HeroPage_Self2` single mode | chosen when screen +0x720 is set (006d0940) | never | GAP (+0x720 not traced) |
| Screen extent at 16:9 | windows are LOW_END horizontally (HeroScreen3 data), so the meeting layout spans x 0..869 virtual px; the exe's virtual screen is 768 high (1365 wide at 16:9), ours is the real pixel size at UI scale 1 | — | GAP (the global UI-scale row under Display; this is why the pages look pushed left at 1920×1080) |
| HeroInfo "Experience:" label | `Description` (UI/HeroScreen3/HeroDescription.txt "<key_value_name>Experience:") merges instance Position (132,36, flag set) with the shared VerAllign EPA_HIGH_END (enum 2, 00bda200) / size 203×26, and 00a4d630 case 2 adds the position on the high end: 128 − 26 + 36 = y 138 in HeroInfo, over the Army page's / tavern's "Attributes" header. 006bf580 / 006bed90 neither write, hide nor move Description. Placement at run time (00a50560, a window vtable slot): the window's data object (vf 8, the instance SWindow; reader 00be4230 Placement at +0x84) is read field by field — HorAllign First +0x18, VerAllign First +0x10, Size +0x20, Position, margins — with no Second test; the shared placement (SWindowShared reader 00be0380, Placement at +0x6c) must be merged into it before, but no code reading the Second flags (instance +0x90/+0x98/+0xa0/+0xac/+0xb8/+0xc4) was found (FindDisp over CMP / TEST / MOV) | data placement as is (per-field Second merge) | GAP (the merge is still untraced; the overlap stays as the data gives it) |
| 3D stand in a meeting | stand scene Arenas/Interface/Hero_screen; the exe's 006c05e0 / 006c72e0 path not traced | stand without the hero (black where nothing is drawn) | GAP |

## Tavern hero panel

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Mana | HeroAttributes/Mana = UI/HeroScreen3/HeroArmy/ManaValue, text minmaxvalue.txt "<value=Attribute>/<value=AttributeMax>" (current / maximum, as on the hero screen) | Attribute = AttributeMax = knowledge × HeroManaPerKnowledge (× Intelligence_ManaPointsBonus with the perk; Adventure.MaxMana) for the offered hero | FIXED (only Attribute was set: "10/") / the exe reads the offered hero object's mana (no object before hiring in ours) |

## Dev input for UI tests

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Synthetic clicks | — | `--click=x,y@s` from the main menu, or relative to the map being up with `--adventure=`; `--wheel=x,y,n@s`, `--press=` / `--release=` / `--move=` (`DevArgs.ScheduleInput`) | EXT (verified: market amount slider arrows / track, tavern rumours tab + pay, thieves list wheel, load list selection + definition wheel, presets grid arrow) |

## Display

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Virtual screen | 1024×768, wider aspects 768·aspect × 768 (00b295d0, logs "virtual screen resolution"); the UI is laid out in virtual px and drawn × screen/virtual (00b29360) — at 1920×1080 the original UI is 1.406× | auto UI scale = largest step ≤ height/1080 (docs/DISPLAY.md) | GAP (decision for the coordinator: the exe's equivalent auto scale is height/768) |

## Scroll bars and sliders (CWindowScrollBar vt 00e8c764, CWindowSlider vt 00e13f2c; `game/src/Ui/UiScrollBar.cs`, `src/H5.Core/Ui/UiSliderModel.cs`)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Parts | the bar builds ButtonLower / ButtonGreater / Slider from WindowScrollBarShared and appends them as children (00c40ee0, found by name in 00c40c20); the slider builds its Lever (WindowSliderShared, 0048f870) | `UiLoader.LoadParts` appends them with `UiWindow.Part`; drawn by their own button visuals | FIXED (screens drew a hand-made bar over the window, or nothing) |
| Range model | slider interface +0xf8 (vt 00e13ef8): SetRange(a,b,page) 0048f730: lo/hi ordered, page ≤ 0 = whole range, lo = hi → 0..100 page 100; scrollable only while range > page (else page = range); max = hi − page; position clamped to [min,max] (0048f440) | `UiSliderModel.SetRange/SetPos` | FIXED |
| Lever | length = len·page/(max−min+page) clamped to MinLeverSize/MaxLeverSize (0 = slider length); offset = round((pos−min)(len−lever)/(max−min)), 0 when the range < 1; placed by flags 10 (vertical) / 5 (horizontal) (0048f440); shown only while scrollable (0048f730 → lever vfunc 0x44) | same | FIXED |
| Arrows | press: special positions → index ±1, else ±ClickScrollDist (instance value when its flag is set, else shared 15; at least 1: 00c40ee0 / 00c40e80); Greater forward, Lower back (00c40780, 00c411e0); held: after 500 ms, every > 10 ms move elapsed ms × Speed (100) × 0.001 (00c40e20) | same | FIXED |
| Track click | 00490310 / 00490460: one page (one special position) towards the mouse; held, repeats after 500 ms then every 10 ms until the lever reaches the mouse; paused while the mouse is over the lever | same (mouse value = lever centred on the mouse) | FIXED / mouse offset UNVERIFIED |
| Lever drag | 004905e0 → 0048f9b0: value from the lever's offset on the free track (grab offset kept); special positions snap | same | FIXED |
| Mouse wheel | 00490460: the slider's accumulated mouse_wheel_scroll value Z (read-and-clear 00492a50) moves pos += Z × page (special positions ±1 by its sign); input_a2.cfg binds `-mouse_wheel_scroll 'MOUSE_AXIS_Z'` (inverted: wheel down = forward) with `bindconfigure MOUSE_AXIS_Z 160`, and 00492e20 scales the raw delta (WHEEL_DELTA 120) by fixed-point / 160 | 0.75 × page per notch (120 / 160), down = forward; routed to the bar under the mouse or its container | FIXED / the fixed-point → float step after 00492e20 untraced (0.75 assumes it normalises) |
| Enabling | 00c40ab0: arrows and slider enabled = scrollable && bar enabled; HideDisabled also hides them | same | FIXED |
| Special positions | slots 8–11: Set (n < 2 → 0), SetIndex pos = (max−min)·i/(N−1), GetIndex ceil((N−1)·pos/(max−min)). Market amount (006299a0): SetRange(0, max + step, step), Set(max / step + 1), SetIndex(current / step) (listener detached meanwhile); duel presets (00742230): SetRange(0, 100, 10), Set(rows − 1) | same; code-driven discrete bars (`UiScrollBar.Setup(count, pos)` = range 0..count page 1, N = count) — for the market (count = max / step + 1) this gives the same positions, lever and indices as the exe's range | FIXED / market MATCH (verified by clicks: arrows ±1 step, track one page); the option screens' ranges untraced (GAP) |
| Code-driven bars | — | `UiScrollerView` added under a WindowScrollBar binds to it and only forwards (market amount, custom game / MP options, game options scrollers) | EXT (adapter) |
| Slider sound | 0048fd60 (after a notified move, e.g. 00490460): at most every 400 ms (GetTickCount, +0x148) the slider sends the SUIGameConsts command at +0xa4 = ScrollBarSound (UI/Sounds/ScrollBar → scroll_click; field order Contexts, Console, DebugInfo, StatsWindow, ButtonClick, DisableButtonClick, Tooltip, ScrollBar) | `UiScrollBar.SliderSound` → `UiAudio.Slider` (uiconsts ScrollBarSound) | FIXED / the +0xa4 → ScrollBarSound mapping is by field order, not by the reader (UNVERIFIED) |

## Scrollable containers (CWindowScrollableContainer vt 00e94034, code 00886000..0088b000; `game/src/Ui/UiScrollableContainer.cs`)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Parts | Shared Interval / ScrollBar / Border / Selection / PreSelection / NegativeSelection (reader 00bde470); Border is the visible area (+0x158), the content window (+0x15c) moves inside it | Border clips; content control inside it; Selection / PreSelection markers live in the content | FIXED |
| Items | 008888e0: walks only the item vector (+0x160..+0x164, 0x20-byte entries filled by the add-item slot 8), each placed at the running offset along the bar's axis (vfunc 0x58 flags 2 / 1), offset += size + Interval (shared +0xcc) between items; total = content size (+0x150). No visibility test; other children of the content (hidden templates) are not stacked | `UiLayout.Stack` stacks `UiWindow.StackItems` only (`AddItem` / `ClearItems`) | FIXED (stacked every visible child of the Border, so hidden templates there took space: the RMG screen had to remove its templates) |
| Range | 00887ba0: content window takes the content size; bar SetRange(0, content size, Border height) (width when horizontal) | same | FIXED |
| Scrolling | 00887c80: content at (0, −pos) / (−pos, 0) | same | FIXED |
| Selection markers | 00887850: the marker takes the item's rect (content coordinates, flags 0xffff) and is shown; 008888e0 re-places them | `Select` / `Preselect` (rects from code) | FIXED |
| Ensure visible | 00887910 (slots 7 / 8 → 00887ab0 / 00887b10): item above the visible part → pos = item start; item end below → pos = item end − Border size; else unchanged. Selecting an item (slot 4 → 00888da0 / 00887850) only moves the Selection marker, it does not scroll | not used (the save list no longer scrolls a selected row into view) | GAP (callers of slots 7 / 8 not found yet) |
| Initial offset | 008888e0 starts the offset at the first item entry's +0x1c; slot 3 (00889590) zeroes it and relayouts; where it is set otherwise is untraced | 0 | GAP |

## Long texts in scroll containers

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Rule | a text that can exceed its box is a ResizeOnTextSet WindowTextView added as an item of a WindowScrollableContainer: the view takes its text height (wrapped at its width), the container clips it to the Border and scrolls it (ranges above); text views themselves never scroll | `UiScrollableContainer.AddItem` + ResizeOnTextSet relayout | FIXED |
| Common boxes | MsgText (ResizeOnTextSet) in ScrollableText (UI/CommonBoxes Text / Text0 containers) | item of the container | FIXED (was stretched to the box: long messages ran out of it) |
| Tavern rumours | RumorsText (ResizeOnTextSet) added to ScrollContainer (vfunc 8, 00637b50) | item of the container | FIXED (was a hand-made wheel offset, no bar) |
| Seer hut quest | DescView (ResizeOnTextSet) in DescContainer | item of the container | FIXED (was a separate text block with a wheel offset) / call site untraced |
| Tavern thieves list | 00635a60: container cleared (slot 0x2c); per category an InfoLine clone (EPA_MARGIN, 100 high) with its caption, InfoBox0 cloned per further entry into the Boxes gathering (00634580, then relayout), shown and added (slot 8) | same (`MapTavernScreen.FillThieves`) | FIXED (lines were placed by code at a wheel-driven row offset, no bar; boxes laid side by side by code) / 00635a60 returns at the first category list with < 1 entry; 005056b0 clears every category's vector and fills it only while the category is reached at the level (and has data), so the list ends at the first category without lines: FIXED (we skipped empty categories and went on) |
| Kingdom overview | 005bba30: scroll_area takes the screen's scroll_of_views bar (slot 14); 005baa00: one townview1 / heroview1 clone per town / hero added as items; 005ba510 keeps the scroll position per page (towns / heroes) | same; positions kept while the box is open | FIXED (two views per page redrawn from a wheel index) / persistence across openings (005ba510 stores into a struct outside the box) GAP |
| Mission objectives | 005b5e70: header clones (ID_SCENARIO_INFO / ID_MAIN_QUESTS / ID_SIDE_QUESTS) and QuestName buttons are items of ScrollContaner1; the entry text an item of ScrollContaner2 | same | FIXED (placed at hand-made offsets 26 / 45 px, no scrolling) |
| Save / load list | 007fecd0: saveslist cleared (slot 0x2c), one row per save added (007fe790, slot 8), the selection set through slot 4 (00cb8d50 → 00888da0: the Selection marker takes the row, 00887850; no scrolling into view) | rows drawn by code in the container content (40 px each), content size = rows × 40, the data's Selection / PreSelection markers | FIXED (rows were drawn from a wheel row offset with hand-drawn selection textures, no bar) |
| Save info definition | 007fdb40 (map saves): definition container cleared (slot 0x2c); UIGameRoot ID_SAVELOAD_DEFINITION_ITEM (created once, +0x184) shown only with a header; gathering children by name: count ("playerscount", hidden when the header's players are −1), gametype (007f8200: UIGameRoot game_type "game_type_%d" / "campain_game_type"), mapsize (007f6410: map_sizes "size%d"), difficulty (hidden when −1, 00a33740 name), mapdescr ("mapdescription"); relayout, added as the container item (slot 8). 007f7bb0 (campaign saves): ID_SAVELOAD_CAMPAIGN_DEFINITION_ITEM, count ("missionscount"), campaigndescr ("campaigndescription", ml_force_split_words / _digits_sequence options) | same (`SaveLoadScreen.ShowDefinition`) | FIXED (rows were drawn by code without the game type and with SaveInfo mapsize%d texts) / our network saves map to game_type_2 (LAN) and the campaign description's split options are not applied (UNVERIFIED) |
| Custom game wait players | 007769d0: one ListItem clone per player, shown and added to playerscontainer (slot 8) | same (rows rebuilt after each change, scroll position kept) | FIXED (rows placed from a wheel row offset, no bar) |
| Custom game wait description | 007132d0 → 00712470 (panel parts: container, gamename, buttontodesc, buttontopref, buttonslayer, preferences_container) → 00713180 → 007129e0: 007128f0 clears container (slot 0x2c) and adds UIGameRoot map_name_definition_item ("value" = map name) and ID_MAP_DESCRIPTION_TEXT ("value" = description); preferences_container gets the map name item and one game_definition_item per row via 007121b0 / 007122b0 (00711e90: name view "value" = the panel's related text, value view "value" / "addition"): players (00707330 ffa_format / players_separator), goal, size (007f6410: map_sizes "size%d"), difficulty (00a33740), time_limit (no_limit, else minutes + related "measurement"); gamename "value" = map name; then 00711d90(1): buttonslayer related texture texture0 / texture1, buttontopref enabled = !prefs, buttontodesc = prefs, container / preferences_container visibility (switch messages 00711e70 / 00711e80) | same (`CustomGameWaitScreen.FillDescription`, `UiScreen.CreateRootWindow`) | FIXED (one hand-made text block per panel overflowed its box; the panel opened on the description) / the size index source (caller of 007129e0) untraced: ours the MapSizeTable band of the map width (UNVERIFIED) |
| Duel presets grid (MPDMWait) | 00742910: one line_template clone per row (line_%d at y = line height × row); 00742230: table_slider SetRange(0, 100, 10), special positions = rows − 1 (0 if not positive), SetIndex(top row), bar enabled only when rows − 1 > 1; lines show the presets from top × columns; 00742350: the bar's index is the new top row | grid drawn by code from the top row, bound to table_slider as traced | FIXED (wheel-only row offset, the bar did nothing) / the exe keeps all rows as windows (no clipping traced) — ours shows the two rows that fit (GAP) |

## Gathering windows (CCommonGatheringWindow, vt 00e73c70; IWindowGathering vt 00e73c48; `src/H5.Core/Ui/UiLayout.cs`)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Layout | 00ac0690 (slot 0x1c → 00ac0a70 relayouts nested gatherings first): instance sMargins Left/Right/Top/Bottom/Delta, Orientation, ChangePosSize (data +0xf4..+0x10c); inner size = own size − margins, grown to the largest shown child across the axis; children from the Left (ECHO_HORISONTAL) / Top (ECHO_VERTICAL) margin, Delta added whenever the offset is past the margin; ChangePosSize: child rect (offset, cross margin, size, inner cross size) flags 0xffff, else only the stacking coordinate + size (flags 5 / 0xa); own size = stacked + margins (00a4f810 flags 0xc) | same (`UiLayout.Gather`); the gathering is re-placed in its parent with the new size | FIXED (every gathering stacked vertically without margins or Delta, e.g. the tavern info boxes, resource price rows) |

## Custom game screen text (CCustomGameCreateScreen 00774f80, map info 007074b0 / 00707920)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Map info items | 007074b0 takes mapname, mapinfo, mapdescheader, mapdesc out of the 'mapinfo' panel; 00707920 clears the panel's container and adds them in that order (all ResizeOnTextSet), then relayouts | same (`UiScrollableContainer.AddItem`) | FIXED (the templates stayed at the panel origin overlapping the header; name + description were a hand-made text block that overflowed the box) |
| Values | mapname "value" = map name; the mapinfo item's 'values' child: players, size, goal, underground (related text underground_yes / underground_no of the panel); mapdesc "value" = description | same | FIXED |
| Map rows | rows of UI/MPCreate/Maps/SingleMap (563 × 21) in the list container, Interval 0, scrolled by its bar; Selection / PreSelection markers on the selected / hovered row | same (rows drawn by code in the content) | FIXED (rows were redrawn a page at a time from a row index, highlight was a plain colour rectangle) |
| Template copies with EPA_MARGIN | — (code-instantiated templates are our mechanism) | a copied template stretched by margins keeps its laid-out size (`UiScreen.Instantiate`) | FIXED (game options check box rows were 0 wide: "Quick / Combat" wrapped per word) |
