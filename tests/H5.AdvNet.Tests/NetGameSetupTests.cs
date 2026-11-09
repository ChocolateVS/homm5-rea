using System.Text.Json.Nodes;
using H5.AdvNet;
using H5.Net;

namespace H5.AdvNet.Tests;

/// <summary>The network setup rows (UI/MPWait, H5_Game.exe 0071f3d0 / 0071e7b0 / 0071d3e0 / 0071d590) and their lobby plumbing.</summary>
public class NetGameSetupTests
{
    const int Host = 1, Guest = 2;

    static NetGameSetup NewSetup()
    {
        var lm = Env.Maps.Load(new AdvNetSettings { Map = Env.TestMap });
        return NetGameSetup.Create(lm.Map, lm.Db, Host, "H");
    }

    [Fact]
    public void HostSeatsFirstHumanRowAndJoinTakesAnOpenRow()
    {
        var g = NewSetup();
        var hostRow = g.RowOf(Host)!;
        Assert.Equal(NetSlotState.Human, hostRow.NetState);
        Assert.Equal("H", hostRow.Name);
        Assert.Contains(g.Rows, r => r.NetState == NetSlotState.Open);
        Assert.True(g.Join(Guest, "G"));
        var guestRow = g.RowOf(Guest)!;
        Assert.Equal(NetSlotState.Human, guestRow.NetState);
        Assert.Equal("G", guestRow.Name);
        g.Leave(Guest);
        Assert.Null(g.RowOf(Guest));
        Assert.Equal(NetSlotState.Open, guestRow.NetState);
    }

    [Fact]
    public void LocksFollowTheRowHolder()
    {
        var g = NewSetup();
        g.Join(Guest, "G");
        int hostRow = g.Rows.ToList().IndexOf(g.RowOf(Host)!), guestRow = g.Rows.ToList().IndexOf(g.RowOf(Guest)!);
        // 0071e7b0: a client edits only its own row; the host its own row (and computer rows), never a client's.
        Assert.NotEqual(0, g.Locks(hostRow, Guest, Host) & NetGameSetup.LockRace);
        Assert.NotEqual(0, g.Locks(guestRow, Host, Host) & NetGameSetup.LockRace);
        Assert.False(g.Request(Guest, Host, hostRow, CustomGameSetup.Change.Race, true));
        if (g.Rows[guestRow].Races.Count > 1)
        {
            Assert.Equal(0, g.Locks(guestRow, Guest, Host) & NetGameSetup.LockRace);
            Assert.True(g.Request(Guest, Host, guestRow, CustomGameSetup.Change.Race, true));
            Assert.NotNull(g.Rows[guestRow].Race);
        }
        // Ready (state 3) locks the row's options (only state 0 is editable).
        Assert.True(g.Request(Guest, Host, guestRow, CustomGameSetup.Change.State, true));
        Assert.Equal(NetSlotState.Ready, g.Rows[guestRow].NetState);
        Assert.NotEqual(0, g.Locks(guestRow, Guest, Host) & NetGameSetup.LockBonus);
    }

    [Fact]
    public void HostStepsFreeRowsAndStartNeedsTwoReadyHumansAndNoOpenRow()
    {
        var g = NewSetup();
        g.Join(Guest, "G");
        Assert.False(g.CanStart());
        g.SetReady(Host, true);
        g.SetReady(Guest, true);
        // Any further open row blocks the start (0071d590) until the host makes it a computer or closes it.
        for (int i = 0; i < g.Rows.Count; i++)
            for (int k = 0; k < 3 && g.Rows[i].NetState == NetSlotState.Open; k++)
                g.Request(Host, Host, i, CustomGameSetup.Change.State, true);
        Assert.DoesNotContain(g.Rows, r => r.NetState == NetSlotState.Open);
        Assert.Equal(g.Model.CanStart(), g.CanStart());
        // A client cannot step a free row.
        var free = g.Rows.ToList().FindIndex(r => r.Owner < 0);
        if (free >= 0) Assert.False(g.Request(Guest, Host, free, CustomGameSetup.Change.State, true));
    }

    [Fact]
    public void RowsRoundTripThroughTheRoomSettings()
    {
        var g = NewSetup();
        g.Join(Guest, "G");
        var row = g.RowOf(Guest)!;
        row.Bonus = 2;
        var lm = Env.Maps.Load(new AdvNetSettings { Map = Env.TestMap });
        var s = new AdvNetSettings { Map = Env.TestMap, NetSetup = true, Setup = g.ToRows() };
        var back = NetGameSetup.FromRows(lm.Map, lm.Db, AdvNetSettings.From(s.ToJson()).Setup!);
        Assert.Equal(2, back.RowOf(Guest)!.Bonus);
        Assert.Equal("G", back.RowOf(Guest)!.Name);
        Assert.Equal(row.MapPlayer, back.RowOf(Guest)!.MapPlayer);
    }

