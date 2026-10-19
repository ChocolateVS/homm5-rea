using System.Text.Json.Nodes;
using H5.AdvNet;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using HeroState = H5.Adventure.State.HeroState;
using Stack = H5.Adventure.State.Stack;
using H5.Net;
using Xunit;

namespace H5.AdvNet.Tests;

/// <summary>
/// Concurrent battles during the simultaneous phase (docs/rules/SIMULTANEOUS.md #33, #34, #37, #40): each player
/// holds its own combat (CPlayer vf +0x8c, 008545b0), the attack gate 00426b30, per-player choice slots.
/// </summary>
public class ConcurrentBattleTests
{
    static void Pest(AdvNetSettings s) { s.SimultaneousTurns = true; s.Pest = true; }

    static void AssertViewsMatchHost(Harness h)
    {
        foreach (var c in h.Clients)
        {
            Assert.False(c.Desynced);
            Assert.Equal(JsonDiff.CanonicalHash(h.Session.ViewFor(c.MyPlayer)), JsonDiff.CanonicalHash(JsonNode.Parse(c.View.Serialize())));
        }
    }

    static IEnumerable<Tile> Around(Tile t) =>
        Enumerable.Range(-1, 3).SelectMany(dx => Enumerable.Range(-1, 3).Select(dy => t.Offset(dx, dy))).Where(x => x != t);

    static bool Free(H5.Adventure.Engine.Adventure g, Tile t)
    {
        var f = g.Map.Floor(t.Floor);
        if (f == null || !f.InBounds(t.X, t.Y) || f.IsBlocked(t.X, t.Y) || f.IsWater(t.X, t.Y)) return false;
        if (g.S.Heroes.Any(h => h.Alive && h.Pos == t)) return false;
        return !g.Map.Objects.Any(o => o.Kind != ObjectKind.Static && o.ActiveTiles.Contains(t) && !(g.S.Objects.TryGetValue(o.Index, out var os) && os.Removed));
    }

    static bool Alive(H5.Adventure.Engine.Adventure g, MapObject m) => !(g.S.Objects.TryGetValue(m.Index, out var os) && os.Removed);

    /// <summary>Test setup on the host: a strong hero standing next to a neutral stack that always fights.</summary>
    static (HeroState Hero, MapObject Monster) NextToMonster(Harness h, int side, IEnumerable<Tile> avoid, string courage = "MONSTER_COURAGE_ALWAYS_FIGHT")
    {
        var g = h.Session.Game;
        int mp = h.Session.MapPlayerOf(side);
        var hero = g.S.Heroes.First(x => x.Owner == mp && x.Alive && !x.Garrisoned);
        var others = g.S.Heroes.Where(x => x.Alive && x != hero).Select(x => x.Pos).Concat(avoid).ToList();
        foreach (var m in g.Map.Objects.Where(o => o.Kind == ObjectKind.Monster && Alive(g, o)))
        {
            if (others.Any(p => p.Floor == m.Pos.Floor && Math.Max(Math.Abs(p.X - m.Pos.X), Math.Abs(p.Y - m.Pos.Y)) < 8)) continue;
            if (g.S.Obj(m.Index).Guards.Count == 0) continue;
            foreach (var t in Around(m.Pos).Where(t => Free(g, t)))
            {
                // Only this stack may guard the spot.
                if (Around(t).Any(n => n != m.Pos && g.Map.Objects.Any(o => o.Kind == ObjectKind.Monster && o.Index != m.Index && Alive(g, o) && o.ActiveTiles.Contains(n)))) continue;
                var old = hero.Pos;
                hero.Pos = t;
                if (g.FindPath(hero, m.Pos) is not { Count: 2 }) { hero.Pos = old; continue; }
                hero.InTown = -1;
                hero.Garrisoned = false;
                hero.MovePoints = 3000;
                hero.Army = Army.Empty();
                hero.Army[0] = new Stack("CREATURE_ARCHANGEL", 60);
                m.Courage = courage;
                return (hero, m);
            }
        }
        throw new InvalidOperationException("no free neutral stack");
    }

    /// <summary>The clients fight their battles with a simple AI until <paramref name="done"/>.</summary>
    static void PlayBattles(Harness h, Func<bool> done)
    {
        var ai = new GreedyBattleAI { MaxCandidates = 8 };
        for (int step = 0; step < 6000 && !done(); step++)
        {
            bool acted = false;
            for (int s = 0; s < h.Clients.Length && !acted; s++)
                if (h.Clients[s].Battle is { IsMyTurn: true } b)
                {
                    Assert.True(h.Send(s, AdvNetCommands.Battle(ai.Decide(b.Battle, b.MySide) with { Side = b.MySide })), string.Join("; ", h.Rejections));
                    acted = true;
                }
            Assert.True(acted, "nobody could act in a battle");
            Assert.All(h.Clients, c => Assert.False(c.Desynced));
        }
        Assert.True(done());
    }

