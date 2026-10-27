using H5.Adventure.Engine;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Custom-game starting bonus: CPlayer vtable 00e1a09c slot 0x1c = 004fd860, called per setup slot by 00c9fa80.</summary>
[Collection("adv")]
public class StartBonusTests(Fixture f)
{
    (Engine.Adventure G, PlayerState P) Start(int? bonus, ulong seed)
    {
        var map = f.Map("A2M1");
        var setup = GameSetup.AllPlayers(map, 1, seed);
        setup.Players[0].Bonus = bonus;
        var g = Engine.Adventure.NewGame(f.Db, map, setup).Game;
        return (g, g.S.Players.First(p => p.Index == setup.Players[0].MapPlayer));
    }

    /// <summary>004fd9c4: gold = (rng % 6) · 100 + 500.</summary>
    [Fact]
    public void GoldBonusIsFiveHundredToAThousandInHundreds()
    {
        var seen = new HashSet<int>();
        for (ulong seed = 1; seed <= 40; seed++)
        {
            int baseGold = Start(null, seed).P.Purse[Purse.Gold];
            int d = Start(2, seed).P.Purse[Purse.Gold] - baseGold;
            Assert.InRange(d, 500, 1000);
            Assert.Equal(0, d % 100);
            seen.Add(d);
        }
        Assert.True(seen.Count > 3);
    }

    /// <summary>004fd8b9 / jump table 004fdadc: (rng &amp; 3) + 3 of the race's resources, by player race +0x198 (3..10).</summary>
    [Theory]
    [InlineData("TOWN_HEAVEN", new[] { Purse.Wood, Purse.Ore })]
    [InlineData("TOWN_NECROMANCY", new[] { Purse.Wood, Purse.Ore })]
    [InlineData("TOWN_PRESERVE", new[] { Purse.Crystal })]
    [InlineData("TOWN_ACADEMY", new[] { Purse.Gem })]
    [InlineData("TOWN_FORTRESS", new[] { Purse.Gem })]
    [InlineData("TOWN_DUNGEON", new[] { Purse.Sulfur })]
    [InlineData("TOWN_INFERNO", new[] { Purse.Mercury })]
    [InlineData("TOWN_STRONGHOLD", new[] { Purse.Mercury })]
    public void ResourceBonusFollowsTheRace(string race, int[] res)
    {
        var g = f.NewGame("A2M1");
        var p = new PlayerState { Index = 77, Race = race };
        for (int i = 0; i < 20; i++)
        {
            var before = (int[])p.Purse.R.Clone();
            Assert.Equal(3, g.ApplyStartBonus(p, 3));
            for (int r = 0; r < 7; r++)
            {
                int d = p.Purse[r] - before[r];
                if (res.Contains(r)) Assert.InRange(d, 3, 6); else Assert.Equal(0, d);
            }
        }
    }

    /// <summary>004fd875: a random bonus is 1 + rng % 3 with heroes, 2 + rng % 2 without; no artifact without heroes.</summary>
    [Fact]
    public void RandomBonusSkipsTheArtifactWithoutHeroes()
    {
        var g = f.NewGame("A2M1");
        var none = new PlayerState { Index = 77, Race = "TOWN_HEAVEN" };
        var got = new HashSet<int>();
        for (int i = 0; i < 60; i++) got.Add(g.ApplyStartBonus(none, 0));
        Assert.Equal(new[] { 2, 3 }, got.OrderBy(x => x));
        Assert.Equal(1, g.ApplyStartBonus(none, 1));   // artifact chosen, nothing given
        Assert.Equal(0, g.S.Heroes.Count(h => h.Owner == 77));

        var owner = g.S.Heroes.First(h => h.Alive).Owner;
        var p = g.S.Players.First(x => x.Index == owner);
        got.Clear();
        for (int i = 0; i < 90; i++) got.Add(g.ApplyStartBonus(p, 0));
        Assert.Equal(new[] { 1, 2, 3 }, got.OrderBy(x => x));
    }

    /// <summary>004fd9fa: one of the player's heroes gets one minor artifact (generator classes 0..0).</summary>
    [Fact]
    public void ArtifactBonusGivesOneMinorArtifactToAHero()
    {
        var (g0, p0) = Start(null, 5);
        var (g, p) = Start(1, 5);
        int Count(Engine.Adventure x, int owner) => x.S.Heroes.Where(h => h.Owner == owner).Sum(h => h.Artifacts.Count);
        Assert.Equal(Count(g0, p0.Index) + 1, Count(g, p.Index));
        var extra = g.S.Heroes.Where(h => h.Owner == p.Index).SelectMany(h => h.Artifacts)
            .Except(g0.S.Heroes.Where(h => h.Owner == p0.Index).SelectMany(h => h.Artifacts)).Single();
        Assert.Equal("ARTF_CLASS_MINOR", f.Db.ArtifactClass[extra]);
    }
}
