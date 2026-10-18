using System.Numerics;
using H5.Assets.Granny;
using H5.Assets.Model;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Granny.Tests;

/// <summary>Shared pak mount; game-data tests return early when no install is present.</summary>
public sealed class GameFixture : IDisposable
{
    public PakFileSystem? Fs { get; }
    public AnimSetLoader? Anims { get; }

    public GameFixture()
    {
        try
        {
            Fs = PakFileSystem.OpenInstall(GameInstall.Locate());
            Anims = new AnimSetLoader(new XdbLoader(Fs));
        }
        catch (DirectoryNotFoundException) { }
    }

    public void Dispose() => Fs?.Dispose();
}

public class GameDataTests(GameFixture game) : IClassFixture<GameFixture>
{
    const string FootmanSkeleton = "E5856CB4-F3E3-44FD-9FBF-74639A66F72A";
    const string FootmanAttack00 = "06661489-7EB6-4E78-9137-20B6753570AF";
    const string FootmanCharacter = "Characters/Creatures/Haven/Footman.(Character).xdb";
    const string FootmanModel = "_(Model)/Creatures/Haven/Footman.(Model).xdb";

    [Fact]
    public void FootmanSkeletonDecodes()
    {
        if (game.Fs == null) return;
        var g = Gr2File.Read(game.Fs.ReadAllBytes("bin/Skeletons/" + FootmanSkeleton));
        Assert.Equal(6, g.FormatVersion);
        Assert.Equal(2u, g.Sections[0].Compression); // Oodle1
        var s = GrannyConvert.ReadSkeleton(g)!;
        GrannyConvert.Validate(s);
        Assert.Equal(45, s.Bones.Count);
        Assert.Equal("Global", s.Bones[0].Name);
        Assert.True(s.IndexOf("Chest") > 0); // the MountBone named by CreatureVisual
        Assert.Equal(1.05f, s.Bones[0].Scale.X, 4);
    }

    [Fact]
    public void FootmanAttackIsContinuousAndCarriesHitEvent()
    {
        if (game.Fs == null) return;
        var a = GrannyConvert.ReadGrannyAnimations(Gr2File.Read(game.Fs.ReadAllBytes("bin/animations/" + FootmanAttack00))).Single();
        Assert.Equal(2.1333f, a.Duration, 3);
        Assert.Contains(a.TextEvents, e => e.Text == "action" && MathF.Abs(e.Time - 0.7667f) < 1e-3f);
        Assert.Equal(45, a.TransformTracks.Count);

        const float dt = 1f / 240f;
        foreach (var t in a.TransformTracks)
        {
            var prevQ = t.SampleOrientation(0);
            var prevP = t.SamplePosition(0);
            for (float time = dt; time <= a.Duration; time += dt)
            {
                var q = t.SampleOrientation(time);
                var p = t.SamplePosition(time);
                Assert.True(float.IsFinite(q.X + q.Y + q.Z + q.W) && float.IsFinite(p.X + p.Y + p.Z), $"{t.Name} NaN at {time}");
                float angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(prevQ, q)), 0f, 1f));
                // A sign flip or bad evaluation shows up as ~pi; fast slashes reach ~0.17 rad per frame.
                Assert.True(angle < 0.3f, $"{t.Name}: rotation jumps {angle:F3} rad at {time:F3}s");
                // 0.1 m per 1/240 s allows the sword-tip speed-line helper (~15 m/s mid-slash).
                Assert.True(Vector3.Distance(prevP, p) < 0.1f, $"{t.Name}: position jumps at {time:F3}s");
                prevQ = q; prevP = p;
            }
        }
    }

    [Fact]
    public void FootmanAnimSetResolvesByKind()
    {
        if (game.Anims == null) return;
        var (model, arena, _) = game.Anims.ReadCharacter(FootmanCharacter);
        Assert.Equal(FootmanModel, model);
        Assert.Equal(FootmanSkeleton, game.Anims.SkeletonUidForModel(model!));
        var set = game.Anims.LoadAnimSet(arena!);
        foreach (var kind in new[] { "idle00", "move", "attack00", "hit", "death" })
            Assert.True(set.ContainsKey(kind), kind);
        Assert.Equal(FootmanAttack00, set["attack00"].Entry.Uid);
        Assert.Equal("attack00", set["attack00"].Clip.Name);
        GrannyConvert.Validate(set["attack00"].Clip);
    }

    [Fact]
    public void SkinnedFootmanPoseLunges()
    {
        if (game.Anims == null || game.Fs == null) return;
        var ml = game.Anims.Install(new ModelLoader(new XdbLoader(game.Fs)));
        var asset = ml.Load(FootmanModel);
        var skel = asset.Skeleton!;
        Assert.NotNull(skel);
        var clip = ml.LoadAnimSet("_(AnimSet)/Creatures/Haven/Footman-arena.(AnimSet).xdb")["attack00"];
        // Skin indices address the Granny skeleton directly (bone order = skeleton order).
        foreach (var p in asset.Mesh.Parts.Where(p => p.Skin != null))
            Assert.All(p.Skin!, s => Assert.True(Math.Max(Math.Max(s.B0, s.B1), Math.Max(s.B2, s.B3)) < skel.Bones.Count));

        var posed = GrannyPose.Deform(asset.Mesh, GrannyPose.SkinMatrices(skel, GrannyPose.AnimatedWorld(skel, clip, clip.Duration * 0.35f)));
        // Bind is a T-pose ~2.4 m tall incl. raised shield; mid-thrust the soldier lunges forward (-X) and crouches.
        Assert.True(posed.BoundsMin.X < -1.4f, $"min x {posed.BoundsMin.X}");
        Assert.True(posed.BoundsMax.Z < 1.6f, $"max z {posed.BoundsMax.Z}");
        Assert.True(posed.BoundsMin.Z > -0.1f, "feet stay on the ground");
    }
}
