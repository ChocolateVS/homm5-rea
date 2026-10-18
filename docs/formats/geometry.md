# Geometry blobs (`bin/Geometries/<uid>`)

Decoder: `src/H5.Assets/Geometry/` (`NivalChunks`, `GeometryFile`, `GeometryConverter`).
Status: all 3572 blobs in data.pak + a2p1-data.pak decode; 3401/3421 decoded bounding boxes
match the xdb `<Size>`/`<Center>` within 1%. The other 20 are empty placeholder geometries (no
vertices) or have stale descriptors. Visually checked: Footman, BlackDragon, Knight
(adventure-map hero), Common_Dwelling_Castle (`h5tool render-mesh`).

## Where the uid comes from
`_(Model)/**.(Model).xdb` → `<Geometry>` (inline `#n:inline(Geometry)` or an href to a
`*-geom.xdb`) → `<uid>`. The same element holds `NumMeshes`, `MeshNames` (one per mesh),
`MaterialQuantities` (number of parts = material slots per mesh), `Size`, `Center`.
Part *j* of mesh *i* uses `Materials[sum(MaterialQuantities[0..i)) + j]`. Ten blobs have one
more part than their (stale) `MaterialQuantities`; the decoder maps extra parts to the mesh's
last material.

## Container: Nival binary structure saver
All bin/ blobs use the same chunk container:

```
chunk   := id:u8  size  payload[size]
size    := if (b0 & 1) == 0: one byte, value = b0 >> 1          (0..127)
           else:            u32 LE starting at b0, value = u32 >> 1
```

A structure is a sequence of chunks; the id is the field number. Field encodings:

| Kind | Encoding |
|---|---|
| scalar | payload is the raw value (int/float = 4 bytes, bool/byte = 1) |
| POD vector | chunk 1 = u32 count, chunk 2 = `count * sizeof(T)` raw bytes (both absent when empty) |
| struct vector | chunk 2 = u32 count, then one chunk **with id 1** per element (payload = element structure) |

## Geometry layout
```
file
 [4] u32 version = 4
 [1] body
      [2] struct vector<Mesh>          (count == NumMeshes)
      [3] u8 flags                      (0x01 most files, 0x40 or 0x00 on some; meaning unknown, not needed)
 [0] [2] [5] empty
Mesh = struct vector<Part>             (count == MaterialQuantities[i])
Part
 [2]  POD vector<float3>  positions                 (12 bytes each)
 [3]  POD vector<Vertex>  vertices                  (20 bytes each, below)
 [4]  POD vector<Skin>    skin, one per POSITION    (24 bytes each; empty for static parts)
 [5]  POD vector<u16>     vertex -> position index
 [6]  POD vector<u16>     vertex -> smoothing/normal-group id (not needed for rendering)
 [7]  POD vector<u16>     triangle list, 3 vertex indices per triangle
 [8]  struct vector, always empty in stock data
 [9]  u32 triangle count  (== [7].count / 3)
 [10] f32 per-part metric (texel density/LOD; varies 0.003..10+, not needed)
 [11] u8  looks like uninitialised exporter padding (often an ASCII letter)
 [12] i32 -1 for skinned parts; otherwise the bone index a rigid part is attached to (0,1,2,...)
```

### Vertex (20 bytes)
| Offset | Type | Meaning |
|---|---|---|
| 0 | i16 ×2 | UV0, fixed point: value / 2048 (range seen ±16) |
| 4 | i16 ×2 | UV1 (lightmap set on static map objects; zero on characters), / 2048 |
| 8 | u8 ×4 | normal, D3DCOLOR order B,G,R,A → (z, y, x, unused=0xFF); component = b / 127.5 − 1 |
| 12 | u8 ×4 | tangent, same packing |
| 16 | u8 ×4 | binormal, same packing; handedness = sign(dot(cross(N, T), B)) |

UV origin is top-left (D3D convention, same as glTF): no V flip is needed.
Triangles are counter-clockwise in source space when the face normal is computed as
`cross(v1 − v0, v2 − v0)` (normals agree with winding, mean dot ≈ 0.93 on Footman).

### Skin (24 bytes, per position)
| Offset | Type | Meaning |
|---|---|---|
| 0 | f32 ×4 | weights (sum 1) |
| 16 | u8 ×4 | the same weights quantised to bytes (sum 255) |
| 20 | u8 ×4 | bone indices; slots with weight 0 hold filler values |

Bone indices refer to the bone order of the model's Granny skeleton
(`bin/Skeletons/<Skeleton uid>`), decoded by the Granny workstream. The geometry blob
contains no bone names.

## Coordinate system
Z-up, right-handed, metres; creatures stand on z = 0. The glTF exporter keeps source data and
adds a root node rotated −90° about X to produce Y-up.

## Textures
Material `Texture`/`Bump`/`Gloss`/`Mirror` hrefs point to a `(Texture)` xdb; its `<DestName>`
(relative to that xdb) is the `.dds`. Stock DDS formats: A8R8G8B8 (5752), DXT3 (2651), DXT1
(843), 16-bit RGB (9), DXT5 (2), DXT4 (1). All decode with `DdsDecoder`.
