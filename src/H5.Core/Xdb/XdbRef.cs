using System.Xml.Linq;
using H5.Core.IO;

namespace H5.Core.Xdb;

/// <summary>
/// An href as written in .xdb files, e.g.
///   "/GameMechanics/Creature/Creatures/Haven/Footman.xdb#xpointer(/Creature)"
///   "#n:inline(Material)"            (object embedded in the current file)
///   "Footman.xdb#xpointer(/Creature)" (relative to the referencing file)
/// </summary>
public readonly record struct XdbRef(string Path, string? Pointer)
{
    public bool IsEmpty => string.IsNullOrEmpty(Path);

    public static XdbRef Parse(string? href, string? currentFile = null)
    {
        if (string.IsNullOrEmpty(href)) return default;
        string path = href, pointer = null!;
        var hash = href.IndexOf('#');
        if (hash >= 0) { path = href[..hash]; pointer = href[(hash + 1)..]; }
        if (path.Length == 0) path = currentFile ?? "";
        else if (!path.StartsWith('/') && currentFile != null)
        {
            var dir = currentFile.Contains('/') ? currentFile[..currentFile.LastIndexOf('/')] : "";
            path = dir.Length > 0 ? dir + "/" + path : path;
        }
        return new XdbRef(PakFileSystem.Normalize(path), pointer);
    }
}

/// <summary>
/// Loads and caches .xdb XML documents and resolves hrefs between them. The cache is bounded: least recently used
/// documents are dropped once the cached files' total size passes <see cref="MaxCachedBytes"/> (a dropped document is
/// parsed again on its next use; elements callers still hold stay valid). Before, every document read in a session stayed
/// parsed for good (the browser build's memory ceiling).
/// </summary>
public sealed class XdbLoader(PakFileSystem fs)
{
    /// <summary>Default for <see cref="MaxCachedBytes"/>: 32 MB of xdb text (a parsed document takes several times that).</summary>
    public static long DefaultMaxCachedBytes = 32L << 20;

    sealed class Entry(string path, XDocument doc, long bytes)
    {
        public readonly string Path = path;
        public readonly XDocument Doc = doc;
        public readonly long Bytes = bytes;
        public LinkedListNode<Entry>? Node;
    }

    readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<Entry> _lru = new();

    public PakFileSystem FileSystem => fs;

    /// <summary>Total size of the cached documents' files; see <see cref="DefaultMaxCachedBytes"/>.</summary>
    public long MaxCachedBytes { get; set; } = DefaultMaxCachedBytes;
    public long CachedBytes { get; private set; }
    public int CachedCount { get { lock (_cache) return _cache.Count; } }
    public long Evictions { get; private set; }

    public XDocument Load(string path)
    {
        path = PakFileSystem.Normalize(path);
        lock (_cache)
        {
            if (_cache.TryGetValue(path, out var hit))
            {
                if (hit.Node!.Previous != null) { _lru.Remove(hit.Node); _lru.AddFirst(hit.Node); }
                return hit.Doc;
            }
            var bytes = fs.ReadAllBytes(path);
            XDocument doc;
            try { doc = XDocument.Load(new MemoryStream(bytes)); }
            catch (System.Xml.XmlException) { doc = XdbRepair.Load(bytes); }
            var e = new Entry(path, doc, bytes.Length);
            e.Node = _lru.AddFirst(e);
            _cache[path] = e;
            CachedBytes += e.Bytes;
            while (CachedBytes > MaxCachedBytes && _lru.Last is { } last && last != e.Node)
            {
                _lru.RemoveLast();
                _cache.Remove(last.Value.Path);
                CachedBytes -= last.Value.Bytes;
                Evictions++;
            }
            return doc;
        }
    }

    /// <summary>Drops every cached document (they are parsed again on next use).</summary>
    public void Clear()
    {
        lock (_cache) { _cache.Clear(); _lru.Clear(); CachedBytes = 0; }
    }

    /// <summary>
    /// Resolves an element that carries an href attribute. Inline objects
    /// ("#n:inline(Type)") are the element's own child; external ones are the
    /// root element of the referenced file.
    /// </summary>
    public XElement? Resolve(XElement? refElement, string currentFile)
    {
        if (refElement == null) return null;
        var href = (string?)refElement.Attribute("href");
        if (string.IsNullOrEmpty(href)) return refElement.HasElements ? refElement : null;
        if (href.StartsWith("#n:inline")) return refElement.Elements().FirstOrDefault();
        var r = XdbRef.Parse(href, currentFile);
        if (r.IsEmpty || !fs.Exists(r.Path)) return null;
        return Load(r.Path).Root;
    }

    /// <summary>Reads a text file referenced from xdb (UTF-16 LE with BOM in stock data).</summary>
    public string? ReadText(string? href, string? currentFile = null)
    {
        var r = XdbRef.Parse(href, currentFile);
        if (r.IsEmpty || !fs.TryReadAllBytes(r.Path, out var bytes)) return null;
        return TextDecoding.Decode(bytes);
    }
}

public static class TextDecoding
{
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
