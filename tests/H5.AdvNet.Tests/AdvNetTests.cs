using System.Text.Json.Nodes;
using H5.AdvNet;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Combat;
using H5.Net;
using Message = H5.Net.Message;

namespace H5.AdvNet.Tests;

public class JsonDiffTests
{
    [Fact]
    public void PatchReproducesTargetAndHashIgnoresKeyOrder()
    {
        var rnd = new Random(3);
        JsonNode Make(int depth)
        {
            if (depth == 0 || rnd.Next(3) == 0) return rnd.Next(3) switch { 0 => JsonValue.Create(rnd.Next(100))!, 1 => JsonValue.Create("s" + rnd.Next(9))!, _ => JsonValue.Create(rnd.Next(2) == 0)! };
            if (rnd.Next(2) == 0)
            {
                var o = new JsonObject();
                for (int i = 0; i < rnd.Next(5); i++) o["k" + rnd.Next(8)] = Make(depth - 1);
                return o;
            }
            var a = new JsonArray();
            for (int i = 0; i < rnd.Next(5); i++) a.Add(Make(depth - 1));
            return a;
        }
        for (int i = 0; i < 300; i++)
        {
            var a = Make(4);
            var b = Make(4);
            var patched = JsonDiff.Apply(a.DeepClone(), JsonDiff.Diff(a, b));
            Assert.True(JsonNode.DeepEquals(patched, b), $"case {i}");
            Assert.Equal(JsonDiff.CanonicalHash(b), JsonDiff.CanonicalHash(patched));
        }
        var x = JsonNode.Parse("""{"a":1,"b":{"c":2,"d":3}}""");
        var y = JsonNode.Parse("""{"b":{"d":3,"c":2},"a":1}""");
        Assert.Equal(JsonDiff.CanonicalHash(x), JsonDiff.CanonicalHash(y));
    }
}

public class ViewFilterTests
{
    [Fact]
    public void ViewsHideOtherPlayersSecrets()
    {
        var h = new Harness(2);
        var full = h.Session.Game.S;
        var map = h.Session.Map;
        int p0 = h.Session.MapPlayerOf(0), p1 = h.Session.MapPlayerOf(1);
        var v0 = AdvState.Deserialize(ViewFilter.ViewFor(full, map, p0).ToJsonString());

        Assert.Equal(full.Players.Find(p => p.Index == p0)!.Purse.R, v0.Players.Find(p => p.Index == p0)!.Purse.R);
        Assert.All(v0.Players.Where(p => p.Index != p0), p =>
        {
            Assert.All(p.Purse.R, r => Assert.Equal(0, r));
            Assert.All(p.Explored, e => Assert.Empty(e));
            Assert.All(p.Tavern, t => Assert.Null(t));
        });
        Assert.Empty(v0.HeroPool);
        Assert.Equal(0UL, v0.Rng.S0 | v0.Rng.S1 | v0.Rng.S2 | v0.Rng.S3);
        // Enemy heroes far away (A2M1 starts in opposite corners) are not in the view at all.
        var mine = full.Players.Find(p => p.Index == p0)!;
        foreach (var hero in full.Heroes.Where(x => x.Owner != p0 && x.Alive))
        {
            bool seen = ViewFilter.Sees(map, mine, hero.Pos);
            Assert.Equal(seen, v0.Heroes.Any(x => x.Id == hero.Id));
        }
        // Visible neutral guards are banded; hidden objects are pristine.
        foreach (var (idx, o) in v0.Objects)
        {
            var real = full.Objects[idx];
            if (!ViewFilter.Sees(map, mine, map.Objects[idx].Pos)) { Assert.Empty(o.Guards); Assert.Equal(-1, o.Owner); continue; }
            Assert.Equal(real.Guards.Select(g => ViewFilter.Band(g.Count)), o.Guards.Select(g => g.Count));
            Assert.Equal(0, o.Amount);
        }

        // Event filtering: another player's resource changes are never delivered.
        var events = new List<AdvEvent> { new ResourcesChanged(p1, new int[7], new int[7]), new ResourcesChanged(p0, new int[7], new int[7]) };
        var filtered = ViewFilter.EventsFor(events, full, map, p0, p1, new Dictionary<int, (int, Tile)>());
        Assert.Single(filtered);
        Assert.Equal(p0, ((ResourcesChanged)filtered[0]).Player);
    }

    [Fact]
    public void ArmyBandsMatchOriginalSizes()
    {
        Assert.Equal(1, ViewFilter.Band(4));
        Assert.Equal(5, ViewFilter.Band(9));
        Assert.Equal(20, ViewFilter.Band(49));
        Assert.Equal(1000, ViewFilter.Band(5000));
        Assert.Equal(0, ViewFilter.Band(0));
    }
}

