using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace H5.Save;

/// <summary>
/// What kind of game a save holds. Mirrors the original's save type word (H5_Game.exe
/// FUN_007e7500 dispatches on it: 0 single game, 4 campaign, 1/2 multiplayer).
/// </summary>
public enum SaveKind { Scenario = 0, Multiplayer = 1, HotSeat = 2, Campaign = 4 }

/// <summary>Identifies the map a save was made on, so loading can reopen it.</summary>
public sealed class MapSpec
{
    /// <summary>"file" (stock map or user .h5m) or "rmg" (generated; <see cref="Generator"/> holds its options).</summary>
    public string Kind { get; set; } = "file";
    public string Id { get; set; } = "";
    /// <summary>Pak path of the map xdb, or the .h5m path for user maps.</summary>
    public string Source { get; set; } = "";
    public bool IsUserMap { get; set; }
    public JsonNode? Generator { get; set; }
}

/// <summary>Everything the save/load screens show without opening the game state.</summary>
public sealed class SaveHeader
{
    public int FormatVersion { get; set; } = SaveArchive.FormatVersion;
    public string Game { get; set; } = "H5Reborn";
    /// <summary>The player's name for the save (original "savename").</summary>
    public string Name { get; set; } = "";
    public SaveKind Kind { get; set; }
    public DateTime SavedUtc { get; set; }
    public string DataFingerprint { get; set; } = "";
    public MapSpec Map { get; set; } = new();
    public string MapName { get; set; } = "";
    public string MapDescription { get; set; } = "";
    /// <summary>0 tiny … 4 extra large (SaveInfo/Definition/mapsizeN), -1 unknown.</summary>
    public int MapSize { get; set; } = -1;
    public int Players { get; set; }
    public int Difficulty { get; set; }
    public int Day { get; set; }
    public string? CampaignName { get; set; }
    public string? CampaignDescription { get; set; }
    public int MissionIndex { get; set; } = -1;
    public int MissionsCount { get; set; }
    /// <summary>AdvState.ComputeHash() at save time; loading verifies the restored state matches.</summary>
    public string StateHash { get; set; } = "";
    public bool HasThumbnail { get; set; }
}

public sealed class SaveCorruptException(string message) : Exception(message);

/// <summary>
/// A save file: a zip holding <c>manifest.json</c> (format version and the SHA-256 of
/// every part), <c>header.json</c> and named parts (adventure state, scripts, campaign
/// context, client state, thumbnail). Reading verifies every hash, so a truncated or
/// edited file is rejected instead of loading a broken game. Zip and SHA-256 work in
/// the browser build too (docs/WEB_PORT.md).
/// </summary>
public sealed class SaveArchive
{
    public const int FormatVersion = 1;
    const string ManifestEntry = "manifest.json", HeaderEntry = "header.json";

    public SaveHeader Header { get; set; } = new();
    public Dictionary<string, byte[]> Parts { get; } = new(StringComparer.Ordinal);

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    sealed class Manifest
    {
        public int FormatVersion { get; set; }
        public string Game { get; set; } = "H5Reborn";
        public Dictionary<string, string> Sha256 { get; set; } = new();
    }

    public void SetText(string part, string text) => Parts[part] = Encoding.UTF8.GetBytes(text);
    public string? GetText(string part) => Parts.TryGetValue(part, out var b) ? Encoding.UTF8.GetString(b) : null;

    public byte[] ToBytes()
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(Header, Json);
        var manifest = new Manifest { FormatVersion = FormatVersion, Game = Header.Game };
        manifest.Sha256[HeaderEntry] = Hash(header);
        foreach (var (name, data) in Parts) manifest.Sha256[name] = Hash(data);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
            Add(zip, HeaderEntry, header);
            // Deterministic entry order: identical states give identical files.
            foreach (var name in Parts.Keys.Order(StringComparer.Ordinal)) Add(zip, name, Parts[name]);
        }
        return ms.ToArray();
    }

    static void Add(ZipArchive zip, string name, byte[] data)
    {
        // A fixed timestamp keeps files reproducible; the save time lives in the header.
        var e = zip.CreateEntry(name, name.EndsWith(".png") ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
        e.LastWriteTime = new DateTimeOffset(2006, 5, 16, 0, 0, 0, TimeSpan.Zero);
        using var s = e.Open();
        s.Write(data);
    }

    /// <summary>Reads only the header (for the saves list); still checks it against the manifest.</summary>
    public static SaveHeader ReadHeader(byte[] file) => Read(file, headerOnly: true).Header;

    public static SaveArchive FromBytes(byte[] file) => Read(file, headerOnly: false);

    static SaveArchive Read(byte[] file, bool headerOnly)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new SaveCorruptException("not a save file"); }
        using (zip)
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(ReadEntry(zip, ManifestEntry), Json)
                           ?? throw new SaveCorruptException("missing manifest");
            if (manifest.FormatVersion > FormatVersion)
                throw new SaveCorruptException($"save format {manifest.FormatVersion} is newer than this version understands ({FormatVersion})");
            var result = new SaveArchive();
            var header = Verified(zip, manifest, HeaderEntry);
            result.Header = JsonSerializer.Deserialize<SaveHeader>(header, Json) ?? throw new SaveCorruptException("bad header");
            if (headerOnly) return result;
            foreach (var name in manifest.Sha256.Keys)
                if (name != HeaderEntry) result.Parts[name] = Verified(zip, manifest, name);
            return result;
        }
    }

    static byte[] Verified(ZipArchive zip, Manifest m, string name)
    {
        var data = ReadEntry(zip, name);
        if (!m.Sha256.TryGetValue(name, out var expected) || Hash(data) != expected)
            throw new SaveCorruptException($"save part '{name}' is damaged");
        return data;
    }

    static byte[] ReadEntry(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name) ?? throw new SaveCorruptException($"save part '{name}' is missing");
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
