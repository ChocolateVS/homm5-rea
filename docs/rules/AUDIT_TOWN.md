# Town and hero screen audit ledger

Every town-screen and hero-screen element and rule compared with `H5_Game.exe` (Ghidra 12.1.4, image VAs) and the
original UI layouts / data. Status: **MATCH** (already as the exe), **FIXED** (changed to match), **GAP** (known
difference or not built), **UNVERIFIED** (implemented from data, exe path not traced). Rows carrying two statuses
count once for each.

Rows at 2026-10-27: 93 FIXED, 45 MATCH, 25 GAP, 36 UNVERIFIED.

Commits: 9153b62 (dwelling growth incl. moon weeks), afe1e7b + 844ce53 (build check codes, town income),
6690c81 (level-up offers, experience table, Enlightenment, perk effects, hero screen fixes),
f65f434 + c6f0b5b (spell learning, blacksmith prices, zero-count transfers), 5d81148 (Town Portal, hover colour),
51d3aa3 (map-start buildings), the user-report commit (hire screen, tavern + rumours, marketplace, town camera, army rows).
Tests: `tests/H5.Adventure.Tests/TownGrowthTests.cs`, `SpellLearnTests.cs`, `LevelUpRulesTests.cs`, `TownBuildRulesTests.cs`, `TownInitialBuildingsTests.cs`, `TownHireTests.cs`, `TavernRumourTests.cs`, `MageGuildTests.cs`;
screens checked off-screen with the default MMH 5.5 data and with H5_TARGET=stock.

# Town

