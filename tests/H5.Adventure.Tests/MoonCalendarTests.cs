using H5.Adventure.Engine;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>
/// Moon calendar (CMoonCalendar, world +0x100): hashed random 008e7860/008e7840, random moons 005089a0, constructor
/// 005093d0, new day 009949d0 / 005091a0, candidates 00509010, Tower of Astrologer 0098cf10 / 0098c5a0 / 0098fae0 /
/// 00429bb0.
/// </summary>
[Collection("adv")]
public class MoonCalendarTests(Fixture f)
{
    static void EndDay(Engine.Adventure g)
    {
        int day = g.S.Day;
        while (g.S.Day == day) Assert.True(g.Apply(new EndTurnCmd { Player = g.Current.Index }).Ok);
    }

    [Fact]
    public void HashedRandomMatchesTheExeFormula()
    {
        // adler32(0x12345678) of the argument dwords + their byte count (00b62340), cross-checked with zlib.adler32.
        Assert.Equal(unchecked((int)0xc61a5681), ExeHashRandom.Hash(1, 0));
        Assert.Equal(0x206c568d, ExeHashRandom.Hash(7, 0, 2));
        Assert.Equal(unchecked((int)0x8b8757b3), ExeHashRandom.Hash(123456, 3, 1, 4));
        // 008e77e0: two MS LCG steps, ((s2 << 6) ^ (s1 >> 7)) % n + lo.
        Assert.Equal(30, ExeHashRandom.Range(0x206c568d, 0, 79));
        Assert.Equal(78, ExeHashRandom.Range(unchecked((int)0xc61a5681), 21, 119));
        Assert.Equal(5, ExeHashRandom.Range(1234, 5, 5));
    }

