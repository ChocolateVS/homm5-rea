using System.Xml.Linq;
using Godot;
using H5.Assets.Scenes;
using H5.Core.Ui;
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
    enum Phase { LoadingWorld, Choose, Loading, PressKey }
    Phase _phase = Phase.LoadingWorld;
    Adventure.AdventureSession? _session;
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
        ShowPicture(MapPicture(mission.MapTag));

        // 005a1a50: the world is built first, under the progress bar; the bonuses are offered afterwards (ShowBonuses).
        SetVisible("BonusSelection", false);
        Find("ProgressBarText")?.SetValue("val", UiResources.StripMarkup(Text("UI/pwlscreen/Loading.txt")));

        void OnBonus(Node? node, bool start)
        {
            for (var n = node; n != null; n = n.GetParent())
                if (n.Name.ToString().StartsWith("Bonus_") && n.HasMeta("bonus")) { Select(n.GetMeta("bonus").AsInt32()); break; }
            if (start) Start();
        }
        Bus.On("select_bonus", (_, node) => OnBonus(node, false));
        // 006b2ad0: select_bonus_and_start selects the bonus and starts at once.
        Bus.On("select_bonus_and_start", (_, node) => OnBonus(node, true));
        Bus.On("start", Start);
    }

    public override void _Ready()
    {
        // Original screens lay out in real pixels (as the main menu and adventure screen do).
        H5Reborn.Settings.VideoSettings.ApplyScaling();
        base._Ready();
        FitBriefing();
        // Dev: `--pwl-autostart` picks the first bonus, starts, and dismisses "press any key".
        _autostart = OS.GetCmdlineUserArgs().Contains("--pwl-autostart");
        // Let the loading text draw before the (main-thread) world load blocks.
        GetTree().ProcessFrame += LoadWorldNextFrame;
    }

    int _worldFrames = 2;

    void LoadWorldNextFrame()
    {
        if (--_worldFrames > 0) return;
        GetTree().ProcessFrame -= LoadWorldNextFrame;
        try
        {
            _session = CampaignFlow.LoadMission(_campaign, _mission);
        }
        catch (Exception e)
        {
            GD.PushError($"[H5] could not start {_mission.MapId}: {e}");
            Find("ProgressBarText")?.SetValue("val", $"Could not load {_mission.MapId}");
            return;
        }
        ShowBonuses(_session);
    }

    /// <summary>
    /// 006b38d0, once the world exists: only the bonuses valid for it (006b25e0) are offered, packed into Bonus_0.. in
    /// order. No valid bonus → no selection panel and the mission starts (005a1a50 → 006b21c0). Nothing is preselected:
    /// Start stays disabled with the NO_BONUS tooltip until a bonus is chosen (006b1eb0, CAN_START once one is).
    /// </summary>
    void ShowBonuses(Adventure.AdventureSession session)
    {
        var bonuses = _mission.Bonuses;
        var valid = CampaignFlow.OfferedBonuses(session, _mission);
        for (int w = 0; w < 3; w++)
        {
            var slot = Find($"Bonus_{w}");
            if (slot == null) continue;
            if (w >= valid.Count) { slot.Visible = false; continue; }
            int i = valid[w];
            slot.Visible = true;
            slot.SetMeta("bonus", i);
            _bonusSlots.Add(i);
            var b = bonuses[i];
            FindIn(slot, "BonusDesc")?.SetText(BonusText(b));
            if (FindIn(slot, "BonusIcon") is { } icon && BonusIcon(b) is { } tex)
                icon.AddChild(new TextureRect { Texture = tex, AnchorRight = 1, AnchorBottom = 1, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore });
        }
        _phase = Phase.Choose;
        if (_bonusSlots.Count == 0) { Start(); return; }
        Find("ProgressBarText")?.SetValue("val", "");
        SetVisible("BonusSelection", true);
        UpdateStart();
        if (_autostart)
        {
            Select(_bonusSlots[0]);
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
        for (int w = 0; w < _bonusSlots.Count; w++)
            if (Find($"Bonus_{w}") is { } slot && FindIn(slot, "BonusSelect") is { } btn) btn.SetState(_bonusSlots[w] == index ? 1 : 0);
        UpdateStart();
    }

    bool CanStart => _bonusSlots.Count == 0 || _selected >= 0;

    /// <summary>006b1eb0: Start is enabled once a bonus is chosen; its tooltip is NO_BONUS / CAN_START.</summary>
    void UpdateStart()
    {
        if (_bonusSlots.Count == 0 || Find("Start") is not { } start) return;
        start.Enabled = CanStart;
        start.TooltipText = UiResources.StripMarkup(Text(CanStart ? "UI/pwlscreen/CAN_START.txt" : "UI/pwlscreen/NO_BONUS.txt"));
    }

    static string Text(string path) => UiResources.Instance.Text(path) ?? "";

    void Start()
    {
        if (_phase != Phase.Choose) return;
        if (!CanStart) return;
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
            // 006b29d0: the bonus's 1-based position among the offered ones goes to the player (vf 0x230).
            _loading = CampaignFlow.OpenMission(_campaign, _mission, _session!, _selected, _bonusSlots.IndexOf(_selected) + 1);
        }
        catch (Exception e)
        {
            GD.PushError($"[H5] could not start {_mission.MapId}: {e}");
            Find("ProgressBarText")?.SetValue("val", $"Could not load {_mission.MapId}");
            _phase = Phase.Choose;
            return;
        }
        // The adventure screen builds the map over the next frames: the progress bar follows it (_Process).
        _loading.WhenLoaded(MapLoaded);
    }

    Adventure.AdventureScreen? _loading;

    void MapLoaded()
    {
        if (Find("Progress") is { } bar) Hero.HeroScreen.SetProgress(bar, 1f);
        _loading = null;
        _phase = Phase.PressKey;
        Find("ProgressBarText")?.SetValue("val", UiResources.StripMarkup(Text("UI/pwlscreen/PressAnyKey.txt")));
        if (_autostart) GetTree().CreateTimer(1.0).Timeout += () => (GetParent() is CanvasLayer layer ? (Node)layer : this).QueueFree();
    }

    public override void _Process(double delta)
    {
        if (_phase == Phase.Loading && _loading != null && IsInstanceValid(_loading) && Find("Progress") is { } bar)
            Hero.HeroScreen.SetProgress(bar, _loading.LoadProgress);
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

    /// <summary>
    /// 006b1530 (campaign mission) passes the map's PWLPicture (SAdvMapDesc +0x238, registered 00a0b4b0) to the screen;
    /// 006b30c0 gives it to Main/CustomPicture (window vf 0xa4) when there is one, and shows Main/Logo only without one;
    /// the same test picks the root background.
    /// </summary>
    void ShowPicture(Texture2D? picture)
    {
        if (Find("CustomPicture") is { } pic)
        {
            pic.Visible = picture != null;
            if (picture != null)
                pic.AddChild(new TextureRect { Texture = picture, AnchorRight = 1, AnchorBottom = 1, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore });
        }
        SetVisible("Logo", picture == null);
        // 006b3223: the root's related texture back_custom (a picture given) or back_common becomes the root window's
        // background (root vf 0x110 by name → vf 0x104 → vf 0x28); a2p1 UI/pwlscreen/Textures: CustomBack / Back.
        if (Root.RelatedTextures.TryGetValue(picture != null ? "back_custom" : "back_common", out var back) && Root.Background is { } bg)
        {
            bg.DdsPath = bg.TexturePath = back;
            NodeOf(Root)?.QueueRedraw();
        }
    }

    /// <summary>The map's PWLPicture: the campaign map tag (map-tag.xdb) names the AdvMapDesc (map.xdb) that holds it.</summary>
    static Texture2D? MapPicture(string mapTag)
    {
        try
        {
            var xdb = Game.Instance.Xdb;
            if (!Game.Instance.Fs.Exists(mapTag)) return null;
            var root = xdb.Load(mapTag).Root;
            string desc = mapTag;
            if (root?.Element("PWLPicture") == null && (string?)root?.Element("AdvMapDesc")?.Attribute("href") is { Length: > 0 } d)
            {
                desc = XdbRef.Parse(d, mapTag).Path;
                root = Game.Instance.Fs.Exists(desc) ? xdb.Load(desc).Root : null;
            }
            var h = (string?)root?.Element("PWLPicture")?.Attribute("href");
            return string.IsNullOrEmpty(h) ? null : Game.Instance.LoadTexture(XdbRef.Parse(h, desc).Path);
        }
        catch (Exception e) { GD.PushWarning($"[H5] PWLPicture {mapTag}: {e.Message}"); return null; }
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
                return Fill("BUILDING", BonusBuilding(b)?.Name is { Length: > 0 } bn ? bn : b.Building);
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

    /// <summary>
    /// 006b2270 (after the check passes): the BUILDING slot shows the main town's building record (FUN_00bd1150) at level
    /// index min(current level, levels − 1) — the level the bonus builds: name 00cd3fc0, description 00cd4070, icon 00cd4120.
    /// </summary>
    H5.Adventure.Data.BuildingDef? BonusBuilding(CampaignBonus b)
    {
        if (_session is not { } s || s.Game.MainTownOf(s.Human.Index) is not { } town) return null;
        int max = s.Game.Db.MaxLevel(town.Type, b.Building);
        if (max <= 0) return null;
        return s.Game.Db.Building(town.Type, b.Building, Math.Min(town.Level(b.Building), max - 1) + 1);
    }

    Texture2D? BonusIcon(CampaignBonus b)
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
                case "E_BONUS_BUILDING":
                    var bp = BonusBuilding(b)?.Path;
                    return bp != null && Game.Instance.Fs.Exists(bp) && Href(xdb.Load(bp).Root?.Element("Icon"), bp) is { } bi
                        ? Game.Instance.LoadTexture(bi) : null;
            }
        }
        catch (Exception e) { GD.PushWarning($"[H5] bonus icon {b.Type}: {e.Message}"); }
        return null;
    }
}

