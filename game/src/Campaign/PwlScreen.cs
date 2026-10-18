using System.Xml.Linq;
using Godot;
using H5.Assets.Scenes;
using H5.Core.Xdb;
using H5Reborn.Ui;

namespace H5Reborn.Campaign;

/// <summary>
/// UI/PWLScreen (UIGameRoot ID_PWL_SCREEN): the pre-mission screen. Shows the campaign
/// name and the mission's number, name and briefing (MapDesc), offers the mission's
/// bonuses (Bonus_0..2: icon, description from UI/pwlscreen/&lt;KIND&gt;.txt, toggle
/// select — the BonusSelect button's second state is the "selected" look), then loads
/// the map under the progress bar and waits for a key (setvars pwl_press_any_key_period
/// 1.0 / pwl_press_any_key_fade 0.5) before revealing the adventure map.
/// </summary>
public partial class PwlScreen : UiScreen
{
    readonly CampaignDef _campaign;
    readonly CampaignMission _mission;
    readonly List<int> _bonusSlots = new();
    int _selected = -1;
    enum Phase { Choose, Loading, PressKey }
    Phase _phase = Phase.Choose;
    double _blink;

    const double PressAnyKeyPeriod = 1.0, PressAnyKeyFade = 0.5;

    public PwlScreen(CampaignDef campaign, CampaignMission mission) : base("UI/PWLScreen.(WindowScreen).xdb")
    {
        _campaign = campaign;
        _mission = mission;
        MouseFilter = MouseFilterEnum.Stop;

        Find("CampaignName")?.SetValue("val", campaign.FullName.Length > 0 ? campaign.FullName : campaign.Name);
        // The briefing box carries mission number, name and description itself (MapDesc.txt).
        SetVisible("MissionNumber", false);
        SetVisible("MapName", false);
        var desc = Find("MapDesc");
        desc?.SetValue("mission_number", mission.Description);
        desc?.SetValue("map_name", mission.Name);
        desc?.SetValue("map_desc", MapDescription(mission.MapTag));
        Find("Progress")?.SetMeta("value", 0);
        Find("ProgressBarText")?.SetValue("val", "");

        var bonuses = mission.Bonuses;
        bool anyBonus = bonuses.Any(b => !b.IsNone);
        SetVisible("BonusSelection", true);
        for (int i = 0; i < 3; i++)
        {
            var slot = Find($"Bonus_{i}");
            if (slot == null) continue;
            if (i >= bonuses.Count || bonuses[i].IsNone && anyBonus) { slot.Visible = false; continue; }
            slot.Visible = true;
            _bonusSlots.Add(i);
            var b = bonuses[i];
            FindIn(slot, "BonusDesc")?.SetText(BonusText(b));
            if (FindIn(slot, "BonusIcon") is { } icon && BonusIcon(b) is { } tex)
                icon.AddChild(new TextureRect { Texture = tex, AnchorRight = 1, AnchorBottom = 1, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore });
        }
        if (_bonusSlots.Count == 0) SetVisible("BonusSelection", false);
        else if (_bonusSlots.Count == 1) Select(_bonusSlots[0]);

        Bus.On("select_bonus", (_, node) =>
        {
            for (var n = node as Node; n != null; n = n.GetParent())
                if (n.Name.ToString().StartsWith("Bonus_") && int.TryParse(n.Name.ToString()[6..], out var idx)) { Select(idx); break; }
        });
        Bus.On("start", Start);
    }

    public override void _Ready()
    {
        // Original screens lay out in real pixels (as the main menu and adventure screen do).
        H5Reborn.Settings.VideoSettings.ApplyScaling();
        base._Ready();
        FitBriefing();
        // Dev: `--pwl-autostart` picks the first bonus, starts, and dismisses "press any key".
        if (OS.GetCmdlineUserArgs().Contains("--pwl-autostart"))
        {
            if (_bonusSlots.Count > 0) Select(_bonusSlots[0]);
            _autostart = true;
            Callable.From(Start).CallDeferred();
        }
    }

