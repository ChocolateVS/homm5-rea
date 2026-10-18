# Effects (`Effects/**`, `bin/effects/<uid>`, `bin/Lights/<uid>`, shots, spell visuals)

Decoder: `src/H5.Assets/Effects/` (`ParticleBake`, `ParticleSampler`, `ParticleSystem`, `EffectLoader`).
Runtime: `game/src/Effects/` (`EffectPlayer`, `ProjectilePlayer`, `SpellVisualPlayer`, dev viewer `--view-effect=`).
Exe addresses refer to `H5_Game.exe` (TotE 3.1, GOG); found with Ghidra 12.1 headless (`tools/fxre/`).

## XDB objects

| type | role |
|---|---|
| `Effect` | `Instances` (ParticleInstance), `Models` (ModelInstance), `Lights` (LightInstance), `Duration`, `WindAffected/WindPower` |
| `ParticleInstance` | `Particle` href, `Position`, `Rotation` (quat), `Scale`, `Speed`, `Offset`, `EndCycle`, `CycleCount`, `Pivot`, `Textures` (16 slots, empty slots kept), `Light` (L_NORMAL/L_LIT), `IsCrown`, `Static`, `DoesCastShadow`, `GlueToBone`, `GlueToNamedBone`, `LeaveParticlesWhereStarted`, `Priority` (reader 0x00c79330) |
| `Particle` | `SrcName`, `WrapSize` (vec2), `Bound`, `PerParticleFog`, `uid` → `bin/effects/<uid>` (reader 0x00c78ed0) |
| `ModelInstance` | `Model`, `SkelAnim` (BasicSkelAnim), transform, `Scale`, `Offset`, `CycleLength`, `CycleCount`, `GlueToBone` (0x00c797e0) |
| `LightInstance` | `Light` → `AnimLight` (`SelectNode`, `uid` → `bin/Lights/<uid>`), transform, `Scale`, `Speed`, `Offset`, `EndCycle`, `CycleCount` (0x00c790b0) |
| `Shot` | `StartEffect`, `RotateStartEffectToTarget`, `MissileShot` {Model, Effect, Speed, Height, ForwardOffset, StartZOffset, RightOffset}, `LineShot` {Effects, SkipSegmentsNumber, SegmentLength, StepDelay, EffectLifeTime}, `StrikeDelay` (ms, clamped ≥ 0), `DelayBetweenChains`, `StrikeVisualEffect` (readers 0x00932bd0, 0x00931630) |
| `SpellVisual` | `Effect`, `SoundEffect`, `Decal`, `HitDelay` (s), `ParticleDuration` |

Spells list their visuals in `<visuals>` (e.g. Armageddon has the cast visual and a hit visual).
Relative hrefs inside a separately stored ParticleInstance/ModelInstance resolve against *that* file.
Of 4262 particle instances in stock effects, 4247 resolve to a bake; 4 are empty in the data
(`<Particle/>`) and 11 reference bakes that were never shipped — both invisible in the original too.

## `bin/effects/<uid>` — baked particle tracks (1924 files, 100 % decoded)

A memory image fixed up in place by `CParticlesLoader` (0x00b67f60). Little-endian.

```
+0  u32  imageSize       file length − 4 (0 in the single all-zero stock file → empty)
+4  f32  length          seconds
+8  f32  fps             30 in 1889 files (also 24, 25, 15)
+12 u32  particleCount
+16 particle records, 34 bytes each:
      +0  i16 birthFrame   particle visible while birth ≤ frame < death
      +2  i16 deathFrame
      +4  5 × { u16 keyCount; u32 offset }   offsets relative to file+4 (the image start)
          track 0 position  key = { i16 frame; f32 x, y, z }  (14 B)
          track 1 rotation  key = { i16 frame; f32 angle }    (6 B, radians)
          track 2 size      key = { i16 frame; f32 w, h }     (10 B)
          track 3 colour    key = { i16 frame; u32 D3DCOLOR } (6 B, 0xAARRGGBB)
          track 4 texture   key = { i16 frame; i16 slot }     (4 B, index into the 16 slots)
key data follows the records contiguously, in track order per particle.
```

### Sampling (0x00bf5fc0 / 60e0 / 61d0 / 5e30 / 5f30)
* One key → constant. Otherwise binary search over keys `[0, count−1]` for the last key with
  `frame ≤ (short)round_even(t − 0.5)`; `hi` is the search's upper bound. The search never selects
  the last key as `lo` (with two keys `lo` is always 0) — the texture track therefore never shows its
  final key; kept on purpose.
* Blend factor `(t − k_lo) / (k_hi − k_lo)` clamped to [0, 1]; position/rotation/size lerp.
* Texture index: stepped (`key[lo]`).
* Colour (MMX, 0x00b67db0): channels expand as `(c·0x101) >> 2`, multiply (`pmulhw`) by
  `round(w·16384)` where the two weights are `intensity·(1−f)` and `intensity·f`, sum, `>> 4`
  (unsigned), clamp to 255. `intensity` = lighting × fade.

