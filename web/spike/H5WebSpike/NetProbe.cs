using System.Diagnostics;
using System.Text;
using Godot;
using H5.Net.Master;
using H5.Net.Transport;

namespace H5WebSpike;

/// <summary>
/// Browser networking probe (page URL <c>?master=ws://host:port</c>): connects the client's own
/// <see cref="WebSocketFrameChannel"/> (System.Net.WebSockets.ClientWebSocket → browser WebSocket) to
/// the master's /ws/signal endpoint with an unknown join code and expects the master's "fail" RvMessage
/// back; then tries the HTTP lobby API with HttpClient (browser fetch; cross-origin needs CORS).
/// </summary>
static class NetProbe
{
    public static async Task RunAsync(string master)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var uri = new Uri(master.TrimEnd('/') + "/ws/signal?code=NOPE00");
            var channel = await WebSocketFrameChannel.ConnectAsync(uri, TimeSpan.FromSeconds(10));
            var connectedMs = sw.ElapsedMilliseconds;
            var got = new TaskCompletionSource<byte[]>();
            channel.FrameReceived += f => got.TrySetResult(f);
            channel.Closed += e => got.TrySetException(e ?? new IOException("closed before any frame"));
            channel.Start();
            var frame = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var msg = RvMessage.Decode(frame);
            GD.Print($"SPIKE_NET ws ok connectMs={connectedMs} rttMs={sw.ElapsedMilliseconds} t={msg?.T} error={msg?.Error} raw={Encoding.UTF8.GetString(frame)}");
            await channel.DisposeAsync();
        }
        catch (Exception e) { GD.PrintErr($"SPIKE_NET ws FAIL {e.GetType().Name}: {e.Message}"); }

        try
        {
            var http = master.Replace("ws://", "http://").Replace("wss://", "https://").TrimEnd('/');
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var body = await client.GetStringAsync(http + "/api/info");
            GD.Print($"SPIKE_NET http ok /api/info {body}");
        }
        catch (Exception e) { GD.Print($"SPIKE_NET http FAIL {e.GetType().Name}: {e.Message}"); }
        try
        {
            // Godot's web platform exposes the browser's native WebRTC; no GDExtension needed here.
            var pc = new WebRtcPeerConnection();
            var init = pc.Initialize(new Godot.Collections.Dictionary { ["iceServers"] = new Godot.Collections.Array() });
            var dc = pc.CreateDataChannel("h5", new Godot.Collections.Dictionary { ["negotiated"] = true, ["id"] = 1 });
            var sdp = new TaskCompletionSource<string>();
            pc.SessionDescriptionCreated += (type, desc) => { pc.SetLocalDescription(type, desc); sdp.TrySetResult(type + " " + desc.Length + " chars"); };
            var err = pc.CreateOffer();
            for (int i = 0; i < 100 && !sdp.Task.IsCompleted; i++) { pc.Poll(); await Task.Delay(20); }
            GD.Print($"SPIKE_NET webrtc init={init} channel={(dc != null)} createOffer={err} offer={(sdp.Task.IsCompleted ? sdp.Task.Result : "none")}");
            pc.Close();
        }
        catch (Exception e) { GD.Print($"SPIKE_NET webrtc FAIL {e.GetType().Name}: {e.Message}"); }
        GD.Print("SPIKE_NET_DONE");
    }
}
