using System.Text.Json.Nodes;
using H5.AdvNet;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Net;
using Xunit;
using Message = H5.Net.Message;

namespace H5.AdvNet.Tests;

/// <summary>Simultaneous turns (docs/rules/SIMULTANEOUS.md).</summary>
public class SimultaneousTests
{
    static bool MyTurn(Harness h, int side) => h.Clients[side].IsMyTurn;

    static void AssertViewsMatchHost(Harness h)
    {
        foreach (var c in h.Clients)
        {
            Assert.False(c.Desynced);
            var expected = ViewFilter.ViewFor(h.Session.Game.S, h.Session.Map, c.MyPlayer, h.Session.Simultaneous?.ViewCurrentFor(c.MyPlayer));
            Assert.Equal(JsonDiff.CanonicalHash(expected), JsonDiff.CanonicalHash(JsonNode.Parse(c.View.Serialize())));
        }
    }

    [Fact]
    public void AreaLimitIsMaxMovementPlusLargestMovementBonus() // 009c2660, RPGStats adventure +0x18..+0x2c
    {
        var h = new Harness(2, s => s.SimultaneousTurns = true);
        var g = h.Session.Game;
        var hero = g.S.Heroes.First(x => x.Owner == h.Session.MapPlayerOf(0) && x.Alive);
        var adv = g.Db.Stats.Root.Element("adventure")!;
        int bonus = new[] { "VisitFountainOfYouthMovementBonus", "VisitRallyFlagMovementBonus", "VisitOasisMovementBonus", "VisitStablesMovementBonus", "LighthouseMaxSeaMovementBonus" }
            .Max(f => int.Parse(adv.Element(f)!.Value));
        Assert.True(bonus > 0);
        Assert.Equal(g.ComputeMaxMovePoints(hero) + bonus, g.SimultaneousAreaLimit(hero));

        int limit = g.SimultaneousAreaLimit(hero);
        var area = g.ReachableCosts(hero, limit);
        Assert.Equal(0, area[hero.Pos]);
        Assert.All(area.Values, c => Assert.InRange(c, 0, limit));
        Assert.True(area.Count > 50);
        // A smaller limit gives a subset with the same costs.
        var small = g.ReachableCosts(hero, limit / 2);
        Assert.All(small, kv => Assert.Equal(kv.Value, area[kv.Key]));
        Assert.True(small.Count < area.Count);
    }

    [Fact]
    public void AdjacentHumansConflictAndTheGameStartsSequential() // 0098f7a0 → 0098fa60 + FUN_009c22f0(0,1)
    {
        var h = new Harness(2, s => { s.SimultaneousTurns = true; s.DevStartAdjacent = true; });
        var sim = h.Session.Simultaneous!;
        Assert.False(sim.Active);
        Assert.False(sim.InPhase);
        Assert.NotNull(sim.LastConflict);
        Assert.Contains("Simultaneous Turns: area of player", sim.LastConflict!.ToString());
        // Every human was told (message box, no approval needed) ...
        foreach (var c in h.Clients) Assert.Contains((false, true), c.SimultaneousNotices);
        // ... and exactly one human has the turn now.
        Assert.Equal(1, h.Clients.Count(c => c.IsMyTurn));
        AssertViewsMatchHost(h);
    }

    [Fact]
    public void ConflictCheckIsReadOnlyAndFindsAdjacentHeroes()
    {
        var h = new Harness(2, s => s.DevStartAdjacent = true);
        var g = h.Session.Game;
        var before = g.S.ComputeHash();
        var humans = new HashSet<int> { h.Session.MapPlayerOf(0), h.Session.MapPlayerOf(1) };
        var c = g.FindSimultaneousConflict(humans);
        Assert.NotNull(c);
        Assert.NotEqual(c!.AreaOwner, c.Intruder);
        Assert.Equal(before, g.S.ComputeHash());
        // One human alone never conflicts with itself.
        Assert.Null(g.FindSimultaneousConflict(new HashSet<int> { h.Session.MapPlayerOf(0) }));
    }

