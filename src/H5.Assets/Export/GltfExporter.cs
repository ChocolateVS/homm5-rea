using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using H5.Assets.Model;
using H5.Assets.Textures;
using H5.Core.IO;

namespace H5.Assets.Export;

/// <summary>
/// Writes a <see cref="ModelAsset"/> as glTF 2.0 (.gltf + .bin + PNG textures).
/// Data stays in source (Z-up) space under a root node rotated -90° about X,
/// which yields glTF's Y-up without touching skin or animation data.
/// </summary>
public sealed class GltfExporter
{
    readonly PakFileSystem _fs;
    readonly MemoryStream _bin = new();
    readonly JsonArray _accessors = new(), _views = new(), _meshes = new(), _nodes = new(),
        _materials = new(), _images = new(), _textures = new(), _skins = new(), _animations = new();

    public GltfExporter(PakFileSystem fs) => _fs = fs;

    public static void Export(PakFileSystem fs, ModelAsset asset, string outDir, string name)
        => new GltfExporter(fs).Write(asset, outDir, name);

    void Write(ModelAsset asset, string outDir, string name)
    {
        Directory.CreateDirectory(outDir);
        WriteMaterials(asset, outDir, name);

        // Node 0: Z-up → Y-up root.
        var rootChildren = new JsonArray();
        _nodes.Add(new JsonObject { ["name"] = name, ["rotation"] = new JsonArray(-MathF.Sqrt(0.5f), 0f, 0f, MathF.Sqrt(0.5f)), ["children"] = rootChildren });

        int jointBase = -1;
        var skel = asset.Skeleton;
        if (skel != null && skel.Bones.Count > 0)
        {
            jointBase = _nodes.Count;
            for (int i = 0; i < skel.Bones.Count; i++)
            {
                var b = skel.Bones[i];
                _nodes.Add(new JsonObject
                {
                    ["name"] = b.Name,
                    ["translation"] = Vec(b.Translation),
                    ["rotation"] = new JsonArray(b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W),
                    ["scale"] = Vec(b.Scale),
                });
            }
            for (int i = 0; i < skel.Bones.Count; i++)
            {
                int p = skel.Bones[i].Parent;
                var parentChildren = p < 0 ? rootChildren : (JsonArray)(_nodes[jointBase + p]!["children"] ??= new JsonArray());
                parentChildren.Add(jointBase + i);
            }
            var ibm = new float[skel.Bones.Count * 16];
            for (int i = 0; i < skel.Bones.Count; i++) WriteMatrix(ibm, i * 16, skel.Bones[i].InverseBind);
            int ibmAcc = AddAccessor(ibm, "MAT4", skel.Bones.Count, 0);
            _skins.Add(new JsonObject
            {
                ["joints"] = new JsonArray(Enumerable.Range(jointBase, skel.Bones.Count).Select(j => (JsonNode)j).ToArray()),
                ["inverseBindMatrices"] = ibmAcc,
                ["skeleton"] = jointBase,
            });
        }

        foreach (var part in asset.Mesh.Parts)
        {
            if (part.Indices.Length == 0) continue;
            var attrs = new JsonObject
            {
                ["POSITION"] = AddVec3(part.Positions, withBounds: true),
                ["NORMAL"] = AddVec3(part.Normals),
                ["TEXCOORD_0"] = AddVec2(part.UV0),
            };
            if (part.UV1 != null) attrs["TEXCOORD_1"] = AddVec2(part.UV1);
            if (part.Tangents != null) attrs["TANGENT"] = AddVec4(part.Tangents);
            bool skinned = part.Skin != null && jointBase >= 0;
            if (skinned)
            {
                var joints = new ushort[part.Skin!.Length * 4];
                var weights = new float[part.Skin.Length * 4];
                int maxBone = skel!.Bones.Count - 1;
                for (int i = 0; i < part.Skin.Length; i++)
                {
                    var s = part.Skin[i];
                    joints[i * 4] = (ushort)Math.Clamp(s.B0, 0, maxBone); joints[i * 4 + 1] = (ushort)Math.Clamp(s.B1, 0, maxBone);
                    joints[i * 4 + 2] = (ushort)Math.Clamp(s.B2, 0, maxBone); joints[i * 4 + 3] = (ushort)Math.Clamp(s.B3, 0, maxBone);
                    weights[i * 4] = s.W0; weights[i * 4 + 1] = s.W1; weights[i * 4 + 2] = s.W2; weights[i * 4 + 3] = s.W3;
                }
                attrs["JOINTS_0"] = AddAccessor(joints, "VEC4", part.Skin.Length, 34962);
                attrs["WEIGHTS_0"] = AddAccessor(weights, "VEC4", part.Skin.Length, 34962);
            }
            var indices = part.Indices.Select(i => (uint)i).ToArray();
            var prim = new JsonObject { ["attributes"] = attrs, ["indices"] = AddAccessor(indices, "SCALAR", indices.Length, 34963) };
            if (part.MaterialIndex < _materials.Count) prim["material"] = part.MaterialIndex;
            _meshes.Add(new JsonObject { ["name"] = part.Name, ["primitives"] = new JsonArray(prim) });
            var node = new JsonObject { ["name"] = part.Name, ["mesh"] = _meshes.Count - 1 };
            if (skinned) node["skin"] = 0;
            _nodes.Add(node);
            rootChildren.Add(_nodes.Count - 1);
        }

        if (jointBase >= 0)
            foreach (var clip in asset.Animations.Values) WriteAnimation(clip, skel!, jointBase);

        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "H5Reborn" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = _nodes,
            ["meshes"] = _meshes,
            ["accessors"] = _accessors,
            ["bufferViews"] = _views,
            ["buffers"] = new JsonArray(new JsonObject { ["uri"] = name + ".bin", ["byteLength"] = _bin.Length }),
        };
        if (_materials.Count > 0) gltf["materials"] = _materials;
        if (_images.Count > 0) { gltf["images"] = _images; gltf["textures"] = _textures; gltf["samplers"] = new JsonArray(new JsonObject { ["wrapS"] = 10497, ["wrapT"] = 10497 }); }
        if (_skins.Count > 0) gltf["skins"] = _skins;
        if (_animations.Count > 0) gltf["animations"] = _animations;

