using System.Net.WebSockets;
using System.Text.Json;
using H5.Net.Master;
using H5.Net.Transport;

namespace H5.Master;

/// <summary>
/// WebSocket endpoints for clients that cannot use UDP (browser builds, or desktops
/// behind UDP-blocking firewalls):
///   /ws/join?code=ABC123  — the master opens a reliable UDP channel to the lobby's
///                           host and splices it onto this socket (relay fallback that
///                           lets browser and desktop players share one lobby).
///   /ws/signal?code=…     — WebRTC signalling (offer/answer/ICE) between a browser
///                           joiner and the host, relayed through the master.
/// </summary>
public static class WebSocketBridge
{
    public static void Map(WebApplication app)
    {
        app.Map("/ws/join", async (HttpContext ctx, RendezvousService rv, ILoggerFactory lf) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            var (key, isCode) = Target(ctx);
            if (key == null) { ctx.Response.StatusCode = 400; return; }
            ReliableUdpChannel hostChannel;
            try { hostChannel = await rv.OpenBridgeAsync(key, isCode, ctx.RequestAborted); }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or TimeoutException)
            {
                ctx.Response.StatusCode = e is KeyNotFoundException ? 404 : 503;
                await ctx.Response.WriteAsync(e.Message);
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var browser = new WebSocketFrameChannel(ws, ctx.Connection.RemoteIpAddress + " (ws)");
            await Splice(browser, hostChannel, ctx.RequestAborted);
            lf.CreateLogger("bridge").LogInformation("ws bridge to {Host} closed", hostChannel.RemoteName);
        });

        app.Map("/ws/signal", async (HttpContext ctx, RendezvousService rv) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            var (key, isCode) = Target(ctx);
            if (key == null) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var channel = new WebSocketFrameChannel(ws, "signal");
            ulong session;
            try { session = rv.OpenSignalling(key, isCode, m => channel.Send(m.Encode())); }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
            {
                channel.Send(new RvMessage { T = RvMessage.Fail, Error = e.Message }.Encode());
                await Task.Delay(200);
                await channel.DisposeAsync();
                return;
            }
            var done = new TaskCompletionSource();
            channel.FrameReceived += f =>
            {
                var m = RvMessage.Decode(f);
                if (m is { T: RvMessage.RtcOffer or RvMessage.RtcIce }) rv.Signal(session, m);
            };
            channel.Closed += _ => done.TrySetResult();
            channel.Start();
            await Task.WhenAny(done.Task, Task.Delay(Timeout.Infinite, ctx.RequestAborted));
            rv.CloseSignalling(session);
            await channel.DisposeAsync();
        });
    }

    static (string? key, bool isCode) Target(HttpContext ctx)
    {
        if (ctx.Request.Query["code"] is { Count: > 0 } c) return (JoinCodes.Normalize(c.ToString()), true);
        if (ctx.Request.Query["lobby"] is { Count: > 0 } l) return (l.ToString(), false);
        return (null, false);
    }

    /// <summary>Pipes frames both ways until either side closes.</summary>
    public static async Task Splice(IFrameChannel a, IFrameChannel b, CancellationToken ct)
    {
        var done = new TaskCompletionSource();
        a.FrameReceived += f => b.Send(f);
        b.FrameReceived += f => a.Send(f);
        a.Closed += _ => done.TrySetResult();
        b.Closed += _ => done.TrySetResult();
        a.Start();
        b.Start();
        await Task.WhenAny(done.Task, Task.Delay(Timeout.Infinite, ct)).ContinueWith(_ => { });
        await a.DisposeAsync();
        await b.DisposeAsync();
    }
}