public class SessionTests
{
    /// <summary>Plays until the condition holds, letting each client's bot act on its own view.</summary>
    static void Play(Harness h, Func<bool> until, int maxSteps = 4000)
    {
        var bots = h.Clients.Select((_, i) => new ClientBot(100 + i)).ToArray();
        for (int step = 0; step < maxSteps && !until(); step++)
        {
            bool acted = false;
            for (int s = 0; s < h.Clients.Length; s++)
                if (bots[s].Decide(h.Clients[s]) is { } cmd) { h.Send(s, cmd); acted = true; break; }
            if (!acted) h.Tick(1000);
            Assert.All(h.Clients, c => Assert.False(c.Desynced));
            if (h.Session.Game.S.Finished) break;
        }
    }

    [Fact]
    public void TwoHumansPlaySeveralDaysWithoutDesync()
    {
        var h = new Harness(2);
        Play(h, () => h.Session.Game.S.Day >= 4);
        Assert.True(h.Session.Game.S.Day >= 4, $"reached day {h.Session.Game.S.Day}; rejections: {string.Join("; ", h.Rejections.Take(5))}");
        foreach (var c in h.Clients)
        {
            Assert.False(c.Desynced);
            Assert.Equal(h.Session.Game.S.Day, c.View.Day);
            // Each client's view equals what the host would show that player now.
            Assert.Equal(JsonDiff.CanonicalHash(ViewFilter.ViewFor(h.Session.Game.S, h.Session.Map, c.MyPlayer)),
                JsonDiff.CanonicalHash(JsonNode.Parse(c.View.Serialize())));
        }
        // The other human's purse never reached this client.
        var p1 = h.Clients[1].MyPlayer;
        Assert.All(h.Clients[0].View.Players.Find(p => p.Index == p1)!.Purse.R, r => Assert.Equal(0, r));
        Assert.DoesNotContain(h.Received[0].SelectMany(u => u.Events["events"]!.AsArray()),
            e => (string?)e!["$t"] == "resources" && (int?)e["Player"] == p1);
    }

    [Fact]
    public void HumanVersusHumanBattleIsFoughtByBothClients()
    {
        var h = new Harness(2);
        var g = h.Session.Game;
        int p0 = h.Session.MapPlayerOf(0), p1 = h.Session.MapPlayerOf(1);
        // Put P1's hero next to P0's main hero (test setup on the host).
        var a = g.S.Heroes.First(x => x.Owner == p0 && x.Alive && !x.Garrisoned);
        var d = g.S.Heroes.First(x => x.Owner == p1 && x.Alive && !x.Garrisoned);
        var floor = g.Map.Floor(a.Pos.Floor)!;
        var spot = Enumerable.Range(-1, 3).SelectMany(dx => Enumerable.Range(-1, 3).Select(dy => a.Pos.Offset(dx, dy)))
            .First(t => t != a.Pos && floor.InBounds(t.X, t.Y) && !floor.IsBlocked(t.X, t.Y) && g.FindPath(a, t) is { Count: > 0 });
        d.Pos = spot;
        d.InTown = -1;
        h.Tick(1000); // republish views with the moved hero

        Assert.True(h.Send(0, AdvNetCommands.Adv(new MoveHeroCmd(a.Id, spot))), string.Join("; ", h.Rejections));
        Assert.NotNull(h.Session.ActiveBattle);
        Assert.NotNull(h.Clients[0].Battle);
        Assert.NotNull(h.Clients[1].Battle);
        Assert.Equal(0, h.Clients[0].Battle!.MySide);
        Assert.Equal(1, h.Clients[1].Battle!.MySide);

        var ai = new GreedyBattleAI { MaxCandidates = 8 };
        for (int step = 0; step < 3000 && h.Session.ActiveBattle != null; step++)
        {
            bool acted = false;
            for (int s = 0; s < 2; s++)
                if (h.Clients[s].Battle is { IsMyTurn: true } b)
                {
                    Assert.True(h.Send(s, AdvNetCommands.Battle(ai.Decide(b.Battle, b.MySide) with { Side = b.MySide })), string.Join("; ", h.Rejections));
                    acted = true;
                    break;
                }
            Assert.True(acted, "nobody could act in the battle");
            Assert.All(h.Clients, c => Assert.False(c.Desynced));
        }
        Assert.Null(h.Session.ActiveBattle);
        Assert.Null(g.S.Battle);
        Assert.All(h.Clients, c => Assert.Null(c.Battle));
        // One of the two heroes lost.
        Assert.True(!a.Alive || !d.Alive || g.S.Hero(a.Id) == null || g.S.Hero(d.Id) == null);
    }

