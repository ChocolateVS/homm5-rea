using System.Numerics;
using System.Text.Json;
using H5.Assets.Export;
using H5.Assets.Geometry;
using H5.Assets.Model;
using H5.Assets.Textures;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Assets.Tests;

public sealed class InstallFixture : IDisposable
{
    public PakFileSystem? Fs { get; }

    public InstallFixture()
    {
        try { Fs = PakFileSystem.OpenInstall(GameInstall.Locate()); }
        catch (DirectoryNotFoundException) { Fs = null; } // tests needing game data become no-ops
    }

    public void Dispose() => Fs?.Dispose();
}

public class ChunkTests
{
    [Fact]
    public void ReadsShortAndLongSizes()
    {
        // [7] size 2 (short form: 2<<1), then [9] size 3 (long form: (3<<1)|1 as u32)
        byte[] data = { 7, 4, 0xAA, 0xBB, 9, 7, 0, 0, 0, 1, 2, 3 };
        var ch = NivalChunks.Read(data, 0, data.Length);
        Assert.Equal(2, ch.Count);
        Assert.Equal((byte)7, ch[0].Id); Assert.Equal(2, ch[0].Offset); Assert.Equal(2, ch[0].Length);
        Assert.Equal((byte)9, ch[1].Id); Assert.Equal(9, ch[1].Offset); Assert.Equal(3, ch[1].Length);
    }

    [Fact]
    public void RejectsOverrun()
    {
        byte[] data = { 1, 20, 0 };
        Assert.Throws<InvalidDataException>(() => NivalChunks.Read(data, 0, data.Length));
    }

    [Fact]
    public void PodAndStructVectors()
    {
        // POD vector: [1]=count 2, [2]=two u16
        byte[] pod = { 1, 8, 2, 0, 0, 0, 2, 8, 5, 0, 6, 0 };
        Assert.Equal(new ushort[] { 5, 6 }, NivalChunks.ReadPodVector<ushort>(pod, new Chunk(0, 0, pod.Length)));
        // struct vector: [2]=count 2, [1]=elem, [1]=elem
        byte[] sv = { 2, 8, 2, 0, 0, 0, 1, 2, 0x11, 1, 2, 0x22 };
        var el = NivalChunks.StructVectorElements(sv, new Chunk(0, 0, sv.Length));
        Assert.Equal(2, el.Count);
        Assert.Equal(0x22, sv[el[1].Offset]);
    }

    [Fact]
    public void UnpacksD3DColorNormals()
    {
        // B=0xFF (z=+1), G=0x80, R=0x80
        var n = GeometryConverter.UnpackUnit(0xFF8080FF);
        Assert.True(n.Z > 0.99f);
    }
}

public class DdsTests
{
    [Fact]
    public void DecodesDxt1SolidBlock()
    {
        var dds = new byte[128 + 8];
        BitConverter.GetBytes(0x20534444u).CopyTo(dds, 0);
        BitConverter.GetBytes(4).CopyTo(dds, 12); BitConverter.GetBytes(4).CopyTo(dds, 16);
        BitConverter.GetBytes(4u).CopyTo(dds, 80);
        "DXT1"u8.ToArray().CopyTo(dds, 84);
        BitConverter.GetBytes((ushort)0xF800).CopyTo(dds, 128); // c0 = pure red
        BitConverter.GetBytes((ushort)0x0000).CopyTo(dds, 130);
        var img = DdsDecoder.Decode(dds); // all indices 0 → red
        Assert.Equal(255, img.Pixels[0]); Assert.Equal(0, img.Pixels[1]); Assert.Equal(0, img.Pixels[2]); Assert.Equal(255, img.Pixels[3]);
    }

    [Fact]
    public void PngRoundTripHeader()
    {
        var png = PngWriter.Encode(new RgbaImage(2, 2));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }
}

public class GameDataGeometryTests : IClassFixture<InstallFixture>
{
    const string FootmanModel = "_(Model)/Creatures/Haven/Footman.(Model).xdb";
    const string FootmanGeometry = "bin/Geometries/044854ED-7D70-4D36-894C-853F853ECACC";
    readonly PakFileSystem? _fs;

    public GameDataGeometryTests(InstallFixture f) => _fs = f.Fs;

    [Fact]
    public void DecodesFootmanGeometry()
    {
        if (_fs == null) return;
        var g = GeometryFile.Decode(_fs.ReadAllBytes(FootmanGeometry));
        Assert.Equal(4, g.Version);
        Assert.Equal(2, g.Meshes.Count);
        var weapon = g.Meshes[0].Parts.Single();
        Assert.Equal(99, weapon.PositionCount);
        Assert.Equal(243, weapon.Vertices.Length);
        Assert.Equal(162, weapon.TriangleCount);
        Assert.Equal(162 * 3, weapon.Triangles.Length);
        Assert.True(weapon.IsSkinned);
        Assert.Equal(-1, weapon.AttachBone);
    }

