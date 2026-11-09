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
            if (g.S.Obj(index).DwellingOff is { } off && i < off.Length && off[i]) continue;   // not offered (009a6040)
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
    public void DwellingsOfferTheirLevelsCreaturesOrTheEnabledOnes()
    {
        // 009a6040: a creature level offers the creatures of that tier; otherwise creaturesEnabled (or a random draw
        // when it is empty / RandomCreatures); never an empty list.
        var g = f.NewGame("A1M4", seed: 11);
        int seen = 0;
        foreach (var o in g.Map.Objects.Where(x => x.Kind == ObjectKind.Dwelling && g.S.Objects.ContainsKey(x.Index)))
        {
            var os = g.S.Obj(o.Index);
            var off = os.DwellingOff!;
            Assert.Contains(false, off);
            for (int i = 0; i < off.Length; i++)
            {
                if (o.DwellingTier >= 1) Assert.Equal(g.Core.Creatures[o.Shared.DwellingCreatures[i]].Tier == o.DwellingTier, !off[i]);
                if (off[i]) Assert.Equal(0, os.Available[i]);
            }
            seen++;
        }
        Assert.True(seen > 0);
    }

    [Fact]
    public void DwellingHireRowsAreTheOfferedCreaturesInOrder()
    {
        // 006df9a0 / 006de820: one hire row per entry of the dwelling's list (009a6040 → 009a5fd0, offered creatures only),
        // in the shared's order; creatures left out get no row (006ddb60 hides every item, 006db6b0 shows the filled ones).
        bool sawGap = false;
        foreach (var map in new[] { "A1M4", "A2M1", "A2M7", "A2M11", "A2M13" })
        {
            var g = f.NewGame(map, seed: 11);
            foreach (var o in g.Map.Objects.Where(x => x.Kind == ObjectKind.Dwelling && g.S.Objects.ContainsKey(x.Index)))
            {
                var off = g.S.Obj(o.Index).DwellingOff!;
                var rows = g.DwellingRows(o.Index);
                Assert.Equal(Enumerable.Range(0, off.Length).Where(i => !off[i]).ToList(), rows);
                if (rows.Count < o.Shared.DwellingCreatures.Count) sawGap = true;
            }
        }
        Assert.True(sawGap);
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