/// <summary>
/// UI/MissionResults (UIGameRoot ID_MISSION_RESULTS_SCREEN), the mission-end window of 005a5d90 case 0xd
/// (CVictoryDialogLogEntry: +0x20 won, +0x14 message): window 007a1800 / 007a0e50 with the title
/// T_MISSION_END_TITLE_WIN / _LOOSE in Header, the message in Description, and WinIcon showing TX_MISSION_WIN /
/// TX_MISSION_LOSE (vf 0xa4) with the tooltip T_END_ICON_DESC_WIN / _LOOSE (vf 0x68) — the texts and textures of
/// UI/AdventureScreen-heavy (Texts / Textures related lists); Close continues (the campaign, or the main menu).
/// </summary>
public partial class MissionResultsScreen : UiScreen
{
    const string AdvRelatedTexts = "UI/AdventureScreen-heavy/Texts.(WindowRelatedTexts).xdb";
    const string AdvRelatedTextures = "UI/AdventureScreen-heavy/Textures.(WindowRelatedTextures).xdb";
    readonly Action _onClose;

    /// <summary>A campaign mission's end: the description is the mission's name (not traced against the log entry's message).</summary>
    public MissionResultsScreen(bool won, string campaignOrMap, int mission, Action onClose, H5.Adventure.Engine.MissionSummary? stats = null) : this(won, onClose)
    {
        if (stats != null) FillStatistics(stats);
        var c = CampaignFlow.Find(campaignOrMap);
        var m = c != null && mission >= 0 && mission < c.Missions.Count ? c.Missions[mission] : null;
        // Substituted values are plain text (not markup), so no <br> here.
        Find("Description")?.SetValue("text", m != null ? $"{m.Description}: {m.Name}" : campaignOrMap);
        if (c != null) Audio.MissionResultsAudio.Attach(this, c.Path, mission, won);
    }