    [Fact]
    public void HumansMoveAtOnceThenComputersThenANewDay() // 00998060, 00998760, 00998520
    {
        var h = new Harness(2, s => { s.SimultaneousTurns = true; s.SimultaneousAlwaysOn = true; });
        var sim = h.Session.Simultaneous!;
        Assert.True(sim.Active);
        Assert.True(sim.InPhase);
        Assert.True(MyTurn(h, 0));
        Assert.True(MyTurn(h, 1));
        int day = h.Session.Game.S.Day;

        // Side 1 acts while side 0 has not ended.
        var r1 = h.Clients[1];
        var hero = r1.View.Heroes.First(x => x.Owner == r1.MyPlayer && x.Alive && !x.Garrisoned);
        Tile? target = null;
        foreach (var t in Enumerable.Range(-3, 7).SelectMany(dx => Enumerable.Range(-3, 7).Select(dy => hero.Pos.Offset(dx, dy))))
        {
            var f = r1.Map.Map.Floor(t.Floor);
            if (t == hero.Pos || f == null || !f.InBounds(t.X, t.Y) || f.IsBlocked(t.X, t.Y)) continue;
            if (r1.Game.FindPath(hero, t) is { Count: > 1 }) { target = t; break; }
        }
        Assert.NotNull(target);
        Assert.True(h.Send(1, AdvNetCommands.Adv(new MoveHeroCmd(hero.Id, target!.Value))), string.Join("; ", h.Rejections));
        Assert.True(MyTurn(h, 0));
        AssertViewsMatchHost(h);

        // Side 0 ends: it waits for the others; side 1 still plays.
        Assert.True(h.Send(0, AdvNetCommands.Adv(new EndTurnCmd())), string.Join("; ", h.Rejections));
        Assert.False(MyTurn(h, 0));
        Assert.True(h.Clients[0].WaitingForPlayers);
        Assert.True(MyTurn(h, 1));
        Assert.False(h.Send(0, AdvNetCommands.Adv(new EndTurnCmd())));
        Assert.Equal(day, h.Session.Game.S.Day);

        // Side 1 ends: computer players move, a new day starts and both humans play again.
        Assert.True(h.Send(1, AdvNetCommands.Adv(new EndTurnCmd())), string.Join("; ", h.Rejections));
        Assert.Equal(day + 1, h.Session.Game.S.Day);
        Assert.True(sim.InPhase);
        Assert.True(MyTurn(h, 0));
        Assert.True(MyTurn(h, 1));
        Assert.False(h.Clients[0].WaitingForPlayers);
        AssertViewsMatchHost(h);
    }

    [Fact]
    public void SharedPhaseTimerEndsEveryHumansTurn() // 00998520
    {
        var h = new Harness(2, s => { s.SimultaneousTurns = true; s.SimultaneousAlwaysOn = true; s.TimeLimitMinutes = 1; });
        int day = h.Session.Game.S.Day;
        h.Tick(30_000);
        Assert.Equal(day, h.Session.Game.S.Day);
        h.Tick(31_000);
        Assert.Equal(day + 1, h.Session.Game.S.Day);
        Assert.True(MyTurn(h, 0) && MyTurn(h, 1));
        AssertViewsMatchHost(h);
    }

    [Fact]
    public void DeferredBreakWaitsForEveryApproval() // 009c25c0, 004289a0, 009c2270
    {
        var h = new Harness(2, s => { s.SimultaneousTurns = true; s.SimultaneousAlwaysOn = true; });
        var sim = h.Session.Simultaneous!;
        // Switch to the original policy's flow by hand: a deferred conflict check after a battle-like trigger.
        var probe = new SimultaneousTurns(h.Session.Game, mp => mp == h.Session.MapPlayerOf(0) || mp == h.Session.MapPlayerOf(1), new OriginalSimultaneousPolicy());
        // Make the two humans adjacent so their areas meet.
        var g = h.Session.Game;
        var a = g.S.Heroes.First(x => x.Owner == h.Session.MapPlayerOf(0) && x.Alive);
        var d = g.S.Heroes.First(x => x.Owner == h.Session.MapPlayerOf(1) && x.Alive);
        d.Pos = a.Pos.Offset(1, 0);
        Assert.Equal(-1, probe.Step());             // phase starts, immediate check → breaks at once
        Assert.False(probe.Active);
        Assert.All(probe.Notices, n => Assert.Equal((false, true), (n.Approve, n.Message)));

        // The phase begins while the policy ignores the meeting, then a deferred trigger finds it.
        var toggle = new TogglePolicy();
        var deferred = new SimultaneousTurns(g, mp => mp == h.Session.MapPlayerOf(0) || mp == h.Session.MapPlayerOf(1), toggle);
        deferred.Step();
        Assert.True(deferred.InPhase);
        toggle.BreakOnIntersection = true;
        deferred.OnCommand(new ResolveBattleCmd(0, new(), new()));
        deferred.Step();
        Assert.True(deferred.Active);
        Assert.Equal(2, deferred.Pending.Count);
        Assert.All(deferred.Notices, n => Assert.Equal((true, true), (n.Approve, n.Message)));
        Assert.Null(deferred.Approve(h.Session.MapPlayerOf(0)));
        Assert.True(deferred.Active);
        Assert.NotNull(deferred.Approve(h.Session.MapPlayerOf(0)));
        Assert.Null(deferred.Approve(h.Session.MapPlayerOf(1)));
        Assert.False(deferred.Active);
        Assert.False(deferred.InPhase);
        _ = sim;
    }

