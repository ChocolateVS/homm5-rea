using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>
/// Creature pools at map start (00cd6460 → 00cd4a40 new-dwelling path; no weekly update before the first new day,
/// 00993bf0 is reached only from 009949d0) and the map dwellings' weekly update 009a71f0(calendar, full).
/// </summary>
[Collection("adv")]
public class MapDwellingGrowthTests(Fixture f)
{
    static void EndDay(Engine.Adventure g)
    {
        int day = g.S.Day;
        while (g.S.Day == day) Assert.True(g.Apply(new EndTurnCmd { Player = g.Current.Index }).Ok);
    }

    [Fact]
    public void MapStartTownPoolsHoldTheBaseWeeklyGrowth()
    {
        var g = f.NewGame();
        int checkedPools = 0;
        foreach (var t in g.S.Towns)
            for (int tier = 1; tier <= 7; tier++)
            {
                if (t.Level($"TB_DWELLING_{tier}") <= 0) continue;
                var creature = g.Db.Building(t.Type, $"TB_DWELLING_{tier}", 1)!.Creature!;
                Assert.Equal(g.Core.Creatures[creature].WeeklyGrowth, t.Available[tier - 1]);
                checkedPools++;
            }
        Assert.True(checkedPools > 0);
    }

    static List<(int Index, int[] Pools)> DwellingPools(Engine.Adventure g) =>
        g.S.Objects.Where(kv => g.Map.Objects[kv.Key].Kind == ObjectKind.Dwelling && !kv.Value.Removed)
            .Select(kv => (kv.Key, (int[])kv.Value.Available.Clone())).ToList();

    static int[] Expected(Engine.Adventure g, int index, int[] before, bool full)
    {
        var o = g.Map.Objects[index];
        int week = g.CurrentMoonWeekIndex;
        var want = (int[])before.Clone();
        for (int i = 0; i < want.Length && i < o.Shared.DwellingCreatures.Count; i++)
        {
            var c = o.Shared.DwellingCreatures[i];
            if (!g.Core.Creatures.TryGetValue(c, out var cd)) continue;
            want[i] = Engine.Adventure.MoonWeekPool(week, want[i]);
            int total = g.MoonWeekGrowth(week, cd.WeeklyGrowth, c);
            int n = full ? total : total - cd.WeeklyGrowth;
            if (n > 0) want[i] += n;
        }
        return want;
    }

    [Fact]
    public void NewWeekAddsTheDwellingRecordTotalAfterThePoolReduction()
    {
        var g = f.NewGame("A2M1", seed: 3);
        while (g.S.DayOfWeek != 7) EndDay(g);
        var before = DwellingPools(g);
        Assert.NotEmpty(before);
        EndDay(g);
        Assert.Equal(1, g.S.DayOfWeek);
        foreach (var (idx, pools) in before)
            Assert.Equal(Expected(g, idx, pools, full: true), g.S.Obj(idx).Available);
    }

    [Fact]
    public void BoughtWeekMidWeekAddsOnlyTheMoonPartToDwellings()
    {
        var g = f.NewGame("A2M1", seed: 3);
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        p.Purse = new Purse(new H5.Core.Data.Resources(0, 0, 0, 0, 0, 0, g.AstrologerCost.Gold));
        Assert.True(g.Apply(new ChangeMoonWeekCmd(h.Id, g.AstrologerCandidates[0]) { Player = p.Index }).Ok);
        var before = DwellingPools(g);
        Assert.NotEmpty(before);
        EndDay(g);
        Assert.Equal(2, g.S.DayOfWeek);
        foreach (var (idx, pools) in before)
            Assert.Equal(Expected(g, idx, pools, full: false), g.S.Obj(idx).Available);
    }
}
