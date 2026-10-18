using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Godot;
using H5.Core.Ui;
using H5Reborn.Options;
using H5Reborn.Ui;

namespace H5Reborn.Profiles;

/// <summary>
/// Hall of Fame data from UI/UIGameRoot → HallOfFame: the default results shown before
/// any game is won (Single/CampaignDefaultResults), the creature icon earned by a score
/// (Ranks, by SingleMinPointCount/CampaignMinPointCount), and the score factors for map
/// size and difficulty (MapSizes/uiDifficulties PointFactor). Results are kept per
/// profile in user://profiles/&lt;name&gt;/halloffame.json (the original's HallOfFame.sav).
/// </summary>
public sealed class HallOfFame
{
    public const string RootPath = "UI/UIGameRoot.(UIGameRoot).xdb";
    public const int MaxEntries = 10;

    public sealed class Entry
    {
        public string Name { get; set; } = "";
        public int Days { get; set; }
        public int Points { get; set; }
        public bool Campaign { get; set; }
        /// <summary>Scenario/campaign shown in the filter combo ("&lt;size&gt; - &lt;name&gt;").</summary>
        public string Scenario { get; set; } = "";
        public string Info { get; set; } = "";
        public bool Default { get; set; }
    }

    public List<(string Name, int Points)> SingleDefaults = new(), CampaignDefaults = new();
    public List<(string Creature, int Single, int Campaign)> Ranks = new();
    public Dictionary<string, (string Short, float Factor)> MapSizes = new();
    public float[] DifficultyFactors = { 0.8f, 1f, 1.2f, 1.4f };

    static HallOfFame? _data;
    public static HallOfFame Data => _data ??= Load();

