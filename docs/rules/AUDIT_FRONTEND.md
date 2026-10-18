# Front-end audit (main menu, credits)

Ground truth: stock data (`UI/MainMenu2`, `UI/Credits`, `Arenas/Interface/*`) and H5_Game.exe
(headless Ghidra, private project `tools/ghidra-projects/frontend`, helper `tools/frontend/ghidra.sh`).
Status: **MATCH** (already identical), **FIXED** (changed to match), **GAP** (not yet done).

## Main menu 3D backdrop (`NUI::CMainMenuScreen`)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Scene | `Arenas/Interface/MainMenu/MainMenu.(ArenaDesc).xdb`, static resource 0x00fddb48 initialised at 00db2da0; loaded into a 3D window by 00694610 | `game/src/Arena/MainMenuBackground.cs` | MATCH |
| Is it a video? | No: a live 3D scene. `movies` (UIGameRoot) only lists the intro logos; no menu movie is referenced | live scene | MATCH |
| Camera | `cameraFlyupRole` = animated `_(Model)/Cutscenes/camera` + `Mainmenu_H5A2_Camera` (idle00, 30 s, seamless loop). 00694490 → 007d1b80 builds the animated model and wraps it in a track-camera operator 007d2140, which reads the world matrix of the bone named `camera` each frame (007d20e0). Engine camera forward = local +Y, up = +Z | Godot camera = bone global transform (local +Y→−Z, +Z→+Y after the basis change) | FIXED (was static `initialCamPos`, mis-read) |
| Projection | FOV/near/far from `initialCamPos` (54.43°, 0.5, 10000) passed to the operator (vtable +0x3c / +0x40); projection 00cf7c30: FOV horizontal while H/W ≤ 0.75 | `FrontEnd/ExeProjection.cs`; 4:3–16:9 exact, wider than 16:9 keeps 16:9 vertical extent (Hor+, user-requested extension) | FIXED |
| Animated troops | every ArenaEnvObject with an AnimSet loops its idle00 (Cyclops, Haven troops, griffin…) | `CharacterBuilder.BuildFromModel` per object | FIXED (were static meshes) |
| Effects | 13 ArenaDesc effects (leaves, glow, mist wall, arrows, fire wall, meteors, dust/blood, clouds, earthquake, storm…), empty Locator = scene origin | `EffectPlayer.Play` at origin | FIXED (were missing) |
| Scene clock pre-roll | 00694610 updates the scene to 10000 then 10001 ms before the camera operator starts at that time | effects and idles sought to 10 s; camera clip starts at 0 | FIXED |
| Placement matrices | written with Y negated relative to mesh/skeleton space (only the Background card matters: lands far behind the walls) | conjugated by diag(1,−1,1) | MATCH (kept, now documented) |
| Uncovered sky | the 3D window clears to the scene colour | backdrop viewport opaque, background = AmbientLight FogColor | FIXED (was black) |
| Ultrawide | n/a in original (3.1 crops by fixed horizontal FOV) | Hor+ beyond 16:9, scene fills the sides | FIXED (extension) |
| Menu music | `MAIN_MENU_MUSIC_ID` (006931a0), `MP_MAIN_MENU_MUSIC_ID` in multiplayer pages | — | GAP (audio workstream) |
| Ambient lighting / materials | AmbientLight `Rampart_Sky` | approximate (sun + ambient) | GAP (rendering workstream) |

## Main menu pages (`UI/MainMenu2`)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Page switching | panels toggled by UISRunReaction / ARSendGameMessage commands; the data has **no** slide/fade animation commands | `ShowOnly` show/hide | MATCH |
| Buttons | single, multi, options, high_score (hall_of_fame), credits, main_menu_exit (string table at 006939a0) | all wired | MATCH (credits FIXED) |
| Version text | literal L"3.1" (00693370) into `Version` | "3.1" | MATCH |
| Credits | `credits` → CCreateCreditsScreenCmd → CCreditsScreen | `FrontEnd/CreditsScreen.cs` | FIXED (was unhandled) |
| User campaigns | `user_campaign`, `select_load_user_campaign` | unhandled | GAP |
| Replays / movie maker | `replay_combat`, `replay_dialog`, `load_replay_back`, `start_movie_maker` | unhandled | GAP |
| Triple duel (3×3) | `triple_duel_mode_hotseat` | unhandled | GAP |
| Hover/click sounds | button sound refs in the window xdbs | — | GAP (audio workstream) |

## Credits screen (`NUI::CCreditsScreen`, 00793d20 / 007942a0 / 007936b0 / 00794970)

| Item | Original (evidence) | Ours | Status |
|---|---|---|---|
| Backdrop | its own 3D view of `Arenas/Interface/Magic_Guild` (resource 0x00fe34d8, initialised at 00dbbf20), object `mgi` set to upgrade 5 (vtable +0x10 with 5), 10 s pre-roll | `Town.TownScene3D` + `ShowLevel("mgi", 5)`, effects sought 10 s, exe projection | FIXED |
| Window | `UI/Credits/CreditsScreen` (TextPanel 347×698 at right edge −57, margins 50; TextItem template hidden) | stock window via `UiScreen` | FIXED |
| Text | T_CREDITS (Credits.txt) then T_NIVAL_CREDITS (Nival.txt), split at "\n", one TextItem per line, mlTags `credits_h1/h2/body` | same | FIXED |
| Scrolling | lines created at the panel bottom while the last line's bottom ≤ panel height; move up by `credits_move_speed` px/s (code default 30, stock autoexec 40); destroyed when y + h < −1 | same | FIXED |
| Fast mode | ×300 while flag +0x1c0 set until the Ubisoft block ends; the constructor clears it (normal path never fast) | not used | MATCH |
| Fade | `credits_fade_zone` (default 1/6): alpha 1 while d < 1 − 2z, ramps to 0 over 2z (007936b0) | `CreditsScreen.Alpha` | FIXED |
| End of roll | no auto-close or loop; stays on the backdrop | same | MATCH |
| Close | `esc_pressed` → `close` (UI/Credits) | Esc → main menu | FIXED |
| Music | `CREDITS_SCREEN_MUSIC_ID` → `Sounds/_(Music)/Credits/Final-Credits` (fade 2000 ms) | — | GAP (audio workstream) |
