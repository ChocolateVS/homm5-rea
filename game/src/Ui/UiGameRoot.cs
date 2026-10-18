using System.Xml.Linq;
using H5.Core.Xdb;

namespace H5Reborn.Ui;

/// <summary>
/// The stock UI/UIGameRoot.(UIGameRoot).xdb id table: named texts, textures, sounds,
/// windows and WindowRelatedTexts the engine looks up by id (FUN_00401b20 + FUN_007c3f50
/// / 007c3e80 / 007c4b30 call sites). Hrefs are relative to UI/.
/// </summary>
public static class UiGameRoot
{
    const string Path = "UI/UIGameRoot.(UIGameRoot).xdb";
    static Dictionary<string, (string Kind, string Href)>? _items;
    static readonly Dictionary<string, Dictionary<string, string>> _related = new(StringComparer.OrdinalIgnoreCase);

    static Dictionary<string, (string Kind, string Href)> Items
    {
        get
        {
            if (_items != null) return _items;
            _items = new(StringComparer.OrdinalIgnoreCase);
            var root = Game.Instance.Xdb.Load(Path).Root;
            if (root == null) return _items;
            foreach (var item in root.Descendants("Item"))
            {
                var id = (string?)item.Element("ID");
                if (string.IsNullOrEmpty(id)) continue;
                var refEl = item.Elements().FirstOrDefault(e => e.Name != "ID" && e.Attribute("href") != null);
                if (refEl == null) continue;
                _items.TryAdd(id, (refEl.Name.LocalName, (string)refEl.Attribute("href")!));
            }
            return _items;
        }
    }

    static string? Resolve(string id) =>
        Items.TryGetValue(id, out var it) && it.Href.Length > 0 ? XdbRef.Parse(it.Href, Path).Path : null;

    /// <summary>Text of a TextFileRef id (raw markup), or null.</summary>
    public static string? Text(string id) =>
        Resolve(id) is { } p ? UiResources.Instance.Text(p) : null;

    /// <summary>Pak path of a Texture id (the (Texture) xdb), or null.</summary>
    public static string? Texture(string id) => Resolve(id);

    /// <summary>A text of a RelatedTexts id (WindowRelatedTexts TextName → text), or null.</summary>
    public static string? RelatedText(string id, string textName)
    {
        if (!_related.TryGetValue(id, out var map))
        {
            map = new(StringComparer.OrdinalIgnoreCase);
            if (Resolve(id) is { } p && Game.Instance.Fs.Exists(p) && Game.Instance.Xdb.Load(p).Root is XElement r)
                foreach (var item in r.Descendants("Item"))
                    if ((string?)item.Element("TextName") is { } n && (string?)item.Element("text")?.Attribute("href") is { } h)
                        map[n] = XdbRef.Parse(h, p).Path;
            _related[id] = map;
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
