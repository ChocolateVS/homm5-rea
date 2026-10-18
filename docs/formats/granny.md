# Granny 2 skeletons and animations (`bin/Skeletons/<uid>`, `bin/animations/<uid>`)

Status: **fully decoded in pure C#** (`src/H5.Assets/Granny`). All 2247 skeletons and 3409
animations in data.pak + a2p1-data.pak decode and validate (`h5tool gr2-stats`).
No native granny2.dll is needed, so the decoder runs in the 64-bit Godot client and on Linux servers.

## Container

Standard little-endian 32-bit Granny 2 file, **file format 6**, exported with
"Granny Standard Exporter, SDK version 2.5.0.5" from Maya 6 (`ArtToolInfo`: UnitsPerMeter 100,
Z up, back = -Y; stored values are already in metres, engine space).

| Offset | Content |
|---|---|
| 0x00 | magic `B8 67 B0 CA F8 6D B1 0F 84 72 8C 7E 5E 19 00 1E` (16 bytes) |
| 0x10 | u32 size of header + file info + section table; u32 header format (0); 8 reserved |
| 0x20 | file info (0x38 bytes): format (6), total size, CRC32, info size (0x38), section count (8), type ref (section, offset), root ref (section, offset), type tag, 16 extra bytes |
| 0x58 | section table: 8 × 44 bytes |

Section header (11 × u32): compression, data offset, compressed size, decompressed size,
alignment, Oodle stop 0, Oodle stop 1, fixup offset, fixup count, marshalling offset, marshalling count.

* Compression is **2 = Oodle1** for every non-empty section (5 is stored uncompressed and empty).
  `Oodle1.cs` is a C# port of libopengrn's decoder (MPL-2.0, derived from nwn2mdk). Each section is
  decoded in three runs split at stop0 / stop1 / size, each with fresh adaptive dictionaries but a
  shared arithmetic decoder; the literal model index is `outputPosition % 4`.
* Fixups are `{srcOffset, dstSection, dstOffset}` triples (12 bytes). They are the only way to tell a
  pointer from data: `Gr2File` keeps a map srcAddress → dstAddress over the concatenated sections, and a
  pointer field with no fixup is null.
* Marshalling records are absent (count 0); files are native little-endian.
* Type tags seen: 0x80000010 (32 files), 0x80000011 (2787), 0x80000013 (2836), 0x80000015 (1). Types are
  self-described in the file (section 6), so the generic reader handles all tags identically.

### Type tree

Member definition, 32 bytes in 32-bit files: `u32 type, ptr name, ptr children, i32 arraySize,
u32[3] reserved, u32 reserved`. A list ends with type 0. Member types follow `granny_member_type`
(Inline 1, Reference 2, ReferenceToArray 3, ArrayOfReferences 4, VariantReference 5,
ReferenceToVariantArray 7, String 8, Transform 9, Real32 10, Int8..UInt32 11-20, Real16 21,
EmptyReference 22). Sizes: pointer 4, ReferenceToArray/ArrayOfReferences 8 (count, ptr), Variant 8,
VariantArray 12, Transform 68 (flags, t[3], q[4] xyzw, scaleShear[9] row-major).
`Gr2File` decodes the root into `GrObject` dictionaries keyed by member name.

## Root object (granny_file_info, 2.5 layout)

`ArtToolInfo, ExporterInfo, FromFileName, Textures, Materials, Skeletons, VertexDatas, TriTopologies,
Meshes, Models, TrackGroups, Animations, ExtendedData`. Skeleton blobs fill `Skeletons`/`Models`;
animation blobs fill `Animations` (always exactly one, with one track group). No meshes or textures:
geometry lives in Nival's own `bin/Geometries` format (see geometry.md).

### Skeleton

`Bones[]`: `Name, ParentIndex, Transform (rest pose, local), InverseWorldTransform[16], LightInfo,
CameraInfo, ExtendedData`. Parents always precede children; exactly one root (usually `Global`, which
often carries a uniform scale, e.g. 1.05 on the Footman).

`InverseWorldTransform` is the **bind pose** used for skinning and is row-major with translation in
the last row — the same layout as `System.Numerics.Matrix4x4`. It is *not* always the inverse of
the rest-pose chain: 448 skeletons (mostly map objects/effects) have root bones whose rest transform
differs from the bind matrix. Skinning must therefore use `InverseBind * AnimatedWorld`; an
unanimated model should be drawn with its geometry as stored (bind space), not by applying rest-pose
skinning. Shear in rest transforms is negligible (15 bones); `Bone.Scale` takes the diagonal.

Skinning convention (row vectors): `local = S * R * T`, `world = local * parentWorld`,
`skin = InverseBind * world`. Geometry skin indices (`bin/Geometries` skin records) index this
skeleton's bone array directly: verified over 482 character models — no index is out of range, and
94.4% of sampled vertices lie nearer their dominant bone than a random bone (`h5tool gr2-skin-check`).
Footman attack00 renders correctly when CPU-skinned (`h5tool gr2-pose`), and an exported glTF skinned
independently in numpy matches the CPU result to the millimetre.

### Animation

