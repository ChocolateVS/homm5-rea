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

    [StockFact]
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

    /// <summary>
    /// FUN_00cd0a30: BaseCreatureToPlace becomes its first Upgrades entry (record +0x104) when a dwelling creature list of the
    /// town (pool list +0x148, filled by 00cd46c0 from every built level) holds it; 00853f90 places that creature.
    /// </summary>
    [Fact]
    public void GuardPostPlacesTheFirstUpgradeWhenTheTownOffersIt()
    {
        var g = f.NewGame();
        var t = new TownState { Type = "TOWN_FORTRESS", Owner = 0 };
        t.Built["TB_SPECIAL_3"] = 1;
        string baseC = g.Core.Stats.Str("adventure/TownBuildingBonuses/Fortress_GuardPostStats/BaseCreatureToPlace", "");
        var up = g.Core.Creatures[baseC].Upgrades[0];
        int tier = Enumerable.Range(1, 7).First(i => g.Db.Building(t.Type, $"TB_DWELLING_{i}", 2)?.Creature == up);
        t.Built[$"TB_DWELLING_{tier}"] = 1;
        Assert.Equal(baseC, g.GuardPostCreature(t));
        t.Built[$"TB_DWELLING_{tier}"] = 2;
        Assert.Equal(up, g.GuardPostCreature(t));
        Assert.Equal(up, g.SiegeFor(t).GuardPostCreature);
    }

    /// <summary>Player +0x1c8 (recount 004fcd80 / 004fce0c): +1 per owned town with ability 0x2c (Stronghold TB_SPECIAL_4).</summary>
    [Fact]
    public void PileOfSkullsCountsThePlayersTowns()
    {
        var g = f.NewGame();
        g.S.Towns.RemoveAll(t => t.Owner == 0);
        Assert.Equal(0, g.PileOfSkullsTowns(0));
        g.S.Towns.Add(new TownState { Type = "TOWN_STRONGHOLD", Owner = 0, Built = { ["TB_SPECIAL_4"] = 1 } });
        g.S.Towns.Add(new TownState { Type = "TOWN_STRONGHOLD", Owner = 0, Built = { ["TB_SPECIAL_4"] = 1 } });
        g.S.Towns.Add(new TownState { Type = "TOWN_STRONGHOLD", Owner = 0, Built = { ["TB_SPECIAL_3"] = 1 } });
        g.S.Towns.Add(new TownState { Type = "TOWN_FORTRESS", Owner = 0, Built = { ["TB_SPECIAL_4"] = 1 } });
        g.S.Towns.Add(new TownState { Type = "TOWN_STRONGHOLD", Owner = 1, Built = { ["TB_SPECIAL_4"] = 1 } });
        Assert.Equal(2, g.PileOfSkullsTowns(0));
    }

    /// <summary>
    /// FUN_00cd0a30: town vf 0x78 (sum of building levels) × CreaturesPerTownLevelPerMonth (5) × (day counter / 28 + 1),
    /// the day counter being 0-based (day 28 is still month 1, day 29 month 2).
    /// </summary>
    [Fact]
    public void GuardPostCountFollowsTownLevelAndMonth()
    {
        var g = f.NewGame();
        var t = new TownState { Type = "TOWN_FORTRESS", Owner = 0 };
        Assert.Equal(0, g.GuardPostCount(t));
        t.Built["TB_FORT"] = 2;
        t.Built["TB_SPECIAL_3"] = 1;
        t.Built["TB_DWELLING_1"] = 2;          // town level 5
        g.S.Day = 28;
        Assert.Equal(5 * 5 * 1, g.GuardPostCount(t));
        g.S.Day = 29;
        Assert.Equal(5 * 5 * 2, g.GuardPostCount(t));
        g.S.Day = 85;
        Assert.Equal(5 * 5 * 4, g.GuardPostCount(t));
        Assert.Equal(0, g.GuardPostCount(new TownState { Type = "TOWN_HEAVEN", Owner = 0, Built = { ["TB_SPECIAL_3"] = 1 } }));
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
