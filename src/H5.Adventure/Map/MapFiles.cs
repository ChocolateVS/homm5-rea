using System.Xml.Linq;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Adventure.Map;

/// <summary>
/// File access for one adventure map. A map lives either inside the game archives
/// (e.g. "Maps/Multiplayer/A2M1/map.xdb") or in a user .h5m file (a zip whose
/// "Maps/.../map.xdb" entries are layered over the game archives). Relative hrefs
/// resolve against the map's directory; absolute ones fall through to the game data.
/// </summary>
public sealed class MapFiles : IDisposable
{
    readonly PakFileSystem _game;
    readonly PakArchive? _h5m;
    readonly Dictionary<string, PakArchive.Entry> _local = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, XDocument> _docs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pak path of the map descriptor, e.g. "Maps/Multiplayer/A2M1/map.xdb".</summary>
    public string MapXdbPath { get; }
    /// <summary>Directory of the descriptor (no trailing slash).</summary>
    public string MapDir { get; }
    /// <summary>Human-readable source: pak path or .h5m file path.</summary>
    public string Source { get; }
    public PakFileSystem Game => _game;

    MapFiles(PakFileSystem game, string mapXdbPath, PakArchive? h5m, string source)
    {
        _game = game;
        _h5m = h5m;
        MapXdbPath = PakFileSystem.Normalize(mapXdbPath);
        MapDir = MapXdbPath.Contains('/') ? MapXdbPath[..MapXdbPath.LastIndexOf('/')] : "";
        Source = source;
        if (h5m != null)
            foreach (var e in h5m.Entries)
                if (!e.Name.EndsWith('/')) _local[PakFileSystem.Normalize(e.Name)] = e;
    }

    public static MapFiles FromPak(PakFileSystem game, string mapXdbPath) =>
        new(game, mapXdbPath, null, mapXdbPath);

    /// <summary>Opens a user map (.h5m). Its first map.xdb entry is the descriptor.</summary>
    public static MapFiles FromH5m(PakFileSystem game, string h5mPath)
    {
        var archive = new PakArchive(h5mPath);
        var desc = archive.Entries.Select(e => e.Name).FirstOrDefault(n => n.EndsWith("/map.xdb", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidDataException($"{h5mPath}: no map.xdb inside");
        return new MapFiles(game, desc, archive, h5mPath);
    }

    public bool Exists(string path)
    {
        path = PakFileSystem.Normalize(path);
        return _local.ContainsKey(path) || _game.Exists(path);
    }

    public byte[] ReadAllBytes(string path)
    {
        path = PakFileSystem.Normalize(path);
        if (_local.TryGetValue(path, out var e)) return _h5m!.Read(e);
        return _game.ReadAllBytes(path);
    }

    public bool TryReadAllBytes(string path, out byte[] data)
    {
        if (!Exists(path)) { data = Array.Empty<byte>(); return false; }
        data = ReadAllBytes(path);
        return true;
    }

    public XDocument LoadXml(string path)
    {
        path = PakFileSystem.Normalize(path);
        if (_docs.TryGetValue(path, out var d)) return d;
        using var ms = new MemoryStream(ReadAllBytes(path));
        d = XDocument.Load(ms);
        _docs[path] = d;
        return d;
    }

    /// <summary>Resolves an href written in a map-local file to a full path.</summary>
    public string Resolve(string? href, string? currentFile = null) =>
        XdbRef.Parse(href, currentFile ?? MapXdbPath).Path;

    public string? ReadText(string? href, string? currentFile = null)
    {
        if (string.IsNullOrEmpty(href)) return null;
        var p = Resolve(href, currentFile);
        return p.Length > 0 && TryReadAllBytes(p, out var b) ? TextDecoding.Decode(b) : null;
    }

    public void Dispose() => _h5m?.Dispose();
}

/// <summary>Where every playable adventure map lives.</summary>
public static class MapCatalog
{
    static readonly string[] PakFolders = { "Maps/Multiplayer/", "Maps/Scenario/", "Maps/SingleMissions/" };

    public sealed record MapRef(string Id, string Source, bool IsUserMap)
    {
        public MapFiles Open(PakFileSystem game) =>
            IsUserMap ? MapFiles.FromH5m(game, Source) : MapFiles.FromPak(game, Source);
        public override string ToString() => Id;
    }

    /// <summary>All stock maps (from the archives) and user maps (install "Maps" folder, *.h5m).</summary>
    public static List<MapRef> All(PakFileSystem game, GameInstall? install)
    {
        var list = new List<MapRef>();
        foreach (var folder in PakFolders)
            foreach (var p in game.Enumerate(folder).Where(p => p.EndsWith("/map.xdb", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
                list.Add(new MapRef(p[..^"/map.xdb".Length], p, false));
        if (install != null)
        {
            var dir = Path.Combine(install.Root, "Maps");
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.h5m").Order(StringComparer.OrdinalIgnoreCase))
                    list.Add(new MapRef("user:" + Path.GetFileNameWithoutExtension(f), f, true));
        }
        return list;
    }

    /// <summary>Finds a map by id, pak path, short name (e.g. "A2M1") or .h5m path.</summary>
    public static MapRef? Find(PakFileSystem game, GameInstall? install, string query)
    {
        if (File.Exists(query) && query.EndsWith(".h5m", StringComparison.OrdinalIgnoreCase))
            return new MapRef("user:" + Path.GetFileNameWithoutExtension(query), Path.GetFullPath(query), true);
        var all = All(game, install);
        query = PakFileSystem.Normalize(query);
        return all.FirstOrDefault(m => m.Id.Equals(query, StringComparison.OrdinalIgnoreCase) || m.Source.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m => m.Id.EndsWith("/" + query, StringComparison.OrdinalIgnoreCase) || m.Id.Equals("user:" + query, StringComparison.OrdinalIgnoreCase));
    }
}
