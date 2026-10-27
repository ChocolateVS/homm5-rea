using H5.Adventure.Engine;

namespace H5.Adventure.Tests;

/// <summary>CArtifactsGenerator: pools 009cf6c0, draw 009cf360 (via 009cf8b0), callers 00b02740 / 009cd2a0.</summary>
[Collection("adv")]
public class ArtifactGeneratorTests(Fixture f)
{
    [Fact]
    public void DrawsStayInTheClassRangeAndSkipArtifactsNotGeneratedToSell()
    {
        var g = f.NewGame("A2M1");
        for (int i = 0; i < 200; i++)
        {
            var a = g.GenerateArtifact(0, 1)!;
            Assert.Contains(g.Db.ArtifactClass[a], new[] { "ARTF_CLASS_MINOR", "ARTF_CLASS_MAJOR" });
            Assert.DoesNotContain(a, g.Db.ArtifactsNotForSale);
        }
        // hi is clamped to 2: the grail class never comes out.
        for (int i = 0; i < 100; i++) Assert.NotEqual("ARTF_CLASS_GRAIL", g.Db.ArtifactClass[g.GenerateArtifact(-3, 9)!]);
    }

    [Fact]
    public void FirstPassPrefersArtifactsNotGeneratedYet()
    {
        var g = f.NewGame("A2M1");
        var relics = g.Db.ArtifactClass.Where(kv => kv.Value == "ARTF_CLASS_RELIC" && !g.Db.ArtifactsNotForSale.Contains(kv.Key))
            .Select(kv => kv.Key).Where(a => g.Map.ArtifactIds.Count == 0 || g.Map.ArtifactIds.Contains(a)).ToHashSet();
        Assert.NotEmpty(relics);
        g.S.ArtifactsGenerated.Clear();   // the map's random artifacts were drawn at the start
        var drawn = new HashSet<string>();
        for (int i = 0; i < relics.Count; i++) Assert.True(drawn.Add(g.GenerateArtifact(2, 2)!));   // no repeat until all are out
        Assert.True(drawn.SetEquals(relics));
        Assert.Contains(g.GenerateArtifact(2, 2)!, relics);   // second pass: any of them again
        Assert.Equal(relics.Count + 1, g.S.ArtifactsGenerated.Where(kv => relics.Contains(kv.Key)).Sum(kv => kv.Value));
    }

    [Fact]
    public void TheMapListIsThePool()
    {
        var g = f.NewGame("A2M1");
        var minor = g.Db.ArtifactClass.First(kv => kv.Value == "ARTF_CLASS_MINOR" && !g.Db.ArtifactsNotForSale.Contains(kv.Key)).Key;
        var major = g.Db.ArtifactClass.First(kv => kv.Value == "ARTF_CLASS_MAJOR" && !g.Db.ArtifactsNotForSale.Contains(kv.Key)).Key;
        var saved = g.Map.ArtifactIds.ToList();
        try
        {
            g.Map.ArtifactIds.Clear();
            g.Map.ArtifactIds.AddRange(new[] { "ARTIFACT_NONE", minor, major });
            var g2 = new Engine.Adventure(g.Db, g.Map, g.S);   // pools are built on the first draw
            for (int i = 0; i < 20; i++) Assert.Equal(minor, g2.GenerateArtifact(0, 0));
            Assert.Equal(major, g2.GenerateArtifact(1, 1));
            Assert.Null(g2.GenerateArtifact(2, 2));
        }
        finally { g.Map.ArtifactIds.Clear(); g.Map.ArtifactIds.AddRange(saved); }
    }
}