    /// <summary>The adventure map's end (005a5d90 case 0xd): <paramref name="message"/> is the log entry's message.</summary>
    public MissionResultsScreen(bool won, string message, Action onClose, H5.Adventure.Engine.MissionSummary? stats = null) : this(won, onClose)
    {
        if (stats != null) FillStatistics(stats);
        Find("Description")?.SetValue("text", message);
    }

    MissionResultsScreen(bool won, Action onClose) : base("UI/MissionResults.(WindowScreen).xdb")
    {
        _onClose = onClose;
        MouseFilter = MouseFilterEnum.Stop;
        var header = UiGameRoot.RelatedTextAt(AdvRelatedTexts, won ? "T_MISSION_END_TITLE_WIN" : "T_MISSION_END_TITLE_LOOSE")
            ?? (won ? "Mission completed" : "Mission failed");
        Find("Header")?.SetValue("text", UiResources.StripMarkup(header).Trim());
        if (Find("WinIcon") is { } icon)
        {
            if (RelatedTexture(AdvRelatedTextures, won ? "TX_MISSION_WIN" : "TX_MISSION_LOSE") is { } tex)
                icon.AddChild(new TextureRect
                {
                    Texture = tex, AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
                });
            if (UiGameRoot.RelatedTextAt(AdvRelatedTexts, won ? "T_END_ICON_DESC_WIN" : "T_END_ICON_DESC_LOOSE") is { Length: > 0 } tip)
                icon.SetTooltip(tip);
        }
        foreach (var n in new[] { "LeftScrollButton", "RightScrollButton" }) SetVisible(n, false);
        // UISSendGameMessage arguments arrive as written in the data ("0 close_mission_results0 0").
        foreach (var name in new[] { "close_mission_results0", "close_mission_results", "0 close_mission_results0 0" })
            Bus.On(name, Close);
    }

