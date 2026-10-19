using System.Collections.Concurrent;

namespace H5.Core.IO;

/// <summary>
/// Read-only view over the original game's .pak archives (zip files).
/// Like the original engine, a path found in several archives resolves per file to the entry with the
/// newest zip timestamp (ties: the archive mounted first); that is how expansion patches (a2p1-*),
/// MMH55's data/MMH55-*.pak and user mods override data.pak.
/// Paths are case-insensitive and use '/' separators without a leading slash.
/// Thread-safe for concurrent reads.
/// Archives over async-only sources (browser Blobs) are mounted with <see cref="MountAsync"/>; their
/// entries are read with <see cref="ReadAllBytesAsync"/>, or prefetched (<see cref="PrefetchAsync"/>,
/// <see cref="RunWithPrefetchAsync{T}"/>) so the synchronous decoders can run unchanged.
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

    readonly ConcurrentDictionary<string, byte[]> _prefetched = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _misses = new(StringComparer.OrdinalIgnoreCase);

    public void Mount(string pakPath) => Add(new PakArchive(pakPath));

    /// <summary>Mounts an archive over a sync-readable source (takes ownership).</summary>
    public void Mount(IRandomAccessSource source) => Add(new PakArchive(source));

    /// <summary>Mounts an archive over any source, reading its directory asynchronously (takes ownership).</summary>
    public async Task MountAsync(IRandomAccessSource source, CancellationToken ct = default) =>
        Add(await PakArchive.OpenAsync(source, ct).ConfigureAwait(false));

    void Add(PakArchive archive)
    {
        _archives.Add(archive);
        foreach (var e in archive.Entries)
        {
            if (e.Name.EndsWith('/')) continue;
            // H5_Game.exe FUN_009def50: when two archives hold the same path, the entry with the strictly
            // newer DOS date/time wins; on a tie the one mounted first stays (see PakLoadOrder).
            var key = Normalize(e.Name);
            if (_index.TryGetValue(key, out var old) && e.DosTime <= old.Entry.DosTime) continue;
            _index[key] = (archive, e);
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
        path = Normalize(path);
        if (!_index.TryGetValue(path, out var e))
            throw new FileNotFoundException($"Not found in game archives: {path}");
        return ReadSync(path, e.Archive, e.Entry);
    }

    public bool TryReadAllBytes(string path, out byte[] data)
    {
        path = Normalize(path);
        if (!_index.TryGetValue(path, out var e)) { data = Array.Empty<byte>(); return false; }
        data = ReadSync(path, e.Archive, e.Entry);
        return true;
    }

    byte[] ReadSync(string path, PakArchive archive, PakArchive.Entry entry)
    {
        if (_prefetched.TryGetValue(path, out var cached)) return cached;
        if (archive.Source.SupportsSyncRead) return archive.Read(entry);
        lock (_misses) _misses.Add(path);
        throw new PakEntryNotReadyException(path);
    }

    /// <summary>Reads an entry without blocking (any source).</summary>
    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
    {
        path = Normalize(path);
        if (!_index.TryGetValue(path, out var e))
            throw new FileNotFoundException($"Not found in game archives: {path}");
        if (_prefetched.TryGetValue(path, out var cached)) return cached;
        return await e.Archive.ReadAsync(e.Entry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads entries of async-only archives into memory so later synchronous reads succeed.
    /// Unknown paths and entries of sync-readable archives are skipped.
    /// </summary>
    public async Task PrefetchAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        foreach (var raw in paths)
        {
            var path = Normalize(raw);
            if (_prefetched.ContainsKey(path) || !_index.TryGetValue(path, out var e) || e.Archive.Source.SupportsSyncRead)
                continue;
            _prefetched[path] = await e.Archive.ReadAsync(e.Entry, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Total bytes held by <see cref="PrefetchAsync"/>.</summary>
    public long PrefetchedBytes => _prefetched.Values.Sum(b => (long)b.LongLength);

    /// <summary>Drops prefetched entries (all, or those under <paramref name="prefix"/>).</summary>
    public void ClearPrefetched(string? prefix = null)
    {
        if (prefix == null) { _prefetched.Clear(); return; }
        prefix = Normalize(prefix);
        foreach (var k in _prefetched.Keys)
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) _prefetched.TryRemove(k, out _);
    }

    /// <summary>
    /// Runs synchronous loading code against async-only archives: every entry it asks for that is not
    /// yet in memory is recorded (even if the caller swallows the <see cref="PakEntryNotReadyException"/>),
    /// prefetched, and <paramref name="work"/> is run again until it completes without misses.
    /// <paramref name="work"/> must be free of side effects other than its result (it may run several times).
    /// </summary>
    public async Task<T> RunWithPrefetchAsync<T>(Func<T> work, int maxRounds = 256, CancellationToken ct = default)
    {
        for (int round = 0; ; round++)
        {
            lock (_misses) _misses.Clear();
            T result = default!;
            PakEntryNotReadyException? miss = null;
            try { result = work(); }
            catch (PakEntryNotReadyException ex) { miss = ex; }
            string[] missing;
            lock (_misses) missing = _misses.ToArray();
            if (missing.Length == 0)
            {
                if (miss != null) throw miss;
                return result;
            }
            if (round + 1 >= maxRounds)
                throw new InvalidOperationException($"RunWithPrefetchAsync: still missing entries after {maxRounds} rounds ({missing[0]})");
            await PrefetchAsync(missing, ct).ConfigureAwait(false);
        }
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
        _prefetched.Clear();
    }
}