    [Fact]
    public void ComputerAttackOnHumanPausesTheAiTurnForAnInteractiveBattle()
    {
        var h = new Harness(2);
        var g = h.Session.Game;
        int p0 = h.Session.MapPlayerOf(0);
        var human = g.S.Heroes.First(x => x.Owner == p0 && x.Alive && !x.Garrisoned);
        var aiPlayer = g.S.Players.First(p => !h.Clients.Any(c => c.MyPlayer == p.Index));
        var aiHero = g.S.Heroes.First(x => x.Owner == aiPlayer.Index && x.Alive);
        var floor = g.Map.Floor(human.Pos.Floor)!;
        var spot = Enumerable.Range(-1, 3).SelectMany(dx => Enumerable.Range(-1, 3).Select(dy => human.Pos.Offset(dx, dy)))
            .First(t => t != human.Pos && floor.InBounds(t.X, t.Y) && !floor.IsBlocked(t.X, t.Y) && !g.S.Heroes.Any(o => o.Alive && o.Pos == t));
        aiHero.Pos = spot;
        aiHero.InTown = -1;
        aiHero.Garrisoned = false;
        aiHero.Army = Army.Empty();
        aiHero.Army[0] = new Stack("CREATURE_ARCHANGEL", 300);
        human.Army = Army.Empty();
        human.Army[0] = new Stack("CREATURE_PEASANT", 1);

        // Both humans end their turns; the computer players then move and one attacks P0's hero.
        var ai = new GreedyBattleAI { MaxCandidates = 8 };
        bool fought = false;
        for (int step = 0; step < 400 && !fought; step++)
        {
            var active = h.Clients.FirstOrDefault(c => c.IsMyTurn);
            if (h.Clients[0].Battle is { } b)
            {
                fought = true;
                Assert.Equal(1, b.MySide);              // P0 defends
                Assert.NotNull(h.Session.ActiveBattle);
                while (h.Session.ActiveBattle != null)
                {
                    var rb = h.Clients[0].Battle;
                    Assert.True(rb is { IsMyTurn: true }, "defender should be the only human to act");
                    Assert.True(h.Send(0, AdvNetCommands.Battle(ai.Decide(rb!.Battle, rb.MySide) with { Side = rb.MySide })), string.Join("; ", h.Rejections));
                }
                break;
            }
            if (active == null) { h.Tick(1000); continue; }
            Assert.True(h.Send(active.MySide, AdvNetCommands.Adv(new EndTurnCmd())), string.Join("; ", h.Rejections));
        }
        Assert.True(fought, "the computer player never attacked");
        Assert.Null(g.S.Battle);
        Assert.All(h.Clients, c => Assert.False(c.Desynced));
        // The computer turns carried on afterwards: eventually a human moves again.
        for (int i = 0; i < 50 && !h.Clients.Any(c => c.IsMyTurn); i++) h.Tick(1000);
        Assert.True(h.Clients.Any(c => c.IsMyTurn) || g.S.Finished);
    }

    [Fact]
    public void TurnTimeLimitEndsTheTurn()
    {
        var h = new Harness(2, s => s.TimeLimitMinutes = 1);
        int first = h.Session.Game.S.CurrentPlayer;
        h.Tick(30_000);
        Assert.Equal(first, h.Session.Game.S.CurrentPlayer);
        h.Tick(31_000);
        Assert.NotEqual(first, h.Session.Game.S.CurrentPlayer);
        Assert.All(h.Clients, c => Assert.False(c.Desynced));
    }

    [Fact]
    public void ChatScopes()
    {
        var h = new Harness(2);
        Assert.True(h.Send(0, AdvNetCommands.Chat("/P1 secret")));
        Assert.True(h.Send(1, AdvNetCommands.Chat("hello all")));
        var c0 = h.Clients[0].Chat.ToList();
        var c1 = h.Clients[1].Chat.ToList();
        Assert.Contains(c1, l => l.Text == "secret" && l.Scope == "private");
        Assert.Contains(c0, l => l.Text == "secret");
        Assert.Contains(c0, l => l.Text == "hello all");
        Assert.False(h.Send(0, AdvNetCommands.Chat("/nobody hi")));
    }