    bool _autostart;

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationResized) FitBriefing();
    }

    /// <summary>
    /// MapDesc is bottom-anchored with ResizeOnTextSet: the box keeps its bottom edge and grows
    /// upwards to fit the briefing (UiScreen doesn't implement ResizeOnTextSet generally).
    /// </summary>
    void FitBriefing()
    {
        if (Find("MapDesc") is not { } desc || desc.Size.X <= 0) return;
        var markup = UiResources.Instance.Text("UI/pwlscreen/MapDesc.txt") ?? "";
        var values = new Dictionary<string, string>
        {
            ["mission_number"] = _mission.Description, ["map_name"] = _mission.Name, ["map_desc"] = MapDescription(_mission.MapTag),
        };
        var layout = BitmapTextLayout.Build(markup, values, new Rect2(0, 0, desc.Size.X, 4000));
        float bottom = desc.Position.Y + desc.Window.Rect.H;
        float h = Math.Max(desc.Window.Rect.H, layout.Size.Y);
        desc.Position = new Vector2(desc.Position.X, bottom - h);
        desc.Size = new Vector2(desc.Size.X, h);
    }

    static UiNode? FindIn(Node parent, string name)
    {
        foreach (var c in parent.GetChildren())
        {
            if (c is UiNode u && u.Name == name) return u;
            if (FindIn(c, name) is { } deep) return deep;
        }
        return null;
    }

    void Select(int index)
    {
        _selected = index;
        foreach (var i in _bonusSlots)
            if (Find($"Bonus_{i}") is { } slot && FindIn(slot, "BonusSelect") is { } btn) btn.SetState(i == index ? 1 : 0);
    }

    static string Text(string path) => UiResources.Instance.Text(path) ?? "";

    void Start()
    {
        if (_phase != Phase.Choose) return;
        if (_bonusSlots.Count > 1 && _selected < 0)
        {
            Find("ProgressBarText")?.SetValue("val", UiResources.StripMarkup(Text("UI/pwlscreen/NO_BONUS.txt")));
            return;
        }
        _phase = Phase.Loading;
        SetVisible("BonusSelection", false);
        Find("ProgressBarText")?.SetValue("val", UiResources.StripMarkup(Text("UI/pwlscreen/Loading.txt")));
        // Let the loading text draw before the (main-thread) map load blocks for a few seconds.
        GetTree().ProcessFrame += LoadNextFrame;
    }

    int _waitFrames = 2;

    void LoadNextFrame()
    {
        if (--_waitFrames > 0) return;
        GetTree().ProcessFrame -= LoadNextFrame;
        try
        {
            CampaignFlow.StartMission(_campaign, _mission, _selected);
        }
        catch (Exception e)
        {
            GD.PushError($"[H5] could not start {_mission.MapId}: {e}");
            Find("ProgressBarText")?.SetValue("val", $"Could not load {_mission.MapId}");
            _phase = Phase.Choose;
            return;
        }
        _phase = Phase.PressKey;
        Find("ProgressBarText")?.SetValue("val", UiResources.StripMarkup(Text("UI/pwlscreen/PressAnyKey.txt")));
        if (_autostart) GetTree().CreateTimer(1.0).Timeout += () => (GetParent() is CanvasLayer layer ? (Node)layer : this).QueueFree();
    }

    public override void _Process(double delta)
    {
        if (_phase != Phase.PressKey || Find("ProgressBarText") is not { } t) return;
        _blink = (_blink + delta) % (PressAnyKeyPeriod + PressAnyKeyFade);
        double a = _blink < PressAnyKeyPeriod ? 1 : 1 - (_blink - PressAnyKeyPeriod) / PressAnyKeyFade;
        t.Modulate = new Color(1, 1, 1, (float)a);
    }

    public override void _Input(InputEvent e)
    {
        if (_phase != Phase.PressKey) return;
        if (e is InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true })
        {
            GetViewport().SetInputAsHandled();
            (GetParent() is CanvasLayer layer ? (Node)layer : this).QueueFree();
        }
    }

    // ---- bonus presentation ------------------------------------------------------------

    static string MapDescription(string mapTag)
    {
        var xdb = Game.Instance.Xdb;
        if (!Game.Instance.Fs.Exists(mapTag)) return "";
        var root = xdb.Load(mapTag).Root!;
        return xdb.ReadText((string?)root.Element("DescriptionFileRef")?.Attribute("href"), mapTag)?.Trim() ?? "";
    }

    static readonly string[] ResourceIds = { "WOOD", "ORE", "MERCURY", "CRYSTAL", "SULFUR", "GEM", "GOLD" };

    string BonusText(CampaignBonus b)
    {
        var db = Game.Instance.Db;
        string Fill(string file, string name, string? count = null)
        {
            var t = Text($"UI/pwlscreen/{file}.txt").Replace("<value=name>", name);
            return count != null ? t.Replace("<value=count>", count) : t;
        }
        switch (b.Type)
        {
            case "E_BONUS_ARTIFACT":
                return Fill("ARTIFACT", db.Artifacts.TryGetValue(b.Artifact, out var a) ? a.Name : b.Artifact);
            case "E_BONUS_CREATURE":
                return Fill("CREATURE", db.Creatures.TryGetValue(b.Creature, out var c) ? c.Name : b.Creature, b.Count.ToString());
            case "E_BONUS_SPELL":
                return Fill("SPELL", db.Spells.TryGetValue(b.Spell, out var s) ? s.Name : b.Spell);
            case "E_BONUS_RESOURCE":
                var lines = Enumerable.Range(0, 7).Where(r => b.Resources[r] != 0)
                    .Select(r => Fill("RESOURCE", ResourceName(r), b.Resources[r].ToString()));
                return string.Join("<br>", lines);
            case "E_BONUS_BUILDING":
                return Fill("BUILDING", b.Building);
            default:
                return "";
        }
    }

    static string ResourceName(int r)
    {
        var info = "GameMechanics/RefTables/ResourcesInfo.xdb";
        var xdb = Game.Instance.Xdb;
        if (Game.Instance.Fs.Exists(info))
        {
            var items = xdb.Load(info).Root?.Descendants("Item").ToList() ?? new();
            var item = items.FirstOrDefault(i => ((string?)i.Element("ID") ?? "").EndsWith(ResourceIds[r]));
            if (xdb.ReadText((string?)item?.Descendants("NameFileRef").FirstOrDefault()?.Attribute("href"), info) is { } n) return n.Trim();
        }
        return CampaignBonus.ResourceNames[r];
    }

    static Texture2D? BonusIcon(CampaignBonus b)
    {
        var xdb = Game.Instance.Xdb;
        var db = Game.Instance.Db;
        string? Href(XElement? e, string file)
        {
            var h = (string?)e?.Attribute("href");
            return string.IsNullOrEmpty(h) ? null : XdbRef.Parse(h, file).Path;
        }
        try
        {
            switch (b.Type)
            {
                case "E_BONUS_ARTIFACT":
                    const string table = "GameMechanics/RefTables/Artifacts.xdb";
                    var item = xdb.Load(table).Root?.Element("objects")?.Elements("Item").FirstOrDefault(i => (string?)i.Element("ID") == b.Artifact);
                    return Href(item?.Element("obj")?.Element("Icon"), table) is { } ai ? Game.Instance.LoadTexture(ai) : null;
                case "E_BONUS_CREATURE":
                    var visual = db.Creatures.GetValueOrDefault(b.Creature)?.VisualPath;
                    return visual != null && Game.Instance.Fs.Exists(visual) && Href(xdb.Load(visual).Root?.Element("Icon128"), visual) is { } ci
                        ? Game.Instance.LoadTexture(ci) : null;
                case "E_BONUS_SPELL":
                    var spell = db.Spells.GetValueOrDefault(b.Spell)?.SourcePath;
                    return spell != null && Game.Instance.Fs.Exists(spell) && Href(xdb.Load(spell).Root?.Element("Texture"), spell) is { } si
                        ? Game.Instance.LoadTexture(si) : null;
            }
        }
        catch (Exception e) { GD.PushWarning($"[H5] bonus icon {b.Type}: {e.Message}"); }
        return null;
    }
}

