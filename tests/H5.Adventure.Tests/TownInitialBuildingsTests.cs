using H5.Adventure.Engine;
using H5.Adventure.Map;

namespace H5.Adventure.Tests;

/// <summary>
/// Map-start town buildings per H5_Game.exe town init 00cd6460: limit = min(MaxUpgrade, data maximum)
/// (BLD_UPG_NONE forbids), start level = min(InitialUpgrade, limit), town hall at least level 1.
/// </summary>
[Collection("adv")]
public class TownInitialBuildingsTests(Fixture f)
{
    [Fact]
    public void TownsStartWithTheirMapBuildings()
    {
        var map = f.Map("A2M1");
        var g = f.NewGame("A2M1");
        Assert.NotEmpty(g.S.Towns);
        foreach (var t in g.S.Towns)
        {
            foreach (var b in map.Objects[t.Object].Buildings)
            {
                int limit = Math.Min(b.Max, f.Db.MaxLevel(t.Type, b.Type));
                Assert.Equal(limit, t.MaxLevel[b.Type]);
                if (b.Type != "TB_TOWN_HALL") Assert.Equal(Math.Min(b.Initial, limit), t.Level(b.Type));
            }
            Assert.True(t.Level("TB_TOWN_HALL") >= 1);
        }
    }

    [Fact]
    public void MaxUpgradeNoneForbidsAndTheHallIsAtLeastLevelOne()
    {
        var map = f.Loader.Load(MapCatalog.Find(f.Fs, f.Install, "A2M1")!);
        var town = map.Objects.First(o => o.Kind == ObjectKind.Town);
        town.Buildings.Clear();
        town.Buildings.Add(new TownBuildingInit("TB_TOWN_HALL", 0, 4));
        town.Buildings.Add(new TownBuildingInit("TB_TAVERN", 1, 0));
        town.Buildings.Add(new TownBuildingInit("TB_FORT", 3, 2));
        var g = Engine.Adventure.NewGame(f.Db, map, GameSetup.AllPlayers(map, 0, 7)).Game;
        var t = g.S.Towns.First(x => x.Object == town.Index);
        Assert.Equal(1, t.Level("TB_TOWN_HALL"));
        Assert.Equal(0, t.Level("TB_TAVERN"));
        Assert.Equal(0, t.MaxLevel["TB_TAVERN"]);
        Assert.Equal(2, t.Level("TB_FORT"));
        Assert.Equal(2, t.MaxLevel["TB_FORT"]);
    }
}
