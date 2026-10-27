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
| Outside the map | grid texels 0 | Black (lit geometry) | Black through the grid; the sky dome is not darkened (see "Beyond the map edge, sky dome") | MATCH |
| Underground | per-floor `CWarFogArray` (FUN_004fb7d0, FUN_004ffd30) | Separate array per floor, same pipeline | Per-floor explored bits; a floor switch snaps | MATCH |
| Config | data.pak / a2p1-data.pak | No xdb parameters; 1000 ms, scale 0.5, 2 blur passes and the 4-corner rule are hard-coded | Constants as in the exe | MATCH |
| Puzzle map | — | Separate view | `PuzzleMapScreen` sets `AdvWorld.SuppressWarFog` | MATCH (kept behaviour) |

## Beyond the map edge, sky dome

Beta report: the map looked like a square floating inside a lit sphere. Our dome was a fixed model at the map centre, so
the camera (especially with Extended map zoom, distance 160) left it and saw its outside. Code: `AdvWorld.BuildSkyDome`,
`FollowCameraWithSkyDome`. Captures: A2M1 corner at zoom 70 and 160 (`--adventure-look=2,2 --adventure-zoom=160`).

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Creation | scene light setup FUN_00b77910 → CSkyDome ctor FUN_00b24d50 | the light's SkyDome model, identity transform, no scale | same model | MATCH |
| Position | each scene render FUN_00b74540 calls the dome's SetPosition (vtable +0x1c, FUN_00b24720) with the camera eye (FUN_00b790e0) | translation (eye.x, eye.y, 0): centred under the camera on the ground plane | same every frame | FIXED (was fixed at the map centre) |
| Material | M_SIMPLE_SKY / M_VIRTUAL_SKY (FUN_00898e10 cases 7 and 9, FUN_0089ba60): vs_1_1 00f4c3e8 scales clip x/y by FloatParam (c30/c31 from FUN_00896c00; 1 when ≤ 1.19e-7), ps_1_1 00f61940 outputs the texture | unlit, no distance fog, no fog of war, back faces culled (Is2Sided false in all stock sky materials) | `SkyDomeShader` (unshaded, the clip scale, cull back); other effects in a dome keep the model material | FIXED (was a two-sided H5Lit material darkened by the fog of war) |
| Draw order | own render pass FUN_008984d0 (pass 100/3; scene models pass 2), no depth write | behind the whole scene | on the far plane, no depth write | FIXED |
| Geometry past the terrain edge | — | no border or skirt mesh found; off-map ground is black through the fog-of-war grid (row/column 0 and beyond = 0) | same | MATCH / UNVERIFIED (no border object searched for beyond the renderer paths above) |

## Distance fog

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Formula | FUN_009add40: c19 = (−1/(Max−Min), Max/(Max−Min), −1/(MaxZ−MinZ), MaxZ/(MaxZ−MinZ)); FOGTABLEMODE/FOGVERTEXMODE NONE | Per-vertex `f = max(min(depth·c19.x + c19.y, z·c19.z + c19.w), 0)` from the light's DistanceFog record (e.g. AdvMap: MinDist 70, MaxDist 100) | `H5Lighting.ReadFog` + `h5_fog_factor` | MATCH |
| AmbientLight FogColor / FogStartDistance / FogDistance | +0xc8/+0xd4/+0xd8 referenced only by the serializer FUN_00c78010 (0xc783ea, 0xc78426, 0xc78464) | No renderer consumer found | Removed the Godot `Environment` exponential fog that was built from them. It fired for e.g. Night.xdb, Sand_Bright_day.xdb and A2C1M1 (start 10), on top of the H5Lit fog | FIXED |
| Fog vs camera zoom | FUN_009add40 | Linear in view depth; nothing zoom-specific | Removed `OnCameraDistance` thinning of the Godot fog (it was invented). The extended-zoom shadow range is kept | FIXED |

## Shader globals ownership (fog of war vs overlay scenes)

| Item | Evidence | Original | Ours | Status |
|---|---|---|---|---|
| Fog of war under a dialog scene / hero screen stand | Fog darkening is the adventure renderer's own vertex pass (FUN_00896430, see Fog of war); a dialog scene or the hero screen is a separate scene with its own light | Dialog actors and set lit by the scene's light, no fog of war | Was: AdvWorld stays visible under `DialogLayer` and re-published the map-space grid every frame, so H5Lit models in the dialog set (A1SM1 opening, any campaign scene started from the map, stock or 5.5) sampled unexplored texels → black silhouettes; the hero screen stand likewise. Now `H5Lighting.Apply(…, owner)` records the scene that owns the globals; AdvWorld skips the fog while another visible owner exists and re-applies its floor light + fog once that owner is gone (also after the town and battles, which used to leave their light on the map) | FIXED |
| Small hero face (town hero bar, building screens) | FaceTextureSmall (+0x134) is only named by the serializers FUN_00a02450 / FUN_00a0c3b0; no reader with its own fallback found (scan of all 350 functions using disp 0x134) | Faces shown for heroes whose shared xdb has an empty FaceTextureSmall (27/119 stock, e.g. Brand, Shadwyn; MMH 5.5 GodricMP) | `CombatIcons.Hero(small)` falls back to FaceTexture when FaceTextureSmall is empty (as AdvHud already did) | FIXED (exact exe selector not traced) |

## Captures

Off-screen captures used for this ledger: A2M1 start at the default zoom and at zoom 60, before and
after (`--adventure=A2M1 --adventure-zoom=60 --adventure-shots=…`). At zoom 60 the map's sunbeam cards
(M_CLOUDS_H5, additive) standing in explored land are now visible above it. The old overlay used to
black out every pixel whose depth fell outside the explored area.
