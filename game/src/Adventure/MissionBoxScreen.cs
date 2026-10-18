using Godot;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Ui;
using H5Reborn.Town;
using H5Reborn.Ui;
using TownBuildState = H5.Adventure.Engine.Adventure.TownBuildState;

namespace H5Reborn.Adventure;

/// <summary>
/// The adventure "information" box (UI/MissionObjectives/Main): Mission Objectives, Mission Log and Kingdom
/// Overview tabs, the Puzzle Map button and Close. The kingdom tab lists the player's towns or heroes with the
/// stock townview1 / heroview1 templates (two views per page, the mouse wheel scrolls), the kingdom's
/// resources with their daily income, the moon week and the date.
/// </summary>
public sealed partial class MissionBoxScreen : Control
{
    const string Ko = "UI/MissionObjectives/KingdomOverview/";
    const int ViewHeight = 205, ViewsPerPage = 2;
    static readonly string[] ResKeys = { "wood", "ore", "mercury", "crystals", "sulfur", "gems", "gold" };

    readonly AdventureScreen _adv;
    readonly UiScreen _ui;
    readonly List<UiNode> _views = new();
    string _tab = "TabKingdom";
    bool _heroes;
    int _top;

    public MissionBoxScreen(AdventureScreen adv, string tab = "TabKingdom")
    {
        _adv = adv;
        _tab = tab;
        Name = "MissionBox";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Stop;
        _ui = new UiScreen("UI/MissionObjectives/Main.(WindowSimple).xdb");
        AddChild(_ui);
        var bus = _ui.Bus;
        bus.On("objectives_close", Close);
        bus.On("tab_objectives", () => ShowTab("TabObjectives"));
        bus.On("tab_log", () => ShowTab("TabLog"));
        bus.On("tab_kingdom", () => ShowTab("TabKingdom"));
        bus.On("kingdom_overview_towns", () => { _heroes = false; _top = 0; Refresh(); });
        bus.On("kingdom_overview_heroes", () => { _heroes = true; _top = 0; Refresh(); });
        bus.On("puzzle", () => { Close(); _adv.ShowPuzzleMap(); });
        foreach (var t in new[] { "townview1", "heroview1" }) _ui.SetVisible(t, false);
    }

    PlayerState Me => _adv.Session.Human;

    /// <summary>Dev (`--adventure-open=heroes`): the kingdom tab's hero list.</summary>
    public void ShowHeroesForDev() { _heroes = true; _top = 0; Refresh(); }

    public override void _Ready() => ShowTab(_tab);

    void ShowTab(string tab)
    {
        _tab = tab;
        _ui.ShowOnly(tab switch { "TabObjectives" => "Objectives", "TabLog" => "Log", _ => "KingdomOverview" }, "Objectives", "Log", "KingdomOverview");
        // The Puzzle Map button shares its place with the kingdom's Towns button.
        _ui.SetVisible("Puzzle", tab != "TabKingdom");
        foreach (var t in new[] { "TabObjectives", "TabLog", "TabKingdom" })
            if (_ui.Find(t) is { } b && b.Window.ButtonStates.Count > 1) b.SetState(t == tab ? 1 : 0);
        Refresh();
    }