    static Tile? Reachable(AdventureNetReplica r, HeroState hero)
    {
        foreach (var t in Enumerable.Range(-3, 7).SelectMany(dx => Enumerable.Range(-3, 7).Select(dy => hero.Pos.Offset(dx, dy))))
        {
            var f = r.Map.Map.Floor(t.Floor);
            if (t == hero.Pos || f == null || !f.InBounds(t.X, t.Y) || f.IsBlocked(t.X, t.Y)) continue;
            if (r.Game.GuardingMonster(t) != null) continue;
            if (r.Game.FindPath(hero, t) is { Count: > 1 }) return t;
        }
        return null;
    }

    [Fact]
    public void TwoHumansFightNeutralsAtTheSameTime() // CPlayer vf +0x8c per player, 008545b0; results applied as each ends
    {
        var h = new Harness(2, Pest);
        var g = h.Session.Game;
        int p0 = h.Session.MapPlayerOf(0), p1 = h.Session.MapPlayerOf(1);
        var (a, m0) = NextToMonster(h, 0, Array.Empty<Tile>());
        var (b, m1) = NextToMonster(h, 1, new[] { m0.Pos });
        h.Tick(1000);
        var aAt = a.Pos;
        int day = g.S.Day;

        Assert.True(h.Send(0, AdvNetCommands.Adv(new MoveHeroCmd(a.Id, m0.Pos))), string.Join("; ", h.Rejections));
        Assert.NotNull(h.Session.BattleOf(p0));
        Assert.NotNull(h.Clients[0].Battle);
        Assert.Null(h.Clients[1].Battle);
        Assert.True(h.Clients[1].IsMyTurn);
        Assert.False(h.Clients[0].IsMyTurn);
        Assert.Null(g.S.Battle);                       // parked: the engine stays free for the others
        // The fighting player is on its combat screen.
        Assert.False(h.Send(0, AdvNetCommands.Adv(new EndTurnCmd())));

        // The other human keeps playing and starts its own battle.
        Assert.True(h.Send(1, AdvNetCommands.Adv(new MoveHeroCmd(b.Id, m1.Pos))), string.Join("; ", h.Rejections));
        Assert.Equal(2, h.Session.Battles.Count);
        Assert.NotNull(h.Clients[1].Battle);
        // Nothing of a battle changes the adventure map until its result: side 1 still sees the hero and the stack.
        Assert.Equal(aAt, g.S.Hero(a.Id)!.Pos);
        Assert.True(Alive(g, m0));
        Assert.True(h.Clients[1].View.Heroes.Find(x => x.Id == a.Id) is not { } seenA || seenA.Pos == aAt);
        AssertViewsMatchHost(h);

        PlayBattles(h, () => h.Session.Battles.Count == 0);
        Assert.False(Alive(g, m0));
        Assert.False(Alive(g, m1));
        Assert.Equal(day, g.S.Day);
        Assert.True(h.Clients[0].IsMyTurn && h.Clients[1].IsMyTurn);
        AssertViewsMatchHost(h);
    }

