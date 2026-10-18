using System.Net;
using System.Text.Json.Nodes;
using H5.Combat;
using H5.Core.Data;
using H5.Core.IO;
using H5.Duel;
using H5.Master;
using H5.Net;
using H5.Net.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace H5.Net.Tests;

static class Db
{
    static readonly Lazy<GameDatabase> Lazy = new(() => GameDatabase.Load(PakFileSystem.OpenInstall(GameInstall.Locate())));
    public static GameDatabase Value => Lazy.Value;
}

/// <summary>A test player: a NetClient plus the duel replica it maintains, driven by the AI.</summary>
sealed class TestPlayer : IAsyncDisposable
{
    public readonly NetClient Net = new();
    public readonly List<Message> Inbox = new();
    public readonly string Name;
    public readonly int Preset;
    public DuelReplica? Replica;
    public GameEnded? Ended;
    public bool SentEndTactics;
    public int Rejections, Resumes;
    public IFrameChannel? CurrentChannel;

    public TestPlayer(string name, int preset)
    {
        Name = name;
        Preset = preset;
        Net.MessageReceived += m =>
        {
            switch (m)
            {
                case GameStarted gs: Replica = new DuelReplica(Db.Value, gs); break;
                case GameUpdate up: Replica!.Apply(up); break;
                case GameEnded ge: Ended = ge; break;
                case CommandRejected: Rejections++; break;
                case Resumed: Resumes++; Inbox.Add(m); break;
                default: Inbox.Add(m); break;
            }
        };
    }

    /// <summary>Connects through a connector, remembering the live channel so tests can cut it.</summary>
    public async Task ConnectAsync(Func<CancellationToken, Task<IFrameChannel>> connector)
    {
        await Net.ConnectAsync(async ct => CurrentChannel = await connector(ct), Name, "");
        await Pump(() => Net.Connected);
    }

    public async Task Pump(Func<bool> until, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!until())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{Name}: {string.Join(",", Inbox.Select(m => m.GetType().Name))}");
            Net.Poll();
            await Task.Delay(2);
        }
    }

    public ValueTask DisposeAsync() => Net.DisposeAsync();
}

static class DuelDriver
{
    /// <summary>Creates a duel room with players[0] as host, starts it and plays it to the end with the AI.</summary>
    public static async Task PlayAsync(TestPlayer[] players, Action<int>? afterCommand = null)
    {
        var (a, b) = (players[0], players[1]);
        a.Net.Send(new CreateRoom("duel", "duel", 2, null, null));
        await a.Pump(() => a.Inbox.OfType<RoomState>().Any());
        var roomId = a.Inbox.OfType<RoomState>().First().RoomId;
        b.Net.Send(new JoinRoom(roomId, null));
        a.Net.Send(new SetPlayerOptions(new JsonObject { ["preset"] = a.Preset }));
        b.Net.Send(new SetPlayerOptions(new JsonObject { ["preset"] = b.Preset }));
        b.Net.Send(new SetReady(true));
        await a.Pump(() => a.Inbox.OfType<RoomState>().Any(r => r.Players.Count == 2 && r.Players[1].Ready && r.Players[1].Options != null));
        a.Net.Send(new StartGame());
        foreach (var p in players) await p.Pump(() => p.Replica != null);

        var ai = new GreedyBattleAI { MaxCandidates = 8 };
        int sent = 0;
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (players.Any(p => p.Ended == null) && DateTime.UtcNow < deadline)
        {
            foreach (var p in players)
            {
                p.Net.Poll();
                var r = p.Replica!;
                if (p.Ended != null || r.Desynced || !p.Net.Connected) continue;
                if (r.Battle.State.Phase == BattlePhase.Tactics)
                {
                    if (!p.SentEndTactics) { p.Net.SendCommand(JsonNode.Parse(MessageJson.Serialize(new EndTacticsCmd()))!); p.SentEndTactics = true; }
                    continue;
                }
                if (!r.IsMyTurn || p.Net.PendingCommands > 0) continue;
                p.Net.SendCommand(JsonNode.Parse(MessageJson.Serialize(ai.Decide(r.Battle, r.MySide)))!);
                sent++;
                afterCommand?.Invoke(sent);
            }
            await Task.Delay(1);
        }
        foreach (var p in players)
        {
            Assert.False(p.Replica!.Desynced, $"{p.Name} desynced");
            Assert.NotNull(p.Ended);
        }
        Assert.Equal(a.Replica!.Battle.State.ComputeHash(), b.Replica!.Battle.State.ComputeHash());
        Assert.Equal(a.Ended!.WinnerSide, b.Ended!.WinnerSide);
        Assert.True(sent > 5, $"only {sent} commands; ended: {a.Ended!.Reason} winner {a.Ended.WinnerSide}");
    }
}

/// <summary>An in-process master server on loopback ports.</summary>
sealed class TestMaster : IAsyncDisposable
{
    public WebApplication App { get; }
    public Uri Url { get; }
    public RendezvousService Rendezvous => App.Services.GetRequiredService<RendezvousService>();

    TestMaster(WebApplication app, Uri url) { App = app; Url = url; }

    public static async Task<TestMaster> StartAsync()
    {
        var app = MasterApp.Build(new MasterOptions { Urls = "http://127.0.0.1:0", UdpPort = 0, UdpBind = "127.0.0.1" });
        await app.StartAsync();
        return new TestMaster(app, new Uri(app.Urls.First()));
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

static class Loopback
{
    public static IPEndPoint Any0 => new(IPAddress.Loopback, 0);
}