/// <summary>
/// UI/MissionResults (UIGameRoot ID_MISSION_RESULTS_SCREEN): "Mission completed" /
/// "Mission failed" (UI/AdventureScreen-heavy/MissionEndWin/Loose.txt) with the mission's
/// description; Close continues the campaign.
/// </summary>
public partial class MissionResultsScreen : UiScreen
{
    readonly Action _onClose;

    public MissionResultsScreen(bool won, string campaignOrMap, int mission, Action onClose) : base("UI/MissionResults.(WindowScreen).xdb")
    {
        _onClose = onClose;
        MouseFilter = MouseFilterEnum.Stop;
        var header = UiResources.Instance.Text(won ? "UI/AdventureScreen-heavy/MissionEndWin.txt" : "UI/AdventureScreen-heavy/MissionEndLoose.txt") ?? (won ? "Mission completed" : "Mission failed");
        Find("Header")?.SetValue("text", UiResources.StripMarkup(header).Trim());
        var c = CampaignFlow.Find(campaignOrMap);
        var m = c != null && mission >= 0 && mission < c.Missions.Count ? c.Missions[mission] : null;
        // Substituted values are plain text (not markup), so no <br> here.
        Find("Description")?.SetValue("text", m != null ? $"{m.Description}: {m.Name}" : campaignOrMap);
        foreach (var n in new[] { "LeftScrollButton", "RightScrollButton" }) SetVisible(n, false);
        // UISSendGameMessage arguments arrive as written in the data ("0 close_mission_results0 0").
        foreach (var name in new[] { "close_mission_results0", "close_mission_results", "0 close_mission_results0 0" })
            Bus.On(name, Close);
    }

    public override void _Ready()
    {
        H5Reborn.Settings.VideoSettings.ApplyScaling();
        base._Ready();
    }

    void Close()
    {
        QueueFree();
        _onClose();
    }
}
