using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;
using H5.Core.Numerics;

namespace H5.Adventure.Tests;

/// <summary>
/// Neutral stack sizes: the generator FUN_004ee750 (Respawn MonstersMin/Max/RandomUpgradeSub/DowngradeMult, table root
/// 0046ed30), the CountCoefs scaling when a monster enters the world (IAdvMapMonster vf0 004ef500 via 0041aa40) and the
/// weekly growth with its per-stack remainder (IAdvMapWeeklyUpdate vf0 004eec20).
/// </summary>
[Collection("adv")]
public class NeutralCountTests(Fixture f)
{
    static CreatureDef Def(int tier, int growth, string town = "TOWN_HEAVEN", string baseCreature = "") =>
        new() { Id = "T", Tier = tier, WeeklyGrowth = growth, Town = town, BaseCreature = baseCreature };

    static int Count(CreatureDef cd, uint u, int random = 0) =>
        Engine.Adventure.NeutralStackCount(cd, false, 0, 0, random, u, 2f, 3f, 0.3f, 2f);

    [Fact]
    public void TableRootMatchesTheFloatRootForTiers()
    {
        Assert.Equal(0f, Engine.Adventure.TableSqrt(0f));
        Assert.Equal(1f, Engine.Adventure.TableSqrt(1f));
        Assert.Equal(2f, Engine.Adventure.TableSqrt(4f));
        for (int t = 1; t <= 7; t++) Assert.Equal((float)Math.Sqrt(t), Engine.Adventure.TableSqrt(t));
    }

    [Fact]
    public void GeneratorScalesTheGrowthBySqrtTierAndTheDraw()
    {
        // 004ee750: t = (3 − 2)·u·2⁻³² + 2, n = (int)(√tier · t · growth).
        Assert.Equal(44, Count(Def(1, 22), 0));                    // t = 2
        Assert.Equal(55, Count(Def(1, 22), 0x80000000));           // t = 2.5
        Assert.Equal(15, Count(Def(4, 3), 0x80000000));            // √4 = 2 → 2 · 2.5 · 3
        // u = 2^32 − 1: the FADD of 2^32 is chopped to 24 bits, t just below 3 → 65, not 66.
        Assert.Equal(65, Count(Def(1, 22), 0xffffffff));
        // tier 7, growth 1: √7 · 2.5 = 6.61 → 6.
        Assert.Equal(6, Count(Def(7, 1), 0x80000000));
    }

    [Fact]
    public void RandomUpgradesAndNeutralsLoseTheUpgradeSubAndDowngradesDouble()
    {
        // random factor ≠ 0 and an upgrade (BaseCreature) or TOWN_NO_TYPE: (√tier · t − 0.3) · growth.
        var up = Def(1, 22, baseCreature: "CREATURE_PEASANT");
        Assert.Equal(55, Count(up, 0x80000000, random: 0));        // placed creature: no sub
        Assert.Equal(48, Count(up, 0x80000000, random: 1));        // (2.5 − 0.3) · 22 = 48.4
        Assert.Equal(48, Count(Def(1, 22, town: "TOWN_NO_TYPE"), 0x80000000, random: 1));
        Assert.Equal(55, Count(Def(1, 22), 0x80000000, random: 1)); // base creature of a town: no sub
        Assert.Equal(110, Count(Def(1, 22), 0x80000000, random: 2)); // downgraded: × MonsterDowngradeMult
    }

    [Fact]
    public void CustomAmountsUseTheMapRange()
    {
        var cd = Def(1, 22);
        Assert.Equal(10, Engine.Adventure.NeutralStackCount(cd, true, 10, 0, 0, 12345, 2, 3, 0.3f, 2));
        Assert.Equal(13, Engine.Adventure.NeutralStackCount(cd, true, 10, 20, 0, 25, 2, 3, 0.3f, 2));   // 10 + 25 % 11
        Assert.Equal(1, Engine.Adventure.NeutralStackCount(cd, true, 0, 0, 0, 0, 2, 3, 0.3f, 2));       // "Zero count monster"
    }

    static float Coef(Engine.Adventure g, string list) =>
        (float)XmlHelpers.ParseDouble(g.Db.Stats.Node($"adventure/NeutralsNotInCampaign/{list}")!.Elements("Item").ElementAt(g.S.Difficulty).Value, 1);

    [Theory]
    [InlineData("A2M13")]
    [InlineData("A1M4")]
    [InlineData("A2S4")]
    public void PlacedMonstersAreScaledByTheDifficultyCountCoef(string id)
    {
        var map = f.Map(id);
        var setup = GameSetup.AllPlayers(map, 0, 7);
        setup.Difficulty = 2;     // hard: NotInCampaign/CountCoefs item 2
        var g = Engine.Adventure.NewGame(f.Db, map, setup).Game;
        Assert.False(g.S.Campaign);
        float k = Coef(g, "CountCoefs");
        Assert.NotEqual(1f, k);
        int seen = 0;
        foreach (var o in g.Map.Objects.Where(o => o.Kind == ObjectKind.Monster && o.Creature != null && !o.ScriptCreated))
        {
            var cd = g.Core.Creatures[o.Creature!];
            int n = g.S.Obj(o.Index).Guards[0].Count;
            if (o.CustomAmount && o.Amount2 <= o.Amount)
            {
                int raw = Math.Max(1, o.Amount);
                Assert.Equal(o.DoesNotDependOnDifficulty ? raw : Math.Max(1, (int)X87.Mul(raw, k)), n);
                seen++;
            }
            else if (!o.CustomAmount && !o.DoesNotDependOnDifficulty)
            {
                // a placed creature: (int)((int)(sqrt(tier) * t * growth) * k) for some t in [2, 3)
                int lo = Math.Max(1, (int)X87.Mul(Count(cd, 0), k));
                int hi = Math.Max(1, (int)X87.Mul(Count(cd, 0xffffffff), k));
                Assert.InRange(n, lo, hi);
                seen++;
            }
        }
        Assert.True(seen > 0);
    }

    [Fact]
    public void WeeklyGrowthKeepsTheRemainder()
    {
        // 004eec20: v = (int)((rem + 100·count) · k), rem = v % 100, count += v / 100 − count.
        var os = new ObjState { Guards = { new Stack("CREATURE_PEASANT", 10), new Stack("CREATURE_WOLF", 3) } };
        // 1.13f = 1.1299999952…: 1000 · k = 1129.99… → 1129.
        Engine.Adventure.NeutralWeeklyGrowth(os, 1.13f);
        Assert.Equal(11, os.Guards[0].Count);
        Assert.Equal(29, os.GrowthRemainder[0]);
        Assert.Equal(3, os.Guards[1].Count);     // 338: no gain, the remainder keeps 38
        Assert.Equal(38, os.GrowthRemainder[1]);
        Engine.Adventure.NeutralWeeklyGrowth(os, 1.13f);
        Assert.Equal(12, os.Guards[0].Count);    // 1129 · k = 1275.76 → 1275
        Assert.Equal(75, os.GrowthRemainder[0]);
        Assert.Equal(3, os.Guards[1].Count);     // 338 · k = 381.93 → 381
        Assert.Equal(81, os.GrowthRemainder[1]);
        Engine.Adventure.NeutralWeeklyGrowth(os, 1.13f);
        Assert.Equal(4, os.Guards[1].Count);     // 381 · k = 430.52 → 430
        Assert.Equal(30, os.GrowthRemainder[1]);
    }
}
