using System.Net;
using H5.Net.Discovery;
using H5.Net.Hosting;
using H5.Net.Transport;

namespace H5.Net.Tests;

public class TransportTests
{
    [Fact]
    public async Task ReliableUdpDeliversLargeFramesInOrderDespiteLoss()
    {
        await using var server = UdpHost.Bind(Loopback.Any0);
        await using var client = UdpHost.Bind(Loopback.Any0);
        var rng = new Random(5);
        // 20% loss both ways (the Connect handshake retries until it gets through).
        server.DropOutgoing = _ => rng.NextDouble() < 0.2;
        client.DropOutgoing = _ => rng.NextDouble() < 0.2;
        server.AcceptConnections = true;
        var accepted = new TaskCompletionSource<ReliableUdpChannel>();
        server.Accepted += ch => accepted.TrySetResult(ch);

        var c = await client.ConnectAsync(new UdpPath(server.LocalEndPoint), TimeSpan.FromSeconds(10));
        var s = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var got = new List<byte[]>();
        var all = new TaskCompletionSource();
        s.FrameReceived += f => { lock (got) { got.Add(f); if (got.Count == 6) all.TrySetResult(); } };

        var frames = Enumerable.Range(0, 6).Select(i =>
        {
            var b = new byte[i == 3 ? 400_000 : 50 + i * 1000];
            new Random(i).NextBytes(b);
            return b;
        }).ToList();
        foreach (var f in frames) c.Send(f);
        await all.Task.WaitAsync(TimeSpan.FromSeconds(30));
        for (int i = 0; i < frames.Count; i++) Assert.Equal(frames[i], got[i]);
    }

    [Fact]
    public async Task ManualPumpHostWorksWithoutBackgroundThreads()
    {
        await using var server = UdpHost.Bind(Loopback.Any0);
        await using var client = UdpHost.Bind(Loopback.Any0, manualPump: true);
        server.AcceptConnections = true;
        var accepted = new TaskCompletionSource<ReliableUdpChannel>();
        server.Accepted += ch => accepted.TrySetResult(ch);
        var c = await client.ConnectAsync(new UdpPath(server.LocalEndPoint), TimeSpan.FromSeconds(5));
        var s = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reply = new TaskCompletionSource<byte[]>();
        c.FrameReceived += f => reply.TrySetResult(f);
        s.FrameReceived += f => s.Send(f.Reverse().ToArray());
        c.Send(new byte[] { 1, 2, 3 });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!reply.Task.IsCompleted && DateTime.UtcNow < deadline) { client.Pump(); await Task.Delay(5); }
        Assert.Equal(new byte[] { 3, 2, 1 }, await reply.Task);
    }

    [Fact]
    public async Task LanBrowserFindsHost()
    {
        await using var host = GameHost.Start(new GameHostOptions
        {
            Name = "Alice's LAN game", Port = 0, Bind = IPAddress.Loopback, LanBind = Loopback.Any0, Fingerprint = "FP",
        }, Array.Empty<IGameSessionFactory>());
        var beaconPort = typeof(GameHost).GetField("_beacon", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(host) is LanBeacon b ? b.Port : 0;
        await using var browser = new LanBrowser(new[] { new IPEndPoint(IPAddress.Loopback, beaconPort) }, TimeSpan.FromMilliseconds(200));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (browser.Games.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        var g = Assert.Single(browser.Games);
        Assert.Equal("Alice's LAN game", g.Name);
        Assert.Equal(host.Port, g.Port);
        Assert.Equal("127.0.0.1", g.Address);
        Assert.Equal("FP", g.Fingerprint);
    }

    [Fact]
    public async Task OnePortServesNativeTcpAndWebSocketClients()
    {
        await using var host = GameHost.Start(new GameHostOptions { Port = 0, Bind = IPAddress.Loopback, LanDiscovery = false },
            Array.Empty<IGameSessionFactory>());
        await using var native = new TestPlayer("Native", 0);
        await native.ConnectAsync(async ct => await TcpFrameChannel.ConnectAsync("127.0.0.1", host.Port, TimeSpan.FromSeconds(5), ct));
        await using var web = new TestPlayer("Browser", 0);
        await web.ConnectAsync(async ct => await WebSocketFrameChannel.ConnectAsync(new Uri($"ws://127.0.0.1:{host.Port}/"), TimeSpan.FromSeconds(5), ct));
        Assert.True(native.Net.Connected && web.Net.Connected);
        Assert.NotEqual(native.Net.PlayerId, web.Net.PlayerId);
    }
}
