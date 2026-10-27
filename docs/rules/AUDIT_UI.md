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

## Display

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Virtual screen | 1024×768, wider aspects 768·aspect × 768 (00b295d0, logs "virtual screen resolution"); the UI is laid out in virtual px and drawn × screen/virtual (00b29360) — at 1920×1080 the original UI is 1.406× | auto UI scale = largest step ≤ height/1080 (docs/DISPLAY.md) | GAP (decision for the coordinator: the exe's equivalent auto scale is height/768) |