    static HallOfFame Load()
    {
        var h = new HallOfFame();
        var xdb = Game.Instance.Xdb;
        var root = xdb.Load(RootPath).Root?.Element("HallOfFame");
        if (root == null) return h;
        string Text(XElement? e) => xdb.ReadText((string?)e?.Attribute("href"), RootPath)?.Trim() ?? "";
        static int I(XElement? e) => int.TryParse(e?.Value, out var v) ? v : 0;
        static float F(XElement? e) => float.TryParse(e?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1;
        foreach (var it in root.Element("SingleDefaultResults")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            h.SingleDefaults.Add((Text(it.Element("NameFileRef")), I(it.Element("PointCount"))));
        foreach (var it in root.Element("CampaignDefaultResults")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            h.CampaignDefaults.Add((Text(it.Element("NameFileRef")), I(it.Element("PointCount"))));
        foreach (var it in root.Element("Ranks")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            h.Ranks.Add(((string?)it.Element("CreatureType") ?? "", I(it.Element("SingleMinPointCount")), I(it.Element("CampaignMinPointCount"))));
        foreach (var it in root.Element("MapSizes")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            h.MapSizes[(string?)it.Element("Size") ?? ""] = (Text(it.Element("ShortNameFileRef")), F(it.Element("PointFactor")));
        var diffs = root.Element("uiDifficulties")?.Elements("Item").Select(e => F(e.Element("PointFactor"))).ToArray();
        if (diffs is { Length: 4 }) h.DifficultyFactors = diffs;
        return h;
    }

    /// <summary>The creature whose icon a score earns (highest rank whose minimum is reached).</summary>
    public string CreatureFor(int points, bool campaign) =>
        Ranks.LastOrDefault(r => points >= (campaign ? r.Campaign : r.Single)).Creature is { Length: > 0 } c ? c : "CREATURE_PEASANT";

    /// <summary>
    /// A finished game's score: <paramref name="basePoints"/> scaled by the data's map size and
    /// difficulty factors. How the original derives the base value (from days, etc.) has not
    /// been recovered from H5_Game.exe; callers supply it.
    /// </summary>
    public int Score(int basePoints, string mapSizeEnum, int difficulty) =>
        (int)Math.Round(basePoints * (MapSizes.TryGetValue(mapSizeEnum, out var s) ? s.Factor : 1f)
                        * DifficultyFactors[Math.Clamp(difficulty, 0, DifficultyFactors.Length - 1)]);

    // ---------------------------------------------------------------- per-profile results

    static string FilePath => $"{ProfileManager.CurrentDir}/halloffame.json";

    public static List<Entry> Results()
    {
        var list = new List<Entry>();
        if (Godot.FileAccess.FileExists(FilePath))
        {
            try { list = JsonSerializer.Deserialize<List<Entry>>(Godot.FileAccess.GetFileAsString(FilePath)) ?? new(); }
            catch (JsonException e) { GD.PushWarning($"[H5] hall of fame: {e.Message}"); }
        }
        if (!list.Any(e => !e.Campaign)) list.AddRange(Data.SingleDefaults.Select(d => new Entry { Name = d.Name, Points = d.Points, Default = true }));
        if (!list.Any(e => e.Campaign)) list.AddRange(Data.CampaignDefaults.Select(d => new Entry { Name = d.Name, Points = d.Points, Campaign = true, Default = true }));
        return list;
    }

    public static void Record(Entry entry)
    {
        var list = Results();
        list.Add(entry);
        var kept = list.Where(e => e.Campaign).OrderByDescending(e => e.Points).Take(MaxEntries)
            .Concat(list.Where(e => !e.Campaign).OrderByDescending(e => e.Points).Take(MaxEntries)).ToList();
        Save(kept);
    }

    public static void Clear() => Save(new List<Entry>());

    static void Save(List<Entry> list)
    {
        DirAccess.MakeDirRecursiveAbsolute(ProfileManager.CurrentDir);
        using var f = Godot.FileAccess.Open(FilePath, Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(JsonSerializer.Serialize(list.Where(e => !e.Default).ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// UI/HallOfFame: Single Scenario / Campaign bookmarks, the ranked table (Rank, Name,
/// Days, Points, Icon = the rank creature's portrait), the scenario filter combo, Clear
/// (UI/MainMenu2/HallOfFame/MsgBoxClear*) and Close.
/// </summary>
public partial class HallOfFameScreen : UiScreen
{
    const string Dir = "UI/MainMenu2/HallOfFame/";
    readonly Action _close;
    bool _campaign;
    readonly HallOfFameRows _rows = new();
    string? _filter;

    public HallOfFameScreen(Action close) : base("UI/HallOfFame.(WindowScreen).xdb")
    {
        _close = close;
        Find("PlayersCont")?.AddChild(_rows);
        Bus.On("close", () => _close());
        Bus.On("bookmark01", () => Show(false));
        Bus.On("bookmark02", () => Show(true));
        Bus.On("clear", () => MessageBoxes.YesNo(this, T("MsgBoxClearHeader.txt", "Clear All Results"),
            T("MsgBoxClearText.txt", "Clear All Results. Are You sure?"), yes => { if (yes) { HallOfFame.Clear(); Show(_campaign); } }));
        if (Find("ScenarioListComboBox") is UiComboBox combo)
            combo.SelectionChanged += i => { _filter = i <= 0 ? null : combo.Items[i]; Refresh(); };
        Show(false);
    }

    static string T(string file, string fallback) => UiResources.Instance.Text(Dir + file)?.Trim() is { Length: > 0 } t ? t : fallback;

    void Show(bool campaign)
    {
        _campaign = campaign;
        _filter = null;
        if (Find("Bookmark01Btn") is { } b1) b1.SetState(campaign ? 0 : 1);
        if (Find("Bookmark02Btn") is { } b2) b2.SetState(campaign ? 1 : 0);
        var entries = HallOfFame.Results().Where(e => e.Campaign == campaign).ToList();
        if (Find("ScenarioListComboBox") is UiComboBox combo)
        {
            var names = new List<string> { campaign ? T("CategoryCampaign.txt", "Campaign") : T("CategorySingle.txt", "Single Scenario") };
            names.AddRange(entries.Where(e => e.Scenario.Length > 0).Select(e => e.Info.Length > 0 ? $"{e.Info} - {e.Scenario}" : e.Scenario).Distinct());
            combo.SetItems(names, 0);
        }
        Refresh();
    }

    void Refresh()
    {
        var entries = HallOfFame.Results().Where(e => e.Campaign == _campaign)
            .Where(e => _filter == null || (e.Info.Length > 0 ? $"{e.Info} - {e.Scenario}" : e.Scenario) == _filter)
            .OrderByDescending(e => e.Points).Take(HallOfFame.MaxEntries).ToList();
        _rows.Set(entries, _campaign);
    }
}

/// <summary>The table rows, in the header columns' geometry (Rank 65, Name 345, Days 80, Points 87, Icon 70).</summary>
public partial class HallOfFameRows : Control
{
    // Column x offsets relative to PlayersCont (x=138) from the stock header windows.
    static readonly (float X, float W)[] Cols = { (0, 65), (69, 345), (418, 80), (502, 87), (593, 70) };
    const float Top = 30, RowH = 41;
    List<HallOfFame.Entry> _entries = new();
    bool _campaign;
    readonly string _rank = UiResources.Instance.Text("UI/MainMenu2/HallOfFame/RankItem.txt") ?? "<h3><center><value=value>";
    readonly string _name = UiResources.Instance.Text("UI/MainMenu2/HallOfFame/NameItem.txt") ?? "<h3><value=value>";
    readonly string _days = UiResources.Instance.Text("UI/MainMenu2/HallOfFame/DaysItem.txt") ?? "<h3><center><value=value>";
    readonly string _points = UiResources.Instance.Text("UI/MainMenu2/HallOfFame/PointsItem.txt") ?? "<h3><center><value=value>";

    public HallOfFameRows()
    {
        AnchorRight = 1;
        AnchorBottom = 1;
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.ParentNode;
    }

    public void Set(List<HallOfFame.Entry> entries, bool campaign) { _entries = entries; _campaign = campaign; QueueRedraw(); }

    public override void _Draw()
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            float y = Top + i * RowH;
            void Cell(int c, string fmt, string value) =>
                BitmapTextLayout.Build(fmt, new Dictionary<string, string> { ["value"] = value }, new Rect2(Cols[c].X, y, Cols[c].W, RowH), UiAlign.Center, wrap: false).Draw(this);
            Cell(0, _rank, (i + 1).ToString());
            Cell(1, _name, e.Name.Replace('<', '‹').Replace('>', '›'));
            Cell(2, _days, e.Days > 0 ? e.Days.ToString() : "");
            Cell(3, _points, e.Points.ToString());
            if (Icon(HallOfFame.Data.CreatureFor(e.Points, _campaign)) is { } tex)
            {
                float s = RowH - 4;
                DrawTextureRect(tex, new Rect2(Cols[4].X + (Cols[4].W - s) / 2, y + 2, s, s), false);
            }
        }
    }

    static readonly Dictionary<string, Texture2D?> _icons = new();

    static Texture2D? Icon(string creature)
    {
        if (_icons.TryGetValue(creature, out var t)) return t;
        t = Combat.CombatIcons.Creature(creature, large: false) is { } icon ? UiResources.Instance.Texture(icon) : null;
        _icons[creature] = t;
        return t;
    }
}
