using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Adventure.Map;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>
/// Flee / surrender on the adventure map per H5_Game.exe: CanRetreat's tavern-town rule (004d9e20 reason 8),
/// CFinishCombat 00af64b0 (ransom, army) and the loser player's 004fe770 / 004fe890 → 004fe360 (the same hero
/// object into the player's tavern), rehired by 004fec50.
/// </summary>
[Collection("adv")]
public class RetreatTests(Fixture f)
{
    /// <summary>A field battle: <c>mine</c> (current player's hero) against another player's hero.</summary>
    (Engine.Adventure G, HeroState Mine, HeroState Theirs) Setup(bool mineAttacks)
    {
        var g = f.NewGame();
        var mine = g.S.Heroes.First(h => h.Alive && h.Owner == g.Current.Index);
        var theirs = g.S.Heroes.First(h => h.Alive && h.Owner != g.Current.Index && h.Owner >= 0);
        var (att, def) = mineAttacks ? (mine, theirs) : (theirs, mine);
        g.S.Battle = new PendingBattle
        {
            AttackerHero = att.Id, DefenderHero = def.Id, DefenderObject = -1, Where = def.Pos,
            DefenderArmy = def.Army.Live().Select(s => new Stack(s.Creature, s.Count)).ToList(),
        };
        return (g, mine, theirs);
    }

    static List<ArmySlot> Slots(params (string, int)[] s) => s.Select(x => new ArmySlot(x.Item1, x.Item2)).ToList();
    static int Gold(Engine.Adventure g, int player) => g.S.Players.First(p => p.Index == player).Purse[Purse.Gold];

    // 004d9e20 reason 8: the loop over the army's player's towns (player vf 0xc4) for one with TB_TAVERN (town vf 0x34(4)).
    [Fact]
    public void RetreatNeedsATownWithATavern()
    {
        var (g, mine, theirs) = Setup(mineAttacks: true);
        foreach (var t in g.S.Towns.Where(t => t.Owner == mine.Owner)) t.Built["TB_TAVERN"] = 0;
        var t0 = g.S.Towns.First(t => t.Owner == theirs.Owner);
        t0.Built["TB_TAVERN"] = 1;
        var state = g.CreatePendingBattle(3);
        Assert.True(state.NoTavernTown[0]);
        Assert.False(state.NoTavernTown[1]);
        var b = new H5.Combat.Battle(g.Core, state);
        Assert.Equal(8, b.RetreatRefusal(0, surrender: false));
        Assert.Equal(8, b.RetreatRefusal(0, surrender: true));
        Assert.Equal(0, b.RetreatRefusal(1, surrender: false));
    }

    // 00af64b0 case 3: the ransom (half the surviving army's gold value) is paid at once, loser − cost, winner + cost;
    // the army is kept; 004fe360: the same hero object goes into the loser's own tavern; 004fec50 rehires it.
    [Fact]
    public void DefenderSurrendersPaysRansomAndWaitsInHisTavern()
    {
        var (g, mine, theirs) = Setup(mineAttacks: false);   // I defend and surrender
        mine.Level = 9; mine.Experience = 12345;
        mine.Artifacts.Add("SHACKLES_OF_WAR");
        var survivors = Slots(("CREATURE_PEASANT", 10), ("CREATURE_ARCHER", 3));
        long cost = (10L * g.Core.Creatures["CREATURE_PEASANT"].Cost.Gold + 3L * g.Core.Creatures["CREATURE_ARCHER"].Cost.Gold) / 2;
        g.Current.Purse[Purse.Gold] = 5000;
        int theirGold = Gold(g, theirs.Owner);
        Array.Fill(g.Current.Tavern, null);
        var r = g.Apply(new ResolveBattleCmd(0, theirs.Army.Live().Select(s => new ArmySlot(s.Creature, s.Count)).ToList(), survivors)
        { Player = g.Current.Index, EndReason = "surrender" });
        Assert.True(r.Ok, r.Error);
        Assert.False(mine.Alive);
        Assert.Equal(2, mine.Retreat);
        Assert.True(theirs.Alive);
        Assert.Equal(5000 - cost, g.Current.Purse[Purse.Gold]);
        Assert.Equal(theirGold + cost, Gold(g, theirs.Owner));
        Assert.Equal(mine.DefId, g.Current.Tavern[0]);
        Assert.Equal(13, mine.Army.Live().Sum(s => s.Count));

        // Hiring it back brings the same hero: level, experience, artifacts and the kept army.
        var town = g.S.Towns.First(t => t.Owner == g.Current.Index);
        town.Built["TB_TAVERN"] = 1;
        foreach (var h in g.S.Heroes.Where(h => h.Alive && h.Pos == g.EntranceOf(town))) h.Pos = new Tile(0, 0, h.Pos.Floor);
        g.Current.Purse[Purse.Gold] = 100000;
        int heroes = g.S.Heroes.Count;
        var hire = g.Apply(new HireHeroCmd(town.Object, Array.IndexOf(g.TownTavernOffers(g.Current, town), mine.DefId)) { Player = g.Current.Index });
        Assert.True(hire.Ok, hire.Error);
        Assert.Equal(heroes, g.S.Heroes.Count);
        Assert.True(mine.Alive);
        Assert.Equal(0, mine.Retreat);
        Assert.Equal(9, mine.Level);
        Assert.Equal(12345, mine.Experience);
        Assert.Contains("SHACKLES_OF_WAR", mine.Artifacts);
        Assert.Equal(13, mine.Army.Live().Sum(s => s.Count));
    }

