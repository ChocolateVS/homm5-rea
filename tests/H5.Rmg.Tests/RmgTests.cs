using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Core.Data;
using H5.Core.IO;
using H5.Core.Xdb;
using H5.Rmg;

namespace H5.Rmg.Tests;

public sealed class RmgFixture
{
    public readonly PakFileSystem Fs = PakFileSystem.OpenInstall(GameInstall.Locate());
    public readonly XdbLoader Xdb;
    public readonly RmgDatabase Db;
    readonly Lazy<GameDatabase> _core;
    readonly Lazy<AdventureDb> _adv;

    public RmgFixture()
    {
        Xdb = new XdbLoader(Fs);
        Db = RmgDatabase.Load(Xdb);
        _core = new(() => GameDatabase.Load(Fs));
        _adv = new(() => AdventureDb.Load(_core.Value, Fs));
    }

    public AdventureDb Adventure => _adv.Value;

    public RmgResult Generate(string template, int size, int seed, int players, params int[] races)
    {
        var o = new RmgOptions { Template = template, Size = size, Seed = seed, Players = players };
        o.PlayerRaces.AddRange(races);
        return new RmgGenerator(Db, o).Generate();
    }
}

[CollectionDefinition("rmg")]
public sealed class RmgCollection : ICollectionFixture<RmgFixture> { }

public class RandomTests
{
    [Fact]
    public void LcgMatchesExeFormula()
    {
        // s' = s·0x343FD + 0x269EC3 (mod 2^64); Next(n) = ((s >> 16) & (2^47-1)) % n.
        var r = new RmgRandom(12345);
        ulong s = 12345;
        for (int i = 0; i < 1000; i++)
        {
            s = unchecked(s * 0x343FDUL + 0x269EC3UL);
            Assert.Equal((int)(((s >> 16) & 0x7FFF_FFFF_FFFFUL) % 97), r.Next(97));
        }
        Assert.Equal(1000, r.Counter);
    }

    [Fact]
    public void NegativeSeedsAreSignExtended()
    {
        var a = new RmgRandom(-5);
        ulong s = unchecked((ulong)-5L);
        s = unchecked(s * 0x343FDUL + 0x269EC3UL);
        Assert.Equal((int)(((s >> 16) & 0x7FFF_FFFF_FFFFUL) % 1000), a.Next(1000));
    }

    [Fact]
    public void ZeroRangeDoesNotDraw()
    {
        var r = new RmgRandom(1);
        Assert.Equal(0, r.Next(0));
        Assert.Equal(0, r.Counter);
    }

    [Fact]
    public void ShuffleIsMsvcRandomShuffle()
    {
        var a = Enumerable.Range(0, 20).ToList();
        var r1 = new RmgRandom(9);
        r1.Shuffle(a);
        var b = Enumerable.Range(0, 20).ToList();
        var r2 = new RmgRandom(9);
        for (int i = 1; i < b.Count; i++) { int j = r2.Next(i + 1); (b[i], b[j]) = (b[j], b[i]); }
        Assert.Equal(b, a);
        Assert.Equal(19, r1.Counter);
    }
}

public class HashMapTests
{
    [Fact]
    public void IteratesByBucketThenNewestFirst()
    {
        var m = new NivalHashMap<string>();     // 13 buckets
        m.Add(1, "a"); m.Add(14, "b"); m.Add(2, "c"); m.Add(-1, "d");
        // -1 → 0xFFFFFFFF % 13 = 3; bucket 1 holds 14 then 1 (head insertion).
        Assert.Equal(new[] { 14, 1, 2, -1 }, m.Items().Select(i => i.Key).ToArray());
    }

    [Fact]
    public void GrowsToNextPrimeWhenFull()
    {
        var m = new NivalHashMap<int>();
        for (int i = 0; i < 14; i++) m.Add(i, i);   // the 14th insert rehashes to 29 buckets
        Assert.Equal(Enumerable.Range(0, 14), m.Items().Select(i => i.Key));
        Assert.Equal(14, m.Count);
    }
}