        File.WriteAllBytes(Path.Combine(outDir, name + ".bin"), _bin.ToArray());
        File.WriteAllText(Path.Combine(outDir, name + ".gltf"), gltf.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
    }

    void WriteMaterials(ModelAsset asset, string outDir, string name)
    {
        var imageIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < asset.Materials.Count; i++)
        {
            var m = asset.Materials[i];
            var pbr = new JsonObject { ["metallicFactor"] = 0f, ["roughnessFactor"] = 1f };
            if (m.DiffuseTexture != null && _fs.TryReadAllBytes(m.DiffuseTexture, out var dds))
            {
                if (!imageIndex.TryGetValue(m.DiffuseTexture, out var img))
                {
                    try
                    {
                        var png = PngWriter.Encode(DdsDecoder.Decode(dds));
                        var file = $"{name}_tex{_images.Count}.png";
                        File.WriteAllBytes(Path.Combine(outDir, file), png);
                        _images.Add(new JsonObject { ["uri"] = file, ["name"] = Path.GetFileName(m.DiffuseTexture) });
                        _textures.Add(new JsonObject { ["source"] = _images.Count - 1, ["sampler"] = 0 });
                        img = _textures.Count - 1;
                    }
                    catch (Exception e) when (e is NotSupportedException or InvalidDataException) { img = -1; }
                    imageIndex[m.DiffuseTexture] = img;
                }
                if (img >= 0) pbr["baseColorTexture"] = new JsonObject { ["index"] = img };
            }
            var mat = new JsonObject { ["name"] = $"mat{i}", ["pbrMetallicRoughness"] = pbr, ["doubleSided"] = m.TwoSided };
            switch (m.AlphaMode)
            {
                case "AM_ALPHA_TEST": mat["alphaMode"] = "MASK"; mat["alphaCutoff"] = 0.5f; break;
                case "AM_ALPHA_BLEND": case "AM_TRANSPARENT": case "AM_DECAL": mat["alphaMode"] = "BLEND"; break;
            }
            _materials.Add(mat);
        }
    }

    void WriteAnimation(AnimationClip clip, SkeletonData skel, int jointBase)
    {
        var samplers = new JsonArray();
        var channels = new JsonArray();
        foreach (var t in clip.Tracks)
        {
            int bone = skel.IndexOf(t.BoneName);
            if (bone < 0) continue;
            void Add(float[] times, float[] values, string type, string path)
            {
                if (times.Length == 0) return;
                int input = AddAccessor(times, "SCALAR", times.Length, 0, withMinMax: true);
                int output = AddAccessor(values, type, times.Length, 0);
                samplers.Add(new JsonObject { ["input"] = input, ["output"] = output, ["interpolation"] = "LINEAR" });
                channels.Add(new JsonObject { ["sampler"] = samplers.Count - 1, ["target"] = new JsonObject { ["node"] = jointBase + bone, ["path"] = path } });
            }
            Add(t.TranslationTimes, t.Translations.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray(), "VEC3", "translation");
            Add(t.RotationTimes, t.Rotations.SelectMany(q => new[] { q.X, q.Y, q.Z, q.W }).ToArray(), "VEC4", "rotation");
            Add(t.ScaleTimes, t.Scales.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray(), "VEC3", "scale");
        }
        if (channels.Count > 0)
            _animations.Add(new JsonObject { ["name"] = clip.Name, ["samplers"] = samplers, ["channels"] = channels });
    }

    int AddVec3(Vector3[] v, bool withBounds = false)
    {
        var f = new float[v.Length * 3];
        for (int i = 0; i < v.Length; i++) { f[i * 3] = v[i].X; f[i * 3 + 1] = v[i].Y; f[i * 3 + 2] = v[i].Z; }
        return AddAccessor(f, "VEC3", v.Length, 34962, withBounds);
    }

    int AddVec2(Vector2[] v)
    {
        var f = new float[v.Length * 2];
        for (int i = 0; i < v.Length; i++) { f[i * 2] = v[i].X; f[i * 2 + 1] = v[i].Y; }
        return AddAccessor(f, "VEC2", v.Length, 34962);
    }

    int AddVec4(Vector4[] v)
    {
        var f = new float[v.Length * 4];
        for (int i = 0; i < v.Length; i++) { f[i * 4] = v[i].X; f[i * 4 + 1] = v[i].Y; f[i * 4 + 2] = v[i].Z; f[i * 4 + 3] = v[i].W; }
        return AddAccessor(f, "VEC4", v.Length, 34962);
    }

    int AddAccessor<T>(T[] data, string type, int count, int target, bool withMinMax = false) where T : unmanaged
    {
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan());
        var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = _bin.Length, ["byteLength"] = bytes.Length };
        if (target != 0) view["target"] = target;
        _bin.Write(bytes);
        _views.Add(view);
        int componentType = typeof(T) == typeof(float) ? 5126 : typeof(T) == typeof(uint) ? 5125 : 5123;
        var acc = new JsonObject { ["bufferView"] = _views.Count - 1, ["componentType"] = componentType, ["count"] = count, ["type"] = type };
        if (withMinMax && data is float[] fl && count > 0)
        {
            int comps = fl.Length / count;
            var min = new JsonArray(); var max = new JsonArray();
            for (int c = 0; c < comps; c++)
            {
                float mn = float.MaxValue, mx = float.MinValue;
                for (int i = 0; i < count; i++) { mn = MathF.Min(mn, fl[i * comps + c]); mx = MathF.Max(mx, fl[i * comps + c]); }
                min.Add(mn); max.Add(mx);
            }
            acc["min"] = min; acc["max"] = max;
        }
        _accessors.Add(acc);
        return _accessors.Count - 1;
    }

    static JsonArray Vec(Vector3 v) => new(v.X, v.Y, v.Z);

    static void WriteMatrix(float[] dst, int o, Matrix4x4 m)
    {
        // System.Numerics uses row vectors (translation in M41..M43); glTF is column-major
        // with column vectors, which has the same memory layout.
        dst[o + 0] = m.M11; dst[o + 1] = m.M12; dst[o + 2] = m.M13; dst[o + 3] = m.M14;
        dst[o + 4] = m.M21; dst[o + 5] = m.M22; dst[o + 6] = m.M23; dst[o + 7] = m.M24;
        dst[o + 8] = m.M31; dst[o + 9] = m.M32; dst[o + 10] = m.M33; dst[o + 11] = m.M34;
        dst[o + 12] = m.M41; dst[o + 13] = m.M42; dst[o + 14] = m.M43; dst[o + 15] = m.M44;
    }
}
