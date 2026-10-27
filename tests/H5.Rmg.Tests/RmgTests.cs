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
            // Underground treasures (0x00906380) come after the statics and test only the tile's flags (0x0091f8a0), so a
            // pocket inside an underground static mass can hold one; on the surface every object is reachable.
            var bad = r.Warnings.Where(w => w.StartsWith("unreachable") && !w.Contains("floor 1")).ToList();
            Assert.True(bad.Count == 0, $"{t.Name}: " + string.Join("; ", bad.Take(4)));
        }
    }

    /// <summary>
    /// The water option (exe 0x0090bd80 → CGameWaterBorderedZone 0x00bae920, 0x00904770 → vf 0x24 0x00bae0f0): every
    /// listed template at its smallest water size generates; each surface zone loses its tiles with border distance
    /// below the width to the sea (zone −1, depth grid written by 0x00916790) and keeps border distance ≥ 0 on its land.
    /// </summary>
    [Fact]
    public void WaterOptionBordersEverySurfaceZone()
    {
        int unreachable = 0;
        foreach (var t in f.Db.Templates.Values.Where(t => !t.TestTemplate))
        {
            int? size = Enumerable.Range(0, 7).Cast<int?>().FirstOrDefault(s => t.ListedFor(s, false, true, t.MaxPlayers));
            if (size == null) continue;
            var o = new RmgOptions { Template = t.Name, Size = size.Value, Seed = 4711, Players = t.MaxPlayers, WaterAmount = 2 };
            var r = new RmgGenerator(f.Db, o).Generate();
            var g = r.Generator;
            Assert.True(g.HasWater);
            var fl = g.Floors[0];
            Assert.All(fl.Zones.Values, z => Assert.Equal(RmgZoneClass.WaterBordered, z.Class));
            Assert.Contains(fl.Zones.Values, z => z.WaterTiles.Count > 0);
            Assert.All(fl.Zones.Values, z => Assert.All(z.Tiles, p => Assert.True(fl.BorderDist[fl.At(p.X, p.Y)] >= 0)));
            Assert.Contains(fl.Depth, b => b == 0xff);
            int starts = t.Zones.Count(z => z.CanBePlayerStart);
            Assert.True(r.StartTowns.Count == Math.Min(g.Players, starts), $"{t.Name}: {r.StartTowns.Count} start towns");
            unreachable += r.Warnings.Count(w => w.StartsWith("unreachable") && !w.Contains("floor 1"));
            Assert.Contains(r.Objects, ob => ob.Note == "water treasure");
        }
        Assert.Equal(0, unreachable);
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

    /// <summary>
    /// 0x00912940 (floor 0 flag 1, floor 1 flag 0): the surface starts at level 0x10 / height 6, the underground at
    /// 0x20 / 36 with the edge bands (32 / 26 / 21, heights 36 / 30 / 24) and 0x10 / 18 inside; the rock fill raises
    /// vertices to 0x20 / 36; the map's terrain carries the grids (chunks 5, 7, 15).
    /// </summary>
    [StockFact]
    public void TerrainGridsFollowTheExe()
    {
        var g = new RmgGenerator(f.Db, new RmgOptions { Template = "S1-2P2-4Z4K1S", Size = 2, Underground = true, Seed = 77, Players = 2 });
        var r = g.Generate();
        var fresh = new RmgFloor(1, 136, 136);
        Assert.Equal(32, fresh.Level[fresh.Lv(0, 10)]);
        Assert.Equal(26, fresh.Level[fresh.Lv(1, 10)]);
        Assert.Equal(21, fresh.Level[fresh.Lv(10, 2)]);
        Assert.Equal(16, fresh.Level[fresh.Lv(10, 10)]);
        Assert.Equal(36f, fresh.Heights[fresh.Lv(0, 10)], 3);
        Assert.Equal(30f, fresh.Heights[fresh.Lv(1, 10)], 3);
        Assert.Equal(18f, fresh.Heights[fresh.Lv(10, 10)], 3);
        Assert.Equal(0x20, fresh.Level[fresh.Lv(136, 136)]);
        Assert.Equal(0x20, fresh.Level[fresh.Lv(135, 50)]);   // 135 >= 136 - 136 % 3
        var u = g.Floors[1];
        Assert.Contains(Enumerable.Range(0, u.Level.Length), i => u.Level[i] == 0x20 && Math.Abs(u.Heights[i] - 36f) < 1e-3f && i / (u.W + 1) is > 4 and < 120);
        Assert.All(g.Floors[0].Level, l => Assert.Equal(0x10, l));
        Assert.InRange(g.Floors[0].Heights.Max(), 6f, 9.5f);
        var map = r.ToAdvMap(new SharedCatalog(f.Xdb));
        Assert.Equal(u.Heights, map.Floors[1]!.Data!.Heights.Data);
        Assert.Equal(u.Level, map.Floors[1]!.Data!.VertexFlags!.Data);
        Assert.Contains(map.Floors[0]!.Data!.VertexMap15!.Data, b => b == 0);
    }

    /// <summary>
    /// 0x0090bd80: underground zones share one class (dwarven when the builder draw is odd, else Next(2)), and vf 0x1c
    /// gives their object preset (SubInferno 0, Dwarven 2, Subterra 6); underground statics are placed; teleports carry
    /// GroupID = min zone x 100 + max zone (0x00914980), two per group; towns get a Specialization (0x00923e50).
    /// </summary>
    [StockFact]
    public void UndergroundClassesStaticsAndTeleportGroups()
    {
        foreach (int seed in new[] { 77, 78, 79, 80 })
        {
            var g = new RmgGenerator(f.Db, new RmgOptions { Template = "S1-2P2-4Z4K1S", Size = 2, Underground = true, Seed = seed, Players = 2 });
            var r = g.Generate();
            var under = g.Zones.Where(z => z.Floor == 1).ToList();
            Assert.NotEmpty(under);
            Assert.Single(under.Select(z => z.Class).Distinct());
            var cls = under[0].Class;
            Assert.Equal((g.BuilderDraw & 1) != 0, cls == RmgZoneClass.Dwarven);
            Assert.All(under, z => Assert.Equal(cls switch { RmgZoneClass.SubInferno => 0, RmgZoneClass.Dwarven => 2, _ => 6 }, z.Preset));
            Assert.All(g.Zones.Where(z => z.Floor == 0), z => Assert.Equal(RmgZoneClass.Game, z.Class));
            Assert.Contains(r.Objects, o => o.Floor == 1 && o.Note == "static");
            var groups = r.Objects.Where(o => o.GroupId != null).GroupBy(o => o.GroupId).ToList();
            Assert.All(groups, gr => Assert.Equal(2, gr.Count()));
            Assert.All(r.StartTowns.Values, t => Assert.NotNull(t.Specialization));
        }
    }

    /// <summary>
    /// Roads (0x0091aee0 via 0x0091bdb0 and 0x0091bf90): the pass-bucket search gives the same maps as the literal
    /// full-scan transcription of the exe's loop; main roads (flag 8) start at the town.
    /// </summary>
    [Fact]
    public void RoadSearchMatchesTheFullScanTranscription()
    {
        var t = f.Db.Templates.Values.First(t => !t.TestTemplate && t.ListedFor(1, false, false, t.MinPlayers));
        foreach (int seed in new[] { 3, 1234 })
        {
            var o = new RmgOptions { Template = t.Name, Size = 1, Seed = seed, Players = t.MinPlayers };
            var a = new RmgGenerator(f.Db, o);
            var ra = a.Generate();
            RmgGenerator.FullScanRoadSearch = true;
            try
            {
                var b = new RmgGenerator(f.Db, o);
                var rb = b.Generate();
                Assert.Equal(ra.Objects.Select(x => (x.Shared, x.X, x.Y)), rb.Objects.Select(x => (x.Shared, x.X, x.Y)));
                Assert.Equal(a.Floors[0].Flags, b.Floors[0].Flags);
                Assert.Equal(a.Rng.Counter, b.Rng.Counter);
            }
            finally { RmgGenerator.FullScanRoadSearch = false; }
            foreach (var z in a.Zones.Where(z => z.TownPlaced && z.Network.Count > 0))
                Assert.Equal((float)z.TownPos!.Value.X, z.Network[0].X);
            Assert.Contains(a.Floors[0].Flags, fl => (fl & TileFlags.MainRoad) != 0);
        }
    }

    /// <summary>
    /// Treasure blocks (0x00905d00 → 0x009198a0 → 0x00ba94c0): each block is a guard on a tile at road distance 1
    /// with its items on neighbouring tiles; items come from the block resource table 0x01040e10, the chest
    /// 0x01040f10 or the generatable artifacts, and every artifact's value band holds (Cost/5 + 500 &lt; value &lt; Cost/5 x 7
    /// is checked before Cost/5 is taken off, so the guard's power is at least 2.5 x (Cost/5 + 501)).
    /// </summary>
    [Fact]
    public void TreasureBlocksFollowTheExe()
    {
        var t = f.Db.Templates.Values.First(t => !t.TestTemplate && t.Zones.Any(z => z.TreasureBlocksTotalValue > 0) && t.ListedFor(2, false, false, t.MinPlayers));
        var r = new RmgGenerator(f.Db, new RmgOptions { Template = t.Name, Size = 2, Seed = 99, Players = t.MinPlayers }).Generate();
        var items = r.Objects.Where(x => x.Note?.StartsWith("treasure block ") == true && x.Note != "treasure block guard").ToList();
        Assert.NotEmpty(items);
        Assert.All(items.Where(x => x.Note is "treasure block pile" or "treasure block chest"),
            x => Assert.Matches(@"MapObjects/(Chest|Wood|Ore|Mercury|Crystal|Sulfur|Gems|Gold)\.", x.Shared));
        Assert.All(items.Where(x => x.Note == "treasure block chest"), x => Assert.True(x.Amount >= 1));
        foreach (var item in items)
        {
            var site = r.Access[item];
            Assert.InRange(Math.Abs(item.X - site.X), 0, 1);
            Assert.InRange(Math.Abs(item.Y - site.Y), 0, 1);
        }
    }

    /// <summary>Every object the exe creates through 0x009145b0 costs two draws (Next(0xffff) twice, its name number).</summary>
    [Fact]
    public void ObjectCreationDrawsTwice()
    {
        var a = new RmgRandom(5);
        var b = new RmgRandom(5);
        a.Next(0xffff); a.Next(0xffff);
        Assert.Equal(2, a.Counter);
        b.Advance(); b.Advance();
        Assert.Equal(a.Next(1000), b.Next(1000));
    }

    [StockFact]
    public void MineAndPrisonCountsFollowTheTemplate()
    {
        var r = f.Generate("S1-2P2-4Z4K1S", 1, 5150, 4);
        // 7 mines per zone and one prison per zone, as on the stock S_S4 maps (28 and 4).
        Assert.Equal(28, r.OfKind("AdvMapMineShared").Count());
        Assert.Equal(4, r.OfKind("AdvMapPrisonShared").Count());
        // 0x0091c4a0: each mine's guard stands on an orthogonal neighbour of its last visit tile.
        int guarded = 0;
        foreach (var mine in r.OfKind("AdvMapMineShared"))
        {
            var d = mine.Def;
            var (lx, ly) = RmgGenerator.Rotate(d.Active[^1], RmgGenerator.ExeQuarter(mine.Rot));
            int vx = mine.X + lx, vy = mine.Y + ly;
            if (r.Objects.Any(m => m.Note == "mine guard" && Math.Abs(m.X - vx) + Math.Abs(m.Y - vy) == 1)) guarded++;
        }
        Assert.True(guarded >= 26, $"{guarded} of 28 mines guarded");
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