[Collection("rmg")]
public class GeneratorTests(RmgFixture f)
{
    [Fact]
    public void LoadsStockData()
    {
        Assert.True(f.Db.Templates.Count >= 20);
        Assert.Equal(12, f.Db.Races.Count);
        Assert.Equal(1000, f.Db.Params.BasicLevelGuardPower);
        Assert.Equal(7, f.Db.Race(3).Mines.Count);
        Assert.Contains(f.Db.Creatures, c => c.Id == "CREATURE_FOOTMAN" && c.Power > 0);
    }

    [StockTheory]
    // Player races of the user's RMG maps (Maps/*_RMG_Map_*.h5m): player 1 was fixed on the setup
    // screen (Dungeon/Preserve), the others were drawn by the generator. Zone creation reproduces them.
    [InlineData("S1-2P2-4Z4K1S", 1, 1736315553, 4, 6, new[] { 6, 8, 3, 10 })]   // S_S4_RMG_Map_009
    [InlineData("S1-2P2-4Z4K1S", 1, 1736402108, 4, 6, new[] { 6, 3, 5, 4 })]    // S_S4_RMG_Map_010
    [InlineData("S0-1P2Z2K3T", 0, 1658912490, 2, 4, new[] { 4, 9 })]            // T_I2_RMG_Map_002
    public void ReproducesReferenceRaces(string template, int size, int seed, int players, int firstRace, int[] expected)
    {
        var r = f.Generate(template, size, seed, players, firstRace);
        var races = Enumerable.Range(1, players).Select(p => r.Generator.Zones.First(z => z.Player == p).Race).ToArray();
        Assert.Equal(expected, races);
    }

    [StockFact]
    public void SameSeedSameMap()
    {
        var a = f.Generate("S1-2P2-4Z4K1S", 1, 4242, 4);
        var b = f.Generate("S1-2P2-4Z4K1S", 1, 4242, 4);
        Assert.Equal(a.Objects.Select(o => (o.Shared, o.X, o.Y, o.Quarter, o.Amount)), b.Objects.Select(o => (o.Shared, o.X, o.Y, o.Quarter, o.Amount)));
        Assert.Equal(a.Generator.Rng.Counter, b.Generator.Rng.Counter);
    }

    [StockFact]
    public void ZonesCoverTheMapAndAreSeparated()
    {
        var r = f.Generate("S1-2P2-4Z4K1S", 1, 99, 4);
        var g = r.Generator;
        var fl = g.Floors[0];
        int assigned = fl.Zone.Count(z => z != -1);
        Assert.True(assigned > g.W * g.H * 9 / 10, $"only {assigned} of {g.W * g.H} tiles in zones");
        Assert.All(g.Zones, z => Assert.InRange(z.Tiles.Count, g.W * g.H / 8, g.W * g.H / 2));
        Assert.Equal(0, r.Unreachable);
        Assert.Equal(4, r.StartTowns.Count);
    }

    /// <summary>
    /// Every template at the smallest size the RMG screen lists it for (exe 0x0090c880), with its most players:
    /// one start town per player while start zones last (exe 0x0090bd80 hands player records to start zones only).
    /// </summary>
    [Fact]
    public void EveryTemplateGeneratesAtItsSmallestListedSize()
    {
        foreach (var t in f.Db.Templates.Values.Where(t => !t.TestTemplate))
        {
            var (size, ug) = Enumerable.Range(0, 7).SelectMany(s => new[] { (s, false), (s, true) })
                .First(c => t.ListedFor(c.Item1, c.Item2, false, t.MaxPlayers));
            var o = new RmgOptions { Template = t.Name, Size = size, Underground = ug, Seed = 31337, Players = t.MaxPlayers };
            var r = new RmgGenerator(f.Db, o).Generate();
            int starts = t.Zones.Count(z => z.CanBePlayerStart);
            Assert.True(r.StartTowns.Count == Math.Min(r.Generator.Players, starts), $"{t.Name}: {r.StartTowns.Count} start towns");
            Assert.Equal(0, r.Unreachable);
        }
    }

