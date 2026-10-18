using System.Net;
using System.Text.Json.Nodes;
using H5.Net;

namespace H5.Tests;

public class LobbyServerTests
{
    /// <summary>Toy game: sides alternate adding numbers; first to reach 10 wins.</summary>
    sealed class CountingSession : IGameSession
    {
        int _total, _turn;
        public IReadOnlyList<JsonNode> Start(IReadOnlyList<RoomPlayer> players, JsonObject? settings, ulong seed) =>
            players.Select((_, i) => (JsonNode)new JsonObject { ["side"] = i }).ToList();

        public SessionResult Apply(int side, JsonNode command)
        {
            if (side != _turn) return new SessionResult.Rejected("not your turn");
            var add = command["add"]!.GetValue<int>();
            if (add is < 1 or > 3) return new SessionResult.Rejected("add 1..3");
            _total += add;
            _turn = 1 - _turn;
            var ended = _total >= 10 ? new GameEnded(side, "reached 10") : null;
            return new SessionResult.Accepted(new JsonObject { ["total"] = _total }, _total.ToString(), ended);
        }

        public GameEnded? OnSideLeft(int side) => new(1 - side, "opponent left");
    }

    sealed class CountingFactory : IGameSessionFactory
    {
        public string Mode => "count";
        public int MinPlayers => 2;
        public int MaxPlayers => 2;
        public string? ValidatePlayerOptions(JsonObject? options) => null;
        public IGameSession Create() => new CountingSession();
    }

    static async Task<T> WaitFor<T>(NetClient c, List<Message> log, Func<T, bool>? pred = null) where T : Message
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            c.Poll();
            var hit = log.OfType<T>().FirstOrDefault(m => pred?.Invoke(m) ?? true);
            if (hit != null) { log.Remove(hit); return hit; }
            await Task.Delay(5);
        }
        throw new TimeoutException($"no {typeof(T).Name}; got: {string.Join(", ", log.Select(m => m.GetType().Name))}");
    }

    static async Task<(NetClient, List<Message>)> Join(int port, string name, string fp = "FP")
    {
        var c = new NetClient();
        var log = new List<Message>();
        c.MessageReceived += log.Add;
        await c.ConnectAsync("127.0.0.1", port, name, fp);
        await WaitFor<Welcome>(c, log);
        return (c, log);
    }

    [Fact]
    public async Task FullLobbyAndGameFlow()
    {
        await using var server = new LobbyServer(new[] { new CountingFactory() }, requiredFingerprint: "FP");
        server.Start(0, IPAddress.Loopback);

        var (a, aLog) = await Join(server.Port, "Alice");
        var (b, bLog) = await Join(server.Port, "Bob");
        await using var _a = a; await using var _b = b;

        a.Send(new CreateRoom("duel", "count", 2, null, null));
        var room = await WaitFor<RoomState>(a, aLog);
        b.Send(new JoinRoom(room.RoomId, null));
        await WaitFor<RoomState>(b, bLog, r => r.Players.Count == 2);

        a.Send(new StartGame());
        await WaitFor<ErrorMsg>(a, aLog, e => e.Code == "start"); // Bob not ready yet
        b.Send(new SetReady(true));
        await WaitFor<RoomState>(a, aLog, r => r.Players.All(p => p.Ready || p.IsHost));
        a.Send(new StartGame());
        Assert.Equal(0, (await WaitFor<GameStarted>(a, aLog)).YourSide);
        Assert.Equal(1, (await WaitFor<GameStarted>(b, bLog)).YourSide);

        var badSeq = b.SendCommand(new JsonObject { ["add"] = 1 });
        Assert.Equal(badSeq, (await WaitFor<CommandRejected>(b, bLog)).ClientSeq);

        int total = 0;
        for (int turn = 0; total < 10; turn++)
        {
            var (mover, log) = turn % 2 == 0 ? (a, aLog) : (b, bLog);
            mover.SendCommand(new JsonObject { ["add"] = 3 });
            var upd = await WaitFor<GameUpdate>(mover, log);
            total = upd.Events["total"]!.GetValue<int>();
            var other = turn % 2 == 0 ? (b, bLog) : (a, aLog);
            Assert.Equal(upd.StateHash, (await WaitFor<GameUpdate>(other.Item1, other.Item2)).StateHash);
        }
        // Moves go Alice 3, Bob 6, Alice 9, Bob 12, so Bob (side 1) crosses 10 first.
        Assert.Equal(1, (await WaitFor<GameEnded>(a, aLog)).WinnerSide);
    }

    [Fact]
    public async Task RejectsMismatchedGameData()
    {
        await using var server = new LobbyServer(new[] { new CountingFactory() }, requiredFingerprint: "FP");
        server.Start(0, IPAddress.Loopback);
        var c = new NetClient();
        var log = new List<Message>();
        c.MessageReceived += log.Add;
        await c.ConnectAsync("127.0.0.1", server.Port, "Mallory", "OTHER");
        Assert.Equal("data_mismatch", (await WaitFor<ErrorMsg>(c, log)).Code);
        await c.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectForfeitsRunningGame()
    {
        await using var server = new LobbyServer(new[] { new CountingFactory() });
        server.Start(0, IPAddress.Loopback);
        var (a, aLog) = await Join(server.Port, "Alice");
        var (b, bLog) = await Join(server.Port, "Bob");
        await using var _a = a;
        a.Send(new CreateRoom("", "count", 2, null, null));
        var room = await WaitFor<RoomState>(a, aLog);
        b.Send(new JoinRoom(room.RoomId, null));
        b.Send(new SetReady(true));
        await WaitFor<RoomState>(a, aLog, r => r.Players.Count == 2 && r.Players[1].Ready);
        a.Send(new StartGame());
        await WaitFor<GameStarted>(a, aLog);
        await b.DisposeAsync();
        Assert.Equal(0, (await WaitFor<GameEnded>(a, aLog)).WinnerSide);
    }
}