    [Fact]
    public void DefaultCalendarIsFiveByFiveByFive()
    {
        var data = f.Db.MoonCalendarData(MoonCalendar.DefaultPath);
        Assert.NotNull(data);
        Assert.Equal(125, data!.Length);
        Assert.Equal(1, f.Db.MoonWeekIndex("WEEK_OF_TOAD"));
        Assert.Equal("WEEK_OF_BONEDRAGON", f.Db.MoonWeekId(125));
        Assert.Equal("CREATURE_FIRE_DRAGON", f.Db.EnumName("CreatureType", MoonCalendar.CreatureOf(f.Db.MoonWeekIndex("WEEK_OF_SALAMANDER"))));
        Assert.True(MoonCalendar.IsNeutral(f.Db.MoonWeekIndex("WEEK_OF_CALM")));
        Assert.False(MoonCalendar.IsNeutral(f.Db.MoonWeekIndex("WEEK_OF_PLAGUE")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(-55555)]
    public void RandomMoonsAreCoprimeAndStartOnANeutralWeekBeforeANonCreatureWeek(int seed)
    {
        var data = f.Db.MoonCalendarData(MoonCalendar.DefaultPath)!;
        var m = MoonCalendar.RandomMoons(seed, data);
        Assert.All(m, x => Assert.InRange(x.Pos, 0, 79));
        Assert.All(m, x => Assert.InRange(x.Step, 21, 119));
        static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
        Assert.Equal(1, Gcd(m[0].Step, m[1].Step));
        Assert.Equal(1, Gcd(m[0].Step, m[2].Step));
        Assert.Equal(1, Gcd(m[1].Step, m[2].Step));
        var s = MoonCalendar.Create(m, MoonCalendar.DefaultPath, data, false, 0);
        Assert.True(MoonCalendar.IsNeutral(s.Week));
        Assert.Equal(0, MoonCalendar.CreatureOf(MoonCalendar.Forecast(s, data, 1)[0]));
    }

    [Fact]
    public void NewGameStartsOnTheCalendarWeekAndEachWeekAdvancesTheMoons()
    {
        var g = f.NewGame("A2M1", seed: 11);
        var moon = g.S.Moon!;
        Assert.Equal(g.Db.MoonWeekId(moon.Week), g.S.MoonWeek);
        Assert.Equal(3, moon.Candidates.Count);
        Assert.DoesNotContain(moon.Week, moon.Candidates);
        var next = g.MoonForecast(2);
        for (int d = 0; d < 7; d++) EndDay(g);
        Assert.Equal(8, g.S.Day);
        Assert.Equal(g.Db.MoonWeekId(next[0]), g.S.MoonWeek);
        for (int d = 0; d < 7; d++) EndDay(g);
        Assert.Equal(g.Db.MoonWeekId(next[1]), g.S.MoonWeek);
        // Save/load keeps the calendar (serializer 005099a0).
        var copy = g.S.Clone();
        Assert.Equal(g.S.Moon!.Pos, copy.Moon!.Pos);
        Assert.Equal(g.S.Moon.Candidates, copy.Moon.Candidates);
    }

    [Fact]
    public void BlockMonstersWeeksReadsCreatureWeeksAsNothing()
    {
        var data = f.Db.MoonCalendarData(MoonCalendar.DefaultPath)!;
        int salamander = f.Db.MoonWeekIndex("WEEK_OF_SALAMANDER");
        int at = Array.IndexOf(data, salamander);
        int a = at / 25, b = at / 5 % 5, c = at % 5;
        // Position p has phase p/10 for p < 50.
        var moons = new[] { (a * 10, 21), (b * 10, 21), (c * 10, 21) };
        Assert.Equal(salamander, MoonCalendar.Create(moons, "x", data, false, 0).Week);
        Assert.Equal(0, MoonCalendar.Create(moons, "x", data, true, 0).Week);
        // 005087c0 clamps map moons: position to 0..79, step to 21..119.
        var s = MoonCalendar.Create(new[] { (-5, 0), (99, 500), (40, 30) }, "x", data, false, 0);
        Assert.Equal(new[] { 0, 79, 40 }, s.Pos);
        Assert.Equal(new[] { 21, 119, 30 }, s.Step);
        Assert.Equal(new[] { 0, 1, 4 }, s.Phase);
    }

    /// <summary>
    /// 009949d0: a bought week taking effect on days 2..7 runs 00993bf0(0) → 00cd27b0(full = false) for every town:
    /// the week's pool reduction, then only the growth record's moon part (+0x1c), owner-adjusted, when positive.
    /// </summary>
    [Fact]
    public void BoughtWeekMidWeekAddsOnlyTheMoonPartToTownPools()
    {
        var g = f.NewGame("A2M1", seed: 3);
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        p.Purse = new Purse(new H5.Core.Data.Resources(0, 0, 0, 0, 0, 0, g.AstrologerCost.Gold));
        Assert.True(g.Apply(new ChangeMoonWeekCmd(h.Id, g.AstrologerCandidates[0]) { Player = p.Index }).Ok);
        var pools = g.S.Towns.Select(t => (int[])t.Available.Clone()).ToList();
        EndDay(g);
        Assert.Equal(2, g.S.DayOfWeek);
        int week = g.CurrentMoonWeekIndex;
        for (int k = 0; k < g.S.Towns.Count; k++)
        {
            var t = g.S.Towns[k];
            for (int tier = 1; tier <= 7; tier++)
            {
                int want = pools[k][tier - 1];
                if (t.Level($"TB_DWELLING_{tier}") > 0)
                {
                    want = Engine.Adventure.MoonWeekPool(week, want);
                    int n = g.OwnerAdjustedGrowth(t, g.GrowthOf(t, tier).MoonWeek);
                    if (n > 0) want += n;
                }
                Assert.Equal(want, t.Available[tier - 1]);
            }
        }
    }

    [Fact]
    public void AstrologerBuysACandidateThatTakesEffectNextDay()
    {
        var g = f.NewGame("A2M1", seed: 3);
        var p = g.Current;
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        var cost = g.AstrologerCost;
        Assert.True(cost.Gold > 0);
        int week = g.AstrologerCandidates[0];
        Assert.Equal(0, g.AstrologerStatus(week, p.Index));
        Assert.Equal(3, g.AstrologerStatus(g.CurrentMoonWeekIndex, p.Index));   // not a candidate
        p.Purse = new Purse(new H5.Core.Data.Resources(0, 0, 0, 0, 0, 0, cost.Gold - 1));
        Assert.False(g.Apply(new ChangeMoonWeekCmd(h.Id, week) { Player = p.Index }).Ok);
        p.Purse = new Purse(new H5.Core.Data.Resources(0, 0, 0, 0, 0, 0, cost.Gold));
        Assert.True(g.Apply(new ChangeMoonWeekCmd(h.Id, week) { Player = p.Index }).Ok);
        Assert.Equal(0, p.Purse.R[6]);
        Assert.DoesNotContain(week, g.AstrologerCandidates);
        Assert.Equal(new[] { week }, g.S.Moon!.Queue);
        Assert.Equal(2, g.AstrologerStatus(g.AstrologerCandidates[0], p.Index));   // once per week
        Assert.True(g.S.AstrologerChanged);

        // The next day the moons run to the bought week (when it comes up within 125 steps).
        var probe = g.S.Moon.Clone();
        MoonCalendar.Advance(probe, g.Db.MoonCalendarData(probe.Calendar), 0, false);
        EndDay(g);
        Assert.False(g.S.AstrologerChanged);
        Assert.Empty(g.S.Moon.Queue);
        Assert.Equal(probe.Week, g.S.Moon.Week);
        Assert.Equal(g.Db.MoonWeekId(g.S.Moon.Week), g.S.MoonWeek);
        Assert.Equal(2, g.S.WeekNoticeDay);
        if (probe.Week == week) Assert.Equal(g.Db.MoonWeekId(week), g.S.MoonWeek);

        // Day 7: the astrologer refuses (_ON_SUNDAY); a new week clears the buyers' list.
        while (g.S.DayOfWeek != 7) EndDay(g);
        Assert.Equal(2, g.AstrologerStatus(-1, p.Index));
        Assert.Equal(1, g.AstrologerStatus(-1, p.Index + 100));
        EndDay(g);
        Assert.Empty(g.S.AstrologerPlayers);
        Assert.Equal(1, g.S.WeekNoticeDay);
        Assert.Equal(3, g.S.Moon.Candidates.Count);
    }

    [Fact]
    public void HumanTurnsOnTheNoticeDayAnnounceTheWeek()
    {
        var map = f.Map("A2M1");
        var setup = GameSetup.AllPlayers(map, 1, 5);
        var (g, opening) = Engine.Adventure.NewGame(f.Db, map, setup);
        bool human = g.Current.Human;
        Assert.Equal(human, opening.OfType<WeekStarted>().Any(w => w.MoonWeek == g.S.MoonWeek));
        // Mid-week days announce nothing.
        int day = g.S.Day;
        var evs = new List<AdvEvent>();
        while (g.S.Day == day) evs.AddRange(g.Apply(new EndTurnCmd { Player = g.Current.Index }).Events);
        Assert.Empty(evs.OfType<WeekStarted>());
    }
}