    [Fact]
    public void ScreenListsTemplatesLikeTheExe()
    {
        var t = new RmgTemplate { Name = "t", MinMapSize = 30, MaxMapSize = 35, MinPlayers = 2, MaxPlayers = 4 };
        Assert.True(t.ListedFor(3, false, false, 4));     // 31 units
        Assert.False(t.ListedFor(2, false, false, 4));    // 18
        Assert.False(t.ListedFor(2, true, false, 2));     // underground doubles: 2 x 18 = 36 > 35
        Assert.True(new RmgTemplate { MinMapSize = 30, MaxMapSize = 40 }.ListedFor(2, true, false, null));
        Assert.False(t.ListedFor(3, false, false, 5));    // players above the maximum
        Assert.True(t.ListedFor(null, null, false, null));
        Assert.True(t.ListedFor(4, false, true, 2));      // water: round(47 x 0.7) = 33
        t.TestTemplate = true;
        Assert.False(t.ListedFor(3, false, false, 4));
    }

    /// <summary>
    /// MMH55's 4P-Diamond-XL-# allows 8 players but has 4 start zones: the exe gives start zones to the first 4
    /// player records (0x0090bd80) and still writes 8 active players (0x009058e0: ActivePlayer for i &lt; Players).
    /// </summary>
    [Fact]
    public void MorePlayersThanStartZonesKeepsTheExtraPlayersActive()
    {
        var t = f.Db.Templates.Values.FirstOrDefault(t => t.Zones.Count(z => z.CanBePlayerStart) < t.MaxPlayers);
        if (t == null) return; // stock data has no such template
        var (size, ug) = Enumerable.Range(0, 7).SelectMany(s => new[] { (s, false), (s, true) })
            .First(c => t.ListedFor(c.Item1, c.Item2, false, t.MaxPlayers));
        var r = new RmgGenerator(f.Db, new RmgOptions { Template = t.Name, Size = size, Underground = ug, Seed = 7, Players = t.MaxPlayers }).Generate();
        Assert.Equal(t.Zones.Count(z => z.CanBePlayerStart), r.StartTowns.Count);
        var map = r.ToAdvMap(new SharedCatalog(f.Xdb));
        Assert.Equal(t.MaxPlayers, map.Players.Count(p => p.Active));
    }

    /// <summary>
    /// Object rules traced in H5_Game.exe (docs/rules/AUDIT_RMG.md): guards use two-stack army templates part of the
    /// time (0x00ba7690), shrines are Shrines of Magic (0x00920000), BuffPoints place nothing (0x00918b30), loose
    /// treasures come from the 9-entry table (0x0091f8a0), each zone gets tiles/4000 + 1 Redwood Observatories.
    /// </summary>
    [StockFact]
    public void ObjectRulesFollowTheExe()
    {
        var o = new RmgOptions { Template = "S1-2P2-4Z4K1S", Size = 2, Seed = 1736932596, Players = 4 };
        var g = new RmgGenerator(f.Db, o);
        var r = g.Generate();
        var guards = r.Objects.Where(x => x.Def.Tag == "AdvMapMonsterShared").ToList();
        Assert.Contains(guards, x => x.AdditionalStacks.Count > 0);
        Assert.All(guards, x => Assert.True(x.Amount >= 0));
        Assert.All(r.Objects.Where(x => x.Def.Tag == "AdvMapShrineShared"), x => Assert.Contains("Shrine_Of_Magic_", x.Shared));
        var buffs = g.Zones.Select(z => f.Db.Race(z.Race)).SelectMany(p => p.Buffs).Select(b => b.Shared).ToHashSet();
        Assert.DoesNotContain(r.Objects, x => buffs.Contains(x.Shared) && x.Note == "buff building");
        Assert.All(r.Objects.Where(x => x.Note is "treasure" or "chest"), x => Assert.Matches(@"MapObjects/(Campfire|Chest|Crystal|Gems|Gold|Mercury|Ore|Sulfur|Wood)\.", x.Shared));
        int observatories = r.Objects.Count(x => x.Note == "redwood observatory");
        Assert.Equal(g.Zones.Sum(z => z.Tiles.Count / 4000 + 1), observatories);
    }

