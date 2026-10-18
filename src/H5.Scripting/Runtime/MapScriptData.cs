using System.Xml.Linq;
using H5.Adventure.Map;
using H5.Core.Data;

namespace H5.Scripting.Runtime;

/// <summary>A script region from map.xdb &lt;regions&gt;: an inclusive tile rectangle on a floor.</summary>
public sealed class MapRegion
{
    public string Name = "";
    public int Floor;
    public int X1, Y1, X2, Y2;
    /// <summary>Function names set in the editor (empty = none).</summary>
    public string EnterAndStop = "", EnterWithoutStop = "", Exit = "";

    public bool Contains(Tile t) => t.Floor == Floor && t.X >= X1 && t.X <= X2 && t.Y >= Y1 && t.Y <= Y2;

    public IEnumerable<Tile> Tiles()
    {
        for (int y = Y1; y <= Y2; y++)
            for (int x = X1; x <= X2; x++)
                yield return new Tile(x, y, Floor);
    }
}

/// <summary>Extra objective data used by script functions (state machine and visibility).</summary>
public sealed class ObjectiveDef
{
    public string Name = "";
    public int Player = -1;           // -1 = common
    public bool Primary;
    public string Kind = "";
    public bool InitiallyActive = true;
    public bool InitiallyVisible = true;
    public int ProgressSteps;
    public List<string> Dependencies = new();
}

/// <summary>An adventure-map dialog (StartAdvMapDialog index): sentences with text and voice.</summary>
public sealed class AdvMapDialog
{
    public int Index;
    public List<(string Text, string Sound, double Pause)> Sentences = new();
}

/// <summary>
/// The parts of map.xdb that only scripts use: regions, editor-set trigger functions (map, regions,
/// objects), objective details, adventure-map dialogs and save-file names.
/// </summary>
public sealed class MapScriptData
{
    public string MapXdb = "";
    public List<MapRegion> Regions = new();
    public Dictionary<string, MapRegion> RegionByName = new(StringComparer.Ordinal);
    public string NewDayTrigger = "", WarFogEnterTrigger = "";
    /// <summary>Per object uid: trigger element name → function name (e.g. "LossTrigger" → "OnHeroLost").</summary>
    public Dictionary<string, Dictionary<string, string>> ObjectTriggers = new(StringComparer.Ordinal);
    /// <summary>Per object uid: CombatScript path (heroes, monsters).</summary>
    public Dictionary<string, string> CombatScripts = new(StringComparer.Ordinal);
    public List<ObjectiveDef> Objectives = new();
    public List<AdvMapDialog> Dialogs = new();
    public List<string> SaveFilenames = new();

    public static MapScriptData Load(MapFiles files)
    {
        var d = new MapScriptData { MapXdb = files.MapXdbPath };
        var root = files.LoadXml(files.MapXdbPath).Root!;

        foreach (var r in root.Items("regions"))
        {
            var rect = r.Element("Rect");
            var reg = new MapRegion
            {
                Name = r.Str("Name"),
                Floor = r.Int("Floor"),
                X1 = rect.Int("x1"), Y1 = rect.Int("y1"), X2 = rect.Int("x2"), Y2 = rect.Int("y2"),
                EnterAndStop = TriggerFunction(r.Element("EnterAndStopTrigger")),
                EnterWithoutStop = TriggerFunction(r.Element("EnterWithoutStopTrigger")),
                Exit = TriggerFunction(r.Element("ExitTrigger")),
            };
            if (reg.X1 > reg.X2) (reg.X1, reg.X2) = (reg.X2, reg.X1);
            if (reg.Y1 > reg.Y2) (reg.Y1, reg.Y2) = (reg.Y2, reg.Y1);
            d.Regions.Add(reg);
            if (reg.Name.Length > 0) d.RegionByName.TryAdd(reg.Name, reg);
        }

        d.NewDayTrigger = TriggerFunction(root.Element("NewDayTrigger"));
        d.WarFogEnterTrigger = TriggerFunction(root.Element("WarFogEnterTrigger"));

        foreach (var item in root.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var uid = (string?)item.Attribute("id") ?? "";
            var e = item.Elements().FirstOrDefault();
            if (e == null || uid.Length == 0) continue;
            foreach (var t in e.Descendants().Where(x => x.Name.LocalName.EndsWith("Trigger", StringComparison.Ordinal)))
            {
                var fn = TriggerFunction(t);
                if (fn.Length == 0) continue;
                if (!d.ObjectTriggers.TryGetValue(uid, out var map)) d.ObjectTriggers[uid] = map = new(StringComparer.Ordinal);
                map[t.Name.LocalName] = fn;
            }
            // Seer Hut quests are objectives too (GetObjectiveState(questName)); unknown until taken.
            if (e.Name.LocalName == "AdvMapSeerHut" && e.Element("Quest") is { } quest && quest.Str("Name").Length > 0)
                d.Objectives.Add(new ObjectiveDef
                {
                    Name = quest.Str("Name"), Player = -1, Primary = false, Kind = quest.Str("Kind"),
                    InitiallyActive = false, InitiallyVisible = true,
                    ProgressSteps = quest.Element("ProgressCommentsFileRef")?.Elements("Item").Count() ?? 0,
                });
            var cs = e.Element("CombatScript");
            var csHref = (string?)cs?.Attribute("href");
            if (!string.IsNullOrEmpty(csHref)) d.CombatScripts[uid] = files.Resolve(csHref, files.MapXdbPath);
        }

        var objectives = root.Element("Objectives");
        foreach (var (group, primary) in new[] { ("Primary", true), ("Secondary", false) })
        {
            var g = objectives?.Element(group);
            if (g == null) continue;
            foreach (var o in g.Element("Common").Items("Objectives")) d.Objectives.Add(ReadObjective(o, primary, -1));
            int player = 0;
            foreach (var ps in g.Items("PlayerSpecific"))
            {
                foreach (var o in ps.Items("Objectives")) d.Objectives.Add(ReadObjective(o, primary, player));
                player++;
            }
        }

        int di = 0;
        foreach (var dl in root.Items("dialogs"))
        {
            var dialog = new AdvMapDialog { Index = di++ };
            foreach (var s in dl.Items("Sentences"))
                dialog.Sentences.Add((files.Resolve(s.Href("Text"), files.MapXdbPath), files.Resolve(s.Href("Sound"), files.MapXdbPath), s.Dbl("Pause")));
            d.Dialogs.Add(dialog);
        }

        foreach (var s in root.Element("SavesFilenames")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var href = (string?)s.Attribute("href");
            if (!string.IsNullOrEmpty(href)) d.SaveFilenames.Add(files.Resolve(href, files.MapXdbPath));
        }
        return d;
    }

    static ObjectiveDef ReadObjective(XElement o, bool primary, int player) => new()
    {
        Name = o.Str("Name"),
        Player = player,
        Primary = primary,
        Kind = o.Str("Kind"),
        InitiallyActive = o.Bool("IsInitialyActive", true),
        InitiallyVisible = o.Bool("IsInitialyVisible", true),
        ProgressSteps = o.Element("ProgressCommentsFileRef")?.Elements("Item").Count() ?? 0,
        Dependencies = o.StrItems("Dependencies"),
    };

    static string TriggerFunction(XElement? trigger) =>
        trigger?.Element("Action")?.Element("FunctionName")?.Value.Trim() ?? "";
}