    [Fact]
    public void FootmanBoundsMatchDescriptor()
    {
        if (_fs == null) return;
        var asset = new ModelLoader(new XdbLoader(_fs)).Load(FootmanModel);
        var size = asset.Mesh.BoundsMax - asset.Mesh.BoundsMin;
        var center = (asset.Mesh.BoundsMax + asset.Mesh.BoundsMin) * 0.5f;
        Assert.True((size - new Vector3(2.07853f, 1.88616f, 1.72413f)).Length() < 0.01f, $"size {size}");
        Assert.True((center - new Vector3(-0.0305844f, -0.240343f, 0.863112f)).Length() < 0.01f, $"center {center}");
        Assert.Equal(2, asset.Materials.Count);
        Assert.All(asset.Materials, m => Assert.True(m.DiffuseTexture != null && _fs.Exists(m.DiffuseTexture)));
        Assert.Equal(new[] { 0, 1 }, asset.Mesh.Parts.Select(p => p.MaterialIndex));
        Assert.Equal("WeaponShape", asset.Mesh.Parts[0].Name);
    }

    [Fact]
    public void NormalsAgreeWithWinding()
    {
        if (_fs == null) return;
        var md = GeometryConverter.ToMeshData(GeometryFile.Decode(_fs.ReadAllBytes(FootmanGeometry)));
        double agree = 0; int n = 0;
        foreach (var p in md.Parts)
            for (int t = 0; t < p.Indices.Length; t += 3)
            {
                var a = p.Positions[p.Indices[t]]; var b = p.Positions[p.Indices[t + 1]]; var c = p.Positions[p.Indices[t + 2]];
                var fn = Vector3.Cross(b - a, c - a);
                if (fn.LengthSquared() < 1e-12f) continue;
                agree += Vector3.Dot(Vector3.Normalize(fn), p.Normals[p.Indices[t]]); n++;
            }
        Assert.True(agree / n > 0.7, $"mean normal agreement {agree / n}");
    }

    [Fact]
    public void EveryGeometryDecodes()
    {
        if (_fs == null) return;
        var failures = new List<string>();
        foreach (var path in _fs.Enumerate("bin/Geometries/"))
        {
            try { GeometryConverter.ToMeshData(GeometryFile.Decode(_fs.ReadAllBytes(path))); }
            catch (Exception e) { failures.Add($"{path}: {e.Message}"); }
        }
        Assert.Empty(failures);
    }

    [Fact]
    public void ExportsValidGltf()
    {
        if (_fs == null) return;
        var asset = new ModelLoader(new XdbLoader(_fs)).Load(FootmanModel);
        var dir = Path.Combine(Path.GetTempPath(), "h5gltf_" + Guid.NewGuid().ToString("N"));
        try
        {
            GltfExporter.Export(_fs, asset, dir, "Footman");
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "Footman.gltf")));
            var root = doc.RootElement;
            long binLen = new FileInfo(Path.Combine(dir, "Footman.bin")).Length;
            Assert.Equal(binLen, root.GetProperty("buffers")[0].GetProperty("byteLength").GetInt64());
            foreach (var v in root.GetProperty("bufferViews").EnumerateArray())
                Assert.True(v.GetProperty("byteOffset").GetInt64() + v.GetProperty("byteLength").GetInt64() <= binLen);
            Assert.Equal(2, root.GetProperty("meshes").GetArrayLength());
            Assert.Equal(2, root.GetProperty("images").GetArrayLength());
            foreach (var img in root.GetProperty("images").EnumerateArray())
                Assert.True(File.Exists(Path.Combine(dir, img.GetProperty("uri").GetString()!)));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void DecodesStockTextureFormats()
    {
        if (_fs == null) return;
        foreach (var path in new[]
                 {
                     "Textures/Creatures/Haven/Footman_LOD/Footman_weapon.tga.dds", // DXT3
                 })
        {
            if (!_fs.Exists(path)) continue;
            var img = DdsDecoder.Decode(_fs.ReadAllBytes(path));
            Assert.True(img.Width > 0 && img.Height > 0);
        }
    }
}

public class GameDataTextureTests : IClassFixture<InstallFixture>
{
    readonly PakFileSystem? _fs;
    public GameDataTextureTests(InstallFixture f) => _fs = f.Fs;

    [Fact]
    public void EveryStockTextureDecodes()
    {
        if (_fs == null) return;
        var failures = new List<string>();
        foreach (var path in _fs.Paths.Where(p => p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            try
            {
                var img = DdsDecoder.Decode(_fs.ReadAllBytes(path));
                if (img.Width <= 0 || img.Height <= 0) failures.Add(path + ": empty");
            }
            catch (Exception e) { failures.Add($"{path}: {e.Message}"); }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)) + $"\n({failures.Count} failures)");
    }
}
