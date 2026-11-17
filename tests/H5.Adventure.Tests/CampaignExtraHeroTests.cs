using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>
/// Carried extra heroes (HeroesPool Count) placed on the map's class-NONE heroes (EntryPoint): H5_Game.exe 0041ce20 →
/// 0041cc60 → 009aafa0; the record map's order (00461520 / 00460a50 / 00421a30 / 00c44170 / 00422770).
/// </summary>
[Collection("adv")]
public class CampaignExtraHeroTests(Fixture f)
{
    static CampaignHeroRecord R(string id, long exp = 0) => new() { ScriptName = id, Experience = exp };

    /// <summary>13 buckets, hash h·5 + c: "A" (65) and "N" (78) share bucket 0, "B" (66) is in bucket 1; a later key
    /// goes to the head of its bucket.</summary>
    [Fact]
    public void ExtrasAreReadInTheHashMapsOrder()
    {
        Assert.Equal(65u, CampaignHeroRecord.KeyHash("A"));
        Assert.Equal(78u * 1 + 0, CampaignHeroRecord.KeyHash("N"));
        Assert.Equal(66u * 5 + 65, CampaignHeroRecord.KeyHash("BA"));
        var order = CampaignHeroRecord.InMapOrder(new[] { R("B"), R("A"), R("N"), R("A") });
        Assert.Equal(new[] { "N", "A", "B" }, order.Select(r => r.ScriptName));
    }

    (Engine.Adventure G, Tile Pos, int Owner) Start(params CampaignHeroRecord[] extras)
    {
        var map = f.Loader.Load(MapCatalog.Find(f.Fs, f.Install, "A2C1M1")!);
        var o = map.OfKind(ObjectKind.Hero).First(x => x.Hero?.Shared != null && x.Owner >= 0);
        o.Hero!.Shared = new HeroDef { Id = "EntryPoint", Class = "HERO_CLASS_NONE", SourcePath = "MapObjects/Utility/EntryPoint.xdb" };
        var setup = GameSetup.AllPlayers(map, 1, 3);
        setup.Campaign = true;
        setup.CampaignExtras = extras.ToList();
        return (Engine.Adventure.NewGame(f.Db, map, setup).Game, o.Pos, o.Owner);
    }

    [Fact]
    public void PlaceholderTakesTheNextExtraOrMakesNoHero()
    {
        var id = f.Core.Heroes.Values.First(d => d.Class.Length > 0 && d.Class != "HERO_CLASS_NONE" && !d.Id.StartsWith("TEST")).Id;
        var exp = f.Core.Experience.For(8);
        var (g, pos, owner) = Start(R(id, exp));
        var h = g.S.Heroes.Single(x => x.Alive && x.Pos == pos && x.Owner == owner);
        Assert.Equal(id, h.DefId);
        Assert.Equal(exp, h.Experience);
        Assert.DoesNotContain(g.S.Heroes, x => x.DefId == "EntryPoint");

        var (g2, pos2, owner2) = Start();
        Assert.DoesNotContain(g2.S.Heroes, x => x.Alive && x.Pos == pos2 && x.Owner == owner2);
    }
}
