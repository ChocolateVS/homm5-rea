using System.Numerics;
using H5.Assets.Scenes;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Cinematics.Tests;

public sealed class GameFixture : IDisposable
{
    public PakFileSystem Fs { get; }
    public XdbLoader Xdb { get; }
    public GameInstall Install { get; }

    public GameFixture()
    {
        Install = GameInstall.Locate();
        Fs = PakFileSystem.OpenInstall(Install);
        Xdb = new XdbLoader(Fs);
    }

    public void Dispose() => Fs.Dispose();
}

public class CameraPosTests
{
    static void Near(Vector3 a, Vector3 b) => Assert.True(Vector3.Distance(a, b) < 1e-4f, $"{a} != {b}");

    [Fact]
    public void ZeroAnglesLookAlongPlusY()
    {
        var c = CameraPos.Default with { Yaw = 0, Pitch = 0, Rod = 10, Anchor = new Vector3(5, 5, 1) };
        Near(c.Forward, Vector3.UnitY);
        Near(c.Up, Vector3.UnitZ);
        Near(c.Eye, new Vector3(5, -5, 1));
    }

    [Fact]
    public void NegativePitchLooksDownAndForwardMatchesQuaternion()
    {
        var c = CameraPos.Default with { Yaw = 5.39963f, Pitch = -0.713758f, Rod = 53.4476f };
        Assert.True(c.Forward.Z < -0.6f);
        // The analytic forward equals the exe's quaternion order applied to the +Y axis.
        Near(c.Forward, Vector3.Transform(Vector3.UnitY, c.Rotation));
        Assert.True(c.Eye.Z > 30); // the adventure-dialog camera of A2C1M1 sits high above the map
    }

    [Fact]
    public void LerpTakesShortestYawUnlessDirected()
    {
        var a = CameraPos.Default with { Yaw = 0.1f };
        var b = CameraPos.Default with { Yaw = 6.2f };
        Assert.True(CameraPos.Lerp(a, b, 0.5f).Yaw < 0.1f);              // wraps backwards through 0
        Assert.True(CameraPos.Lerp(a, b, 0.5f, direction: 1).Yaw > 3f);   // forced increasing
        var full = CameraPos.Lerp(a, b, 1f, direction: 1, circles: 1).Yaw;
        Assert.InRange(full, 6.2f + MathF.Tau - 1e-3f, 6.2f + MathF.Tau + 1e-3f);
    }
}

public class CampaignTests(GameFixture g) : IClassFixture<GameFixture>
{
    [StockFact]
    public void StandardSetLoadsAllStockCampaigns()
    {
        var set = CampaignLoader.LoadStandardSet(g.Xdb);
        Assert.Equal(new[] { "A2C0", "A2C1", "A2C2", "A2C3" }, set.Select(c => c.InternalName));
        foreach (var c in set)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name), $"{c.InternalName} has no name");
            Assert.NotEmpty(c.Missions);
            foreach (var m in c.Missions)
            {
                Assert.True(g.Fs.Exists(m.MapTag), $"{c.InternalName} mission {m.Index}: missing {m.MapTag}");
                Assert.False(string.IsNullOrWhiteSpace(m.Name), $"{c.InternalName} mission {m.Index} has no name");
                Assert.StartsWith(c.InternalName, m.MapId);
            }
        }
        // A2C1 depends on the prologue and carries Ornella to mission 2.
        var c1 = set.Single(c => c.InternalName == "A2C1");
        Assert.Contains(c1.DependentCampaigns, d => d.EndsWith("A2C0.(Campaign).xdb", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(c1.Missions[0].HeroesPool, h => h.HeroScriptName == "OrnellaNecro" && h.TargetMission == 2);
        Assert.Contains(c1.Missions[0].Bonuses, b => b.Type == "E_BONUS_CREATURE" && b.Creature == "CREATURE_VAMPIRE" && b.Count == 10);
    }
}

public class DialogSceneTests(GameFixture g) : IClassFixture<GameFixture>
{
    [Fact]
    public void EveryStockDialogSceneLoadsWithResolvedActors()
    {
        var all = DialogSceneLoader.ListAll(g.Fs);
        Assert.True(all.Count >= 60, $"only {all.Count} scenes");
        int sentences = 0, voiced = 0;
        foreach (var path in all)
        {
            var d = DialogSceneLoader.Load(g.Xdb, path);
            // Only the campaign and single-mission scenes are used by the shipped scripts; the
            // rest (Presentation, For_KRI, FX_Test, A2PWL stub…) point at unshipped test maps.
            bool shipped = path.StartsWith("DialogScenes/A2C", StringComparison.OrdinalIgnoreCase)
                           || path.StartsWith("DialogScenes/A2Single", StringComparison.OrdinalIgnoreCase);
            if (!shipped) continue;
            Assert.True(g.Fs.Exists(d.MapPath), $"{path}: map {d.MapPath} missing");
            Assert.NotEmpty(d.Sentences);
            foreach (var s in d.Sentences)
            {
                sentences++;
                if (s.VoiceHref != null) voiced++;
                if (s.SpeakerId.Length > 0) Assert.True(d.Actors.ContainsKey(s.SpeakerId), $"{path}: speaker {s.SpeakerId} unresolved");
                foreach (var a in s.Animations) Assert.True(d.Actors.ContainsKey(a.ActorId));
            }
            foreach (var a in d.Actors.Values) Assert.True(g.Fs.Exists(a.SharedPath), $"{path}: actor shared {a.SharedPath} missing");
        }
        Assert.True(sentences >= 560, $"sentences {sentences} voiced {voiced}");
        Assert.True(voiced > 350);
    }

    [Fact]
    public void FirstNecromancerSceneMatchesData()
    {
        var d = DialogSceneLoader.Load(g.Xdb, "/DialogScenes/A2C1/M1/S1/DialogScene.xdb#xpointer(/DialogScene)");
        Assert.EndsWith("SmallSpecialArenas/A2C1/M1/S1/map.xdb", d.MapPath);
        var first = d.Sentences[0];
        Assert.Contains("Giovanni", d.Actors[first.SpeakerId].SharedPath);
        Assert.NotNull(first.Camera);
        Assert.Equal(11f, first.Camera!.Start.Pos.Rod);
        Assert.Equal(new Vector3(55, 30, 3.5f), first.Camera.Start.Pos.Anchor);
        Assert.Equal(5f, first.Duration);
        Assert.Contains(first.Animations, a => a.AnimName == "move" && a.MovePoints.Count == 1 && a.MovePoints[0] == new Vector2(29, 13));
        Assert.Equal(2, d.PresentActors.Count);
    }
}

public class MovieListTests(GameFixture g) : IClassFixture<GameFixture>
{
    [Fact]
    public void IntroListParsesAndResolvesShippedMovies()
    {
        var xml = File.ReadAllText(Path.Combine(g.Install.Root, "video", "intro_a1.xml"));
        var list = MovieList.Parse(xml);
        Assert.Equal(new[] { "../video/logo-ubi6", "../video/logo-nival", "../video/claim" }, list.Select(m => m.FileName));
        Assert.All(list, m => Assert.True(m.CanSkipMovie && m.CanSkipSequence));
        Assert.NotNull(MovieList.Resolve(g.Install, list[0].FileName));
        Assert.NotNull(MovieList.Resolve(g.Install, list[1].FileName));
        Assert.Null(MovieList.Resolve(g.Install, list[2].FileName)); // not shipped in this build
        Assert.Empty(MovieList.Parse(File.ReadAllText(Path.Combine(g.Install.Root, "video", "outro_a1.xml"))));
    }
}