    /// <summary>
    /// Underground zones get their loose treasures and chests too, after the statics: 0x00907150 calls 0x00905e60
    /// only for zones with floor +0xf4 == 0, and 0x00906380 ("additional objects set") calls it for the others.
    /// </summary>
    [StockFact]
    public void UndergroundZonesGetTreasuresAfterTheStatics()
    {
        var o = new RmgOptions { Template = "S1-2P2-4Z4K1S", Size = 2, Underground = true, Seed = 77, Players = 2 };
        var g = new RmgGenerator(f.Db, o);
        var r = g.Generate();
        var under = g.Zones.Where(z => z.Floor == 1 && z.Def.TreasureDensity > 0).ToList();
        Assert.NotEmpty(under);
        Assert.Contains(r.Objects, x => x.Floor == 1 && x.Note == "treasure");
        // The underground treasures come after the statics in the object list.
        int lastStatic = r.Objects.FindLastIndex(x => x.Note == "static");
        Assert.All(r.Objects.Where(x => x.Floor == 1 && x.Note is "treasure" or "chest"), x => Assert.True(r.Objects.IndexOf(x) > lastStatic));
    }

    [StockFact]
    public void MineAndPrisonCountsFollowTheTemplate()
    {
        var r = f.Generate("S1-2P2-4Z4K1S", 1, 5150, 4);
        // 7 mines per zone and one prison per zone, as on the stock S_S4 maps (28 and 4).
        Assert.Equal(28, r.OfKind("AdvMapMineShared").Count());
        Assert.Equal(4, r.OfKind("AdvMapPrisonShared").Count());
        // Every mine is guarded: a monster stands on its visit tile.
        foreach (var mine in r.OfKind("AdvMapMineShared"))
        {
            var acc = r.Access[mine];
            Assert.Contains(r.OfKind("AdvMapMonsterShared"), m => m.X == acc.X && m.Y == acc.Y);
        }
    }

    [StockFact]
    public void GraalOptionBuriesATearOfAshaAndPlacesObelisks()
    {
        var o = new RmgOptions { Template = "S1-2P2-4Z4K1S", Size = 1, Seed = 5150, Players = 4, Grail = true };
        var r = new RmgGenerator(f.Db, o).Generate();
        var graal = r.Objects.Where(x => x.Shared == f.Db.Params.Grail).ToList();
        Assert.Single(graal);
        // (zone tiles · 18) / (W · H) + 2 per zone at size index 1.
        int expected = r.Generator.Zones.Sum(z => z.Tiles.Count * 18 / (r.Generator.W * r.Generator.H) + 2);
        Assert.True(r.Objects.Count(x => x.Shared == f.Db.Params.Obelisk) <= expected);
        var map = r.ToAdvMap(new SharedCatalog(f.Xdb));
        var (game, _) = Adventure.Engine.Adventure.NewGame(f.Adventure, map, GameSetup.AllPlayers(map, 0, 1));
        Assert.NotNull(game.S.Grail);
        // Without the option no Graal is generated.
        var plain = f.Generate("S1-2P2-4Z4K1S", 1, 5150, 4);
        Assert.DoesNotContain(plain.Objects, x => x.Shared == f.Db.Params.Grail);
    }

    [StockFact]
    public void GeneratedMapPlaysInTheAdventureEngine()
    {
        var r = f.Generate("S0-1P2Z2K3T", 0, 2024, 2);
        var map = r.ToAdvMap(new SharedCatalog(f.Xdb));
        Assert.Equal(2, map.Players.Count(p => p.Active));
        Assert.All(map.Players.Where(p => p.Active), p => Assert.NotNull(p.MainTownUid));
        var (game, _) = Adventure.Engine.Adventure.NewGame(f.Adventure, map, GameSetup.AllPlayers(map, 0, 1));
        var bot = new SimpleAdventureBot();
        for (int i = 0; i < 6 && !game.S.Finished; i++) bot.PlayTurn(game, (ulong)i);
        Assert.True(game.S.Day >= 2);
        Assert.Equal(2, game.S.Players.Count);
    }
}