    [Fact]
    public void HumanVersusHumanBattleWhileAThirdHumanMoves() // 00432d30 interactive for both, 00998520 day waits
    {
        var h = new Harness(3, s => { Pest(s); s.DevStartAdjacent = true; });
        var g = h.Session.Game;
        int p0 = h.Session.MapPlayerOf(0), p1 = h.Session.MapPlayerOf(1);
        var a = g.S.Heroes.First(x => x.Owner == p0 && x.Alive && !x.Garrisoned);
        var d = g.S.Heroes.First(x => x.Owner == p1 && x.Alive && !x.Garrisoned);
        int day = g.S.Day;

        Assert.True(h.Send(0, AdvNetCommands.Adv(new MoveHeroCmd(a.Id, d.Pos))), string.Join("; ", h.Rejections));
        Assert.NotNull(h.Clients[0].Battle);
        Assert.NotNull(h.Clients[1].Battle);
        Assert.Null(h.Clients[2].Battle);
        Assert.Same(h.Session.BattleOf(p0), h.Session.BattleOf(p1));
        Assert.Equal(0, h.Clients[0].Battle!.MySide);
        Assert.Equal(1, h.Clients[1].Battle!.MySide);
        Assert.False(h.Send(1, AdvNetCommands.Adv(new EndTurnCmd())));   // the defender fights too

        // The third human moves meanwhile and ends its turn; the day waits for the battle.
        var r2 = h.Clients[2];
        var hero2 = r2.View.Heroes.First(x => x.Owner == r2.MyPlayer && x.Alive && !x.Garrisoned);
        var to = Reachable(r2, hero2);
        Assert.NotNull(to);
        var from2 = hero2.Pos;
        Assert.True(h.Send(2, AdvNetCommands.Adv(new MoveHeroCmd(hero2.Id, to!.Value))), string.Join("; ", h.Rejections));
        Assert.NotEqual(from2, g.S.Hero(hero2.Id)!.Pos);
        Assert.True(h.Send(2, AdvNetCommands.Adv(new EndTurnCmd())), string.Join("; ", h.Rejections));
        Assert.True(h.Clients[2].WaitingForPlayers);
        AssertViewsMatchHost(h);

        PlayBattles(h, () => h.Session.Battles.Count == 0);
        Assert.True(g.S.Hero(a.Id) == null || g.S.Hero(d.Id) == null);
        Assert.Equal(day, g.S.Day);
        Assert.True(h.Clients[0].IsMyTurn || !g.S.Players.First(p => p.Index == p0).Alive);
        AssertViewsMatchHost(h);
    }

    [Fact]
    public void AttackIsRefusedWhileTheTargetIsInABattle() // 00426b30 from 00433440
    {
        var h = new Harness(2, Pest);
        var g = h.Session.Game;
        int p0 = h.Session.MapPlayerOf(0), p1 = h.Session.MapPlayerOf(1);
        var (a, m) = NextToMonster(h, 0, Array.Empty<Tile>());
        // Side 1's hero stands next to both the stack and side 0's hero.
        var b = g.S.Heroes.First(x => x.Owner == p1 && x.Alive && !x.Garrisoned);
        var spot = Around(m.Pos).First(t => Free(g, t) && Math.Max(Math.Abs(t.X - a.Pos.X), Math.Abs(t.Y - a.Pos.Y)) == 1);
        b.Pos = spot;
        b.InTown = -1;
        b.Garrisoned = false;
        b.MovePoints = 3000;
        b.Army = Army.Empty();
        b.Army[0] = new Stack("CREATURE_ARCHANGEL", 60);
        h.Tick(1000);

        Assert.True(h.Send(0, AdvNetCommands.Adv(new MoveHeroCmd(a.Id, m.Pos))), string.Join("; ", h.Rejections));
        Assert.NotNull(h.Session.BattleOf(p0));

        // The defending stack of a running combat: nothing happens.
        Assert.True(h.Send(1, AdvNetCommands.Adv(new MoveHeroCmd(b.Id, m.Pos))), string.Join("; ", h.Rejections));
        Assert.Null(h.Session.BattleOf(p1));
        Assert.Null(g.S.Battle);
        Assert.Equal(spot, b.Pos);
        // A hero whose owner holds a combat: nothing happens.
        Assert.True(h.Send(1, AdvNetCommands.Adv(new MoveHeroCmd(b.Id, a.Pos))), string.Join("; ", h.Rejections));
        Assert.Null(h.Session.BattleOf(p1));
        Assert.Single(h.Session.Battles);
        AssertViewsMatchHost(h);

        // Once the combat is over the attack goes ahead.
        PlayBattles(h, () => h.Session.Battles.Count == 0);
        Assert.True(h.Send(1, AdvNetCommands.Adv(new MoveHeroCmd(b.Id, a.Pos))), string.Join("; ", h.Rejections));
        Assert.NotNull(h.Session.BattleOf(p1));
        Assert.Same(h.Session.BattleOf(p0), h.Session.BattleOf(p1));
        AssertViewsMatchHost(h);
    }

