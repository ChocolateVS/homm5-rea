using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Read-only queries behind the special prediction formats and the Elemental Chains log entry.</summary>
[Collection("game")]
public class UiQueryTests(GameDataFixture f)
{
    /// <summary>exe 00977e60: total = D + trunc(D·m − D); the bonus is split off the reported total.</summary>
    [Fact]
    public void ElementalVisionBonusInvertsTheChop()
    {
        foreach (float m in new[] { 1.1f, 1.3f, 1.6f, 2.2f })
            for (int d = 1; d < 600; d += 7)
            {
                int extra = (int)((double)d * m - d);
                Assert.Equal(extra, Battle.ElementalVisionBonus(d + extra, m));
            }
        Assert.Equal(0, Battle.ElementalVisionBonus(100, 1f));
    }

    /// <summary>The log's multiplier is the engine's: splitting the folded total gives back ElementalChainExtra's bonus.</summary>
    [StockFact]
    public void ElementalVisionMultiplierMatchesTheEngine()
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new() { new ArmySlot("CREATURE_FOOTMAN", 10) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new() { new ArmySlot("CREATURE_PEASANT", 10) } },
            7, skipTactics: true, arena: "Grass_Big_01");
        foreach (var hh in s.Heroes) { hh.Specialization = ""; hh.Artifacts.Clear(); hh.Perks.Clear(); }
        var b = new Battle(f.Db, s);
        var h = s.Heroes[0];
        var t = s.Units.Single(u => u.Side == 1 && u.CreatureId == "CREATURE_PEASANT");
        var bolt = f.Db.Spell("SPELL_LIGHTNING_BOLT")!;
        h.Class = "HERO_CLASS_WARLOCK";
        h.Perks.Add(Sk.ElementalVision);
        h.Skills.RemoveAll(x => x.SkillId == Sk.Invocation);
        h.Skills.Add(new SkillEntry(Sk.Invocation, Mastery.Expert));
        h.ElementalAltars = 1;
        foreach (bool chains in new[] { false, true })
        {
            if (chains) h.Perks.Add("HERO_SKILL_ABSOLUTE_CHAINS");
            for (int d = 5; d < 400; d += 13)
            {
                t.Element1 = Elements.Earth;
                int extra = b.ElementalChainExtra(h, bolt, t, d);
                Assert.Equal(extra, Battle.ElementalVisionBonus(d + extra, b.ElementalVisionMultiplier(0)));
            }
        }
    }

    /// <summary>exe 0097f610 case 0x148: Seduce shows the 009774a0 duration; mind-immune targets get no format.</summary>
    [Fact]
    public void SeducePredictionUsesTheEngineDuration()
    {
        var b = f.Duel("CREATURE_SUCCUBUS_SEDUCER", 20, "CREATURE_PEASANT", 30, posA: new Cell(4, 4), posB: new Cell(8, 4));
        var (a, t) = (b.State.Units[0], b.State.Units[1]);
        var p = b.PredictSeduce(a.Id, t.Id)!;
        Assert.Equal("DURATION", p.Kind);
        Assert.Equal((float)Battle.SeduceDuration(a.Count, a.Power, t.Count, t.Power), p.Turns, 3);
        var golem = f.Duel("CREATURE_SUCCUBUS_SEDUCER", 20, "CREATURE_IRON_GOLEM", 10, posA: new Cell(4, 4), posB: new Cell(8, 4));
        Assert.Null(golem.PredictSeduce(golem.State.Units[0].Id, golem.State.Units[1].Id));
    }

    /// <summary>The prediction runs on a copy: hovering a damage-over-time spell leaves the battle unchanged.</summary>
    [Fact]
    public void DotPredictionDoesNotChangeState()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 30, posA: new Cell(4, 4), posB: new Cell(8, 4));
        string before = b.State.Serialize();
        b.PredictDotSpell(0, "SPELL_PLAGUE", b.State.Units[1].Id);
        b.PredictWarcry(0, "SPELL_WARCRY_FEAR_MY_ROAR", b.State.Units[1].Id);
        Assert.Equal(before, b.State.Serialize());
    }
}
