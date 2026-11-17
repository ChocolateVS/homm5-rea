using System.Xml.Linq;
using H5.Core.Xdb;

namespace H5Reborn.Ui;

/// <summary>
/// The stock UI/UIGameRoot.(UIGameRoot).xdb id table: named texts, textures, sounds,
/// windows and WindowRelatedTexts the engine looks up by id (FUN_00401b20 + FUN_007c3f50
/// / 007c3e80 / 007c4b30 call sites). Hrefs are relative to UI/.
/// Each kind is its own table in the exe (007c3f50 texts at +0x74, 007c3e80 at +0x68, 007c4b30 at +0x11c, …), searched
/// in order with a case-sensitive compare of equal length (strncmp), the first match wins — so "HIRE_CREATURES"
/// (a Screen) and "hire_creatures" (RelatedTexts) are different entries.
/// </summary>
public static class UiGameRoot
{
    const string Path = "UI/UIGameRoot.(UIGameRoot).xdb";
    static Dictionary<(string Kind, string Id), string>? _items;
    static readonly Dictionary<string, Dictionary<string, string>> _related = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, Dictionary<string, string>> _relatedById = new(StringComparer.Ordinal);

    /// <summary>(element kind, ID) → href; ordinal (case-sensitive) and the first entry of a kind wins.</summary>
    static Dictionary<(string Kind, string Id), string> Items
    {
        get
        {
            if (_items != null) return _items;
            _items = new();
            var root = Game.Instance.Xdb.Load(Path).Root;
            if (root == null) return _items;
            foreach (var item in root.Descendants("Item"))
            {
                var id = (string?)item.Element("ID");
                if (string.IsNullOrEmpty(id)) continue;
                var refEl = item.Elements().FirstOrDefault(e => e.Name != "ID" && e.Attribute("href") != null);
                if (refEl == null) continue;
                _items.TryAdd((refEl.Name.LocalName, id), (string)refEl.Attribute("href")!);
            }
            return _items;
        }
    }

    static string? Resolve(string kind, string id) =>
        Items.TryGetValue((kind, id), out var href) && href.Length > 0 ? XdbRef.Parse(href, Path).Path : null;

    /// <summary>Text of a TextFileRef id (raw markup), or null.</summary>
    public static string? Text(string id) =>
        Resolve("TextFileRef", id) is { } p ? UiResources.Instance.Text(p) : null;

    /// <summary>Pak path of a Texture id (the (Texture) xdb), or null.</summary>
    public static string? Texture(string id) => Resolve("Texture", id);

    /// <summary>Pak path of a Button id (e.g. a (JogDialButton) xdb), or null.</summary>
    public static string? Button(string id) => Resolve("Button", id);

    /// <summary>A text of a RelatedTexts id (WindowRelatedTexts TextName → text), or null.</summary>
    public static string? RelatedText(string id, string textName)
    {
        if (!_relatedById.TryGetValue(id, out var map))
        {
            map = new(StringComparer.OrdinalIgnoreCase);
            if (Resolve("RelatedTexts", id) is { } p && Game.Instance.Fs.Exists(p) && Game.Instance.Xdb.Load(p).Root is XElement r)
                foreach (var item in r.Descendants("Item"))
                    if ((string?)item.Element("TextName") is { } n && (string?)item.Element("text")?.Attribute("href") is { } h)
                        map[n] = XdbRef.Parse(h, p).Path;
            _relatedById[id] = map;
        }
        return map.TryGetValue(textName, out var tp) ? UiResources.Instance.Text(tp) : null;
    }

    /// <summary>Loads a WindowRelatedTexts xdb by path (TextName → text).</summary>
    public static string? RelatedTextAt(string relatedXdb, string textName)
    {
        if (!_related.TryGetValue(relatedXdb, out var map))
        {
            map = new(StringComparer.OrdinalIgnoreCase);
            if (Game.Instance.Fs.Exists(relatedXdb) && Game.Instance.Xdb.Load(relatedXdb).Root is XElement r)
                foreach (var item in r.Descendants("Item"))
                    if ((string?)item.Element("TextName") is { } n && (string?)item.Element("text")?.Attribute("href") is { } h)
                        map[n] = XdbRef.Parse(h, relatedXdb).Path;
            _related[relatedXdb] = map;
        }
        return map.TryGetValue(textName, out var tp) ? UiResources.Instance.Text(tp) : null;
    }
}