    [Fact]
    public async Task RequestsGoThroughTheHostAndReachTheStartedGame()
    {
        var factory = new AdventureSessionFactory(Env.Maps);
        await using var server = new LobbyServer(new IGameSessionFactory[] { factory });
        server.Start(0, System.Net.IPAddress.Loopback);
        var clients = new[] { new NetClient(), new NetClient() };
        var inbox = new[] { new List<Message>(), new List<Message>() };
        for (int i = 0; i < 2; i++)
        {
            int k = i;
            clients[i].MessageReceived += m => { lock (inbox[k]) inbox[k].Add(m); };
            await clients[i].ConnectAsync("127.0.0.1", server.Port, $"P{i}", "");
        }
        async Task Until(Func<bool> cond)
        {
            var end = DateTime.UtcNow.AddSeconds(60);
            while (!cond())
            {
                foreach (var c in clients) c.Poll();
                Assert.True(DateTime.UtcNow < end, "timed out");
                await Task.Delay(5);
            }
        }
        RoomState? Last(int k) { lock (inbox[k]) return inbox[k].OfType<RoomState>().LastOrDefault(); }
        AdvNetSettings Rows(int k) => AdvNetSettings.From(Last(k)!.Settings);

        await Until(() => clients.All(c => c.Connected));
        clients[0].Send(new CreateRoom("adv", "adventure", 2, null, Env.Settings(s => s.NetSetup = true)));
        await Until(() => Last(0) != null);
        clients[1].Send(new JoinRoom(Last(0)!.RoomId, null));
        await Until(() => Last(0)!.Players.Count == 2 && Last(1) != null);
        int hostId = Last(0)!.Players[0].PlayerId, guestId = Last(0)!.Players[1].PlayerId;
        var rows = Rows(1).Setup!;
        int guestRow = rows.FindIndex(r => r.Owner == guestId), hostRow = rows.FindIndex(r => r.Owner == hostId);
        Assert.True(guestRow >= 0 && hostRow >= 0);

        // The guest's bonus arrow on its own row: the host applies it and every peer sees it.
        int before = rows[guestRow].Bonus;
        clients[1].Send(new SetupRequest(guestRow, (int)CustomGameSetup.Change.Bonus, true));
        await Until(() => Rows(0).Setup![guestRow].Bonus != before && Rows(1).Setup![guestRow].Bonus != before);
        int chosen = Rows(0).Setup![guestRow].Bonus;
        // The guest's arrow on the host's row is ignored.
        int hostBonus = Rows(0).Setup![hostRow].Bonus;
        clients[1].Send(new SetupRequest(hostRow, (int)CustomGameSetup.Change.Bonus, true));
        clients[1].Send(new SetReady(true));
        await Until(() => Rows(0).Setup![guestRow].State == NetSlotState.Ready);
        Assert.Equal(hostBonus, Rows(0).Setup![hostRow].Bonus);
        Assert.True(Last(0)!.Players[1].Ready);

        // Free rows: the host makes every open row a computer (or closes it), gets ready and starts.
        for (int i = 0; i < Rows(0).Setup!.Count; i++)
            for (int k = 0; k < 3 && Rows(0).Setup![i].State == NetSlotState.Open; k++)
            {
                var seen = Last(0);
                clients[0].Send(new SetupRequest(i, (int)CustomGameSetup.Change.State, true));
                await Until(() => !ReferenceEquals(Last(0), seen));
            }
        clients[0].Send(new SetReady(true));
        await Until(() => Rows(0).Setup![hostRow].State == NetSlotState.Ready);
        clients[0].Send(new StartGame());
        await Until(() => { lock (inbox[1]) return inbox[1].OfType<GameStarted>().Any(); });
        GameStarted gs;
        lock (inbox[1]) gs = inbox[1].OfType<GameStarted>().First();
        var replica = new AdventureNetReplica(Env.Maps, gs);
        Assert.Equal(Rows(0).Setup![guestRow].MapPlayer, replica.MyPlayer);
        Assert.NotEqual(-1, chosen);
        foreach (var c in clients) await c.DisposeAsync();
    }

    [Fact]
    public void JoiningASavedGameNeedsTheNameAmongTheSavedPlayers()
    {
        var factory = new AdventureSessionFactory(Env.Maps);
        var s = new AdvNetSettings { Map = Env.TestMap, Load = "x", LoadSlots = new() { new NetSaveSlot { MapPlayer = 0, Name = "Ann" } } }.ToJson();
        var (_, err) = factory.Joining(s, 5, "Bob", new[] { "Ann" });
        Assert.Equal("not_in_save", err?.Code);
        var j = JsonNode.Parse(err!.Text)!;
        Assert.Equal("Bob", (string?)j["playername"]);
        Assert.Equal("Ann", (string?)j["players"]![0]);
        Assert.Null(factory.Joining(s, 5, "Ann", new[] { "Ann" }).Error);
    }
}
