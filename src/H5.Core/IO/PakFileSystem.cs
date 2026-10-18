namespace H5.Core.IO;

/// <summary>
/// Read-only view over the original game's .pak archives (zip files).
/// Archives are layered in load order: an entry in a later archive overrides
/// the same path in an earlier one, matching how the original engine applies
/// expansion patches (a2p1-*) and user mods on top of data.pak.
/// Paths are case-insensitive and use '/' separators without a leading slash.
/// Thread-safe for concurrent reads.
/// </summary>
public sealed class PakFileSystem : IDisposable
{
    readonly List<PakArchive> _archives = new();
    readonly Dictionary<string, (PakArchive Archive, PakArchive.Entry Entry)> _index = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Paths => _index.Keys;

    public static PakFileSystem OpenInstall(GameInstall install)
    {
        var fs = new PakFileSystem();
        foreach (var pak in install.EnumeratePaks())
            fs.Mount(pak);
        return fs;
    }

    public void Mount(string pakPath)
    {
        var archive = new PakArchive(pakPath);
        _archives.Add(archive);
        foreach (var e in archive.Entries)
        {
            if (e.Name.EndsWith('/')) continue;
            _index[Normalize(e.Name)] = (archive, e);
        }
    }

    public static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        return path.StartsWith('/') ? path[1..] : path;
    }

    public bool Exists(string path) => _index.ContainsKey(Normalize(path));

    public long SizeOf(string path) => _index.TryGetValue(Normalize(path), out var e) ? e.Entry.Size : -1;

    public byte[] ReadAllBytes(string path)
    {
        if (!_index.TryGetValue(Normalize(path), out var e))
            throw new FileNotFoundException($"Not found in game archives: {path}");
        return e.Archive.Read(e.Entry);
    }

    public bool TryReadAllBytes(string path, out byte[] data)
    {
        if (!_index.TryGetValue(Normalize(path), out var e)) { data = Array.Empty<byte>(); return false; }
        data = e.Archive.Read(e.Entry);
        return true;
    }

    public IEnumerable<string> Enumerate(string prefix)
    {
        prefix = Normalize(prefix);
        return _index.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Which archive currently supplies the given path (for diagnostics).</summary>
    public string? SourceOf(string path) =>
        _index.TryGetValue(Normalize(path), out var e) ? e.Archive.FilePath : null;

    public void Dispose()
    {
        foreach (var a in _archives) a.Dispose();
        _archives.Clear();
        _index.Clear();
    }
}
