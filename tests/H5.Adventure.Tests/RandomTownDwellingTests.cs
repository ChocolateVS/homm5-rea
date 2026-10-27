using H5.Adventure.Map;

namespace H5.Adventure.Tests;

/// <summary>Random towns and dwellings resolved at game start (H5_Game.exe 0041d360 → 0041b5e0 / 0041b450 / 0041b9f0).</summary>
[Collection("adv")]
public class RandomTownDwellingTests(Fixture f)
{
    static readonly string[] Races =
        { "TOWN_HEAVEN", "TOWN_PRESERVE", "TOWN_ACADEMY", "TOWN_DUNGEON", "TOWN_NECROMANCY", "TOWN_INFERNO", "TOWN_FORTRESS", "TOWN_STRONGHOLD" };

    [Fact]
    public void RandomTownsTakeTheirOwnersRaceAndDwellingsFollowTheirLinkedTown()
    {
        var g = f.NewGame("A1M4", seed: 11);
        var towns = g.Map.Objects.Where(o => o.Kind == ObjectKind.Town && o.PlacedTownType == "TOWN_RANDOM_TYPE").ToList();
        Assert.NotEmpty(towns);
        foreach (var o in towns)
        {
            var t = g.S.Towns.First(x => x.Object == o.Index);
            Assert.Contains(t.Type, Races);
            // 0041b450: a player's MainTown takes the player's race.
            if (o.Owner >= 0 && g.Map.Players[o.Owner].MainTownUid == o.Uid)
                Assert.Equal(g.S.Players.First(p => p.Index == o.Owner).Race, t.Type);
        }
        var dwellings = g.Map.Objects.Where(o => o.Kind == ObjectKind.Dwelling && o.PlacedShared?.DwellingRandomType is { } rt && rt.StartsWith("DWELLING_TYPE_RANDOM")).ToList();
        Assert.NotEmpty(dwellings);
        foreach (var o in dwellings)
        {
            Assert.NotEqual(o.PlacedSharedPath, o.SharedPath);
            Assert.Equal("DWELLING_TYPE_SPECIFIC", o.Shared.DwellingRandomType);
            // 0041b9f0: RND_TOWN follows the LinkToTown town; the level picks link 0 / 1 / 2, else 3.
            if (o.RndSource == 2 && o.LinkToTownUid is { } uid)
            {
                var town = g.Map.Objects.First(x => x.Uid == uid);
                var race = g.S.Towns.First(x => x.Object == town.Index).Type;
                var group = g.Db.DwellingGroup(race);
                int level = int.Parse(o.PlacedShared!.DwellingRandomType!["DWELLING_TYPE_RANDOM_".Length..^1]);
                Assert.Equal(group[Math.Min(level - 1, 3)], o.SharedPath, ignoreCase: true);
            }
        }
    }

    [Fact]
    public void TheResolutionIsReplayedFromTheStateAndRepeatsForTheSameSeed()
    {
        var a = f.NewGame("A1M4", seed: 5);
        var saved = a.S.RandomObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        var b = f.NewGame("A1M4", seed: 5);                // same cached map: resolved again from the placed shareds
        Assert.Equal(saved, b.S.RandomObjects);
        f.NewGame("A1M4", seed: 6);                         // another game rewrites the shared map objects …
        var again = new Engine.Adventure(a.Db, a.Map, a.S); // … an engine over the first state puts its own back
        foreach (var (index, value) in saved)
        {
            var o = again.Map.Objects[index];
            if (o.Kind == ObjectKind.Town) Assert.Equal(value, o.TownType);
            else Assert.StartsWith(o.SharedPath + "|", value);
        }
    }
}
