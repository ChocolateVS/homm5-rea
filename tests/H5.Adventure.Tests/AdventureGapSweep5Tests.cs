using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>
/// Adventure gap sweep 5: Haunt Mine (00afa9c0), allied mines (00b08d40 / 00ce3310 → 00a85030), Forest Guard Emblem
/// (00b54770 case 0x73) and the creatures gained of the mission statistics (logger vf 0x1c = 00877610).
/// </summary>
[Collection("adv")]
public class AdventureGapSweep5Tests(Fixture f)
{
    static readonly (int dx, int dy)[] Around = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    static (HeroState H, MapObject Mine, Tile Entrance) NextToAMine(Engine.Adventure g)
    {
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.MovePoints = h.MaxMovePoints = 2000;
        var fl = g.Map.Floor(0)!;
        foreach (var o in g.Map.Objects.Where(o => o.Kind == ObjectKind.Mine && o.Pos.Floor == 0 && o.ActiveTiles.Any()))
        {
            var e = o.ActiveTiles.First();
            if (g.GuardingMonster(e) != null) continue;
            foreach (var (dx, dy) in Around)
            {
                var n = e.Offset(dx, dy);
                if (!fl.InBounds(n.X, n.Y) || fl.IsBlocked(n.X, n.Y) || fl.IsWater(n.X, n.Y) || o.ActiveTiles.Contains(n)) continue;
                if (g.S.Heroes.Any(x => x.Alive && x.Pos == n) || g.GuardingMonster(n) != null || g.ObjectAtTile(n) != null) continue;
                h.Pos = n;
                var os = g.S.Obj(o.Index);
                os.Guards.Clear();
                os.Owner = -1;
                os.Army = Army.Empty();
                if (g.FindPath(h, e) is { Count: > 0 }) return (h, o, e);
            }
        }
        throw new InvalidOperationException("no reachable mine");
    }