### Cycle scheduling (CParticleAnimator, 0x00b10030)
`t = (elapsed − Offset)·Speed` (0x00b0fea0). `t < 0` → nothing, not finished.
* `EndCycle = 0`: `CycleCount = 0` → frame 0 forever; else one cycle of age `t` while `t < length`.
* `EndCycle > 0`: `n = round_even(t / EndCycle) + 1`, capped by CycleCount (0 = no cap; a runtime
  override exists, default −1); ages start at `t − (n−1)·EndCycle` and step by `EndCycle` while
  `age < length`; the first (smallest age) is the newest cycle. Finished once
  `(cap−1)·EndCycle + length ≤ t`.
* Each cycle shows bake frame `age·fps`. Data: (EndCycle, CycleCount) is (0,1) 410×, (3,0) 299×, …

### Quad generation (CStandardParticleEffect, 0x00bf4e80)
For every cycle and particle: skip unless `birth ≤ frame < death`; older cycles skip particles born
at or after `EndCycle·fps`; a stopped effect's newest cycle skips particles born after the stop frame.
Position is sampled, optionally wrapped by `WrapSize` around the camera, transformed by the
instance matrix. Colour sampled with intensity; quads with `(colour & 0xF8F8F8F8) == 0` are skipped;
texture slot must be < slot count. Then, with `sw, sh = size · Scale · 0.5` and rotation `r`:

```
(s, c) = lerp in a 512-entry table sin(i·2π/512): idx = round_even(r·512/2π − 0.5),
         sin = T[idx & 511], cos = T[(idx & 511) + 128]       (table built at 0x00bf4780)
pivot  = (−sw·Pivot.x, −sh·Pivot.y) rotated by r
U = (c, s)·sw,  V = (−s, c)·sh          in camera right/up
corners: 0 = P−U−V, 1 = P+U−V, 2 = P+U+V, 3 = P−U+V   (P = rotated pivot)
depth jitter per corner along the view axis: (|sw|+|sh|)·J[u], J = {0.02,−0.05,0.07,−0.12,0,0.03,0.08,−0.04}
  (0x00f78bd4), u = (i² + cycleIndex) & 7, stepping by (i|1) & 7 per corner
sort key = 0.01·Priority + view depth
```
Output (0x00881b40 shader path / 0x00881d70 fixed-function): corner k gets slot UV corner k
(atlas rect of the slot, UVs as i16 / 2048); colour is the vertex colour.

### Textures and blending
All of an instance's slot textures are packed into one atlas (`CParticleTexture`) and drawn in one
depth-sorted batch. Exported texture data decides the look under a single premultiplied blend
(`dst = src + dst·(1 − src.a)`):
* `CONVERT_TRANSPARENT` — RGB premultiplied by alpha (RGB ≤ A in every pixel).
* `CONVERT_TRANSPARENT_ADD` — alpha = 0 everywhere, so the colour adds.
* `CONVERT_ORDINARY` — straight alpha (colour where A = 0); premultiplied on load by H5Reborn so the
  same blend gives a normal alpha blend.

## `bin/Lights/<uid>` — animated lights (98 files, 100 % decoded)
Nival chunk stream; chunk 1 = { 1: f32 fps, 2: i32 type (0), 3: f32 length (s),
4: vector{ i16 frame; vec3 } (14 B) position, 5: vector{ i16 frame; vec3 } RGB colour,
6: vector{ i16 frame; f32 } }. Interpretation of 4 and 6 is inferred, not traced in the exe:
track 6 ranges 5…300 (median peak 43) and behaves like intensity in percent; positions look like
Maya Y-up (Armageddon's light descends from 15 to 4 in the middle component).

## Shots (projectiles) — partly inferred
`Speed` ranges 0.01–0.1 and `Height` 0–0.3 (arrows 0.03/0.03, axes 0.03/0.1, catapult 0.03/0.2,
Scatter Shot 0.015/0.3, beams 0). H5Reborn flies missiles for `distance / (Speed·1000)` seconds on a
parabola with apex `Height·distance`, starting at the shooter + ForwardOffset/StartZOffset/RightOffset.
The runtime routine wasn't located (`CCombatEventMissileMove` only carries event data; functions
reading the MissileShot fields were not identified). Line shots place their effects every
`SegmentLength` along the path after `SkipSegmentsNumber` segments, `StepDelay` ms apart, each living
`EffectLifeTime` ms. The exe also has `CCombatEventFlyingText/FlyingNumber`: floating combat text
exists in the original.

## Open items
* `AM_TRANSPARENT` effect-model blend not traced in the exe (render states are set through engine
  wrappers; no shader files); premultiplied/straight-alpha as above is assumed.
* `L_LIT` particle lighting, `IsCrown`, `GlueToBone`, wind, decals (`SpellVisual.Decal`) not implemented.
* Whether instance `Scale` affects bake positions: the exe passes Scale as the size factor; the data
  (creature hit bakes ~2.7 units authored at Scale 0.5–0.7) indicates positions scale too, which
  H5Reborn does.
* Armageddon's late phase (≈7 s) shows a large white faceted model; likely a missing fade on that
  effect model.
