namespace H5.Master;

/// <summary>Master server settings, all overridable by environment variables (no secrets needed).</summary>
public sealed class MasterOptions
{
    /// <summary>H5_MASTER_URLS: HTTP listen URLs (Kestrel syntax).</summary>
    public string Urls { get; init; } = "http://0.0.0.0:27620";
    /// <summary>H5_MASTER_UDP_PORT: UDP port for rendezvous and relay.</summary>
    public int UdpPort { get; init; } = 27621;
    /// <summary>H5_MASTER_UDP_BIND: address the UDP socket binds to.</summary>
    public string UdpBind { get; init; } = "0.0.0.0";
    public int MaxLobbies { get; init; } = 2000;
    public int MaxLobbiesPerAddress { get; init; } = 4;
    public TimeSpan LobbyTimeout { get; init; } = TimeSpan.FromSeconds(45);
    /// <summary>H5_MASTER_RELAY_KBPS: per-session relay bandwidth cap (both directions).</summary>
    public int RelayKilobytesPerSecond { get; init; } = 512;
    public int MaxRelaySessions { get; init; } = 2000;
    public TimeSpan RelayIdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>H5_MASTER_REQUESTS_PER_MINUTE: per-IP HTTP budget.</summary>
    public int RequestsPerMinute { get; init; } = 120;
    /// <summary>H5_MASTER_MOTD: message of the day shown in the lobby browser.</summary>
    public string Motd { get; init; } = "";

    public static MasterOptions FromEnvironment()
    {
        static string? E(string n) => Environment.GetEnvironmentVariable(n);
        static int I(string n, int d) => int.TryParse(E(n), out var v) ? v : d;
        return new MasterOptions
        {
            Urls = E("H5_MASTER_URLS") ?? "http://0.0.0.0:27620",
            UdpPort = I("H5_MASTER_UDP_PORT", 27621),
            UdpBind = E("H5_MASTER_UDP_BIND") ?? "0.0.0.0",
            MaxLobbies = I("H5_MASTER_MAX_LOBBIES", 2000),
            MaxLobbiesPerAddress = I("H5_MASTER_MAX_LOBBIES_PER_IP", 4),
            RelayKilobytesPerSecond = I("H5_MASTER_RELAY_KBPS", 512),
            MaxRelaySessions = I("H5_MASTER_MAX_RELAYS", 2000),
            RequestsPerMinute = I("H5_MASTER_REQUESTS_PER_MINUTE", 120),
            Motd = E("H5_MASTER_MOTD") ?? "",
        };
    }
}
