using System.Net;
using System.Text.Json.Nodes;
using H5.Combat;
using H5.Core.Data;
using H5.Core.IO;
using H5.Duel;
using H5.Net;

namespace H5.Tests;

public class DuelNetworkTests
{
    static readonly Lazy<GameDatabase> Db = new(() => GameDatabase.Load(PakFileSystem.OpenInstall(GameInstall.Locate())));

    sealed class Player(string name, int preset)
    {
        public readonly NetClient Net = new();
        public readonly List<Message> Inbox = new();
        public readonly string Name = name;
        public readonly int Preset = preset;
        public DuelReplica? Replica;
        public GameEnded? Ended;
        public bool SentEndTactics;
        public int Rejections;
    }

    static async Task Pump(Player p, Func<bool> until, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!until())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{p.Name}: {string.Join(",", p.Inbox.Select(m => m.GetType().Name))}");
            p.Net.Poll();
            await Task.Delay(2);
        }
    }

    [Fact]
    public async Task TwoClientsPlayAFullDuelInLockstep()
    {
        var db = Db.Value;
        await using var server = new LobbyServer(new[] { new DuelSessionFactory(db) });
        server.Start(0, IPAddress.Loopback);

        var alice = new Player("Alice", 0);
        var bob = new Player("Bob", 3);
        var players = new[] { alice, bob };
        foreach (var p in players)
        {
            p.Net.MessageReceived += m =>
            {
                switch (m)
                {
                    case GameStarted gs: p.Replica = new DuelReplica(db, gs); break;
                    case GameUpdate up: p.Replica!.Apply(up); break;
                    case GameEnded ge: p.Ended = ge; break;
                    case CommandRejected: p.Rejections++; break;
                    default: p.Inbox.Add(m); break;
                }
            };
            await p.Net.ConnectAsync("127.0.0.1", server.Port, p.Name, "");
            await Pump(p, () => p.Net.Connected);
        }

        alice.Net.Send(new CreateRoom("duel", "duel", 2, null, null));
        await Pump(alice, () => alice.Inbox.OfType<RoomState>().Any());
        var roomId = alice.Inbox.OfType<RoomState>().First().RoomId;
        bob.Net.Send(new JoinRoom(roomId, null));
        alice.Net.Send(new SetPlayerOptions(new JsonObject { ["preset"] = alice.Preset }));
        bob.Net.Send(new SetPlayerOptions(new JsonObject { ["preset"] = bob.Preset }));
        bob.Net.Send(new SetReady(true));
        await Pump(alice, () => alice.Inbox.OfType<RoomState>().Any(r => r.Players.Count == 2 && r.Players[1].Ready && r.Players[1].Options != null));
        alice.Net.Send(new StartGame());
        foreach (var p in players) await Pump(p, () => p.Replica != null);
        Assert.Equal(new[] { 0, 3 }, alice.Replica!.Presets);

        // Each client decides with the AI on its own replica, exactly as a human UI would.
        var ai = new GreedyBattleAI { MaxCandidates = 12 };
        int sent = 0;
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (players.Any(p => p.Ended == null) && DateTime.UtcNow < deadline)
        {
            foreach (var p in players)
            {
                p.Net.Poll();
                var r = p.Replica!;
                if (p.Ended != null || r.Desynced) continue;
                var s = r.Battle.State;
                if (s.Phase == BattlePhase.Tactics)
                {
                    if (!p.SentEndTactics) { p.Net.SendCommand(JsonNode.Parse(MessageJson.Serialize(new EndTacticsCmd()))!); p.SentEndTactics = true; }
                    continue;
                }
                // Only act when it's our turn and our previous command has been applied.
                if (!r.IsMyTurn || p.Net.PendingCommands > 0) continue;
                var cmd = ai.Decide(r.Battle, r.MySide);
                p.Net.SendCommand(JsonNode.Parse(MessageJson.Serialize(cmd))!);
                sent++;
            }
            await Task.Delay(1);
        }

        foreach (var p in players)
        {
            Assert.False(p.Replica!.Desynced, $"{p.Name} desynced");
            Assert.NotNull(p.Ended);
            Assert.Equal(0, p.Rejections);
        }
        Assert.Equal(alice.Replica!.Battle.State.ComputeHash(), bob.Replica!.Battle.State.ComputeHash());
        Assert.Equal(alice.Ended!.WinnerSide, bob.Ended!.WinnerSide);
        Assert.True(sent > 5);
        foreach (var p in players) await p.Net.DisposeAsync();
    }
}