    const string ResultStrings = "UI/MissionResults/ResultStrings.(WindowRelatedTexts).xdb";

    /// <summary>
    /// The Statistics rows of 0079fcf0, in its order, as items of the Statistics scrollable container: text rows are
    /// copies of StatisticsSlotTemplate with the ResultStrings text (0079ef50; &lt;value=count&gt; etc. filled), the two
    /// resource rows copies of StatisticsResourcesSlotTemplate (0079f110). A counter the engine does not record
    /// (its event source is not traced) keeps its row without a number.
    /// </summary>
    void FillStatistics(H5.Adventure.Engine.MissionSummary st)
    {
        if (Find("Statistics") is not UiScrollableContainer list || Find("StatisticsSlotTemplate") is not { } slot) return;
        slot.Visible = slot.Window.Visible = false;
        if (Find("StatisticsResourcesSlotTemplate") is { } resSlot) resSlot.Visible = resSlot.Window.Visible = false;
        string T(string id) => UiGameRoot.RelatedTextAt(ResultStrings, id) ?? "";
        void Row(string id, int? count, params (string Name, int Value)[] values)
        {
            var row = Instantiate(slot.Window, list.Window, Int2.Zero);
            var text = T(id).Replace("<value=count>", count?.ToString() ?? "");
            foreach (var (n, v) in values) text = text.Replace($"<value={n}>", v.ToString());
            row.SetText(text);
            list.AddItem(row);
        }
        Row("T_MISSION_TIME", null, ("days_count", st.Days), ("weeks_count", st.Weeks), ("months_count", st.Months));
        ResourceRows(list, T("T_RESOURCES_AT_END_OF_MISSION"), st.ResourcesAtEnd);
        ResourceRows(list, T("T_RESOURCES_DURING_MISSION"), st.ResourcesDuring);
        Row("T_BATTLES_TAKE_PART", st.BattlesFought);
        Row("T_BATTLES_WIN", st.BattlesWon);
        Row("T_BATTLES_LOOSE", st.BattlesLost);
        Row("T_HEROES_KILL", st.HeroesKilled);
        Row("T_HEROES_LOST", st.HeroesLost);
        Row("T_TOWNS_OCCUPY", st.TownsOccupied);
        Row("T_TOWNS_LOST", st.TownsLost);
        Row("T_CREATURES_AT_END_OF_MISSION", st.CreaturesAtEnd);
        Row("T_CREATURES_DURING_MISSION", st.CreaturesDuring);
        Row("T_ENEMY_CREATURES_AT_END_OF_MISSION", st.EnemyCreaturesAtEnd);
        Row("T_ENEMY_CREATURES_DURING_MISSION", st.EnemyCreaturesDuring);
        Row("T_ARTEFACTS_AT_END_OF_MISSION", st.ArtifactsAtEnd);
        Row("T_ARTEFACTS_DURING_MISSION", st.ArtifactsDuring);
    }

    static readonly string[] ResourceIds = { "E_WOOD", "E_ORE", "E_MERCURY", "E_CRYSTAL", "E_SULFUR", "E_GEM", "E_GOLD" };