    sealed class TogglePolicy : ISimultaneousPolicy { public bool BreakOnIntersection { get; set; } }

    [Fact]
    public async Task TwoClientsPlaySimultaneousTurnsThroughTheLobbyServer()
    {
        var factory = new AdventureSessionFactory(Env.Maps);
        await using var server = new LobbyServer(new IGameSessionFactory[] { factory });
        server.Start(0, System.Net.IPAddress.Loopback);
        var clients = new NetClient[2];
        var replicas = new AdventureNetReplica?[2];
        var inbox = new List<Message>[] { new(), new() };
        for (int i = 0; i < 2; i++)
        {
            int k = i;
            clients[i] = new NetClient();
            clients[i].MessageReceived += m =>
            {
                switch (m)
                {
                    case GameStarted gs: replicas[k] = new AdventureNetReplica(Env.Maps, gs); break;
                    case GameUpdate u: replicas[k]!.Apply(u); break;
                }
                inbox[k].Add(m);
            };
            await clients[i].ConnectAsync("127.0.0.1", server.Port, $"P{i}", "");
        }
        async Task Pump(Func<bool> until, int ms = 60000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (!until() && DateTime.UtcNow < end) { foreach (var c in clients) c.Poll(); await Task.Delay(2); }
            Assert.True(until(), "timed out");
        }
        await Pump(() => clients.All(c => c.Connected));
        clients[0].Send(new CreateRoom("simul", "adventure", 2, null, Env.Settings(s => { s.SimultaneousTurns = true; s.SimultaneousAlwaysOn = true; })));
        await Pump(() => inbox[0].OfType<RoomState>().Any());
        var room = inbox[0].OfType<RoomState>().First().RoomId;
        clients[1].Send(new JoinRoom(room, null));
        clients[1].Send(new SetReady(true));
        await Pump(() => inbox[0].OfType<RoomState>().Any(r => r.Players.Count == 2 && r.Players[1].Ready));
        clients[0].Send(new StartGame());
        await Pump(() => replicas.All(r => r != null), 120000);
        Assert.True(replicas[0]!.IsMyTurn && replicas[1]!.IsMyTurn, "both humans play the first day at once");

        var bots = new[] { new ClientBot(1), new ClientBot(2) };
        var end = DateTime.UtcNow.AddMinutes(3);
        while (replicas[0]!.View.Day < 3 && DateTime.UtcNow < end)
        {
            for (int i = 0; i < 2; i++)
            {
                clients[i].Poll();
                while (replicas[i]!.SimultaneousNotices.TryDequeue(out var n))
                    if (n.Approve) clients[i].SendCommand(AdvNetCommands.SimultaneousApprove());
                if (clients[i].PendingCommands == 0 && bots[i].Decide(replicas[i]!) is { } cmd) clients[i].SendCommand(cmd);
            }
            Assert.All(replicas, r => Assert.False(r!.Desynced));
            await Task.Delay(1);
        }
        Assert.True(replicas[0]!.View.Day >= 3, $"day {replicas[0]!.View.Day}");
        foreach (var c in clients) await c.DisposeAsync();
    }
}