    // 00af64b0 case 3 with less gold than the ransom: the loser ends at 0, the winner still gets the whole cost.
    [Fact]
    public void RansomStopsTheLoserAtZeroGold()
    {
        var (g, mine, theirs) = Setup(mineAttacks: true);
        var survivors = Slots(("CREATURE_ARCHER", 20));
        long cost = 20L * g.Core.Creatures["CREATURE_ARCHER"].Cost.Gold / 2;
        g.Current.Purse[Purse.Gold] = 10;
        int theirGold = Gold(g, theirs.Owner);
        Assert.True(g.Apply(new ResolveBattleCmd(1, survivors, new()) { Player = g.Current.Index, EndReason = "surrender" }).Ok);
        Assert.Equal(0, g.Current.Purse[Purse.Gold]);
        Assert.Equal(theirGold + cost, Gold(g, theirs.Owner));
    }

    // 00af64b0 case 2: a fleeing attacker loses his army and pays nothing; 004fe360 takes the first free tavern slot,
    // a random one when both are taken.
    [Fact]
    public void AttackerFleesWithoutArmyIntoHisTavern()
    {
        var (g, mine, theirs) = Setup(mineAttacks: true);
        int gold = g.Current.Purse[Purse.Gold];
        g.Current.Tavern[0] = "X"; g.Current.Tavern[1] = null;
        Assert.True(g.Apply(new ResolveBattleCmd(1, Slots(("CREATURE_ARCHER", 5)), new()) { Player = g.Current.Index, EndReason = "fled" }).Ok);
        Assert.False(mine.Alive);
        Assert.Equal(1, mine.Retreat);
        Assert.Equal(0, mine.Army.StackCount());
        Assert.Equal(gold, g.Current.Purse[Purse.Gold]);
        Assert.Equal(mine.DefId, g.Current.Tavern[1]);
        Assert.Contains(mine.DefId, g.S.HeroPool);
    }

    // The retreating side can be the defender: the attacker wins, the defender is not "defeated" (no +500, kept hero).
    [Fact]
    public void FleeingDefenderIsNotKilled()
    {
        var (g, mine, theirs) = Setup(mineAttacks: false);
        Array.Fill(g.Current.Tavern, null);
        var r = g.Apply(new ResolveBattleCmd(0, theirs.Army.Live().Select(s => new ArmySlot(s.Creature, s.Count)).ToList(), new())
        { Player = g.Current.Index, EndReason = "fled" });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(1, mine.Retreat);
        Assert.Contains(mine.DefId, g.Current.Tavern);
        Assert.Contains(r.Events.OfType<HeroRemoved>(), e => e.HeroId == mine.Id && e.Reason == "fled");
    }
}
