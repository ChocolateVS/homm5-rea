using H5.Adventure.State;
using H5.Core.Ui;
using H5.Core.Xdb;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// The rumours page of the tavern screen (town and map): each entry formatted by 008e7d90 (the entry's format file,
/// &lt;value=text&gt; = its text, &lt;value=player&gt; = the player colour's textFormat + text from
/// UI/RefTables/PlayerColourSchemes, table 01022ce8 entries +0x10 and +4 joined by 0054dfa0), joined by 006372f3 case 2:
/// FIRST_RUMOR with cur_rumor = entry 0, then RUMORS_FORMAT with prev_rumors = the text so far and cur_rumor = the entry.
/// </summary>
public static class RumourText
{
    const string RumourTexts = "UI/Tavern/RumorsFormats.(WindowRelatedTexts).xdb";

    public static string Entry(RumourEntry e, AdvState s)
    {
        var fmt = Ui.UiResources.Instance.Text(e.Format) ?? "";
        string player = "";
        if (e.Player > 0 && s.Players.Find(p => p.Index == e.Player - 1) is { } p) player = PlayerName(p.Colour);
        return fmt.Replace("<value=text>", e.Text).Replace("<value=player>", player).Trim();
    }

    public static string Page(IReadOnlyList<RumourEntry> list, AdvState s)
    {
        string first = UiGameRoot.RelatedTextAt(RumourTexts, "FIRST_RUMOR") ?? "<value=cur_rumor>";
        string next = UiGameRoot.RelatedTextAt(RumourTexts, "RUMORS_FORMAT") ?? "<value=prev_rumors><br><value=cur_rumor>";
        string text = "";
        for (int i = 0; i < list.Count; i++)
        {
            var rumour = Entry(list[i], s);
            text = i == 0 ? first.Replace("<value=cur_rumor>", rumour)
                : next.Replace("<value=prev_rumors>", text).Replace("<value=cur_rumor>", rumour);
        }
        return text;
    }

    /// <summary>
    /// Shows the page text: the screen puts a RumorsText clone into ScrollContainer (vfunc 8, 00637b50), which
    /// scrolls it. Returns the shown node (pass it back to replace it on the next refresh).
    /// </summary>
    public static UiNode? Fill(UiScreen ui, UiNode? shown, IReadOnlyList<RumourEntry> list, AdvState s)
    {
        if (ui.Find("tavern-rumors-window/RumorsText") is not { } template || ui.Find("tavern-rumors-window/ScrollContainer") is not { } box) return shown;
        template.Visible = false;
        var sc = box as UiScrollableContainer;
        sc?.ClearItems();
        if (shown != null) ui.RemoveInstance(shown);
        var node = ui.Instantiate(template.Window, box.Window, new Int2(0, 0), "RumorsTextShown");
        node.SetText(Page(list, s));
        sc?.AddItem(node);
        return node;
    }

    static Dictionary<string, string>? _names;

    /// <summary>PlayerColourSchemes: textFormat (e.g. &lt;color=FFFF1212&gt;) followed by text ("Red").</summary>
    static string PlayerName(string pcolor)
    {
        if (_names == null)
        {
            _names = new(StringComparer.OrdinalIgnoreCase);
            const string table = "UI/RefTables/PlayerColourSchemes.xdb";
            try
            {
                if (Game.Instance.Fs.Exists(table))
                    foreach (var item in Game.Instance.Xdb.Load(table).Root?.Element("objects")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                    {
                        var obj = item.Element("obj");
                        string T(string field) => (string?)obj?.Element(field)?.Attribute("href") is { Length: > 0 } h
                            ? Ui.UiResources.Instance.Text(XdbRef.Parse(h, table).Path)?.Trim() ?? "" : "";
                        if ((string?)item.Element("ID") is { } id) _names[id] = T("textFormat") + T("text");
                    }
            }
            catch { /* no data */ }
        }
        var key = pcolor.StartsWith("PCOLOR_", StringComparison.OrdinalIgnoreCase) ? pcolor : "PCOLOR_" + pcolor;
        return _names.TryGetValue(key, out var n) ? n : "";
    }
}
