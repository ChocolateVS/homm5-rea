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

/// <summary>Loads and caches .xdb XML documents and resolves hrefs between them.</summary>
public sealed class XdbLoader(PakFileSystem fs)
{
    readonly Dictionary<string, XDocument> _cache = new(StringComparer.OrdinalIgnoreCase);

    public PakFileSystem FileSystem => fs;

    public XDocument Load(string path)
    {
        path = PakFileSystem.Normalize(path);
        lock (_cache)
        {
            if (_cache.TryGetValue(path, out var doc)) return doc;
            using var ms = new MemoryStream(fs.ReadAllBytes(path));
            doc = XDocument.Load(ms);
            _cache[path] = doc;
            return doc;
        }
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
