using H5.Adventure.State;
using H5.Combat;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.Tests;

[Collection("adv")]
public class SiegeHandOffTests(Fixture f)
{
    /// <summary>
    /// Fan manual p.309 "Town Max Level" column. Stronghold is the exception: the manual says 34, the game
    /// data defines 39 building levels. TODO(exe): confirm the town level ratio (town vcall +0x74, see
    /// docs/rules/EXE_FINDINGS.md) for Stronghold; the data count is used until then.
    /// </summary>
    [Theory]
    [InlineData("TOWN_HEAVEN", 36)]
    [InlineData("TOWN_ACADEMY", 36)]
    [InlineData("TOWN_PRESERVE", 38)]
    [InlineData("TOWN_DUNGEON", 37)]
    [InlineData("TOWN_INFERNO", 36)]
    [InlineData("TOWN_NECROMANCY", 36)]
    [InlineData("TOWN_FORTRESS", 39)]
    [InlineData("TOWN_STRONGHOLD", 39)]
    public void TownMaxLevelMatchesManual(string town, int expected) =>
        Assert.Equal(expected, f.NewGame().TownMaxLevel(town));

    static (Engine.Adventure G, TownState Town, HeroState Attacker) Assault(Fixture f)
    {
        var g = f.NewGame();
        var town = g.S.Towns.First(t => t.Owner >= 0);
        var attacker = g.S.Heroes.First(h => h.Alive && h.Owner != town.Owner);
        town.Specialization = null;
        Array.Fill(town.Garrison, null);
        town.Garrison[0] = new Stack("CREATURE_PEASANT", 10);
        g.S.Battle = new PendingBattle
        {
            AttackerHero = attacker.Id, DefenderObject = town.Object, Where = town.Pos,
            DefenderArmy = town.Garrison.Live().Select(s => new Stack(s.Creature, s.Count)).ToList(), Siege = true,
        };
        return (g, town, attacker);
    }

    [Fact]
    public void TownAssaultBuildsSiegeWithFortifications()
    {
        var (g, town, _) = Assault(f);
        town.Built["TB_FORT"] = 2;
        var state = g.CreatePendingBattle(11);
        Assert.NotNull(state.Siege);
        Assert.Contains(state.Siege!.Buildings, b => b.Type == "GATE");
        var setup = g.SiegeFor(town);
        Assert.Equal(2, setup.FortLevel);
        Assert.Equal(town.Type, setup.Town);
        Assert.Equal(g.TownMaxLevel(town.Type), setup.TownMaxLevel);
    }

    [Fact]
    public void FieldBattlesHaveNoSiegeOrTownBonuses()
    {
        var g = f.NewGame();
        var a = g.S.Heroes.First(h => h.Alive);
        g.S.Battle = new PendingBattle { AttackerHero = a.Id, DefenderArmy = { new Stack(g.Core.Creatures.Keys.First(), 5) } };
        var state = g.CreatePendingBattle(3);
        Assert.Null(state.Siege);
        Assert.All(state.SideBonuses, b => Assert.Equal((0, 0, 0, 0), (b.Attack, b.Defence, b.Morale, b.Luck)));
    }

    [Fact]
    public void TavernGivesGarrisonMoraleWithoutAHero()
    {
        var (g, town, _) = Assault(f);
        town.Built.Remove("TB_TAVERN");
        var without = g.CreatePendingBattle(5);
        Assert.Equal(0, without.SideBonuses[1].Morale);

        var (g2, town2, _) = Assault(f);
        town2.Built["TB_TAVERN"] = 1;
        var state = g2.CreatePendingBattle(5);
        Assert.Equal(1, state.SideBonuses[1].Morale);
        // The engine applies it to garrison creatures even though no hero defends the town.
        var battle = new Battle(g2.Core, state);
        var garrison = state.Units.First(u => u.Side == 1 && u.CreatureId == "CREATURE_PEASANT");
        Assert.Equal(1, battle.MoraleOf(garrison));
    }

    [Fact]
    public void FactionBuildingsAndSpecialisationsMapToSides()
    {
        var g = f.NewGame();
        var t = new TownState { Type = "TOWN_INFERNO", Owner = 0 };
        t.Built["TB_SPECIAL_1"] = 1;          // Infernal Loom
        t.Built["TB_GRAIL"] = 1;              // Deity of Fire
        var (d, a) = g.TownBonuses(t);
        Assert.Equal(-2, a.Luck);
        Assert.Equal(10, d.HeroSpellPower);

        t = new TownState { Type = "TOWN_PRESERVE", Owner = 0 };
        t.Built["TB_SPECIAL_2"] = 2;          // Sparkling Fountain
        (d, _) = g.TownBonuses(t);
        Assert.Equal(2, d.Luck);

        t = new TownState { Type = "TOWN_NECROMANCY", Owner = 0 };
        t.Built["TB_SPECIAL_2"] = 1;          // Unholy Temple
        (_, a) = g.TownBonuses(t);
        Assert.Equal(-2, a.Morale);

        t = new TownState { Type = "TOWN_FORTRESS", Owner = 0 };
        t.Built["TB_GRAIL"] = 1;              // Blazing Anvil
        t.Built["TB_SPECIAL_4"] = 1;          // Stoneworks
        t.Built["TB_SPECIAL_3"] = 1;          // Guard Post
        (d, _) = g.TownBonuses(t);
        Assert.Equal((6, 6), (d.Defence, d.HeroSpellPower));
        var siege = g.SiegeFor(t);
        Assert.True(siege.Stoneworks);
        Assert.Equal(5 * t.DevLevel * g.S.Month, siege.GuardPostDefenders);
    }

    [Fact]
    public void GrailsBoostTheOwnersHeroesEverywhere()
    {
        var g = f.NewGame();
        var town = g.S.Towns.First(t => t.Owner >= 0);
        Assert.Equal((0, 0), g.GrailHeroBonus(town.Owner));
        town.Built["TB_GRAIL"] = 1;
        var (atk, luck) = g.GrailHeroBonus(town.Owner);
        switch (town.Type)
        {
            case "TOWN_HEAVEN" or "TOWN_PRESERVE": Assert.Equal((0, 2), (atk, luck)); break;
            case "TOWN_STRONGHOLD": Assert.Equal((4, 0), (atk, luck)); break;
            default: Assert.Equal((0, 0), (atk, luck)); break;
        }
    }
}