## Town build screen (UI/TownBuildNew) and build rules

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Build check entry | CTown slot +0xac of vtable 00e95844 = 00cd6a90; called by the build screen 0065bab0 (vcall +0xac on screen +0x24c) and by CUpgradeTownBuildingCmd (vtable 00e0d898, execute 0042fd40 → town +0x6c = 00cd4a40, which re-runs +0xac) | one function returns a code 0..10; the command refuses anything but 0 | `Adventure.BuildCheck` used by `DoBuild`, the HUD marker `BuildState` and the build screen | FIXED (was: three separate rule copies with our own error strings) |
| Code → text | switch 00659ad0, jump table 0065a36c; UI/TownBuildNew/Texts/Texts.(WindowRelatedTexts) | 0 can_build, 1 cant_build, 2 limit_upgrade, 3 already_build, 4 prereq, 5 no_money, 6 build_thisturn, 7 no_water, 8 only_one_capitol, 9 grail, 10 town_points | `BuildDenial` / `BuildDenialText`; texts read from the same files | FIXED (no_water, only_one_capitol, town_points, cant_build were missing; town level was folded into prereq) |
| Check order | 00cd6a90 | no record → 1; Stronghold (town type 10) magic guild (type 6) → 1; 00cd0e20 specials; level ≥ data max (rec +0x18) → 3; level ≥ map limit (rec +0x28) → 2; town points → 10; prerequisites (00cd6880) → 4; money (00cd4880) → 5; built today (00cce4e0, skipped by the cheat flag 010dbd84) → 6; else 0 | same order | FIXED (was: built-today first, money last; town level = prereq) |
| Town level / town points | town +0x78 = 00cd2120 (sum of rec +0x14 over all buildings); requirement +0x58 = DevLevelNeeded | town level = sum of built levels; denied when below DevLevelNeeded | `TownState.DevLevel` = sum of levels | MATCH |
| Shipyard needs water | 00cd0e20: type 3 and FUN_00c5a9d0(town) false (town branch FUN_00c5a480) → 7 | no_water | `SlipwayOf` + `IsWaterTile` (the shipyard's "deserted" test) | FIXED / exact tile test of 00c5a480 UNVERIFIED |
| One Capitol | 00cd0e20: type 0, level+1 > 3, player vcall +0xd4 (owns a Capitol) → 8, or 3 when this town has town ability 0x2e (table 00f7c998: any type, TB_TOWN_HALL level 4) | only_one_capitol | `PlayerHasCapitol` | FIXED (was not checked) |
| Grail needs the Tear of Asha | 00cd0e20 type 0xe: level = max → 3; neither town hero slot (+0x18(1), +0x18(0)) carries it (hero +0xa4) → 9 | grail | `GrailBearer` (worn or backpack) | FIXED (was: the grail was buildable for free) |
| Grail uses the Tear | 00cd4a40 type 0xe: slot 1 hero +0xa4 → +0xa8, else slot 0 | the Tear is taken from the hero | `ConsumeTearOfAsha` | FIXED / which slot is visiting vs garrison not identified (only matters when both carry one) |
| Money test | 00cd4880: FUN_009d0500(purse, cost) < 1.0 (00e0a7e4) | not affordable | `Purse.CanAfford` | MATCH |
| Building cost | 00cd4880 → 00cd4180: STownBuildingSharedStats +0x64 (Cost), else an empty purse | the data cost, no town-type or specialisation modifier (Fortress and Stronghold included) | `Db.Building(...).Cost` | MATCH |
| Fortress / Stronghold special abilities | table 00f7c998: Fortress 0x20–0x22 TB_SPECIAL_1 (runes), 0x23 TB_SPECIAL_2, 0x24 TB_SPECIAL_3, 0x25 TB_SPECIAL_4, 0x26 TB_SPECIAL_5; Stronghold 0x27–0x29 TB_SPECIAL_1 (warcries), 0x2a TB_SPECIAL_2, 0x2b TB_SPECIAL_3, 0x2c TB_SPECIAL_4, 0x2d TB_SPECIAL_5 (slave market). Consumers found: 0x24 FUN_00cd0a30 (from 00853f90 / 00bc3af0: a value = vcall 0x78 × stat +0x17c × (n / 0x1c + 1)), 0x25 FUN_00bfe590, 0x2b FUN_00ccf7b0 / 0061b760 (FUN_00614c60) / 009c4710, 0x2c player recount 004fce10 | per ability | runes, warcries, horde growth (0x23 / 0x26 / 0x2a), slave market built; 0x24, 0x25, 0x2b, 0x2c effects not built | GAP |
| One build per day | 00cce4e0: town counter +0x80 (reset to 0 on build, 00cd4a40) < vcall +0x80 = 00ccedf0 (1 for a human, the difficulty row's TownBuildPeriodInDays for a computer player) | build_thisturn | `BuiltToday` (one per day) | MATCH (human) / computer period: the consumer is this check (see AUDIT_AI "Build period"); the counter increment not traced — GAP |
| Building effects on build | 00cd4a40 | magic guild (type 6) rolls its new level (FUN_00cd1b40(level+1, 5)); Fortress abilities 0x20 / Stronghold 0x27 levels give runes (FUN_00ccfd30 / 00ccfe90); ability 0x1d (Dungeon TB_SPECIAL_4, Trade Guild) draws the extra resource FUN_00cce410 | guild learning, artifact merchant stock, Haven training points; guild reroll FUN_00cd1b40(old + 1, 5), runes 1–2 / 3–4 / 5 and warcry circle 1 / 2 / 3 at shrine / hall levels 0 / 1 / 2 (`TownSpellsOnBuild`, also on the script's free build) | FIXED (guild reroll, runes, warcries; `MageGuildTests`; Trade Guild draw `RollTradeGuild`) |
| Cell tint | 0065bab0 → 0065b190, colour table 00f37cd8 (12 bytes/state) and factors 00f37ccc / 00f37cd0 (0.5) | built 0 (255,255,255 ×1); next level: code 0 → state 2 (128,255,128) or 4 (0,128,255, condition not traced); 1, 9, 5, 6 → state 1 (255,90,90); 2 → state 3; 3 → 0; 4, 7, 8, 10 and later levels → (255,255,255) × 0.5 | `CellTint` with those colours; the 0.5 factor read as brightness of the white tints | FIXED / factor semantics (+0xfc) and the blue state UNVERIFIED |
| Tooltip | 006596a0, UI/TownBuildNew/ToolTips/ToBuild + Common/*.txt | name, description, prereq_header + every prerequisite (prereq_%d, red_val when unmet), price, "Required town level" (red when short), deny_code line (red except codes 0 and 3) | plain Godot tooltip with the same lines (unmet prerequisites marked "!") | FIXED (content) / styled window and colours GAP |
| Cell layout, arrows, hover, selection, town level caption | 0065a830 / 0065aae0, TownBuildDefinition per faction | 5×6 grid, arrows Arrow_1..3, Hilight / Selection frames, TownLevel | as before | MATCH (earlier work; not re-traced) |
| Click to build | 0065bab0 / screen messages | not traced here | select, click again to build | UNVERIFIED |
| MMH 5.5 data | MMH55-Data / MMH55-Index (509 building files, own necropolis/stronghold TownBuildDefinition) | same screen | screen renders with 5.5 (default) and H5_TARGET=stock (shots hall_55.png, hall_stock.png) | MATCH |

## Map-start buildings

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Map buildings list | AdvMapTown reader 00ad6990 (Type, InitialUpgrade, MaxUpgrade; BLD_UPG_* 00ad4650), copied to the town (+0x1a4, 16-byte entries) | per building: start level and cap | `MapLoader` → `TownBuildingInit` | MATCH |
| Cap | town init 00cd6460: record +0x28 = min(MaxUpgrade, record data maximum) | BLD_UPG_NONE caps at 0 (code 2 limit_upgrade) | `MaxLevel` = min(Max, data max) (`TownInitialBuildingsTests`) | FIXED (MaxUpgrade NONE meant "no limit") |
| Start level | 00cd6460: level map = min(InitialUpgrade, cap), then town vfunc 0x6c (build, 00cd4a40) that many times | built through the normal build path | level set directly; guild rolled by `RollMageGuild`, dwelling pools filled | MATCH (levels) / build side effects at start UNVERIFIED |
| Town hall | 00cd6460: shared Type (+0xfc) ≠ 0 → hall level = max(level, 1) | every town starts with at least the Village Hall | same (also maps or RMG towns without a list) | FIXED (was 0 when the map gave none) |
| Buildings not in the list | record cap from the data record (creation not traced) | — | cap = data maximum, level 0 | UNVERIFIED |
| User report "everything is built from the beginning" | A1SM1 (MMH55-SPMaps.h5u) towns list only TB_TOWN_HALL BLD_UPG_1; MMH55 H55_ConstructPlayerTowns / _NeutralTowns run only with H55_BuildPlayerTowns / _BuildNeutralTowns ≠ 0 (H55-Settings.lua: 0) | hall only | hall only: engine state, 3D scene (all other ModObjects at level 0) and build screen (shots/town2.png, hall.png); `--town-build-all` is dev only | MATCH (not reproduced; maps such as the user's TrueCastles.h5m start with many buildings by design) |

## Town screen: 3D scene, hotspots, hover

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Building pick | 0061a5d0: picked object must be a CArenaTownModObject whose ModObjectName matches a built building record (screen +0x3ec lookup) | hovering a built building's scene object | ray pick on the building's collision, `ViewFor` (highest built level with that ModObjectName) | MATCH |
| Hover highlight colour | 0061a5d0 with param_3 = 0: highlighter (screen +0x174, vcall +4) colour (0.3961, 0.2196, 0, 0) | brownish-orange highlight | additive overlay of the same colour | FIXED (was (1, 0.9, 0.6) × 0.18) |
| Hover name line | TownScreen BuildingDesc | building name, town name when nothing is hovered | same | MATCH (earlier work) |
| Open on double click | AUDIT_INPUT (click_pointer 00613130 no-op, dblclick_pointer 00619a30) | double click opens | same | MATCH |
| Building tooltip extras | 0061a5d0 switch on the town ability: 0xc / 0xd mystic_pond_bonus, 0x1c ritual_pit_info_witches / _minotaurs, 0x1d trade_guild_income_bonus / trade_guild_this_week_bonus; shipyard_deserted; default name + description (006b9c10) | per-building extra lines | name + description only (right-click tooltip, AUDIT_INPUT) | GAP |

## Town camera

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Camera on opening | 0061c5b0; DAT_00f36490 `town_enable_flyby` (registered by 0061fc10, user_a2.cfg = 1); +0x294 = 0 → 00616d20, else 00616bf0 | fly-up animated camera, else hand camera | same (`StartTownCamera`; dev `--town-no-flyby`) | FIXED (was static initialCamPos) |
| Fly-up camera | 00616d20 → 007d1920 / 007d2140; FOV and clip from initialCamPos | follows the cameraFlyupRole "camera" bone; idle00 is a long tour (Necropolis 53.9 s) | same, looped | FIXED / looping and rig placement UNVERIFIED |
| End of fly-up | 0061bc00 / 006199f0, building/HUD handlers 00618170…00619e80, 00619a30; each sets +0x294 = 1 → 00616bf0 + 00617690 | first left press/release or any action → hand camera at initialCamPos | left press, OpenSub, OpenHero | FIXED |
| No hover during fly-up | 0061b140 | no pick or highlight while +0x294 = 0 | pick skipped | FIXED |
| Default pose | CTownCameraConfigurator vtable 00e446d8, +0x20 = 007d3ad0 → 007ce1a0 | Pos = eye; Rot.x/Rot.y = pitch/yaw; orbit point = Pos + Rod·forward | same | MATCH (far plane kept ≥ 3000 for the sky dome) |
| Projection | 00cf7c30 | FOV horizontal when H/W ≤ 0.75 | `FrontEnd.ExeProjection` (also the hero stand) | FIXED (was vertical: 16:9 looked zoomed out) |
| Operator update | CTownOperator vtable 00e449bc, ctor 007d6c20, update 007d5550 (step ≤ 100 ms) | yaw += 0.7·input, free; pitch += 0.5·input clamped to Min/MaxPitch; orbit-point height += 15·input clamped to MinZ..MaxZ; rod += 15·input clamped; X/Y fixed | same | FIXED (invented speeds, eye-height clamp, no keys) |
| Glide | 007cccb0 / 007ccc10 | shown pose glides to the target (anchor eps 0.03) | same, combat constants | FIXED (was instant) |
| Bindings | input_a2.cfg common camera section | right-drag rotate/pitch; middle or Ctrl+right drag rotate/raise; wheel, PgUp/PgDn zoom; arrows; Ctrl+arrows | same | FIXED / input scale UNVERIFIED |
| Limits records | 007d3bd0; ArenaDesc reader 00932530 (+0x114, +0x11c); 00617690 → 0061f7a0 | CameraLimits inline or in its own file; none → defaults 007d5990 | separate files resolved | FIXED |
| CameraLimitsSet | 007d5550 blend loop | height-dependent rod / pitch limits blended | same (Fortress) | FIXED (was ignored) |
| HOME | 0061bcf0 → 00616300 → 006153e0 | snap to initialCamPos, glide back to the unchanged target | same | FIXED / not observed in the original |

## Town income

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Town income function | CTown 00cd43f0 (slot +0x38 via 00cd90b0), silo part 00cd31b0 | — | `Adventure.TownIncome` | FIXED |
| No hall, no income | 00cd43f0: building interface +0x34(0) = 0 → return | a town without a hall yields nothing (no silo, grail, …) | same | FIXED |
| Hall gold | jump table 00cd4658 | 1 → 500, 2 → 1000, 3 → 2000, 4 → 2000 + 2000 (two stats counters) | 500 / 1000 / 2000 / 4000 | MATCH |
| Specialisation gold | 00cd44a2: specialisation +0x5c | 0x19 TOWN_BONUS_ELVEN_CAPITAL +500, 0x22 TOWN_BONUS_250_GOLD +250 | same | FIXED (was missing) |
| Resource silo | 00cd31b0: TB_MARKETPLACE level > 1 → TownTypesInfo ResourceSiloIncome (+0xa4) | silo income | same | MATCH |
| Extra ore / wood | 00cd3255: inside the silo branch, specialisation 7 → +1 ore, 8 → +1 wood | only with the silo | same | FIXED (was missing) |
| Per-town extra income | building interface +0x88 = 00cce3c0: gold +0xe0, resource [+0xe4] += +0xe8 (town +0x1d8 / +0x1dc / +0x1e0); daily handler FUN_00cd2b90 calls it with the gold pointer 0, adds the result to the owner's purse (player vfunc 0xdc), then FUN_00cce410 | resource = 008e7840(H(world seed, town +8, world +0x58 week) via 008e7930, 0, 5), amount 2 for wood / ore else 1, only with ability 0x1d (Dungeon TB_SPECIAL_4); drawn again every day after paying, on building it (00cd4a40 at 00cd4d89) and at start | `RollTradeGuild`, paid in `DailyIncome` (`TownBuildRulesTests`) | FIXED / town +8 (world object id) taken as the map object index — UNVERIFIED; the moon week's effect on it and on the town income (inside vcall 0x84?) UNVERIFIED |
| Trade Guild bonuses | 00cd0190 (town interface vtable slot at 00e958d0): with ability 0x1d, by FUN_00ca63d0(object) = 0 / 1 / 2: gold +0xe0 += 25 / 50 / 100, amount +0xe8 += 1 (type 0 only for wood / ore) / 1 / 2 | trigger and the gold payer not traced (the daily payer passes no gold pointer) | not built | GAP |
| Rare resource specialisations | FUN_00cd2b90, jump table 00cd2cc4 on specialisation +0x5c 0x1e..0x21 | TOWN_BONUS_MERCURY / _SULFUR / _GEMS / _CRYSTALS: +2 of that resource a day, straight to the purse | `TownDailyExtras` | FIXED (was missing) |
| Grail gold | 00cd4568: +0x34(0xe) > 0 → 0x1388 | +5000 gold | same | FIXED (was missing) |
| Taxpayer | 00cd4593: town list +0x74 (garrison stacks), FUN_00b61ba0(0x46 = ABILITY_TAXPAYER, creature) → + count | 1 gold per Taxpayer creature in the town's garrison | same (`t.Garrison`; a garrisoned hero's army UNVERIFIED) | FIXED |
| Academy Treasure Cave | 00cd4623: town ability 0x18 (table 00f7c998: Academy TB_SPECIAL_4) → +500 | +500 gold | same | FIXED (was missing) |
| Moon week | FUN_00bcda40 / 00bcda60 / 00bcda80 / 00bcdaa0 per component | gold ×2 (0x3a) / ×3 (0x37); wood/ore 0x3c; mercury/sulfur 0x39; crystal/gems 0x38 | applied to the summed income (moon helper's area) | UNVERIFIED |
| Town Info "income" | 005b64c0 / 006650c0: town +0x38(purse, 1, …) gold | whole town gold income | `TownInfoScreen` shows `TownIncome` gold (without the moon factor) | FIXED (was: hall only) |

## Fort, Citadel, Castle

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Growth | growth tooltip lines 006dc5e8.. (T_GROWTH_WEEKLY, _CASTLE, _HORDE, _DWELLING, _SPECIALIZATION, _GRAIL, _SACRIFICE, _MOONWEEK_*) | castle line in the growth record | fork B's growth record (Adventure.Growth.cs) | see fork B rows |
| Siege | combat ledger (EXE_FINDINGS walls/towers, Siege.cs FortLevel = TB_FORT 0..3) | fort level → walls, towers, moat | `SiegeSetup` | MATCH (audited in AUDIT_COMBAT) |
| Town Info fortification text | 006650c0: town +0x34(1) = 0 → FortificationNone, else TownTypesInfo textFort / textCitadel / textCaste | text per fort level | `TownInfoScreen` (FortificationNone + textFort/textCitadel/textCaste) | MATCH |

## Marketplace

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Rates | FUN_008e7b80 tables (Adventure.Market.cs) | three 9-row tables | same | MATCH (earlier audit) |
| Marketplace count | CPlayer +0xf8 = FUN_004fbd80 (slave market row: per town 1 with a marketplace, +1 with TOWN_BONUS_MARKETPLACE); CPlayer +0xb0 = 00500cf0 field +0x610 set only through vtable slot +0xac (00500cd0, no direct caller) | count used by the exchange | one per marketplace town, two with TOWN_BONUS_MARKETPLACE | UNVERIFIED (exchange getter's filler not traced) |
| Resource selection | UI/Marketplace2/Resource/Treasure_N / Rate_N (2 states, AutoChangeState false, select_treasure / select_rate on state 0); controller FUN_00629a90 ("TreasureButton_%d"), 0062e784 ("RateButton_%d") | clicking an unselected treasury / rate row selects it; the selected row does nothing | `Clicked` hook reads the state the click left; only state 0 selects | FIXED (was: no click selected anything, so the market was unusable) |
| Give / get setters | model 0062b6c0 (give = n, amount = 0), 0062b6e0 (get = n, amount = 0), n in 0..6 | each choice resets the amount; the other choice is kept | same; ours also clears get when it equals the new give, and refuses get = give | MATCH / get = give case UNVERIFIED |
| Amount arithmetic | 0062b5f0 (step = rate, 1 for gold), 0062b630 (max given = purse/step·step), 0062b5b0 / 0062b670, 0062b7f0 (set received: clamp, given = received/rate or rate·received) | amount stored as units given | stored as received, cost = received·rate, gold rounded to whole rate steps | MATCH (equivalent) |
| Max button | 0062b9a0 ExchangeMaximumMessage (AmountToGive, AmountToGet, ResourceToGive / ResourceToGet) | confirmation, then exchange of the maximum | same texts and values | MATCH |
| Exchange | 0062bb40: command (give, get, amount, market count via +0xb0), amount = 0, MARKET_EXCHANGE_SOUND | trade, amount reset, sound | `ExchangeResourcesCmd`, amount reset; resource bar refreshed | FIXED (resource bar stayed stale) / sound UNVERIFIED |
| Other bookmarks (Mail / hero artifacts pages) | UI/MarketplaceAndMail ActivateMail / ActivateHero1Artifacts / ActivateHero2Artifacts | send resources / artifact trade pages | hidden; bookmarks not wired | GAP |

## Town portal

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Target town | 00427280 (cast cmd) → 008656e0 case 0x33 → 00865170 → FUN_00ccf6e0 | the owner's town (player +0xc4 list) with the smallest dx² + dy² + (DepthBetweenFloors (RPGStats +0x6c) × Δfloor)²; strict <, so the earlier town wins ties; no choice | `PortalTown` same (`AdventureSpellTests`) | FIXED (was: movement-cost distance, 10⁶ floor penalty, any town when given) |
| Arrival | 008648b0: town hero slot +0x18(0) empty, else nothing happens | cast only when that slot is free | refused when the entrance is occupied | MATCH (slot ↔ entrance identity UNVERIFIED) |
| Movement | hero +0x100(0) after the jump | movement points to 0 | same | MATCH |

## Tavern

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Hire price, offers, filter | AUDIT_ADVENTURE tavern rows (00b4ced0, 00636a10, 00634de0) | — | unchanged | MATCH (verified, see AUDIT_ADVENTURE) |
| Town tavern screen mode | 006186b0 → 006371b0 → CCreateTavernScreen 006393e0 (+0x24 = 1) → 0063a8c0 → 00638670 (param_5 = 1: +0x1b0, +0x260) | town flag on: heroes, rumours | `TavernScreen` (own file) in town mode; town HUD hidden | FIXED (was: hire by double-clicking the face, no jog dial) |
| Pages / bookmarks | 00634220..00634260 (+0x184 = 0..3), page keys 00634270 / 006342a0, initial page 00637b50, empty-page rule 006372f3 | Hero1/Hero2 (with an offer), Rumors (tavern flag), Thiefs (thieves level > 0); opens on hero 1, else rumours | same | FIXED (Rumors could not be selected) |
| Hire button | jog dial 00783f10, button 2 message hire_hero / pay (00637b50), enable 00636d20, action 006350b0 | Buy on the jog dial, enabled when 004f4570 = 0; tooltips hire_result_* | same | FIXED (there was no hire button) |
| Town hire status | 004f4570 / 004f41b0 | 1 player vfunc 0x150; 2 no tavern; 3 gold; 4 no free town hero slot; 5 hero limit | `TownHireStatus` 2/3/4/5; 4 = entrance occupied | FIXED / GAP (status 1 not modelled; the town hire command 00639060 / vtable 00e1976c not traced) |
| After hiring | 006350b0: refresh flag +0x18c, HIRE_HERO_SOUND, callback +0x268 | screen refreshes | screen stays open and refreshes | UNVERIFIED (+0x268 callback) |
| Rumour price | 00b4c260 = RPGStats +0x54 = DefaultStats RumorCost | 100 gold (3.1 and MMH 5.5) | `RumourCost` | FIXED (was "There are no rumours this week") |
| Buy rumour | CRerollRumourCmd (RTTI; vtable 00e197b4, ctor 00639170), sent by 00635320; execute 004f4750: player vfunc 0xe0 pays, then the list's vfunc 0 rerolls; BUY_RUMORS_SOUND | pay, get a new rumour | `BuyRumourCmd` pays and appends one rumour to `TownState.Rumours` (`TavernRumourTests`) | FIXED |
| Rumour text | 006372f3 case 2: FIRST_RUMOR / RUMORS_FORMAT (UI/Tavern/RumorsFormats), RumorsText in a ScrollContainer, pay enabled when price ≤ gold | every entry with a separator | same | FIXED / order UNVERIFIED (oldest first) |
| Rumour list owner | 00635320: player vfunc 0x90 → town → vfunc 0x60 | per town | `TownState.Rumours` | UNVERIFIED (reset not traced) |
| Rumour generation | CRumours (vtables 00e549ac/cc/f0), generator 008e85b0 (RUMOUR_FORMAT_PLAYER_* / _OBJECT_*, weight 100, duplicate test 008ea5d0); CommonGameRumours; MapRumours (00a0b4b0) | player / object rumours plus common and map rumours | weighted draw from CommonGameRumours only | GAP |
| Thieves page in town | 006393e0 +0x2c = vcall 0x138; 00637b50 enables Thiefs when level > 0 | level from that call | bookmark disabled | GAP |
| Hero info button | 006c44f0 | the offered hero's screen | disabled | GAP |

## Computer build period (found, not built)

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Build counter | 00cce2b0 (daily tick: counter = min(counter + 1, period)), reset to 0 by 00cd4a40, checked by 00cce4e0 against 00ccedf0 (1 human / TownBuildPeriodInDays computer) | a computer town builds again only once `period` days have passed since its last build | AI gate `(day − 1) % period == 0` in OriginalAdventureAI | GAP (initial counter value not traced) |

## Dwelling growth and recruiting

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Growth record | FUN_00cd5180 (fill), FUN_00bce140 (moon part), hire tooltip 006dc57d (T_GROWTH_WEEKLY/_CASTLE/_HORDE/_DWELLING/_SPECIALIZATION/_GRAIL/_SACRIFICE/_MOONWEEK_*) | 8 parts summed: weekly, castle, horde, dwelling, specialization, grail, sacrifice, moon week | `Adventure.GrowthOf` → `TownGrowth` (same 8 parts, Total) | FIXED (was: base ×1.5/×2 × moon multiplier) |
| Weekly (base) | 00cd5180: creature +0xa8 of the dwelling's base creature | WeeklyGrowth of the base creature | same | MATCH |
| Citadel / Castle | 00cd5180: TB_FORT level 2 → weekly/2, 3 → weekly | integer half / full | same | FIXED (was float ×1.5 over the sum) |
| Horde buildings | FUN_00cce610 over town ability table 00f7c998 (town type, TB, level) | Farms peasants +5, Spawn of Chaos demons +2, Halls of Horror nightmares +1, Unearthed Graves skeletons +6, Dragon Tombstone bone dragons +1, Blooming Grove pixies +4, Treant Saplings treants +1, Treasure Cave genies +2; Arena brawlers / TB_SPECIAL_5 rune mages / Garbage Pile goblins from DefaultStats TownBuildingBonuses (RPGStats +0x154/+0x158/+0x18c) | `HordeBuildings` table; stats from `adventure/TownBuildingBonuses/*` | FIXED (was missing) |
| Dwelling part (town +0x154 list) | FUN_00ccf230 (ECX = town base, 00cd534c); FUN_00cd0710; filler FUN_00cd4230 (from the start setup 00cd6460, only when global record FUN_004a4f20 +0xfc ≠ 0) | +0x154 holds 16-byte entries {value, creature list} parallel to the pool list +0x148; FUN_00cd4230 pushes one entry per dwelling building 7..13 whose town +0x160 record (key from town vfunc 0xf8+0x5c) lists building stats with creatures at +0x9c / +0xa0, the value always 0 (ESI = 0 stored at 00cd42f0, push 006e4ad0 at 00cd43a6); FUN_00cd0710 matches a pool by that entry's first creature; the part is the matching entry's value | 0 | MATCH (no other writer of +0x154 or its entries in 00cc0000–00ce4000; the constructors 00cd6bc0 / 00cd7340 / 00cd8210 only clear it) |
| Specialization: Recruitment | FUN_00cd3030: garrison/visiting hero (town +0x134/+0x138) with skill 0x1c → max(4−tier,0), ×2 when hero vfunc 0x310 > 0; the larger of the two heroes' multipliers | +3/+2/+1 for tiers 1–3, +6/+4/+2 with a worn Crown of Leadership | same (`SpecializationGrowth`, `TownGrowthTests`) | FIXED (×2: hero vtable 00e7e484 sits at hero +0x1c (ctor 00b58950), so slot 0x310 FUN_00b5abd0's [ECX+0x234] is hero +0x250, which only 00b5a410 changes: ±1 when ARTIFACT_CROWN_OF_LEADER (0x58) is equipped / removed; the same getter is Diplomacy's ×2 in 008e1ad0) |
| Specialization: town bonus | FUN_00cd3030 + FUN_004a63b0 (+0x5c), FUN_00cd35c0 (town type) | TOWN_BONUS_TIER1..4_CREATURE: +2/+1/+1/+1, +1 more for tiers 1–2 in a Fortress (type 9) | same | FIXED (was missing) |
| Grail | 00cd5180: building 0xe level ≠ 0 → (weekly+castle+horde+dwelling+spec)/2 | half of the sum, not moon-scaled | same | FIXED (was missing) |
| Ritual Pit sacrifice | 00cd5180: CREATURE_WITCH (0x49) → town −0x1d0, CREATURE_MINOTAUR (0x4b) → −0x1cc | keyed by creature, no building check | `RitualPitBonus` by creature | FIXED (was tier-keyed with a pit check) |
| Moon week growth | FUN_00bcdb90(week, S = weekly+castle+horde+spec, creature) | PLAGUE 0; DISEASE max(S/3,1); FEVER max(S/2,1) (S>0 only); LIFE ×2; CONJUNCTION ×3; creature week (00bcd640) ×2 when base creatures match (004baf90); moon part = result − S | `MoonWeekGrowth` with the calendar's week (MoonCalendar.CreatureOf) | FIXED (was ×multiplier on everything; creature weeks not applied) |
| Week pool reduction | FUN_00bce0d0 at start of 00cd27b0 | PLAGUE empties pools; DISEASE / FEVER remove max(n/3,1) / max(n/2,1) from a positive pool | `MoonWeekPool` before growth | FIXED (was: keeps a third / half) |
| Weekly update order | 00cd27b0 | pool reduction → total → AI coefficient (computer owner) → if > 0: halve for neutral owner, add | `WeeklyGrowthAll` + `OwnerAdjustedGrowth`; only positive totals added | FIXED |
| 00cd27b0 second argument | disasm 00cd2830 ([esp+0x4c]) | false → only the moon part is added | always full (caller not traced) | UNVERIFIED |
| New dwelling | 00cd4a40 at 00cd4e2c | when FUN_00cd0710 creates the pool: base creature's WeeklyGrowth | same | FIXED (was full weekly growth) |
| Map-start pools | Setup | — | full weekly growth | UNVERIFIED |
| Accumulation / cap | 00cd0710 adds to pool | no cap seen | no cap | MATCH |
| Hire command | CHireCreaturesCmd execute 004f4dd0 | pool ≥ count, army fit (FUN_0045b240), cost × count paid (player vfunc 0xe0), pool − count | `DoRecruit` same checks | MATCH |
| Town hire screen | enter_fort 0061b470 → 0061af30 → 006db170 → 006dd290 / 006e23a0 (HIRE_CREATURES, UI/HireCreatures); dwelling double-click goes the same way | the map-dwelling hire screen: one row per built dwelling with base / upgraded / alternative columns; the 1.0 UI/FortScreen is not used | `TownHireScreen` (enter_fort, `--town-goto=recruit`, dwelling double-click at that row); town HUD hidden | FIXED (was UI/FortScreen 3D hall with ray-picked creatures) / default selected column UNVERIFIED |
| Select / count | CHireItem 006def90 → 006de370 → 006dd9d0 | clicking a column selects it; dial range 0..min(pool, purse/cost), count set to that maximum | same | FIXED (was count 0) |
| Double click | CHireItem IClickNotify slot 1 006d8a40 (row flag +0x3c, 1 for the town) → dial button 2 | double-click buys the dial count | same | FIXED (did nothing) / slot meaning inferred from behaviour |
| Dial | EditJogDial 00782b70 / 007826c0; handler 006e3050; input_a2.cfg hire_screen | 0 Exit, 1 Minus, 2 Buy (CHireCreaturesCmd 006da060), 3 Plus, 4 HireAll; R / Space / A; num +/- | same | FIXED |
| Recruit army | 0061af30 (town vf 0x2c; town hero slot 1 present → hero vf 0x48); CHireCreaturesCmd 004f4dd0 | garrison hero's army, else the town garrison; refused when it does not fit (FUN_0045b240) | `TownRecruitArmy` in `DoRecruit` (`TownHireTests`) | FIXED (was the visiting hero first) |
| Hire all | 006e3050 case 4 → 006da5c0 → 006dcfe0 → buy_all_header / buy_all_text_format → 006da3d0 → CHireAllCreaturesCmd 0042b870 → FUN_009a52a0; columns from 006d9770 | dwellings highest tier first; each row's selected column (else every variant, sharing the pool); a new creature needs a free slot; count = min(pool, floor(purse/cost)) (FLDCW chop 009a5561); fails when nothing can be hired | `HireAllPlan` / `HireAllCmd` (`TownHireTests`); confirmation with the total cost | FIXED (was the selected tier only) / variant order UNVERIFIED |
| Caravans | 006e23a0 hire_to_caravan_window, T_CARAVANS_* | caravan tabs | hidden | GAP |
| Upgrade / alternative variants | 00cd46c0 builds the dwelling creature list (FUN_00ccf130) | base, upgrade, TotE alternative per built level | `DwellingCreature` variants 0/1/2 | UNVERIFIED |
| Upgrade creature command | CUpgradeCreatureCmd execute 004f47e0 | callback / FUN_004bb420 check, "can't upgrade creature %s" | `UpgradeCreaturesScreen` + engine | UNVERIFIED |
| Growth tooltip on plates | 006dc57d T_GROWTH_* lines | hover tooltip with the parts | plate shows the total only | GAP |

## Mage guild

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Can-learn rule | hero spell vfunc 0xe0 = FUN_00b4b8c0 (0xe4 wrapper 00b4b890; vtable 00e7e484) | not DARK_RITUAL / SPECIAL / NONE; runic needs Runelore, warcry Blood Rage, other spells no Blood Rage (00b4b810); hero level ≥ RequiredHeroLevel; runes Runelore ≥1/2/3 by circle (00419960); mass spells need their Master perk (00a33e20/00a33cf0); empowered spells need Empowered Spells (00a33f70); circles 1–2 free; Wisdom: circle 3; school mastery + 2 ≥ circle; adventure free | `CanLearnSpell` same (mastery = EffectiveMastery, hero vfunc 0x174) | FIXED (was: barbarian class check, no Wisdom, no hero level) |
| Wisdom circle 4 | 00b4ba2e: circle 4 with Wisdom when hero vfunc 0x31c > 0 | unknown condition | not applied | GAP |
| MMH 5.5 Wisdom extras | 5.5 Wisdom description (random circle 4/5 at levels 15/25) | script behaviour | not built | GAP |
| Offer per circle | CTown vf 0x24 = FUN_00ccfab0 (vtable 00e95844 +0x24) | warcries with ability 0x27 (circle 1; 0x28 up to 2; 0x29 all); unless Stronghold (type 10): the guild's spells of the circle when guild level ≥ circle, then the Library's (ability 0x15 = Academy TB_SPECIAL_1); runes with ability 0x20 (circles 1–2; 0x21 up to 4; 0x22 up to 5) | `TownSpellsAt`, used by `LearnFromGuild` and the guild screen | FIXED (was: guild lists only; no runes, warcries or Library) |
| Learning on visit | LearnFromGuild over `TownSpellsAt` 1..5 | — | spells passing CanLearnSpell | UNVERIFIED (visit handler not traced) |
| Mana refill on visit | — | — | refill to max with a guild | UNVERIFIED |
| Spell generation | FUN_00cd1b40(from, to) (town +0x190 guild list, +0x1f4 Library list); schools FUN_00cd1990 (shared MagicSchool_0/_1 +0x124/+0x128, reader 00a0d7ba; others from {LIGHT, DARK, DESTRUCTIVE, SUMMONING} minus those two); counts table 00e95544 {0,3,3,3,2,2} | per circle: one random spell of MagicSchool_0, one of MagicSchool_1, then random picks from the other two schools up to the count; if any are left, one more for the Library; then every adventure spell of the circle; rolled (1, 5) at town placement (FUN_00cd79d0) | `RollMageGuild` same | FIXED (was: any school, map spells first) |
| Spell pool (map spell manager) | vtable 00e6eb00: slot 0 FUN_00a35080, register FUN_00a34e20, slot 0x10 FUN_00a34f80, slot 8/4 FUN_00a34d60, buckets +0x24 all / +0x30 non-adventure / +0x3c runes / +0x60 warcries | map spellIDs (map +0x1e0) only, in their order, else the whole table; skips Dark Ritual (0x43), SPECIAL / NONE schools, CUSTOM1..4 (0x15c..0x15f), mass (0xd2..0xdd) and empowered (0xdf..0xe9, 0x12a) spells, circles outside 1..5 (warcries 1..3); with a list, Maps/Multiplayer maps (FUN_00c9d9e0) of Version < 2 / < 3 also get every rune / warcry | same (`SpellDef.Index` = SpellID value) | FIXED (map spell list was ignored) |
| Map per-town spellIDs | AdvMapTown +0xf4 (00a0a5d1), copied by FUN_00cd1650 into the init record (+0x68) | FUN_00cd5be0 does not read it; FUN_00cd1b40 does not either | not used | FIXED (were placed first in the guild) / another reader not searched for exhaustively — UNVERIFIED |
| Runes | FUN_00ccfd30(from, to), town +0x2c4; (1, 5) at placement | one random rune per circle (manager school 5) | `RollRunes` | FIXED (was not built) |
| Warcries | FUN_00ccfe90(from, to), town +0x2d8; (1, 3) at placement | circle 1: every warcry of circle 1; circles 2, 3: one random each | `RollWarcries` | FIXED (was: Stronghold guild of warcries) |
| Guild screen pages | FUN_006309d0 (from setup 00631110; vf 0x24 list split by school FUN_00a34350) | G = level of building FUN_006145a0 (0x11 TB_SPECIAL_1 in a Stronghold, else 6); per existing bookmark i (OrcsButton%02d in a Stronghold, else Button%02d): adventure spells (4) → adventure list when i ≤ G, runes (5) → rune list, warcries (6) → warcry list when i ≤ G; page = warcries (Stronghold) or the rest, only when i ≤ G, caption T_ORCS_CIRCLE_i / T_CIRCLE_i, bookmark shown iff i ≤ G; then Button06 = adventure list, Button07 = runes (shown when non-empty) | `GuildScreenPages` (`MageGuildTests`) + `MageGuildScreen` | FIXED (adventure, rune and warcry pages were missing; captions were literal) |
| Guild screen panels and selection | 00631110 (SpellsPanel_%02d / RunesPanel_%02d via 006308b0; rune panel = the one whose Rune slot count equals the rune count, FUN_00632340), 0062fe60 (select: caption → circle_name, panel min(count, panels) − 1), 00630320 ("button" → page by name), 00630270 / 006303c0 / 00630450 (first page "Button01" from the guild, "Button07" from the Fortress special, else page 0), 0062fbe0 (T_ORCS_HEADER in a Stronghold) | as listed | same | FIXED |
| Stronghold guild entrance | 0061b760 abilities 0x27..0x29 → 00619d70 (town vfunc 0x34(006145a0) ≠ 0) | the Hall of Trial (TB_SPECIAL_1) opens the guild screen with warcry pages | `OpenSub("guild")` uses `GuildScreenBuilding`; TB_SPECIAL_1 click → guild | FIXED (opened nothing: TB_MAGIC_GUILD was required) |

## Blacksmith / war machines

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Offer list | FUN_00cd3f50, table 00f7c958 | ballista, first aid tent, ammo cart | same order | MATCH |
| Price | town vfunc 0xec = FUN_00cceae0, FUN_00ccea20 | gold cost if native (TOWN_BONUS_BALLISTA/_FIRST_AID_TENT/_AMMO_CART specialisation or TownTypesInfo NativeWarMachine), else ×3; gold only | `WarMachinePrice`, used by engine and BlacksmithScreen | FIXED (specialisations ignored; all resources scaled) |
| Buy command | CBuyWarMachineCmd execute 004f4d10 | fail if the hero has it (vfunc 0x70), pay gold (0xe0), give (0x68) | `DoBuyWarMachine` (plus the town/blacksmith checks the UI implies) | MATCH |
| MMH 5.5 data | 5.5 TownTypesInfo NativeWarMachine (all ballista) | data-driven | data-driven | MATCH |
| Screen header | 006ed200 MainHeader_Blacksmith / _WarMachineFactory text refs | localized header | literal "Blacksmith" | GAP |

## Armies (garrison / visiting, split, merge)

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Split / move | CSplitStackCmd execute 00432160 | target counts; same creature only; a hero's single stack can't be emptied; count 0 succeeds | `DoTransfer` | FIXED (count 0 was an error) |
| Transfer | CTransferCreaturesCmd 0042ae70 | n ≤ source, destination empty or same creature, both armies stay valid | `DoTransfer` | MATCH |
| Swap | CSwapCreaturesCmd 00431d10 | swap slots, revert if an army becomes invalid | `DoSwap` (merges same creature across armies) | MATCH / UNVERIFIED (merge is UI behaviour) |
| Dismiss stack | CDismissCreatureCmd 00427c40 | a hero's last stack can't be dismissed | — | UNVERIFIED |
| Garrison ↔ visiting | "garrison visited / creatures swapped" (004e6d30) | not traced | `DoGarrison` merges the garrison into the hero | UNVERIFIED |
| Row layout | 0061c5b0 (List_0 TownContainer PicBox ↔ portrait +0x2a0, HeroContainer PicBox ↔ +0x2a4), tooltips 00615d10 (+0x2a0 "VISITING_SLOT", +0x2a4 "GARRISON_SLOT" with CDBID_TEXTURE_GARRISON when empty) | upper row (HeroContainer) = garrison (garrison hero or the town's army), lower row (TownContainer) = visiting hero | same | FIXED (rows were swapped: the garrison was the lower row and the upper row was hidden without a visiting hero — the reported "only one slot in the top row accepts troops") |
| Empty garrison portrait | 00615d10 CDBID_TEXTURE_GARRISON | garrison texture | town kingdom icon by hall level | UNVERIFIED |
| Empty visiting row | — | not traced | hidden without a visiting hero | UNVERIFIED |
| Drop target | — | — | slot under the release event (was the global mouse position) | MATCH (hero ↔ garrison and hero ↔ hero drags checked off-screen with synthetic input) |
| Hero portraits | click_pointer 00613130 no-op; dblclick_pointer 00619a30 hit-tests +0x2a0 / +0x2a4 → 00619320 | double click opens the hero screen; both heroes present → the two-hero page | same (`OpenTownHeroes`) | FIXED (a single click on the visiting portrait opened it; the garrison portrait did nothing) |

# Hero screen

## Hero development (experience, level-up rules)

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Experience table | 00b4c6b0 (table 01026d90 by level, constants 00f755e0, ×1.2 at 00e12f1c, ×0.1 at 00e12f10) | levels 1–12 from data (0 … 20600); steps × 1.2 up to level 24, then × (level − 13) × 0.1; round to nearest; clamp to 2³¹−1; level ≥ 41 reads 2³¹−1 | `ExperienceTable.Build` the same recurrence (was the Editor PDF table, 1 low from level 18 and drifting: L40 1216586989 → 1216634591) | FIXED |
| Precision of the table products | 00a3fe20 CreateDevice flags 0x50 / 0x20 (no D3DCREATE_FPU_PRESERVE) | x87 left at 24-bit precision by D3D9 | float32 products (only levels 37–40 differ from 64-bit) | UNVERIFIED (assumes the table is first built on the device thread) |
| Level cap | 00b4c6b0 index > 40 → 2³¹−1; map HeroMaxLevel (vfunc 0x170) | 40 unless the map caps lower | `For(level > 40)` = int.MaxValue, cap 40 / `HeroMaxLevel` | FIXED (For clamped to level 40 before) |
| Level-up trigger | 00b52b10 | one level at a time: pending level (+0xd8) = level + 1, attribute vfunc 0x308(0,1), offer 00b52810, box; next level after the choice (00b575d0) | all levels rolled at once; later offers re-rolled after each choice; attribute applied immediately | MATCH (rules) / GAP (roll timing: the exe rolls level n+1's attribute after choice n) |
| Attribute roll | 00b50a50, 00b509b0, enum HeroAttribute (types.xml: 0 Defence, 1 Offence, 2 Knowledge, 3 Spellpower) | class AttributeProbs (Offence, Defence, Spellpower, Knowledge) weighted; a Spellpower roll gives Knowledge unless SP > 2 or K + 1 ≤ 4·SP (hero +0x3c/+0x38); no weights → Defence | `RollLevelUpAttribute` / `LevelUpAttribute` same (was a plain weighted pick, Attack on no weights) | FIXED (offsets +0x38/+0x3c read as base Knowledge/Spellpower by the enum order: inferred) |
| Offer composition | 00b52810 | skill 1 new (00b50db0); none → two upgrades (00b4f400, second excludes first); else an upgrade, or a 2nd new skill; perk 1 standard (00b4f5c0(·,1,0)) else any; perk 2 special (00b4f5c0(p1,0,1)) else any but p1 | `BuildOffer` same | FIXED (was manual p.294 version; equivalent except the perk slot types) |
| New-skill candidates | 00b50db0, 00875e20, 00875550 | none with 6 skills; all skills (type 0) without a HeroClass in enum order + the hero's racial skill; not known, not excluded | `DrawNewSkill` same (enum = Skills.xdb index, `PerkDef.Index`) | FIXED (was only class-listed skills) |
| New-skill weights | 00b50be0 → 00875ba0 (class SkillsProbs), 00b509b0 | weight = class prob (0 if unlisted); r = rand[0,sum−1]; all-zero → first candidate | same (`PickWeighted`) | FIXED (zero-weight skills were never offered) |
| Upgrade pick | 00b4f400 | uniform among known skills below Expert, minus the excluded | same | MATCH |
| Perk unlock | 00876160 → 008766a0 → 00875b30, 00875a40 | perk of a known skill: standard always; class perk iff HeroClass = hero class; special/ultimate iff SkillPrerequisites has the hero's class and all its dependencies are known (vfunc 0x174) | `PerkCandidates` same | FIXED (class perks used the prerequisite path; ultimates were never offered) |
| Perk lock | 00875f20 | taken perks of the skill ≥ mastery (vfunc 0x174: Pendant of Mastery +1 on the racial); limit 4 for perks with a HeroClass once mastery > 2 | same (`EffectiveMastery`) | FIXED (ultimate 4th slot) |
| Perk slot types | 00b4f5c0 (types 1/3 vs 2/4) | standard slot takes STANDART and CLASS perks; special slot SPECIAL and UNIQUE | same | FIXED (class perks were in the special slot) |
| Barbarian perk copies | 00875410 | BARBARIAN_LEARNING etc. count as the original in lookups and on-learn effects | `CanonicalSkill` | FIXED |
| Enlightenment (HERO_SKILL_LEARNING) stats | 00b54770 case 3, 00b4bea0, 00b575d0 loop, undo 00b55d80 | level / max(2, 5 − mastery) random primary stats (vfunc 0x308), given on learning and every level-up, newest taken back on unlearn (+0x2b0 list) | `ApplyLearningBonus` / `RevokeLearningBonus`, `HeroState.LearningStats` | FIXED (was TODO) |
| Attribute perks | 00b54770 → 00b4cbc0; 00b55d80 → vfunc 0x1bc | Graduate, Secrets of Destruction +2 Kn; Lord of Undead +1 Kn; Caster Certificate, Master of Secrets, Academy Award, Insights +2 SP; Forest Guard Emblem +2 Att; Seal of Protection, Defend Us All, Bodybuilding +2 Def; undone by the Memory Mentor | `PerkStat`, `ApplyPerkOnLearn` / `RevertOnUnlearn` | FIXED (were missing) |
| One-time perk rewards | 00b54770 cases 0x4f/0x7f/0x8c, flags +0x2a8/+0x2a9/+0x2ab | Graduate +1000 exp via modifiers; Academy Award +1000 gold; Dark Revelation: L+1 < cap adds T(L+1) − T(L), L+1 = cap sets T(cap); flags never cleared | same, `HeroState.PerkRewardsClaimed` | FIXED (Dark Revelation set exp to the next threshold; no once-flags; Academy gold missing) |
| Other on-learn effects | 00b54770 (Caster Certificate +0x158 += 100 once; Forest Guard Emblem / Defend Us All creatures by week; Insights / Master of Secrets spells; perk ability spells via vfunc 0xec; Intelligence/Leadership/Luck refreshes) | as listed | not applied (derived values recomputed elsewhere) | GAP |
| On-learn for non-level-up paths | vfunc 0x1c8 → 00b54770 | every learn path | level-up choice, Memory Mentor relearn, `ScriptLearnPerk`; Witch Hut / Seer Hut / `ScriptSetSkill` skills skip it (Enlightenment catches up at the next level-up) | GAP |
| Starting perks | 00b56cd0 (00b54770 with param_4 = 1) | caller not traced | not applied | UNVERIFIED |

## Hero screen (UI/HeroScreen3)

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Name / class / level | 006bed90 | HeroName, HeroClass (class name), HeroLevel values of HeroName.txt | same | MATCH |
| Experience bar | 006bed90, HeroExperience.(WindowProgressBar) Forward texture | fill (exp − T(L)) / (T(L+1) − T(L)) | `SetProgress` (was never filled) | FIXED |
| Experience tooltip | 006bed90 keys current / next; ExpTooltip.txt | `<value=current>/<value=next>` = exp / T(L+1) | from ExpTooltip.txt (was hard-coded "a / b") | FIXED |
| "Experience:" label placement | HeroDescription (pos 132,36, shared HIGH_END) | — | drawn over the Attributes header | GAP (UiLayout HIGH_END semantics, not traced) |
| Attribute values | 006be820 / 006be780 | each value compared with a second value: `_red` below, `_green` above; source pair (likely total vs base) not traced | base stats, no colouring, worn artifacts not added | GAP |
| Mana | 006be820 Attribute / AttributeMax | current / max | `Mana` / `MaxMana` | MATCH |
| Artifact doll and backpack icons | Inventory/*.(WindowMSButton) with a Pic child | icon shown in the slot | icon was put under the Pic child (invisible); now on Pic | FIXED |
| Doll slot mapping | 006bf940 (Helm, Necklace, Cloak, Cirase, Weapon, Shield, Ring_1/2, Boots, Additional) | per artifact Slot | same names | MATCH |
| Backpack paging | 006bf940 InvLeft / InvRight | 10 per page | same | MATCH (page clamp rules not traced) |
| Skills table | 006c7a70 (6 cells), Racial_1..5, Racial_Desc | cells filled from the hero's skills | 6 cells; specialisation icon on Racial_1, name on Racial_Desc; Racial_2..5 unused | UNVERIFIED (fill code not traced) |
| Abilities page | 006c9f30 Abilities%d / Perk%d | per skill row its perks | same (3 per skill) | UNVERIFIED (ultimate as a 4th perk not shown) |
| Army / war machines | 006c8690 ArmyStacks, WarMachinesStacks | stacks and machines | same | MATCH (bindings) |
| Bookmarks, dismiss, next/prev hero, cast | 006c4b40 (bookmark, dismiss_hero, cast_spell), 006c5380 (NextHero, PrevHero, DismissHero_Tooltip) | dismiss and hero cycling | bookmarks and cast; dismiss / next / prev not built | GAP |
| Biography | 006c1890 biography / spec_name / spec_desc | values of the biography text view | same | MATCH |
| MMH 5.5 layout | MMH55-Skillwheel.pak HeroScreen3 (adds Biography, FaceCover, Specialization, Pocket, Placeholder; Skills page = class skill & perks / specialisation / biography) | MMH55 screen | opens and shows Army / Inventory; the MMH Skills page leaves Specialization and Biography empty; Backpack header missing | GAP |
| Spellbook from the hero screen | Inventory Cast → cast_spell (006c4b40) | adventure spellbook | `SpellBook3D` (combat book) with the hero's spells | UNVERIFIED |
| 3D backdrop | Arenas/Interface/Hero_screen ArenaDesc: one ArenaEnvObject plane_back, texture Back.tga (DXT1, AverageColor 0xFF000000, every block endpoint 0), ambient light Fort_screen | black backdrop behind the hero | same | MATCH (user report "hero screen has no background": the data's backdrop is solid black) / UI page panels UNVERIFIED |

## Level-up box (UI/LevelUPBox)

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Header text | 006a8f50 (N_LEVEL_UP_WINDOW_ID related text "levelup" / "relearning", values name, level, profession) | "<name> is now a level <level> <profession>…" | related text + values (was an invented English line) | FIXED |
| Attribute row | 006a8f50 SkillBack: UI/HeroAttributes (UIHeroAttributes) entry: heroattribute = DescriptionFileRef, Face_1 = Texture | e.g. "Attack<br>Skill +1" with its icon | same (was "+1 defence" text, no icon) | FIXED |
| Options | 006a8f50 Skill%d, Choose / heroskill, FaceSkill | skills left, perks right | same | MATCH |
| Relearning header | 006a8f50 (level −1 → relearning_header + tooltip, SkillBack hidden) | relearn mode | relearn offers shown through the normal box | GAP |
| Sound | 006a8f50 HERO_LEVEL_UP_SOUND / HERO_RELEARNING_SOUND | — | audio pass (AUDIO.md) | MATCH |
| MMH 5.5 box | MMH55 LevelUpBox override | extra skill-wheel button | box works; the extra button is unbound | GAP |
