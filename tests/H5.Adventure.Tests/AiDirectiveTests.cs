using H5.Adventure.Engine;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Readers of the map-script AI/hiring directives (exe filter, attractor, turn and flee rules).</summary>
[Collection("adv")]
public class AiDirectiveTests(Fixture f)
{
    [Fact]
    public void HeroFilter_follows_the_exe_rules()
    {
        var fl = new HeroFilter();
        Assert.True(fl.Accepts("Brem", "TOWN_HEAVEN"));
        fl.SetRace("TOWN_HEAVEN", allow: false);
        Assert.False(fl.Accepts("Brem", "TOWN_HEAVEN"));
        fl.SetHero("Brem", allow: true);                 // an allowed name overrides the race rule
        Assert.True(fl.Accepts("Brem", "TOWN_HEAVEN"));
        fl.SetHero("Brem", allow: false);                // a denied name always loses
        Assert.False(fl.Accepts("Brem", "TOWN_ACADEMY"));
        fl.ClearHero("Brem");
        Assert.True(fl.Accepts("Brem", "TOWN_ACADEMY"));
        // Deny-all appends every race (FUN_00c06a60 keeps duplicates), allow removes one copy.
        fl.SetAllRaces(allow: false);
        fl.SetRace("TOWN_HEAVEN", allow: true);
        Assert.False(fl.Accepts("Brem", "TOWN_HEAVEN"));
        fl.SetAllRaces(allow: true);
        Assert.Empty(fl.DeniedRaces);
    }

    [Fact]
    public void Attractor_list_overwrites_removes_and_appends()
    {
        var l = new List<AiAttractor>();
        AiAttractor.Set(l, "Mine1", 1);
        AiAttractor.Set(l, "Mine1", -1);
        Assert.Equal(-1, AiAttractor.Get(l, "Mine1"));
        AiAttractor.Set(l, "Mine1", 0);
        Assert.Empty(l);
        AiAttractor.Set(l, "Mine2", 0);                  // a new entry is appended even with priority 0
        Assert.Single(l);
        Assert.Equal(0, AiAttractor.Get(l, "Mine2"));
    }

    [Fact]
    public void Town_filter_hides_tavern_heroes_and_shifts_the_second()
    {
        var g = f.NewGame();
        var p = g.Current;
        var t = g.S.Towns.First(x => x.Owner == p.Index);
        p.Tavern[0] = "A"; p.Tavern[1] = "B";
        t.HireFilter.SetHero("A", allow: false);
        var offers = g.TownTavernOffers(p, t);
        Assert.Equal("B", offers[0]);
        Assert.Null(offers[1]);
    }

    [Fact]
    public void No_turn_without_towns_for_flagged_ai_without_enabled_heroes()
    {
        var g = f.NewGame();
        var p = g.S.Players.First(x => !x.Human);
        foreach (var t in g.S.Towns.Where(t => t.Owner == p.Index)) t.Owner = -1;
        var heroes = g.S.Heroes.Where(h => h.Alive && h.Owner == p.Index).ToList();
        Assert.NotEmpty(heroes);
        Assert.True(g.GetsTurn(p));
        p.AiNoTurnWithoutTownsAndActiveHeroes = true;
        Assert.True(g.GetsTurn(p));
        foreach (var h in heroes) h.AiEnabled = false;
        Assert.False(g.GetsTurn(p));
        p.AiNoTurnWithoutTownsAndActiveHeroes = false;
        Assert.True(g.GetsTurn(p));
        foreach (var h in heroes) h.Alive = false;
        Assert.False(g.GetsTurn(p));                     // neither towns nor heroes: never a turn
    }

    [Fact]
    public void Flee_denial_checks_hero_then_player_list()
    {
        var g = f.NewGame();
        var p = g.S.Players.First(x => !x.Human);
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        var enemy = g.S.Heroes.First(x => x.Alive && x.Owner != p.Index);
        Assert.False(g.AiFleeDenied(h, enemy));
        p.AiNoFleeFrom.Add(enemy.DefId);
        Assert.True(g.AiFleeDenied(h, enemy));
        h.AiFleeControl = true;                          // the hero ignores the player's list
        Assert.False(g.AiFleeDenied(h, enemy));
        h.AiNoFleeFrom.Add("");                          // "" = every enemy, neutrals included
        Assert.True(g.AiFleeDenied(h, null));
    }

    [Fact]
    public void Directives_survive_save_and_load()
    {
        var g = f.NewGame();
        g.S.Players[0].TavernFilter.SetRace("TOWN_INFERNO", false);
        g.S.Heroes[0].AiEnabled = false;
        g.S.Towns[0].AiGarrisonBlocked = true;
        var back = AdvState.Deserialize(g.S.Serialize());
        Assert.Contains("TOWN_INFERNO", back.Players[0].TavernFilter.DeniedRaces);
        Assert.False(back.Heroes[0].AiEnabled);
        Assert.True(back.Towns[0].AiGarrisonBlocked);
    }
}
