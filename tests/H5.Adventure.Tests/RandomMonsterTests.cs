using H5.Adventure.Map;

namespace H5.Adventure.Tests;

/// <summary>Random monsters: map loader 0041d360 → 0041ca20 over the RPGRoot groups MONSTER_1..7 / MONSTER_ANY.</summary>
[Collection("adv")]
public class RandomMonsterTests(Fixture f)
{
    [Fact]
    public void GroupsHoldTheSharedObjectsCreaturesThatAreSubjectOfRandomGeneration()
    {
        var g = f.NewGame("A2M1");
        var l3 = g.Db.MonsterGroups["MONSTER_3"];
        // MapObjects/_(AdvMapSharedGroup)/Monsters/Level_3.xdb: 24 shared monsters, all tier 3.
        Assert.Equal(24, l3.Count);
        Assert.All(l3, c => Assert.Equal(3, g.Core.Creatures[c].Tier));
        Assert.Contains("CREATURE_OBSIDIAN_GOLEM", l3);
        Assert.Contains("MONSTER_ANY", g.Db.MonsterGroups.Keys);
    }

    [Fact]
    public void RandomMonstersComeFromTheirLevelGroupOrTheGroupBelow()
    {
        var g = f.NewGame("A2M1");
        foreach (var o in g.Map.Objects.Where(o => o.Kind == ObjectKind.Monster && o.MonsterRandomLevel is >= 1 and <= 7))
        {
            var c = g.S.Obj(o.Index).Creature!;
            var own = g.Db.MonsterGroups[$"MONSTER_{o.MonsterRandomLevel}"];
            var below = o.MonsterRandomLevel > 1 ? g.Db.MonsterGroups[$"MONSTER_{o.MonsterRandomLevel - 1}"] : new List<string>();
            Assert.True(own.Contains(c) || below.Contains(c), $"{o.Index}: {c} not in MONSTER_{o.MonsterRandomLevel}");
        }
    }

    [Fact]
    public void MapModelAndIconFollowTheRolledCreature()
    {
        // A2M1 #214 (Random-Monster-L3) rolled CREATURE_OBSIDIAN_GOLEM (TotE "Magnetic Golem"): its MonsterShared is
        // MapObjects/Academy/Obsidian_Golem (model T3_Obsidian_Golem) and its visual's Icon128 is Obsidian_Golem —
        // the golden, blade-armed golem, not another creature's files.
        var g = f.NewGame("A2M1");
        var cd = g.Core.Creatures["CREATURE_OBSIDIAN_GOLEM"];
        Assert.Equal("MapObjects/Academy/Obsidian_Golem.(AdvMapMonsterShared).xdb", cd.MonsterSharedPath);
        Assert.EndsWith("Academy/3rd/ObsidianGolem.(CreatureVisual).xdb", cd.VisualPath);
    }
}
