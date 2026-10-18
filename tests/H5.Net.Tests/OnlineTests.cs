using System.Net;
using System.Net.WebSockets;
using H5.Duel;
using H5.Net.Hosting;
using H5.Net.Master;
using H5.Net.Transport;

namespace H5.Net.Tests;

public class OnlineTests
{
    static GameHost StartHost(string name = "Alice's online duel") => GameHost.Start(new GameHostOptions
    {
        Name = name, Port = 0, Bind = IPAddress.Loopback, LanDiscovery = false, Fingerprint = "",
    }, new[] { new DuelSessionFactory(Db.Value) });

    static async Task<OnlineHost> Publish(GameHost host, TestMaster master)
    {
        var online = await host.PublishOnlineAsync(master.Url, "duel", 2, false, mapPorts: false);
        // Wait until the master has learned the host's UDP endpoint.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (online.PublicEndPoint == null && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.NotNull(online.PublicEndPoint);
        return online;
    }

    [Fact]
    public async Task MasterListsLobbyAndFindsItByCode()
    {
        await using var master = await TestMaster.StartAsync();
        await using var host = StartHost();
        var online = await Publish(host, master);
        using var mc = new MasterClient(master.Url);
        var list = await mc.ListAsync(null);
        var lobby = Assert.Single(list);
        Assert.Equal("Alice's online duel", lobby.Name);
        Assert.Equal(online.Lobby.Code, lobby.Code);
        Assert.Equal(6, lobby.Code.Length);
        var found = await mc.FindByCodeAsync(online.Lobby.Code.ToLowerInvariant().Insert(3, "-"));
        Assert.Equal(lobby.Id, found!.Id);
        Assert.Null(await mc.FindByCodeAsync("ZZZZZZ"));
        await host.UnpublishAsync();
        Assert.Empty(await mc.ListAsync(null));
    }

    [Fact]
    public async Task JoinByCodeHolePunchesDirectlyAndPlaysADuel()
    {
        await using var master = await TestMaster.StartAsync();
        await using var host = StartHost();
        var online = await Publish(host, master);
        using var mc = new MasterClient(master.Url);
        await using var bobUdp = UdpHost.Bind(Loopback.Any0);

        await using var alice = new TestPlayer("Alice", 0);
        await alice.ConnectAsync(async ct => await TcpFrameChannel.ConnectAsync("127.0.0.1", host.Port, TimeSpan.FromSeconds(5), ct));
        await using var bob = new TestPlayer("Bob", 3);
        bool relayed = true;
        await bob.ConnectAsync(async ct =>
        {
            var r = await OnlineConnector.ConnectAsync(mc, bobUdp, online.Lobby.Code, ct: ct);
            relayed = r.Relayed;
            return r.Channel;
        });
        Assert.False(relayed);
        await DuelDriver.PlayAsync(new[] { alice, bob });
    }

    [Fact]
    public async Task SymmetricNatFallsBackToRelayAndPlaysADuel()
    {
        await using var master = await TestMaster.StartAsync();
        await using var host = StartHost();
        var online = await Publish(host, master);
        using var mc = new MasterClient(master.Url);
        var masterUdp = await mc.UdpEndPointAsync();
        await using var bobUdp = UdpHost.Bind(Loopback.Any0);
        // Simulate NATs that only allow traffic to/from the master: every direct datagram is lost.
        bobUdp.DropOutgoing = to => !to.Equals(masterUdp);
        host.Udp.DropOutgoing = to => !to.Equals(masterUdp);

        await using var alice = new TestPlayer("Alice", 5);
        await alice.ConnectAsync(async ct => await TcpFrameChannel.ConnectAsync("127.0.0.1", host.Port, TimeSpan.FromSeconds(5), ct));
        await using var bob = new TestPlayer("Bob", 14);
        bool relayed = false;
        await bob.ConnectAsync(async ct =>
        {
            var r = await OnlineConnector.ConnectAsync(mc, bobUdp, online.Lobby.Code, punchTimeout: TimeSpan.FromSeconds(1), ct: ct);
            relayed = r.Relayed;
            return r.Channel;
        });
        Assert.True(relayed);
        await DuelDriver.PlayAsync(new[] { alice, bob });
        Assert.True(master.Rendezvous.RelaySessionCount >= 1);
    }

    [Fact]
    public async Task BrowserJoinsDesktopHostThroughWebSocketBridge()
    {
        await using var master = await TestMaster.StartAsync();
        await using var host = StartHost();
        var online = await Publish(host, master);

        await using var alice = new TestPlayer("Alice (desktop)", 0);
        await alice.ConnectAsync(async ct => await TcpFrameChannel.ConnectAsync("127.0.0.1", host.Port, TimeSpan.FromSeconds(5), ct));
        await using var bob = new TestPlayer("Bob (browser)", 3);
        var wsUrl = new Uri($"ws://{master.Url.Authority}/ws/join?code={online.Lobby.Code}");
        await bob.ConnectAsync(async ct => await WebSocketFrameChannel.ConnectAsync(wsUrl, TimeSpan.FromSeconds(5), ct));
        await DuelDriver.PlayAsync(new[] { alice, bob });
    }

    [Fact]
    public async Task WebRtcSignallingReachesHostAndFallsBack()
    {
        await using var master = await TestMaster.StartAsync();
        await using var host = StartHost();
        var online = await Publish(host, master);
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://{master.Url.Authority}/ws/signal?code={online.Lobby.Code}"), default);
        var signal = new WebSocketFrameChannel(ws, "signal");
        var reply = new TaskCompletionSource<RvMessage>();
        signal.FrameReceived += f => { if (RvMessage.Decode(f) is { } m) reply.TrySetResult(m); };
        signal.Start();
        signal.Send(new RvMessage { T = RvMessage.RtcOffer, Sdp = "v=0 fake offer" }.Encode());
        var m = await reply.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RvMessage.RtcUnsupported, m.T);
        await signal.DisposeAsync();
    }

    [Fact]
    public async Task DroppedPlayerReconnectsMidDuelAndFinishes()
    {
        await using var host = StartHost();
        await using var alice = new TestPlayer("Alice", 0);
        await alice.ConnectAsync(async ct => await TcpFrameChannel.ConnectAsync("127.0.0.1", host.Port, TimeSpan.FromSeconds(5), ct));
        await using var bob = new TestPlayer("Bob", 3);
        await bob.ConnectAsync(async ct => await TcpFrameChannel.ConnectAsync("127.0.0.1", host.Port, TimeSpan.FromSeconds(5), ct));
        bool cut = false;
        await DuelDriver.PlayAsync(new[] { alice, bob }, sent =>
        {
            if (sent == 6 && !cut)
            {
                cut = true;
                _ = bob.CurrentChannel!.DisposeAsync().AsTask(); // the network drops Bob's connection
            }
        });
        Assert.True(cut);
        Assert.Equal(1, bob.Resumes);
    }
}