    [Fact]
    public void OnePlayersPendingChoiceDoesNotBlockTheOthers() // join offer sent to that player only (008e1a80 → 00a7d0e0)
    {
        var h = new Harness(2, Pest);
        var g = h.Session.Game;
        var (a, m) = NextToMonster(h, 0, Array.Empty<Tile>(), "MONSTER_COURAGE_ALWAYS_JOIN");
        h.Tick(1000);

        Assert.True(h.Send(0, AdvNetCommands.Adv(new MoveHeroCmd(a.Id, m.Pos))), string.Join("; ", h.Rejections));
        Assert.NotNull(h.Clients[0].View.Choice);
        Assert.Null(h.Clients[1].View.Choice);
        Assert.Null(g.S.Choice);
        Assert.False(h.Send(0, AdvNetCommands.Adv(new EndTurnCmd())));   // its own choice comes first

        var r1 = h.Clients[1];
        var hero1 = r1.View.Heroes.First(x => x.Owner == r1.MyPlayer && x.Alive && !x.Garrisoned);
        var to = Reachable(r1, hero1);
        Assert.NotNull(to);
        Assert.True(h.Send(1, AdvNetCommands.Adv(new MoveHeroCmd(hero1.Id, to!.Value))), string.Join("; ", h.Rejections));
        AssertViewsMatchHost(h);

        int before = a.Army.Live().Sum(s => s.Count);
        Assert.True(h.Send(0, AdvNetCommands.Adv(new AnswerChoiceCmd(true))), string.Join("; ", h.Rejections));
        Assert.Null(h.Clients[0].View.Choice);
        Assert.True(a.Army.Live().Sum(s => s.Count) > before);
        Assert.False(Alive(g, m));
        AssertViewsMatchHost(h);
    }

    [Fact]
    public void TurnTimerSparesAFightingPlayerAndChargesItsOwnCombatTime() // 00998520, 0098e4e0, 004e0f10
    {
        var h = new Harness(2, s => { Pest(s); s.TimeLimitMinutes = 1; });
        var g = h.Session.Game;
        int p0 = h.Session.MapPlayerOf(0), p1 = h.Session.MapPlayerOf(1);
        var (a, m) = NextToMonster(h, 0, Array.Empty<Tile>());
        int day = g.S.Day;
        Assert.True(h.Send(0, AdvNetCommands.Adv(new MoveHeroCmd(a.Id, m.Pos))), string.Join("; ", h.Rejections));
        var net = h.Session.BattleOf(p0)!;
        // Past the tactics phase (no army is active there): side 0's unit is to act.
        var ai = new GreedyBattleAI { MaxCandidates = 8 };
        for (int i = 0; i < 20 && net.Battle.State.Phase != BattlePhase.Combat && h.Clients[0].Battle is { IsMyTurn: true } rb; i++)
            Assert.True(h.Send(0, AdvNetCommands.Battle(ai.Decide(rb.Battle, rb.MySide) with { Side = rb.MySide })));
        Assert.Equal(BattlePhase.Combat, net.Battle.State.Phase);
        Assert.Equal(0, net.Battle.SideToAct);

        // Time runs out: side 1 ends, side 0 holds a combat and keeps fighting; the day waits.
        h.Tick(70_000);
        Assert.Contains(p1, h.Session.Simultaneous!.Ended);
        Assert.DoesNotContain(p0, h.Session.Simultaneous.Ended);
        Assert.NotNull(h.Session.BattleOf(p0));
        Assert.Equal(day, g.S.Day);

        // It was side 0's move all along, so the combat credits nothing back: its time is up once the battle ends.
        PlayBattles(h, () => h.Session.Battles.Count == 0);
        Assert.True(net.ActingMs[0] >= 69_000, $"acting {net.ActingMs[0]} {net.ActingMs[1]} started {net.StartedMs}");
        h.Tick(1000);
        Assert.Equal(day + 1, g.S.Day);
        AssertViewsMatchHost(h);
    }

    [Fact]
    public void ReconnectingClientRebuildsTheRunningBattle() // replay of the side's updates (docs/MULTIPLAYER.md)
    {
        var h = new Harness(2, Pest);
        int p0 = h.Session.MapPlayerOf(0);
        var (a, m) = NextToMonster(h, 0, Array.Empty<Tile>());
        h.Tick(1000);
        Assert.True(h.Send(0, AdvNetCommands.Adv(new MoveHeroCmd(a.Id, m.Pos))), string.Join("; ", h.Rejections));
        var ai = new GreedyBattleAI { MaxCandidates = 8 };
        if (h.Clients[0].Battle is { IsMyTurn: true } b)
            Assert.True(h.Send(0, AdvNetCommands.Battle(ai.Decide(b.Battle, b.MySide) with { Side = b.MySide })));
        Assert.NotNull(h.Session.BattleOf(p0));

        var replay = new AdventureNetReplica(Env.Maps, new GameStarted(0, h.Setups[0]));
        foreach (var u in h.Received[0]) replay.Apply(u);
        Assert.False(replay.Desynced);
        Assert.NotNull(replay.Battle);
        Assert.Equal(h.Session.BattleOf(p0)!.Battle.State.ComputeHash(), replay.Battle!.Battle.State.ComputeHash());
        Assert.Equal(JsonDiff.CanonicalHash(h.Session.ViewFor(p0)), JsonDiff.CanonicalHash(JsonNode.Parse(replay.View.Serialize())));
    }
}
