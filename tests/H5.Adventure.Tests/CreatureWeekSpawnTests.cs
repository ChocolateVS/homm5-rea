using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Assets.Terrain;
using H5.Core.Numerics;

namespace H5.Adventure.Tests;

/// <summary>The creature-week neutral spawn of H5_Game.exe 009919a0 (see <see cref="CreatureWeekSpawn"/>).</summary>
[Collection("adv")]
public class CreatureWeekSpawnTests(Fixture f)
{
    /// <summary>A flat n×n grass floor at level 0x10, its kinds built by 00990800 (border 1).</summary>
    static MapFloor Flat(int n = 16, int border = 1)
    {
        var t = new TerrainData
        {
            TilesX = n, TilesY = n,
            Heights = new Grid<float>(n + 1, n + 1, new float[(n + 1) * (n + 1)]),
            VertexFlags = new Grid<byte>(n + 1, n + 1, Enumerable.Repeat((byte)0x10, (n + 1) * (n + 1)).ToArray()),
            VertexMap15 = new Grid<byte>(n + 1, n + 1, Enumerable.Repeat((byte)1, (n + 1) * (n + 1)).ToArray()),
        };
        t.Layers.Add(new TerrainLayer { TilePath = "grass", Weights = new Grid<byte>(n + 1, n + 1, new byte[(n + 1) * (n + 1)]) });
        var m = new MapFloor
        {
            Width = n, Height = n, Data = t, LayerTypes = new[] { 1 },
            Terrain = new TerrainClass[n * n], TileType = new string[n * n], Blocked = new bool[n * n],
        };
        TerrainPassability.Build(m, border, m.LayerTypes, new HashSet<(int, int)>());
        return m;
    }

    static readonly (int, int)[] None = Array.Empty<(int, int)>();

    static CreatureWeekSpawn.FloorObjects Objs((int, int)[]? tiles = null, (int, int)[]? monsters = null,
        (int, int)[]? heroes = null, (int, int)[]? towns = null) => new(tiles ?? None, monsters ?? None, heroes ?? None, towns ?? None);

    [Fact]
    public void TheDrawIsTheChoppedFraction()
    {
        // 00991ad9: FILD u (+2³² when negative), × 2⁻³² — under the chop 0xffffffff gives the float just below 1.
        Assert.Equal(0f, CreatureWeekSpawn.Draw(0));
        Assert.Equal(MathF.BitDecrement(1f), CreatureWeekSpawn.Draw(0xffffffff));
        Assert.Equal(0.5f, CreatureWeekSpawn.Draw(0x80000000));
        // 0x80000001 / 2³² keeps 32 bits; the chop drops the low ones.
        Assert.Equal(0.5f, CreatureWeekSpawn.Draw(0x80000001));
    }

    [Fact]
    public void TheMaskRefusesTheEdgeObjectsGuardsHeroesAndTowns()
    {
        var m = Flat();
        var mask = CreatureWeekSpawn.BuildMask(m, 1, Objs());
        // 00cec080: the one-cell map edge; every inner land cell is free.
        Assert.True(mask[0, 5]); Assert.True(mask[15, 5]); Assert.True(mask[5, 0]); Assert.True(mask[5, 15]);
        Assert.False(mask[1, 1]); Assert.False(mask[14, 14]);
        mask = CreatureWeekSpawn.BuildMask(m, 1, Objs(tiles: new[] { (3, 3) }, monsters: new[] { (8, 8) },
            heroes: new[] { (5, 12) }, towns: new[] { (12, 4) }));
        Assert.True(mask[3, 3]); Assert.False(mask[4, 3]);                         // object tile: bitmap +0x10 / record +0x10
        Assert.True(mask[7, 7]); Assert.True(mask[9, 9]); Assert.False(mask[10, 8]); // 00c27d20: radius 1 for a neutral stack
        Assert.True(mask[3, 10]); Assert.True(mask[7, 14]); Assert.False(mask[8, 12]); // 5×5 around a hero
        Assert.True(mask[10, 2]); Assert.True(mask[14, 6]); Assert.False(mask[12, 7]); // 5×5 around a town entrance
    }