`Name` (Maya source path), `Duration`, `TimeStep` (1/60), `Oversampling` (2), `TrackGroups[1]`:
`Name, VectorTracks[], TransformTracks[], TransformLODErrors, TextTracks[], InitialPlacement,
AccumulationFlags, LoopTranslation[3], PeriodicLoop, RootMotion, ExtendedData`.

Transform tracks are matched to bones **by name**. Each has three curves
(`PositionCurve` dim 3, `OrientationCurve` dim 4 quaternion xyzw, `ScaleShearCurve` dim 9), all in the
old 2.5 representation `{ Degree, Knots[], Controls[] }` — not the later compressed `curve2`
formats (DaK32fC32f, D4nK16uC15u ...), none of which occur.

Curve census over all files (`channel:degree/dimension`):

| | empty | constant | deg 0 | deg 1 | deg 2 | deg 3 |
|---|---|---|---|---|---|---|
| position | 12 986 | 257 950 | – | 3 233 | 7 615 | 7 |
| orientation | 18 519 | 27 319 | – | – | 233 130 | 2 823 |
| scale/shear | 273 617 | 5 576 | 77 | 2 049 | 472 | – |

Evaluation (`GrannyCurve.Evaluate`): a clamped non-uniform B-spline. With n controls and degree d, the
knot vector is `[0 × (d+1), k_d … k_{n-1}, k_{n-1} × d]` — the first d stored knots are clamped to
zero (in the data they are 0 and 1/600 s). Evaluated with de Boor's algorithm (as nwn2mdk does for
Granny 2.4 NWN2 files). Knots after the first d are 1/30 s apart (the 60 Hz time step × oversampling 2).
Degree 0 is a step function; a single knot is a constant; repeated knots encode intentional jumps.
Quaternion controls are blended component-wise and normalised.

**Empty curves** (no knots) are Granny identity curves: zero translation, identity rotation, unit
scale/shear — not "keep the bind pose". `GrannyConvert.ToClip` bakes them as one identity key so all
consumers agree. In practice the distinction is invisible (empty channels coincide with identity bind
values except for a handful of sub-10 cm accessory bones). Bones with no track keep their rest pose.

Baked `AnimationClip`s keep linear/step knots exactly and resample degree ≥ 2 curves at 30 Hz
(`GrannyConvert.DefaultSampleRate`) plus the end time; quaternion signs are made continuous.
Use `GrannyCurve`/`GrannyTransformTrack` directly for exact evaluation at arbitrary times.

**Events:** animators placed text events in `TextTracks` (track `Global.Mark`), e.g. Footman attack00
has `B_cut@0.500` and `action@0.767` — `action` is the moment the blow lands and should drive damage
numbers, hit reactions and sounds. A dimension-1 vector track `Mark` carries a related step signal.
Exposed as `GrannyAnimation.TextEvents` / `LoadedAnimation.Events`.

## Descriptor chain (xdb)

* `(Character)` → `Model` href, `ArenaAnimSet` (combat), `AdvMapAnimSet` (adventure map).
* `(Model)` → `Skeleton` (inline or href) → `<uid>` → `bin/Skeletons/<uid>`.
* `(AnimSet)` → `animations/Item { Kind, Anim href }` → `BasicSkelAnim` object (any folder) →
  `<uid>` → `bin/animations/<uid>`; plus `MovementSpeed`, `SpeedFactor`, `Sound`, `Effect` hrefs.
  837 anim sets, 1095 entries. Kinds: idle00 299, death 110, move 97, hit 93, attack00 85, happy 79,
  stir00 58, attack01 49, rangeattack 28, cast 21, specability 20, … (heroes add speech_*/sp* kinds).
* `ExpFrameFirst/ExpFrameLast` in BasicSkelAnim are always 0; the authoritative duration is the
  Granny `Duration` (no knot ever exceeds it).

`AnimSetLoader` implements the chain (thread-safe, cached per uid) and plugs into
`ModelLoader.SkeletonProvider/AnimationProvider` via `AnimSetLoader.Install`.
Over all 540 `(Character)` files: 534 resolve (the 6 failures are TESTS/ leftovers and one hero with no
model); 586 anim sets, 3146 animations, 333 388 tracks, 214 tracks (0.06%) name bones absent from the
model's skeleton (ignored).

## Tools

* `h5tool gr2-dump <uid|path> [--types] [--depth N]` — section table, type tree, object tree.
* `h5tool gr2-curve <anim uid> <bone> [skeleton uid]` — one track's curves and samples vs bind pose.
* `h5tool gr2-stats` — decode everything, curve census.
* `h5tool gr2-check-sets` — resolve every Character and cross-check tracks/skeletons.
* `h5tool gr2-skin-check` — verify geometry skin indices and inverse binds against skeletons.
* `h5tool gr2-pose <Character.xdb> <kind> <time|N%> <out.png> [yaw]` — CPU-skinned render.
* `h5tool gr2-export <Character.xdb> <outDir> [kinds]` — glTF with skin and animations.

## Open questions

* `AccumulationFlags`, `LoopTranslation`, `PeriodicLoop`, `RootMotion` are decoded but unused:
  check whether any move animation carries root motion the client must apply.
* Unknown meaning of the `Mark` vector-track values (0 → 2 steps near the hit).
