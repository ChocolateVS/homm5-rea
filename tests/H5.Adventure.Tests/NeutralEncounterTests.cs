using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Neutral join / flee / fight (exe FUN_008e1ad0, 008dc380, CMonsterFleeCmd 0042ad40).</summary>
[Collection("adv")]
public class NeutralEncounterTests(Fixture f)
{
    static (Engine.Adventure g, HeroState h, MapObject m) Setup(Fixture f, string creature, int count, string mood)
    {
        var g = f.NewGame("A2M1");
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.MovePoints = h.MaxMovePoints = 5000;
        var m = g.Map.Objects.First(x => x.Kind == ObjectKind.Monster && !g.S.Obj(x.Index).Removed);
        m.Mood = mood;
        m.Courage = "MONSTER_COURAGE_CAN_FLEE_JOIN";
        var os = g.S.Obj(m.Index);
        os.Guards = new List<Stack> { new(creature, count) };
        return (g, h, m);
    }

    static List<AdvEvent> Attack(Engine.Adventure g, HeroState h, MapObject m)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if ((dx == 0 && dy == 0) || g.Map.Floor(m.Pos.Floor) is not { } fl || !fl.InBounds(m.Pos.X + dx, m.Pos.Y + dy) || fl.IsBlocked(m.Pos.X + dx, m.Pos.Y + dy)) continue;
                h.Pos = new Tile(m.Pos.X + dx, m.Pos.Y + dy, m.Pos.Floor);
                h.InBoat = false;
                h.MovePoints = 5000;
                var r = g.Apply(new MoveHeroCmd(h.Id, m.Pos) { Player = h.Owner });
                if (r.Ok && r.Events.Any(e => e is BattleRequested or ChoiceOffered)) return r.Events.ToList();
            }
        Assert.Fail("no tile to attack the monster from");
        return new();
    }

    [Fact]
    public void HeirOfUndeadGetsNecropolisStacksForFreeScaledByLevel()
    {
        // 008dd0d0: spec 0x1d and only town-7 creatures → mode 0, count = ((level + 25) · count · 2 + 50) / 100.
        var (g, h, m) = Setup(f, "CREATURE_SKELETON", 40, "MONSTER_MOOD_HOSTILE");
        m.Courage = "MONSTER_COURAGE_ALWAYS_FIGHT";
        h.Specialization = "HERO_SPEC_HEIR_OF_UNDEAD";
        h.Level = 10;
        var offer = g.JoinOffer(h, m);
        Assert.Equal(0, offer.Mode);
        Assert.Equal(((10 + 25) * 40 * 2 + 50) / 100, offer.Stacks[0].Count);
        Assert.Contains(Attack(g, h, m), e => e is ChoiceOffered { Kind: "join_free" });
        Assert.Equal(28, g.S.Choice!.Creatures[0].Count);
        // A living stack among them: the map's courage decides.
        g.S.Obj(m.Index).Guards.Add(new Stack("CREATURE_PEASANT", 5));
        Assert.Equal(1, g.JoinOffer(h, m).Mode);
    }

    [Fact]
    public void HeraldOfDeathOffersTheUndeadCounterpartAndFitsCountsFreeSlots()
    {
        var (g, h, m) = Setup(f, "CREATURE_PEASANT", 10, "MONSTER_MOOD_FRIENDLY");
        Assert.Equal("CREATURE_PEASANT", g.JoinOffer(h, m).Stacks[0].Creature);
        h.Perks.Add("HERO_SKILL_HERALD_OF_DEATH");
        Assert.Equal("CREATURE_SKELETON", g.HeraldOfDeathCreature("CREATURE_PEASANT"));
        Assert.Equal("CREATURE_SKELETON", g.JoinOffer(h, m).Stacks[0].Creature);
        Assert.Equal("CREATURE_ZOMBIE", g.HeraldOfDeathCreature("CREATURE_ZOMBIE"));          // undead stays
        h.Army = Army.Empty();
        for (int i = 0; i < Army.Slots; i++) h.Army[i] = new Stack("CREATURE_ARCHER", 1);
        Assert.False(g.JoinOffer(h, m).Fits);
        h.Army[3] = new Stack("CREATURE_SKELETON", 1);
        Assert.True(g.JoinOffer(h, m).Fits);
    }

    [Fact]
    public void CrownOfLeadershipCountsOnlyWithDiplomacy()
    {
        var g = f.NewGame("A2M1");
        var h = g.S.Heroes.First(x => x.Alive);
        h.Perks.Remove("HERO_SKILL_DIPLOMACY");
        h.Artifacts.Add("ARTIFACT_CROWN_OF_LEADER");
        Assert.Equal(0, g.DiplomacyLevel(h));
        h.Perks.Add("HERO_SKILL_DIPLOMACY");
        Assert.Equal(2, g.DiplomacyLevel(h));
        h.Artifacts.Remove("ARTIFACT_CROWN_OF_LEADER");
        Assert.Equal(1, g.DiplomacyLevel(h));
    }

    [Fact]
    public void FarWeakerHeroIsAttackedWithoutAnOffer()
    {
        var (g, h, m) = Setup(f, "CREATURE_WOLF", 5000, "MONSTER_MOOD_FRIENDLY");
        h.Army = Army.Empty();
        h.Army[0] = new Stack("CREATURE_PEASANT", 1);
        var ev = Attack(g, h, m);
        Assert.Contains(ev, e => e is BattleRequested);
        Assert.Null(g.S.Choice);
    }

    [Fact]
    public void FrighteningHeroIsAskedToPursueAndLettingThemGoGivesHalfTheirExperience()
    {
        // Wild mood (chance −5): never joins; R far above T → the flee / pursue box (008e1a80).
        var (g, h, m) = Setup(f, "CREATURE_WOLF", 10, "MONSTER_MOOD_WILD");
        h.Perks.Remove("HERO_SKILL_DIPLOMACY");
        h.Army = Army.Empty();
        h.Army[0] = new Stack("CREATURE_BLACK_DRAGON", 500);
        var ev = Attack(g, h, m);
        Assert.Contains(ev, e => e is ChoiceOffered { Kind: "monster_flee" });
        var wolf = g.Core.Creatures["CREATURE_WOLF"];
        long exp = h.Experience;
        var r = g.Apply(new AnswerChoiceCmd(false) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        Assert.True(g.S.Obj(m.Index).Removed);
        Assert.Equal(exp + 10L * (wolf.Exp != 0 ? wolf.Exp : wolf.Health) / 2, h.Experience);
    }

    [Fact]
    public void HeroesExpCoefScalesTheFleeExperience()
    {
        // SetHeroesExpCoef → world +0x16c (clamped 0..1000, 0x0099e5b0); CMonsterFleeCmd passes the half through
        // world vf 0x1b4 (0x0099e500): coef · exp / 100 in integers.
        var (g, h, m) = Setup(f, "CREATURE_WOLF", 13, "MONSTER_MOOD_WILD");
        h.Perks.Remove("HERO_SKILL_DIPLOMACY");
        h.Army = Army.Empty();
        h.Army[0] = new Stack("CREATURE_BLACK_DRAGON", 500);
        g.SetHeroesExpCoef(5000);
        Assert.Equal(1000, g.S.HeroesExpCoef);
        g.SetHeroesExpCoef(-3);
        Assert.Equal(0, g.S.HeroesExpCoef);
        g.SetHeroesExpCoef(37);
        Attack(g, h, m);
        var wolf = g.Core.Creatures["CREATURE_WOLF"];
        long exp = h.Experience;
        Assert.True(g.Apply(new AnswerChoiceCmd(false) { Player = h.Owner }).Ok);
        long half = 13L * (wolf.Exp != 0 ? wolf.Exp : wolf.Health) / 2;
        Assert.Equal(exp + 37 * half / 100, h.Experience);
    }

    [Fact]
    public void PursuingFleeingMonstersStartsTheFight()
    {
        var (g, h, m) = Setup(f, "CREATURE_WOLF", 10, "MONSTER_MOOD_WILD");
        h.Army = Army.Empty();
        h.Army[0] = new Stack("CREATURE_BLACK_DRAGON", 500);
        Attack(g, h, m);
        var r = g.Apply(new AnswerChoiceCmd(true) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        Assert.Contains(r.Events, e => e is BattleRequested);
    }
}
