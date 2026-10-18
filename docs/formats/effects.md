# Effects (`Effects/**`)

Survey only — no decoder yet.

## XDB objects (counts in data.pak)

| type | count | role |
|---|---|---|
| `Effect` | 1814 | container: `Instances` (particles), `Models` (ModelInstance), `Lights`, `Duration`, `WindAffected/WindPower` |
| `ParticleInstance` | 1671 | placement of a particle system: `Particle` href, `Position`, `Rotation` (quat), `Scale`, `Speed`, `Offset`, `EndCycle`, `CycleCount`, `Pivot`, `Textures` (16 slots), `Light` mode, `IsCrown`, `Static` (P_STATIC...), `DoesCastShadow`, `GlueToBone` |
| `Particle` | 1192 | `SrcName` (Maya .mb), `ExportPrefix`, `WrapSize`, `Bound` (hex box), `PerParticleFog`, `uid` → `bin/effects/<uid>` |
| `ModelInstance` | 655 | animated mesh effect: `Model`, `SkelAnim`, transform, `Offset`, `CycleLength/Count`, `GlueToBone` (e.g. weapon trails) |
| `LightInstance` / `AnimLight` | 97 / 77 | dynamic lights in effects |

Creature animations reference effects through `BasicSkelAnim.Effect` (e.g.
`Effects/_(Effect)/Characters/Creatures/Haven/Footman/Attack00.xdb` = a sword-trail ModelInstance
with its own skeletal animation). Spells via `SpellVisual.Effect` + `HitDelay` + `ParticleDuration`.

## `bin/effects/<uid>` particle binaries

1919 files. **Not** the Nival chunk container: header e.g. `12 22 0B 00 89 88 18 41 00 00 F0 41 ...`
followed by float data; sizes up to ~730 KB (Arenas/FinalFight/WallFire). Only 191 of the first
200 `Particle` uids have a blob (others possibly in a2p1 paks or unused). These look like *baked*
particle simulations exported from Maya (per-frame particle states sampled at a fixed rate) rather
than emitter parameters — which would mean faithful playback is "replay the bake", not re-simulate.
Next step: decode the header (`0x0B22` count/version?, two floats ≈ 9.5 / 30.0 — duration and fps?)
on a few small blobs, then build a Godot `MultiMesh`/`GPUParticles3D`-free billboard player.

## Godot mapping (plan)

* `Effect` → a `Node3D` with children per instance, auto-freed after `Duration`.
* `ModelInstance` → skinned mesh + `AnimationPlayer` (needs geometry + Granny decoders).
* Baked particles → billboard quads in a `MultiMeshInstance3D`, updated per frame from the bake,
  textures from the 16 `Textures` slots.