    [Fact]
    public void ABorderAboveOneIsRefusedAndWaterCoverReachesTheNeighbours()
    {
        var m = Flat(border: 3);
        var mask = CreatureWeekSpawn.BuildMask(m, 3, Objs());
        Assert.True(mask[2, 8]); Assert.True(mask[13, 8]); Assert.False(mask[3, 8]); Assert.False(mask[12, 8]);
        // A TT_SMALL_WATER layer covering tile (8, 8) (00c0f100 → +0xc): the tile and its 8 neighbours are refused.
        m = Flat();
        var w = new byte[17 * 17];
        foreach (var (x, y) in new[] { (8, 8), (9, 8), (8, 9), (9, 9) }) w[y * 17 + x] = 0xff;
        m.Data!.Layers.Add(new TerrainLayer { TilePath = "water", Weights = new Grid<byte>(17, 17, w) });
        m.LayerTypes = new[] { 1, TerrainPassability.TypeSmallWater };
        Assert.True(TerrainPassability.WaterCovered(m.Data, m.LayerTypes, 8, 8));
        mask = CreatureWeekSpawn.BuildMask(m, 1, Objs());
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++) Assert.True(mask[8 + dx, 8 + dy]);
        Assert.False(mask[11, 8]); Assert.False(mask[5, 5]);
    }

    [Fact]
    public void TheLoopGoesXOuterAndMarksTheSquareAroundEachSpawn()
    {
        // 009919a0: x outer, y inner; a set cell draws nothing; each spawn sets its 3×3.
        var mask = CreatureWeekSpawn.BuildMask(Flat(8), 1, Objs());
        int draws = 0;
        var spawns = CreatureWeekSpawn.Run(mask, 0, 0.5f, () => { draws++; return 0; });
        Assert.Equal(new[] { (1, 1), (1, 3), (1, 5), (3, 1), (3, 3), (3, 5), (5, 1), (5, 3), (5, 5) },
            spawns.Select(s => (s.X, s.Y)).ToArray());
        Assert.Equal(9, draws);
        // A draw at or above the probability spawns nothing: every free cell draws once.
        mask = CreatureWeekSpawn.BuildMask(Flat(8), 1, Objs());
        draws = 0;
        Assert.Empty(CreatureWeekSpawn.Run(mask, 0, 0.5f, () => { draws++; return 0x80000000; }));
        Assert.Equal(36, draws);
    }

    [Fact]
    public void TheFacingTurnsAwayFromBlockedCellsElseHashesTheTile()
    {
        // Nothing set in the 7×7: fmod(floor·7.5 + y·6.5 + x·3.5, 1.5707963705) (00991ca2).
        var mask = new CreatureWeekSpawn.Mask(32, 32);
        float s = X87.Add(X87.Add(X87.Mul(1, 7.5f), X87.Mul(10f, 6.5f)), X87.Mul(12f, 3.5f));   // 7.5 + 65 + 42 = 114.5
        Assert.Equal(X87.Chop(114.5 % 1.5707963705062866), CreatureWeekSpawn.Facing(mask, 1, 12, 10));
        Assert.Equal(1.4026613f, CreatureWeekSpawn.Facing(mask, 1, 12, 10), 5);
        // One set cell at (−1, 0): the sum is (+1, 0) → 004b45e0(0, 2) = (π/2 − asin 1) + π/2.
        mask.Set(11, 10);
        Assert.Equal(1.5707964f, CreatureWeekSpawn.Facing(mask, 0, 12, 10));
        // At (0, −1): sum (0, +1) → (π/2 − asin 0) + π/2 = π.
        mask = new CreatureWeekSpawn.Mask(32, 32);
        mask.Set(12, 9);
        Assert.Equal(3.1415927f, CreatureWeekSpawn.Facing(mask, 0, 12, 10));
        // At (0, +1): sum (0, −1), the sine is negative → 2π − π/2, + π/2.
        mask = new CreatureWeekSpawn.Mask(32, 32);
        mask.Set(12, 11);
        Assert.Equal(X87.Add(X87.Sub(6.2831855f, 1.5707964f), 1.5707964f), CreatureWeekSpawn.Facing(mask, 0, 12, 10));
    }

    [Fact]
    public void ACreatureWeekSpawnsScaledMonstersThatSurviveASaveAndLoad()
    {
        // A fresh copy of the map: the spawn adds map objects.
        var map = f.Loader.Load(MapCatalog.Find(f.Fs, f.Install, "A2M1")!);
        var g = Engine.Adventure.NewGame(f.Db, map, GameSetup.AllPlayers(map, 0, 7)).Game;
        int before = map.Objects.Count;
        g.S.MoonWeek = g.Db.MoonWeekId(0x54);                         // week 0x54 → creature type 1 (00bcd640)
        var creature = g.Db.EnumName("CreatureType", 1)!;
        var node = g.Db.Stats.Node("adventure/Respawn/MonsterProbabilityPerCell")!;
        string old = node.Value;
        try
        {
            node.Value = "0.01";
            g.CreatureWeekSpawns();
        }
        finally { node.Value = old; }
        Assert.NotEmpty(g.S.SpawnedMonsters);
        Assert.Equal(before + g.S.SpawnedMonsters.Count, map.Objects.Count);
        var cd = g.Core.Creatures[creature];
        foreach (var rec in g.S.SpawnedMonsters)
        {
            var o = map.Objects[rec.Index];
            Assert.Equal(ObjectKind.Monster, o.Kind);
            Assert.Equal(creature, o.Creature);
            var st = g.S.Obj(rec.Index);
            int n = (int)X87.Mul(2 * cd.WeeklyGrowth, CountCoef(g));
            Assert.Equal(Math.Max(1, n), st.Guards.Single().Count);
            // No spawn next to a hero or another guard.
            Assert.DoesNotContain(g.S.Heroes, h => h.Alive && h.Pos.Floor == rec.Floor && Math.Abs(h.Pos.X - rec.X) <= 2 && Math.Abs(h.Pos.Y - rec.Y) <= 2);
        }
        // Reload: a fresh map gets the spawned objects back at the same indexes.
        var map2 = f.Loader.Load(MapCatalog.Find(f.Fs, f.Install, "A2M1")!);
        var g2 = new Engine.Adventure(f.Db, map2, H5.Adventure.State.AdvState.Deserialize(g.S.Serialize()));
        Assert.Equal(map.Objects.Count, map2.Objects.Count);
        Assert.Equal(map.Objects[^1].Pos, map2.Objects[^1].Pos);
    }

    static float CountCoef(Engine.Adventure g)
    {
        var coefs = g.Db.Stats.Node((g.S.Campaign ? "adventure/NeutralsInCampaign/" : "adventure/NeutralsNotInCampaign/") + "CountCoefs")?.Elements("Item").ToList();
        return coefs != null && g.S.Difficulty < coefs.Count ? (float)H5.Core.Data.XmlHelpers.ParseDouble(coefs[g.S.Difficulty].Value, 1) : 1f;
    }
}