    [Fact]
    public void GhostsMoveSpendingEnergyDuringEnemyTurns()
    {
        var h = new Harness(2, s => s.GhostMode = true);
        var waiting = h.Clients.Single(c => !c.IsMyTurn);
        int side = waiting.MySide;
        var ghosts = waiting.Ghosts!["ghosts"]!.AsArray().Where(g => (int)g!["owner"]! == waiting.MyPlayer).ToList();
        Assert.NotEmpty(ghosts);
        var gh = ghosts[0]!;
        int energy = (int)gh["energy"]!;
        Assert.True(energy >= Env.GhostData.EnergyReviveBase);
        Assert.True(h.Send(side, AdvNetCommands.GhostMove((int)gh["hero"]!, (int)gh["x"]! + 2, (int)gh["y"]!, (int)gh["floor"]!)), string.Join(";", h.Rejections));
        var after = waiting.Ghosts!["ghosts"]!.AsArray().First(g => (int)g!["hero"]! == (int)gh["hero"]!)!;
        Assert.True((int)after["energy"]! < energy);
        // The active player cannot use ghosts.
        var active = h.Clients.Single(c => c.IsMyTurn);
        Assert.False(h.Send(active.MySide, AdvNetCommands.GhostMove((int)gh["hero"]!, 0, 0, 0)));
        // Energy regenerates per minute of the enemy turn.
        h.Tick(61_000);
        var regen = waiting.Ghosts!["ghosts"]!.AsArray().First(g => (int)g!["hero"]! == (int)gh["hero"]!)!;
        Assert.True((int)regen["energy"]! > (int)after["energy"]!);
    }
}

public class LoopbackTests
{
    [Fact]
    public async Task NetworkedGameAndReconnectReplay()
    {
        var factory = new AdventureSessionFactory(Env.Maps);
        await using var server = new LobbyServer(new IGameSessionFactory[] { factory });
        server.Start(0, System.Net.IPAddress.Loopback);

        var clients = new NetClient[2];
        var replicas = new AdventureNetReplica?[2];
        var inbox = new List<Message>[] { new(), new() };
        string? token0 = null;
        for (int i = 0; i < 2; i++)
        {
            int k = i;
            clients[i] = new NetClient();
            clients[i].MessageReceived += m =>
            {
                switch (m)
                {
                    case Welcome w when k == 0: token0 = w.ResumeToken; break;
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
            Assert.True(until(), "timed out: " + string.Join(",", inbox[0].TakeLast(5).Select(m => m.GetType().Name)));
        }
        await Pump(() => clients.All(c => c.Connected));
        clients[0].Send(new CreateRoom("adv", "adventure", 2, null, Env.Settings()));
        await Pump(() => inbox[0].OfType<RoomState>().Any());
        var room = inbox[0].OfType<RoomState>().First().RoomId;
        clients[1].Send(new JoinRoom(room, null));
        clients[1].Send(new SetReady(true));
        await Pump(() => inbox[0].OfType<RoomState>().Any(r => r.Players.Count == 2 && r.Players[1].Ready));
        clients[0].Send(new StartGame());
        await Pump(() => replicas.All(r => r != null), 120000);

        var bots = new[] { new ClientBot(1), new ClientBot(2) };
        var end = DateTime.UtcNow.AddMinutes(3);
        while (replicas[0]!.View.Day < 3 && DateTime.UtcNow < end)
        {
            for (int i = 0; i < 2; i++)
            {
                clients[i].Poll();
                if (clients[i].PendingCommands == 0 && bots[i].Decide(replicas[i]!) is { } cmd) clients[i].SendCommand(cmd);
            }
            Assert.All(replicas, r => Assert.False(r!.Desynced));
            await Task.Delay(1);
        }
        Assert.True(replicas[0]!.View.Day >= 3, $"day {replicas[0]!.View.Day}");

        // Reconnect as side 0 from scratch: the per-side log must rebuild an identical view.
        await Pump(() => clients[0].PendingCommands == 0 && clients[1].PendingCommands == 0);
        await Task.Delay(200);
        foreach (var c in clients) c.Poll();
        var expected = JsonDiff.CanonicalHash(JsonNode.Parse(replicas[0]!.View.Serialize()));
        AdventureNetReplica? fresh = null;
        bool resumed = false;
        var conn = await Connection.ConnectAsync("127.0.0.1", server.Port);
        conn.Received += (_, m) =>
        {
            lock (inbox) switch (m)
            {
                case GameStarted gs: fresh = new AdventureNetReplica(Env.Maps, gs); break;
                case GameUpdate u: fresh!.Apply(u); break;
                case Resumed: resumed = true; break;
            }
        };
        conn.Start();
        conn.Send(new Hello(ProtocolInfo.Version, "P0", "", token0, -1));
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!resumed && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(resumed);
        lock (inbox)
        {
            Assert.False(fresh!.Desynced);
            Assert.Equal(expected, JsonDiff.CanonicalHash(JsonNode.Parse(fresh.View.Serialize())));
        }
        await conn.DisposeAsync();
        foreach (var c in clients) await c.DisposeAsync();
    }
}
