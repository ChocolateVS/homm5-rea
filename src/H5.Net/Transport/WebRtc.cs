using H5.Net.Master;

namespace H5.Net.Transport;

/// <summary>
/// Hook for browser peer-to-peer play over WebRTC DataChannels, with the master as
/// the signalling server (RvMessage rtc_offer / rtc_answer / rtc_ice relayed between a
/// /ws/signal WebSocket joiner and the lobby host's UDP rendezvous socket).
///
/// No WebRTC stack ships with H5Reborn yet, deliberately: Godot's web export already
/// exposes the browser's native WebRTCPeerConnection/WebRTCDataChannel, and desktop
/// Godot has the official webrtc-native GDExtension, so the client-side implementation
/// belongs in the Godot layer (an adapter producing an <see cref="IFrameChannel"/>),
/// not a third-party .NET WebRTC library (the mature one, SIPSorcery, adds a large
/// dependency with a modified licence). Until an adapter is registered, hosts answer
/// offers with rtc_unsupported and browsers use the master's /ws/join bridge instead,
/// which already lets desktop and browser players share a lobby.
/// </summary>
public interface IRtcAnswerer
{
    /// <summary>
    /// Handles a joiner's offer or ICE candidate for signalling session
    /// <paramref name="session"/>; returns messages to send back (answer, candidates).
    /// When a DataChannel opens, the implementation passes it to <paramref name="onChannel"/>.
    /// </summary>
    IEnumerable<RvMessage> OnSignal(ulong session, RvMessage message, Action<IFrameChannel> onChannel);
}

/// <summary>Default host behaviour: tell the joiner to use the WebSocket bridge.</summary>
public sealed class NoRtcAnswerer : IRtcAnswerer
{
    public IEnumerable<RvMessage> OnSignal(ulong session, RvMessage message, Action<IFrameChannel> onChannel)
    {
        if (message.T == RvMessage.RtcOffer)
            yield return new RvMessage { T = RvMessage.RtcUnsupported, Session = session };
    }
}