    void Close() => QueueFree();

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            GetViewport().SetInputAsHandled();
            Close();
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (_tab != "TabKingdom" || e is not InputEventMouseButton { Pressed: true } mb) return;
        int count = _heroes ? _adv.MyHeroes().Count : _adv.MyTowns().Count;
        if (mb.ButtonIndex == MouseButton.WheelDown && _top + ViewsPerPage < count) { _top++; Refresh(); }
        else if (mb.ButtonIndex == MouseButton.WheelUp && _top > 0) { _top--; Refresh(); }
    }

    void Refresh()
    {
        foreach (var v in _views) _ui.RemoveInstance(v);
        _views.Clear();
        if (_tab == "TabKingdom") RefreshKingdom();
        else if (_tab == "TabObjectives") RefreshObjectives();
    }

    // ---------------------------------------------------------------- objectives

    int _quest;

    /// <summary>
    /// Left: "Scenario info", "Primary objectives" and "Secondary objectives" headers, each followed by
    /// QuestName buttons; right: the selected entry's text in the Bio window.
    /// </summary>
    void RefreshObjectives()
    {
        var map = _adv.Session.Map;
        var mine = map.Objectives.Where(o => o.Kind != "OBJECTIVE_KIND_SCENARIO_INFO" && (o.Player < 0 || o.Player == Me.Index)).ToList();
        string Line(H5.Adventure.Map.MapObjective o) => o.Caption is { Length: > 0 } c ? c : AdventureScreen.GoalText(o.Kind);
        var defeat = AdventureScreen.GoalText("OBJECTIVE_KIND_DEFEAT_ALL");
        var entries = new List<(string Header, List<(string Name, string Text)> Items)>
        {
            ("UI/MissionObjectives/scenarioinfo.(windowstatictext).xdb", new() { (map.Name, map.Description) }),
            ("UI/MissionObjectives/MainQuests.(WindowStaticText).xdb", mine.Where(o => o.Primary).Select(o => (Line(o), Line(o))).DefaultIfEmpty((defeat, defeat)).ToList()),
            ("UI/MissionObjectives/SideQuests.(WindowStaticText).xdb", mine.Where(o => !o.Primary).Select(o => (Line(o), Line(o))).ToList()),
        };
        if (_ui.Find("ScrollContaner1") is not { } left) return;
        var questTemplate = UiResources.Instance.LoadWindow("UI/MissionObjectives/QuestName.(WindowMSButton).xdb");
        int y = 0, index = 0;
        string selected = map.Description;
        foreach (var (header, items) in entries)
        {
            if (items.Count == 0) continue;
            _views.Add(_ui.Instantiate(UiResources.Instance.LoadWindow(header), left.Window, new Int2(0, y)));
            y += 26;
            foreach (var (name, text) in items)
            {
                int i = index++;
                var q = _ui.Instantiate(questTemplate, left.Window, new Int2(0, y));
                F(q, "QuestDesc")?.SetText($"<body>{name}");
                if (F(q, "Marker") is { } marker) marker.Visible = false;
                if (i == _quest) { selected = text; if (q.Window.ButtonStates.Count > 1) q.SetState(1); }
                q.Clicked += _ => { _quest = i; Refresh(); };
                _views.Add(q);
                y += 45;
            }
        }
        if (_ui.Find("ScrollContaner2") is { } right)
        {
            var bio = _ui.Instantiate(UiResources.Instance.LoadWindow("UI/MissionObjectives/Bio.(WindowStaticText).xdb"), right.Window, new Int2(0, 0));
            bio.SetText($"<body_bright>{selected}");
            _views.Add(bio);
        }
    }

    // ---------------------------------------------------------------- kingdom

    static UiNode? F(Node? root, string path)
    {
        Node? cur = root;
        foreach (var part in path.Split('/'))
            cur = cur?.FindChild(part, true, false);
        return cur as UiNode;
    }

    static string T(string file, string fallback = "") => TownUi.Text(Ko + file, fallback);

    void RefreshKingdom()
    {
        var game = _adv.Session.Game;
        var s = _adv.Session.S;
        _ui.Find("KingdomHeader")?.SetText($"<h1>{T("Kingdom_Header.txt", "Kingdom Overview")}");
        if (_ui.Find("scroll_area") is { } area)
        {
            area.ClipContents = true;
            if (_heroes)
            {
                var heroes = _adv.MyHeroes();
                for (int i = _top; i < Math.Min(heroes.Count, _top + ViewsPerPage); i++)
                    _views.Add(HeroView(area, heroes[i], i - _top));
            }
            else
            {
                var towns = _adv.MyTowns();
                for (int i = _top; i < Math.Min(towns.Count, _top + ViewsPerPage); i++)
                    _views.Add(TownView(area, towns[i], i - _top));
            }
        }
        // Kingdom resources and their daily income.
        var income = game.ExpectedIncome(Me);
        for (int r = 0; r < 7; r++)
        {
            _ui.Find($"KO_Resources/{ResKeys[r]}_count")?.SetValue("count", Me.Purse[r].ToString());
            _ui.Find($"KO_Resources/{ResKeys[r]}_income")?.SetValue("count", income[r].ToString());
        }
        var week = _adv.Session.Db.MoonWeeks.FirstOrDefault(w => w.Id == s.MoonWeek)?.Name ?? "";
        _ui.Find("KO_Resources/moon_week_name")?.SetValue("text", week);
        var date = _ui.Find("KO_Resources/month_week_day");
        date?.SetValue("month", s.Month.ToString());
        date?.SetValue("week", s.WeekOfMonth.ToString());
        date?.SetValue("day", s.DayOfWeek.ToString());
        // TODO: allied players' flags (KO_Allies) need the player flag icons; hidden for now.
        _ui.SetVisible("KO_Allies", false);
    }

    static void SetCreatures(Node? block, IReadOnlyList<(string? Creature, int Count)> slots, bool plus = false)
    {
        for (int i = 1; i <= 7; i++)
        {
            var cell = F(block, $"Creature{i}");
            if (cell == null) continue;
            var (creature, count) = i - 1 < slots.Count ? slots[i - 1] : (null, 0);
            bool show = creature != null && count > 0;
            TownUi.SetPicture(cell, show ? TownUi.CreatureIcon(creature!) : null);
            var num = F(cell, "CreatureNumber");
            if (num == null) continue;
            num.Visible = show;
            num.SetValue("colorprefix", plus ? T("Common/CreatureNumber_positive.txt", "<color_positive>") : "");
            num.SetValue("prefix", plus ? "+" : "");
            num.SetValue("num_of_creatures", count.ToString());
        }
    }

    UiNode TownView(UiNode area, TownState t, int row)
    {
        var game = _adv.Session.Game;
        var v = _ui.Instantiate(_ui.Find("townview1")!.Window, area.Window, new Int2(0, row * ViewHeight), $"town{row}");
        var name = F(v, "TownName");
        name?.SetValue("TownName", TownTitle(t));
        name?.SetValue("TownLevel", t.DevLevel.ToString());
        name?.SetValue("TownOwnerPrefix", "");
        name?.SetValue("TownOwner", "");
        var face = F(v, "TownFace");
        TownUi.SetPicture(face, KingdomTownIcon(t));
        var state = game.BuildState(t);
        foreach (var (marker, st) in new[] { ("NothingBuild", TownBuildState.CanBuild), ("BuildDone", TownBuildState.BuiltToday),
                     ("BuildNoResources", TownBuildState.NoResources), ("BuildAllComplete", TownBuildState.AllComplete) })
            if (F(face, marker) is { } m) m.Visible = state == st;
        // Hall gold and the silo's resource.
        F(v, "TownIncome/gold_income")?.SetValue("count", H5.Adventure.Engine.Adventure.TownGoldIncome(t).ToString());
        var silo = _adv.Session.Db.SiloIncome.GetValueOrDefault(t.Type);
        int[] sv = { silo.Wood, silo.Ore, silo.Mercury, silo.Crystal, silo.Sulfur, silo.Gem };
        int kind = Array.FindIndex(sv, x => x > 0);
        for (int r = 0; r < 6; r++)
            if (F(v, $"TownIncome/{ResKeys[r]}_icon") is { } icon) icon.Visible = r == kind;
        bool siloBuilt = t.Level("TB_MARKETPLACE") >= 2;
        if (F(v, "TownIncome/silo_income") is { } si) { si.Visible = kind >= 0; si.SetValue("count", (siloBuilt && kind >= 0 ? sv[kind] : 0).ToString()); }
        // Town hall and fort levels.
        foreach (var (slot, type) in new[] { ("TownBuilding1", "TB_TOWN_HALL"), ("TownBuilding2", "TB_FORT") })
        {
            var b = F(v, slot);
            int level = t.Level(type), max = t.MaxLevel.GetValueOrDefault(type);
            var def = _adv.Session.Db.Building(t.Type, type, Math.Max(1, level));
            TownUi.SetPicture(b, def != null && TownData.View(def).IconHref is { } ih ? Game.Instance.LoadTexture(ih) : null);
            var lv = F(b, "BuildingLevels");
            lv?.SetValue("current", level.ToString());
            lv?.SetValue("max", max.ToString());
        }
        var weekly = new List<(string?, int)>();
        var avail = new List<(string?, int)>();
        for (int tier = 1; tier <= 7; tier++)
        {
            var c = game.DwellingCreature(t, tier, t.Level($"TB_DWELLING_{tier}") >= 2 ? 1 : 0);
            weekly.Add((c, c != null ? game.WeeklyGrowthOf(t, tier) : 0));
            avail.Add((c, c != null ? t.Available[tier - 1] : 0));
        }
        SetCreatures(F(v, "WeeklyCreatures"), weekly, plus: true);
        SetCreatures(F(v, "AvailableCreatures"), avail);
        if (F(v, "KO_TownInfo") is { } hire) hire.Clicked += _ => OpenTown(t, "fort");
        if (F(v, "KO_TownPlan") is { } plan) plan.Clicked += _ => OpenTown(t, "hall");
        // Garrison (or the hero guarding the town) and the visiting hero.
        var s = _adv.Session.S;
        var guard = s.Heroes.FirstOrDefault(h => h.Alive && h.InTown == t.Object && h.Garrisoned);
        var visitor = s.Heroes.FirstOrDefault(h => h.Alive && h.InTown == t.Object && !h.Garrisoned);
        Slot(F(v, "TownSlotGarrison"), guard != null ? T("PanelTown/TownSlotHeader_GuardingHero.txt", "Guarding hero") : T("PanelTown/TownSlotHeader_Garrison.txt", "Garrison"),
            guard, guard?.Army ?? t.Garrison);
        Slot(F(v, "TownSlotVisiting"), T("PanelTown/TownSlotHeader_VisitingHero.txt", "Visiting hero"), visitor, visitor?.Army);
        return v;
    }

    static void Slot(UiNode? slot, string header, HeroState? hero, Stack?[]? army)
    {
        if (slot == null) return;
        F(slot, "TownSlotHeader")?.SetValue("SlotHeader", header);
        TownUi.SetPicture(F(slot, "TownSlotHero"), hero != null ? TownUi.HeroFace(hero, large: false) : null);
        if (F(slot, "Cast") is { } cast) cast.Visible = hero != null && hero.Spells.Count > 0;
        // BlockCreatures lays the seven stacks out with the first one beside the hero picture.
        SetCreatures(F(slot, "BlockCreatures"), army?.Select(st => (st?.Creature, st?.Count ?? 0)).ToList() ?? new List<(string?, int)>());
    }

    UiNode HeroView(UiNode area, HeroState h, int row)
    {
        var v = _ui.Instantiate(_ui.Find("heroview1")!.Window, area.Window, new Int2(0, row * ViewHeight), $"hero{row}");
        var name = F(v, "HeroName");
        name?.SetValue("HeroName", h.Name);
        name?.SetValue("HeroClass", Game.Instance.Db.HeroClasses.TryGetValue(h.Class, out var cls) && cls.Name.Length > 0 ? cls.Name : h.Class);
        name?.SetValue("HeroLevel", h.Level.ToString());
        name?.SetValue("HeroOwnerPrefix", "");
        name?.SetValue("HeroOwner", "");
        TownUi.SetPicture(F(v, "HeroFace"), TownUi.HeroFace(h));
        var game = _adv.Session.Game;
        var attrs = new (string Name, string Value)[]
        {
            (AdvDialogs.AttributeName("ATTACK"), h.Attack.ToString()),
            (AdvDialogs.AttributeName("DEFENCE"), h.Defence.ToString()),
            (AdvDialogs.AttributeName("SPELL_POWER"), h.SpellPower.ToString()),
            (AdvDialogs.AttributeName("KNOWLEDGE"), h.Knowledge.ToString()),
            (T("PanelHero/Attributes/Attribute_Morale.txt", "Morale"), Signed(game.ScriptHeroMorale(h))),
            (T("PanelHero/Attributes/Attribute_Luck.txt", "Luck"), Signed(game.ScriptHeroLuck(h))),
            (T("PanelHero/Attributes/Attribute_Mana.txt", "Mana"), $"{h.Mana}/{game.MaxMana(h)}"),
        };
        for (int i = 0; i < attrs.Length; i++)
        {
            F(v, $"Attribute{i + 1}/AttributeName")?.SetValue("AttributeName", attrs[i].Name);
            var val = F(v, $"Attribute{i + 1}/AttributeValue");
            val?.SetValue("AttributeValuePrefix", "");
            val?.SetValue("AttributeValue", attrs[i].Value);
            val?.SetValue("AttributeSecondValuePrefix", "");
            val?.SetValue("AttributeSecondValue", "");
        }
        if (F(v, "Attribute8/Experience") is { } xp)
        {
            long next = Game.Instance.Db.Experience.For(h.Level + 1);
            // TODO: fill the bar (WindowProgressBar Forward texture); the tooltip carries the numbers.
            xp.SetTooltip(T("PanelHero/Attributes/TTHeroExperience.txt", "<smallbody><value=current>/<value=next>")
                .Replace("<value=current>", h.Experience.ToString()).Replace("<value=next>", next.ToString()));
        }
        SetCreatures(F(v, "HeroArmy"), h.Army.Select(st => (st?.Creature, st?.Count ?? 0)).ToList());
        var machines = new[] { (h.Ballista, "WAR_MACHINE_BALLISTA"), (h.FirstAidTent, "WAR_MACHINE_FIRST_AID_TENT"), (h.AmmoCart, "WAR_MACHINE_AMMO_CART"), (true, "WAR_MACHINE_CATAPULT") }
            .Where(m => m.Item1).Select(m => m.Item2).ToList();
        for (int i = 1; i <= 4; i++)
            TownUi.SetPicture(F(v, $"WarMachines/Machine{i}"), i <= machines.Count ? TownUi.CreatureIcon(machines[i - 1]) : null);
        if (F(v, "BlockA/Cast") is { } cast) cast.Visible = false;
        // Inventory view: worn artifacts in block A, the backpack in block B.
        for (int i = 1; i <= 11; i++)
            TownUi.SetPicture(F(v, i == 1 ? "BlockA/ItemHolder1/Item1" : $"BlockA/Item{i}"), i <= h.Artifacts.Count ? Hero.HeroScreen.ArtifactIcon(h.Artifacts[i - 1]) : null);
        for (int i = 1; i <= 15; i++)
            TownUi.SetPicture(F(v, $"BlockB/Item{i}"), i <= h.Backpack.Count ? Hero.HeroScreen.ArtifactIcon(h.Backpack[i - 1]) : null);
        return v;
    }

    static string Signed(int v) => v > 0 ? $"+{v}" : v.ToString();

    void OpenTown(TownState t, string sub)
    {
        Close();
        var town = TownScreen.Open(_adv, t);
        Callable.From(() => town.OpenSub(sub)).CallDeferred();
    }

    string TownTitle(TownState t)
    {
        if (t.Name.Length > 0) return t.Name;
        if (!string.IsNullOrEmpty(t.Specialization) && Game.Instance.Fs.Exists(t.Specialization))
        {
            var root = Game.Instance.Xdb.Load(t.Specialization).Root;
            var n = Game.Instance.Xdb.ReadText((string?)root?.Element("NameFileRef")?.Attribute("href"), t.Specialization)?.Trim();
            if (!string.IsNullOrEmpty(n)) return n;
        }
        return TownUi.Text($"Text/Game/Towns/Types/{t.Type.Replace("TOWN_", "").ToLowerInvariant()}.txt", t.Type);
    }

    static Texture2D? KingdomTownIcon(TownState t)
    {
        const string table = "GameMechanics/RefTables/TownTypesInfo.xdb";
        var item = Game.Instance.Xdb.Load(table).Root?.Element("objects")?.Elements("Item").FirstOrDefault(i => (string?)i.Element("ID") == t.Type);
        var icons = item?.Element("obj")?.Element("kingdomTownIcons")?.Element("textures")?.Elements("Item").ToList();
        if (icons is not { Count: > 0 }) return null;
        var href = (string?)icons[Math.Clamp(t.Level("TB_TOWN_HALL") - 1, 0, icons.Count - 1)].Attribute("href");
        return string.IsNullOrEmpty(href) ? null : Game.Instance.LoadTexture(H5.Core.Xdb.XdbRef.Parse(href, table).Path);
    }
}