    [Fact]
    public void HauntMinePutsManesIntoACapturedMineAndLogsThem()
    {
        // 00afa9c0: n = min(((d / 7 + 2) / 2) · 5, 25), d = day counter; CREATURE_MANES into the mine's army (vf 0xc).
        var g = f.NewGame("A2M1");
        var (h, mine, e) = NextToAMine(g);
        h.Perks.Add("HERO_SKILL_HAUNT_MINE");
        g.S.Day = 15;                                   // d = 14 → (2 + 2) / 2 · 5 = 10
        var p = g.Current;
        int before = p.Mission.Creatures;
        var r = g.Apply(new MoveHeroCmd(h.Id, e) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(h.Owner, g.S.Obj(mine.Index).Owner);
        Assert.Equal(10, g.S.Obj(mine.Index).Army.Live().Where(s => s.Creature == "CREATURE_MANES").Sum(s => s.Count));
        Assert.Contains(r.Events, ev => ev is ObjectVisited { What: "haunt:10" });
        Assert.Equal(before + 10, p.Mission.Creatures);
        Assert.Equal(25, Math.Min((200 / 7 + 2) / 2 * 5, 25));
    }

    [Fact]
    public void AnAlliedMineIsNotTaken()
    {
        // 00b08d40: the old and new owners on one team (00a85030) → the visit does nothing.
        var g = f.NewGame("A2M1");
        var (h, mine, e) = NextToAMine(g);
        var ally = g.S.Players.First(x => x.Index != h.Owner);
        ally.Team = g.Current.Team;
        g.S.Obj(mine.Index).Owner = ally.Index;
        Assert.True(g.Apply(new MoveHeroCmd(h.Id, e) { Player = h.Owner }).Ok);
        Assert.Equal(ally.Index, g.S.Obj(mine.Index).Owner);
    }

    [Fact]
    public void ForestGuardEmblemGivesBladeJugglersByTheWeekOnce()
    {
        // 00b54770 case 0x73: w = d / 7 + 1 → 10 / 20 / 30 / 40 / 50; FUN_0045b8e0(army, 0x2d); flag +0x2aa.
        var g = f.NewGame("A2M1");
        var h = g.S.Heroes.First(x => x.Alive && x.Owner >= 0);
        var bj = g.Core.Creatures["CREATURE_BLADE_JUGGLER"];
        h.Army = Army.Empty();
        h.Army[0] = new Stack("CREATURE_PEASANT", 5);
        g.S.Day = 15;                                   // d = 14 → w = 3 → 20
        var p = g.S.Players.Single(x => x.Index == h.Owner);
        int before = p.Mission.Creatures;
        g.ScriptLearnPerk(h, "HERO_SKILL_FOREST_GUARD_EMBLEM");
        string expect = bj.Upgrades.Count == 0 ? "CREATURE_BLADE_JUGGLER" : bj.Upgrades[0];
        Assert.Equal(20, h.Army.Live().Where(s => s.Creature == expect).Sum(s => s.Count));
        Assert.Equal(before + 20, p.Mission.Creatures);
        h.Perks.Remove("HERO_SKILL_FOREST_GUARD_EMBLEM");
        g.S.Day = 100;
        g.ScriptLearnPerk(h, "HERO_SKILL_FOREST_GUARD_EMBLEM");
        Assert.Equal(20, h.Army.Live().Where(s => s.Creature == expect).Sum(s => s.Count));
    }

    [Fact]
    public void RecruitsAndHiredArmiesCountAsCreaturesDuring()
    {
        var g = f.NewGame();
        var p = g.Current;
        Assert.Equal(g.Summary(p.Index).CreaturesAtEnd, p.Mission.Creatures);   // map start (vf 0x8)
        var t = g.S.Towns.First(x => x.Owner == p.Index);
        p.Purse = new Purse(new Resources(999, 999, 999, 999, 999, 999, 999999));
        t.Built["TB_DWELLING_1"] = Math.Max(1, t.Built.GetValueOrDefault("TB_DWELLING_1"));
        t.Available[0] = 10;
        for (int i = 0; i < 7; i++) t.Garrison[i] = null;
        foreach (var x in g.S.Heroes.Where(x => x.InTown == t.Object)) x.InTown = -1;
        int before = p.Mission.Creatures;
        Assert.True(g.Apply(new RecruitCmd(t.Object, 1, 3) { Player = p.Index }).Ok);   // CHireCreaturesCmd 004f4faa
        Assert.Equal(before + 3, p.Mission.Creatures);
        Assert.Equal(before + 3, g.Summary(p.Index).CreaturesDuring);

        // A hired hero's army (004fec50).
        t.Built["TB_TAVERN"] = 1;
        foreach (var x in g.S.Heroes.Where(x => x.Alive && x.Pos == g.EntranceOf(t))) x.Pos = new Tile(0, 0, x.Pos.Floor);
        foreach (var x in g.S.Heroes.Where(x => x.Owner == p.Index)) x.Alive = false;
        string defId = g.TownTavernOffers(p, t).First(id => id != null)!;
        int army = p.TavernArmy[Array.IndexOf(p.Tavern, defId)].Live().Sum(s => s.Count);
        before = p.Mission.Creatures;
        Assert.True(g.Apply(new HireHeroCmd(t.Object, Array.IndexOf(g.TownTavernOffers(p, t), defId)) { Player = p.Index }).Ok);
        Assert.Equal(before + army, p.Mission.Creatures);
    }

    [Fact]
    public void ResourcesDuringSumTheGainsOnly()
    {
        // vf 0xdc (004fc3e0) → logger vf 0x3c (00877890): + (new − old) when no component is negative.
        var g = f.NewGame("A2M1");
        var p = g.Current;
        var before = g.Summary(p.Index).ResourcesDuring!.ToArray();
        g.ScriptGive(p.Index, Purse.Gold, 700);
        g.ScriptGive(p.Index, Purse.Gold, -300);
        g.ScriptGive(p.Index, Purse.Wood, 4);
        var s = g.Summary(p.Index).ResourcesDuring!;
        Assert.Equal(before[Purse.Gold] + 700, s[Purse.Gold]);
        Assert.Equal(before[Purse.Wood] + 4, s[Purse.Wood]);
    }
}
