# Adventure map rendering: held props, fog of war, distance fog

This ledger covers two beta reports: props held by heroes and troops glitched while moving, and the
adventure fog "doesn't work very well". Every row is checked against H5_Game.exe (stock 3.1, Ghidra
projects `tools/ghidra-projects/advrender` and `advfog`) or the game data.

## Locomotion and held props

Move clips carry their forward travel inside the skeleton. Weapons, shields, banners, reins and
mounted riders are root-level bones that travel exactly as far as the body. `h5tool gr2-drift --all`
finds 68 of 91 adventure move clips with several such bones; in model space they all travel the same
distance (`h5tool gr2-drift <Character>`). The old build-time extraction removed the drift from the
best-matching body bone only. Every other travelling bone ran ahead during the cycle and snapped back
at the loop: the horse left the rider, the staff, shield and reins behind (`h5tool gr2-drift
<Character> --strip out.png [--old]` renders it).

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Granny root motion | `GrannySetTrackGroupAccumulation(control, 0, 0)` at 0089313f (FUN_00892fc0); RootMotion null, LoopTranslation 0 in the data | No accumulation or extraction; the clip is sampled as authored (FUN_00893720 → GrannySampleModelAnimations) | Clips are sampled as authored | MATCH |
| Playing the move cycle in place | travel of all bones identical in model space (gr2-drift) | Whole model moves together with its props | `CharacterBuilder.AddInPlaceTrack` shifts the whole `Skeleton3D` back by the clip's linear model-space travel (`GrannyPose.LocomotionTravel`). Every clip carries the track (zero when in place), so cross-fades blend it. Same code for combat. Regression: `H5.Granny.Tests/LocomotionTests` | FIXED |
| How the exe keeps the walker on its path | — | Where the exe compensates the authored travel (object position vs. clip time) was not traced; per-bone overrides FUN_00893da0/FUN_00891cf0 are an external bone-keyframe API, not root motion | Linear removal of the travel (body motion within the cycle kept) | GAP (mechanism not traced; result consistent with the body) |
| Rigid (unskinned) mesh parts | geometry field 12 (`gr2-skin-check`: 3 rigid parts in 494 models) | Attached to a bone | Drawn unskinned | GAP (3 parts, not props of heroes/troops checked) |

## Fog of war (shroud)

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Kind of effect | vs_1_1 terrain/model shaders (VS table 00f715b0): `r.w = v4.w*c30.y + v5.w*c30.x`, `oD0 = v4.rgb*w*c29`, `oFog = 1 + max(f,0) - w`; c30 set in FUN_00896430 | Per-vertex darkening of every lit mesh: colour × w, fog factor clamp(f + 1 − w), so unexplored ground is pure black with no distance fog. No texture, clouds or overlay; the `WarFogParams` shaders (VS 1/2) are stubs | `H5Lit.gdshaderinc` `h5_warfog_at` / `h5_finish(…, war)` in H5Lit, AdvTerrain, the sea and water and M_CLOUDS_H5 materials. The full-screen depth overlay (`AdvShroud.gdshader`) was removed | FIXED |
| Grid | FUN_005b0f80 | (nextPow2(w)+1) × (nextPow2(h)+1) bytes, one per tile corner (world 2x, 2y). 255 only if all 4 surrounding tiles are explored, row/column 0 and beyond the map are 0. Two 5-tap /5 blur passes, ping-pong FUN_005b0ea0 (dis 005b10ae/005b10bb). Edge profile 10, 71, 183, 244, 255; an isolated explored tile stays black | `H5.Adventure.Map.WarFogGrid` (tests `WarFogGridTests`) | FIXED (was 1 texel per tile centre, bilinear, no blur, too bright and sharp) |
| Sampling | FUN_00cf9420 / FUN_00cf8b40, scale 0.5 texel per world unit (FUN_00cf9220) | Bilinear, 14-bit fixed point, texel index masked with W − 2 | Bilinear in float with the same index mask (`texelFetch`) | MATCH (fixed-point truncation not reproduced) |
| Change animation | FUN_005b1260, t = clamp(elapsed_ms × 0.001, 0, 1) (DAT_00e155ac) | On an explored-array change: old grid → B, new grid → A, w = A·t + B·(1 − t), linear over 1000 ms. Changes during a fade wait for it to end. A floor switch snaps (A = B = new) | `AdvWorld._Process`: polls the human player's explored bits each frame, same crossfade and snap | FIXED (was instant) |
| Objects and transparent geometry | FUN_00cf9a80 from the CTransparentRenderer slot 0087e370 | Same value applied to transparent objects and particles | All H5Lit materials (opaque, alpha, additive, self-illuminated), M_CLOUDS_H5 (sunbeam cards, lava), water, sea and sky dome sampled per vertex | FIXED. Particles (`game/src/Effects`) not darkened: GAP |
| Hiding objects in the fog | — | The renderer only darkens. Whether game logic also hides objects or enemy heroes was not checked | Unchanged | GAP |
| Outside the map | grid texels 0 | Black (lit geometry) | Black through the grid. The sky dome is now an unlit H5Lit backdrop, darkened like the rest. Whether the exe darkens it was not checked | MATCH / GAP (sky dome) |
| Underground | per-floor `CWarFogArray` (FUN_004fb7d0, FUN_004ffd30) | Separate array per floor, same pipeline | Per-floor explored bits; a floor switch snaps | MATCH |
| Config | data.pak / a2p1-data.pak | No xdb parameters; 1000 ms, scale 0.5, 2 blur passes and the 4-corner rule are hard-coded | Constants as in the exe | MATCH |
| Puzzle map | — | Separate view | `PuzzleMapScreen` sets `AdvWorld.SuppressWarFog` | MATCH (kept behaviour) |

## Distance fog

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Formula | FUN_009add40: c19 = (−1/(Max−Min), Max/(Max−Min), −1/(MaxZ−MinZ), MaxZ/(MaxZ−MinZ)); FOGTABLEMODE/FOGVERTEXMODE NONE | Per-vertex `f = max(min(depth·c19.x + c19.y, z·c19.z + c19.w), 0)` from the light's DistanceFog record (e.g. AdvMap: MinDist 70, MaxDist 100) | `H5Lighting.ReadFog` + `h5_fog_factor` | MATCH |
| AmbientLight FogColor / FogStartDistance / FogDistance | +0xc8/+0xd4/+0xd8 referenced only by the serializer FUN_00c78010 (0xc783ea, 0xc78426, 0xc78464) | No renderer consumer found | Removed the Godot `Environment` exponential fog that was built from them. It fired for e.g. Night.xdb, Sand_Bright_day.xdb and A2C1M1 (start 10), on top of the H5Lit fog | FIXED |
| Fog vs camera zoom | FUN_009add40 | Linear in view depth; nothing zoom-specific | Removed `OnCameraDistance` thinning of the Godot fog (it was invented). The extended-zoom shadow range is kept | FIXED |

## Captures

Off-screen captures used for this ledger: A2M1 start at the default zoom and at zoom 60, before and
after (`--adventure=A2M1 --adventure-zoom=60 --adventure-shots=…`). At zoom 60 the map's sunbeam cards
(M_CLOUDS_H5, additive) standing in explored land are now visible above it. The old overlay used to
black out every pixel whose depth fell outside the explored area.
