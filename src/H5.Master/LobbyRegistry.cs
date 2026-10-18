using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using H5.Net.Master;

namespace H5.Master;

/// <summary>In-memory registry of published lobbies. Lobbies expire when heartbeats stop.</summary>
public sealed class LobbyRegistry(MasterOptions options)
{
    public sealed class Entry
    {
        public required string Id;
        public required string Code;
        public required string HostToken;
        public required LobbyAd Ad;
        public required IPAddress CreatorAddress;
        public DateTime Created = DateTime.UtcNow;
        public DateTime LastHeartbeat = DateTime.UtcNow;
        public int Players, OpenRooms;
        /// <summary>The host's UDP public endpoint, learned from its rendezvous registrations.</summary>
        public IPEndPoint? HostPublic;
        public string[] HostLocal = Array.Empty<string>();
    }

    readonly ConcurrentDictionary<string, Entry> _byId = new();
    readonly ConcurrentDictionary<string, Entry> _byCode = new();

    public int Count => _byId.Count;

    public Entry? Create(LobbyAd ad, IPAddress creator)
    {
        Sweep();
        if (_byId.Count >= options.MaxLobbies) return null;
        if (_byId.Values.Count(e => e.CreatorAddress.Equals(creator)) >= options.MaxLobbiesPerAddress) return null;
        var clean = ad with
        {
            Name = Trim(ad.Name, 48),
            Mode = Trim(ad.Mode, 16),
            Fingerprint = Trim(ad.Fingerprint, 32),
            MaxPlayers = Math.Clamp(ad.MaxPlayers, 1, 8),
        };
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var code = new string(Enumerable.Range(0, JoinCodes.Length)
                .Select(_ => JoinCodes.Alphabet[RandomNumberGenerator.GetInt32(JoinCodes.Alphabet.Length)]).ToArray());
            var e = new Entry
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
                Code = code,
                HostToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
                Ad = clean,
                CreatorAddress = creator,
                Players = clean.Players,
                OpenRooms = clean.OpenRooms,
            };
            if (!_byCode.TryAdd(code, e)) continue;
            _byId[e.Id] = e;
            return e;
        }
        return null;
    }

    static string Trim(string s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : (s ?? "").Trim();

    public Entry? Get(string id) => _byId.TryGetValue(id, out var e) && !Expired(e) ? e : null;

    public Entry? GetByCode(string code) =>
        _byCode.TryGetValue(JoinCodes.Normalize(code), out var e) && !Expired(e) ? e : null;

    public Entry? Authorize(string id, string? token) =>
        Get(id) is { } e && token != null && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(e.HostToken), System.Text.Encoding.UTF8.GetBytes(token)) ? e : null;

    public void Remove(Entry e)
    {
        _byId.TryRemove(e.Id, out _);
        _byCode.TryRemove(e.Code, out _);
    }

    bool Expired(Entry e) => DateTime.UtcNow - e.LastHeartbeat > options.LobbyTimeout;

    public void Sweep()
    {
        foreach (var e in _byId.Values.Where(Expired).ToList()) Remove(e);
    }

    public IEnumerable<Entry> List(string? fingerprint, int? protocol) =>
        _byId.Values.Where(e => !Expired(e)
            && (fingerprint == null || e.Ad.Fingerprint == fingerprint)
            && (protocol == null || e.Ad.ProtocolVersion == protocol));

    public static OnlineLobby ToPublic(Entry e) => new(e.Id, e.Code, e.Ad.Name, e.Ad.Mode, e.Players, e.Ad.MaxPlayers,
        e.Ad.HasPassword, e.Ad.ProtocolVersion, e.Ad.Fingerprint, e.OpenRooms, (int)(DateTime.UtcNow - e.Created).TotalSeconds);
}