    /// <summary>
    /// 0079f110: a StatisticsResourcesSlotTemplate copy holding the title (a StatisticsSlotTemplate copy) and rows of
    /// ResourceInfoSlotTemplate with three resources each (i, i + 1, i + 2 while ≤ 6): ResourceIcon / 02 / 03 = the
    /// resource's icon (resource table 01024bd4 entry, 00ad5aa0; the Icon16_15 of ResourcesInfo by the row height 15),
    /// ResourceText / 02 / 03 = ResourceCount.txt with count, each window placed right after the previous one + 2 by its
    /// width (icon 16, text 60 in the data); rows one row height (15) apart under the title.
    /// </summary>
    void ResourceRows(UiScrollableContainer list, string title, int[]? purse)
    {
        if (Find("StatisticsResourcesSlotTemplate") is not { } tpl || Find("StatisticsSlotTemplate") is not { } slot) return;
        var item = Instantiate(tpl.Window, list.Window, Int2.Zero);
        var rowTpl = item.Window.Find("ResourceInfoSlotTemplate");
        if (rowTpl != null) { rowTpl.Visible = false; if (NodeOf(rowTpl) is { } rn) rn.Visible = false; }
        var head = Instantiate(slot.Window, item.Window, Int2.Zero);
        head.SetText(title);
        int y = head.Window.Rect.H, rowH = rowTpl?.Rect.H ?? 15;
        if (purse != null && rowTpl != null)
            for (int i = 0; i < 7; i += 3)
            {
                var row = Instantiate(rowTpl, item.Window, new Int2(0, y));
                string[] icons = { "ResourceIcon", "ResourceIcon02", "ResourceIcon03" }, texts = { "ResourceText", "ResourceText02", "ResourceText03" };
                int x = 0;
                void Place(UiNode n)
                {
                    n.Window.Placement = n.Window.Placement.MergedWith(new UiPlacement { Position = (new Int2(x, 0), true) });
                    x += n.Window.Placement.Size.Value.X + 2;
                }
                for (int k = 0; k < 3; k++)
                {
                    var icon = row.Window.Find(icons[k]) is { } iw ? NodeOf(iw) : null;
                    var text = row.Window.Find(texts[k]) is { } tw ? NodeOf(tw) : null;
                    bool used = i + k <= 6;
                    if (icon != null) icon.Visible = icon.Window.Visible = used;
                    if (text != null) text.Visible = text.Window.Visible = used;
                    if (!used) continue;
                    if (icon != null) Place(icon);
                    if (text != null) Place(text);
                    text?.SetValue("count", purse[i + k].ToString());
                    if (icon != null && ResourceIcon(ResourceIds[i + k]) is { } tex)
                        icon.AddChild(new TextureRect
                        {
                            Texture = tex, AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore,
                            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
                        });
                }
                y += rowH;
            }
        item.Window.Placement = item.Window.Placement.MergedWith(new UiPlacement { Size = (new Int2(item.Window.Rect.W, y), true) });
        list.AddItem(item);
    }

    /// <summary>A resource's Icon16_15 (GameMechanics/RefTables/ResourcesInfo.xdb).</summary>
    static Texture2D? ResourceIcon(string id)
    {
        const string table = "GameMechanics/RefTables/ResourcesInfo.xdb";
        if (!Game.Instance.Fs.Exists(table) || Game.Instance.Xdb.Load(table).Root is not XElement r) return null;
        foreach (var item in r.Descendants("Item"))
            if ((string?)item.Element("ID") == id && (string?)item.Element("obj")?.Element("Icon16_15")?.Attribute("href") is { Length: > 0 } h)
                return UiResources.Instance.Texture(XdbRef.Parse(h, table).Path);
        return null;
    }

    /// <summary>A texture of a WindowRelatedTextures xdb (TextureName → texture), or null.</summary>
    static Texture2D? RelatedTexture(string relatedXdb, string name)
    {
        if (!Game.Instance.Fs.Exists(relatedXdb) || Game.Instance.Xdb.Load(relatedXdb).Root is not XElement r) return null;
        foreach (var item in r.Descendants("Item"))
            if (string.Equals((string?)item.Element("TextureName"), name, StringComparison.OrdinalIgnoreCase)
                && (string?)item.Element("Texture")?.Attribute("href") is { Length: > 0 } h)
                return UiResources.Instance.Texture(XdbRef.Parse(h, relatedXdb).Path);
        return null;
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
