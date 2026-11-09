using System.Xml.Linq;
using Godot;
using H5.Adventure.Data;
using H5.Adventure.Engine;
using H5.Core.Xdb;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>A town sub-screen shown over the town view: an original window plus close handling (Esc / right click / close messages).</summary>
public abstract partial class TownSubScreen : Control
{
    protected readonly TownScreen Owner;
    protected UiScreen Ui = null!;

    protected TownSubScreen(TownScreen owner, string screenXdb)
    {
        Owner = owner;
        Name = GetType().Name;
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;
        Ui = new UiScreen(screenXdb);
        AddChild(Ui);
        foreach (var msg in new[] { "close", "cancel", "close_window", "timeout_forced_close" })
            Ui.Bus.On(msg, Close);
    }

    protected H5.Adventure.State.PlayerState Me => Owner.Session.S.Players.First(p => p.Index == Owner.Town.Owner);

    protected bool Apply(AdvCommand cmd)
    {
        var r = Owner.Session.Apply(cmd);
        if (!r.Ok) Owner.Notice(Owner.Town.Name, Capitalise(r.Error ?? "Not possible."));
        Refresh();
        Owner.Refresh();
        return r.Ok;
    }

    static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..] + ".";

    public virtual void Refresh() => TownUi.SetResourceBar(Ui, Me);

    public override void _Ready() => Refresh();

    public override void _UnhandledInput(InputEvent e)
    {
        // Section keys from input_a2.cfg (game/src/Input/TownScreen.Input.cs); right click does not close.
        HandleSubInput(e);
    }

    protected void Close()
    {
        QueueFree();
        Owner.SubClosed();
    }
}

/// <summary>
/// The TotE build screen (UI/TownBuildNew, exe 0065a830/0065aae0): a 5×6 grid of building cells laid out by
/// the faction's (TownBuildDefinition) (slot position per building level, prerequisite arrows), with hover
/// highlight, selection, the price strip and the town level. A click only selects (the cell interface at screen +0x17c,
/// 0065a4c0); building is the jog dial's button 2 ("Build", BuyBuilding tooltip, 00783a60 / 00783df0), whose result
/// handler 0065c530 (screen +0x174 interface, vtable 00e2d9c8) sends the build command (005e91f0) for case 2, closes
/// for case 0 (00659400) and refreshes for case 4.
/// </summary>
public sealed partial class TownBuildScreen : TownSubScreen
{
    static readonly Dictionary<string, string> DefinitionFile = new()
    {
        ["TOWN_HEAVEN"] = "haven", ["TOWN_PRESERVE"] = "preserve", ["TOWN_ACADEMY"] = "academy", ["TOWN_DUNGEON"] = "dungeon",
        ["TOWN_INFERNO"] = "inferno", ["TOWN_NECROMANCY"] = "necropolis", ["TOWN_FORTRESS"] = "dwarven", ["TOWN_STRONGHOLD"] = "stronghold",
    };

    readonly Dictionary<(int X, int Y), List<(string Type, int Level)>> _cells = new();
    readonly List<(int X, int Y, int Length)> _arrows = new();
    (int X, int Y)? _hover, _selected;
    readonly UiScreen _jog;

    public TownBuildScreen(TownScreen owner) : base(owner, "UI/TownBuildNew.(WindowScreen).xdb")
    {
        // 006590a0: the jog dial (00783fa0 → class 00784190, vf 0x18 = 00783da0 → 00783a60). Button 0's preset
        // (00783710) is taken to be Exit; 1 EnterDwellings / ToLeft and 3 BuildPlan(Done) / ToRight act through the
        // town screen's interface +0x18c (vtable 00e2a610, handed over as build screen +0x21c by 006182d0 → 00659480 →
        // 0065d0a0 → 0065e260 → 00659170); 2 Build / BuyBuilding (00783da0); 4 the view toggle (00783330, sub-object
        // +0x44, vtable 00e3fd8c: 00724930 get / 00783870 set).
        _jog = new UiScreen("UI/Common/JogDial/JogDial.xdb", Ui.Bus);
        AddChild(_jog);
        SetJogButton("button_0", "Exit", null);
        SetJogButton("button_1", "EnterDwellings", JogText("ToLeft"));
        SetJogButton("button_2", "Build", JogText("BuyBuilding"));
        Ui.Bus.On("jd_button_0_reaction", Close);
        Ui.Bus.On("jd_button_1_reaction", EnterDwellings);
        Ui.Bus.On("jd_button_3_reaction", BuildPlan);
        Ui.Bus.On("jd_button_4_reaction", ToggleView);
        // input_a2.cfg town_build: jd_button_3 B / A, jd_button_1 R, jd_button_2 Space.
        Ui.Bus.On("jd_button_1", EnterDwellings);
        Ui.Bus.On("jd_button_3", BuildPlan);
        Ui.Bus.On("jd_button_2_reaction", Build);
        Ui.Bus.On("jd_button_2", Build);
        // Dev (screenshots): `--town-build-dev=select` selects the first buildable cell, `=build` also presses Build.
        if (OS.GetCmdlineUserArgs().FirstOrDefault(x => x.StartsWith("--town-build-dev=")) is { } dev)
            Callable.From(() =>
            {
                // `=full` / `=full-select`: press the view toggle (jog button 4) first.
                if (dev.Contains("full")) ToggleView();
                for (int x = 1; x <= 5 && _selected == null; x++)
                    for (int y = 1; y <= 6 && _selected == null; y++)
                        if (CellDef((x, y)) is { } d && Owner.Town.Level(d.Type) < d.Level
                            && TownData.Check(Owner.Session, Owner.Town, d) == BuildDenial.CanBuild) ClickCell((x, y));
                GD.Print($"[H5] --town-build-dev: selected {_selected}, build enabled {_jog.Find("button_2")?.Enabled}");
                if (dev.EndsWith("=build")) { var before = Owner.Town.Built.Values.Sum(); Build(); GD.Print($"[H5] --town-build-dev: built, levels {before} -> {Owner.Town.Built.Values.Sum()}"); }
            }).CallDeferred();
        LoadDefinition();
        for (int x = 1; x <= 5; x++)
            for (int y = 1; y <= 6; y++)
                if (Ui.Find($"building_{x}_{y}/button") is { } btn)
                {
                    var cell = (x, y);
                    btn.MouseEntered += () => { _hover = cell; UpdateFrames(); };
                    btn.MouseExited += () => { if (_hover == cell) { _hover = null; UpdateFrames(); } };
                    btn.Clicked += _ => ClickCell(cell);
                }
        AddArrows();
    }

    void LoadDefinition()
    {
        if (!DefinitionFile.TryGetValue(Owner.Town.Type, out var file)) return;
        var path = $"UI/TownBuildNew/Towns/{file}.(TownBuildDefinition).xdb";
        if (!Game.Instance.Fs.Exists(path)) return;
        var root = Game.Instance.Xdb.Load(path).Root!;
        foreach (var slot in root.Element("slots")?.Elements("Item") ?? Enumerable.Empty<XElement>())
        {
            var type = (string?)slot.Element("BuildingType") ?? "";
            foreach (var b in slot.Element("buildings")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                var key = ((int?)b.Element("XSlotPos") ?? 0, (int?)b.Element("YSlotPos") ?? 0);
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new();
                list.Add((type, H5.Adventure.Map.MapLoader.UpgradeLevel((string?)b.Element("Upgrade") ?? "")));
            }
        }
        foreach (var a in root.Element("arrows")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            _arrows.Add(((int?)a.Element("XSlotPos") ?? 0, (int?)a.Element("YSlotPos") ?? 0, (int?)a.Element("Length") ?? 1));
    }

    /// <summary>Arrows (TownBuildNew/BuildingsHolder/Arrows/Arrow_1..3, TOWN_BUILD_ARROW_%d) run down from a cell.</summary>
    void AddArrows()
    {
        foreach (var (x, y, len) in _arrows)
        {
            var path = $"UI/TownBuildNew/BuildingsHolder/Arrows/Arrow_{Math.Clamp(len, 1, 3)}.(WindowSimple).xdb";
            if (!Game.Instance.Fs.Exists(path) || Ui.Find($"building_{x}_{y}") is not { } cell) continue;
            var arrow = new UiScreen(path) { MouseFilter = MouseFilterEnum.Ignore };
            var holder = cell.GetParent<Control>();
            holder.AddChild(arrow);
            holder.MoveChild(arrow, 0);
            Callable.From(() =>
            {
                if (arrow.GetChildOrNull<Control>(0) is not { } root) return;
                var size = root.Size;
                root.Position = cell.Position + new Vector2(cell.Size.X / 2 - size.X / 2, cell.Size.Y);
            }).CallDeferred();
        }
    }

    /// <summary>The building level a shown cell stands for (<see cref="CellView"/>); hidden cells give none.</summary>
    BuildingDef? CellDef((int X, int Y) cell) => CellView(cell) is { Shown: true } v ? v.Def : null;

    /// <summary>
    /// DAT_00fdc638: the full-view flag, a process-wide static. 006590a0 starts the dial's view mode at
    /// (flag == 0) and 0065bab0 writes flag = (mode == 0) on every refresh; the static starts at 0, so the first build
    /// screen opens in the simple view and later ones in the view last used.
    /// </summary>
    static bool _fullView;

    public override void Refresh()
    {
        base.Refresh();
        var t = Owner.Town;
        Ui.Find("town_level")?.SetValue("level", t.DevLevel.ToString());
        for (int x = 1; x <= 5; x++)
            for (int y = 1; y <= 6; y++)
            {
                if (Ui.Find($"building_{x}_{y}") is not { } cellNode) continue;
                var draw = CellView((x, y));
                cellNode.Visible = draw is { Shown: true };
                if (draw is not { } d || Ui.Find($"building_{x}_{y}/button") is not { } btn) continue;
                var view = TownData.View(d.Def);
                TownUi.SetPicture(btn, view.IconHref != null ? Game.Instance.LoadTexture(view.IconHref) : null);
                btn.Modulate = d.Tint;
                btn.TooltipText = Tooltip(view, TownData.Check(Owner.Session, t, d.Def));
            }
        var show = _selected ?? _hover;
        var sel = show is { } s ? CellDef(s) : null;
        TownUi.SetCostStrip(Ui.Find("CreatureCountHolder"),
            sel != null && t.Level(sel.Type) < sel.Level ? CostArray(sel) : null, Me);
        UpdateJog();
        UpdateFrames();
    }

    /// <summary>
    /// The jog buttons' states. 00783600 (polled by the dial): button 1 enabled = interface vf 4(0) = 0061ae60(0) — the
    /// owner check 00613c00 and 0061a390 (EnterFort's condition, <see cref="TownScreen.CanEnterFort"/>); button 3's
    /// preset = BuildPlan when vf 4(1) = 1, else BuildPlanDone (UIGameRoot: EnterTownBuild / EnterTownBuildDone), with
    /// 0061ae60(1) = 0 without the owner, else 1 + town vf 0x9c (00cce4e0: built today); 00783a60 enables button 3
    /// when that value is not 0. Button 2 (0065a4c0 / 0065c530): 00658da0 and a non-empty selection. Button 4
    /// (00783870): mode 0 (full view) ScrollSmall / town_build_to_half, mode 1 (simple view) Scroll / town_build_to_full.
    /// </summary>
    void UpdateJog()
    {
        if (_jog == null) return;
        bool owner = Owner.OwnsTown();
        if (_jog.Find("button_1") is { } dwellings) dwellings.Enabled = owner && Owner.CanEnterFort();
        int plan = owner ? 1 + (Owner.Town.BuiltToday ? 1 : 0) : 0;
        SetJogButton("button_3", plan == 1 ? "EnterTownBuild" : "EnterTownBuildDone", JogText("ToRight"));
        if (_jog.Find("button_3") is { } buildPlan) buildPlan.Enabled = plan != 0;
        if (_jog.Find("button_2") is { } build) build.Enabled = SelectedBuildable() != null;
        SetJogButton("button_4", _fullView ? "ScrollSmall" : "Scroll", JogText(_fullView ? "town_build_to_half" : "town_build_to_full"));
    }

    static string JogText(string name) => $"UI/Common/JogDial/TownJogDial/{name}.txt";

    /// <summary>Button 1: interface slot 0 with 0 = 0061b720(0): town +0x478 = 0, then 0061af30 (the town hire screen).</summary>
    void EnterDwellings()
    {
        if (!Owner.OwnsTown() || !Owner.CanEnterFort()) return;
        Owner.OpenSub("fort");
    }

    /// <summary>
    /// Button 3: interface slot 0 with 1 = 0061b720(1): town +0x478 = 0, then 006182d0 — ends the fly-up (00618170),
    /// plays the town's sound and opens a new build screen (00659480 → 00615cb0), replacing this one.
    /// </summary>
    void BuildPlan()
    {
        if (!Owner.OwnsTown()) return;
        Owner.OpenSub("hall");
    }

    /// <summary>Button 4: 00783330 sets mode = (mode == 0); the result handler 0065c530 case 4 then refreshes (0065bab0).</summary>
    void ToggleView()
    {
        _fullView = !_fullView;
        Refresh();
    }

    static int[] CostArray(BuildingDef def) =>
        new[] { def.Cost.Wood, def.Cost.Ore, def.Cost.Mercury, def.Cost.Crystal, def.Cost.Sulfur, def.Cost.Gem, def.Cost.Gold };

    readonly record struct CellDraw(BuildingDef Def, Color Tint, bool Shown);

    // Colour table 00f37cd8 (12 bytes per state): 0 (255,255,255), 1 (255,90,90), 2 (128,255,128), 4 (0,128,255);
    // state 3 and the 00f37cfc colour (255,255,255) go with the factor 00f37cd0 = 0.5, read here as brightness.
    static readonly Color Built = Colors.White, Red = new(1, 90 / 255f, 90 / 255f), New = new(128 / 255f, 1, 128 / 255f),
        Upgrade = new(0, 128 / 255f, 1), Dim = new(0.5f, 0.5f, 0.5f);

    /// <summary>What 0065bab0 draws into a cell (<see cref="H5.Adventure.Engine.Adventure.BuildScreenCell"/>).</summary>
    CellDraw? CellView((int X, int Y) cell)
    {
        if (!_cells.TryGetValue(cell, out var list)) return null;
        var db = Owner.Session.Db;
        var t = Owner.Town;
        var d = H5.Adventure.Engine.Adventure.BuildScreenCell(list, t.Level, type => t.MaxLevel.GetValueOrDefault(type),
            (type, level) => db.Building(t.Type, type, level) is { } def ? TownData.Check(Owner.Session, t, def) : null, _fullView);
        if (d is not { } draw || db.Building(t.Type, draw.Type, draw.Level) is not { } bdef) return null;
        var tint = draw.Tint switch
        {
            BuildCellTint.Built => Built, BuildCellTint.Denied => Red, BuildCellTint.New => New,
            BuildCellTint.Upgrade => Upgrade, _ => Dim,
        };
        return new CellDraw(bdef, tint, draw.Shown);
    }

    /// <summary>
    /// The building tooltip (006596a0, UI/TownBuildNew/ToolTips/ToBuild): name, description, "Requirements:" with every
    /// prerequisite (prereq_%d, red when not met), the price, "Required town level", and the deny line — the text of
    /// the check code (red except can_build / already_build). Shown here as a plain tooltip.
    /// </summary>
    string Tooltip(BuildingView v, BuildDenial state)
    {
        var db = Owner.Session.Db;
        var t = Owner.Town;
        string T(string path, string fb) => UiResources.StripMarkup(TownUi.Text(path, fb)).Trim();
        var lines = new List<string> { v.Name, UiResources.StripMarkup(v.Description) };
        if (v.Def.Dependencies.Count > 0)
        {
            lines.Add(T("UI/TownBuildNew/ToolTips/Common/prereq_header.txt", "Requirements:"));
            foreach (var d in v.Def.Dependencies)
                lines.Add((t.Level(d.Type) < d.Level ? "! " : "  ") + (db.Building(t.Type, d.Type, d.Level)?.Name ?? d.Type));
        }
        var lvl = T("UI/TownBuildNew/ToolTips/Common/RequiredTownLevel.txt", "Required town level:");
        lines.Add(lvl.Replace("<value=red_val>", "").Replace("<value=normal_val>", "").TrimEnd() + " " + v.Def.DevLevelNeeded);
        var reason = H5.Adventure.Engine.Adventure.BuildDenialText(state);
        lines.Add(T($"UI/TownBuildNew/Texts/{reason}.txt", reason));
        return string.Join("\n", lines.Where(l => l.Length > 0));
    }

    void UpdateFrames()
    {
        Place("hilight", _hover);
        Place("selection", _selected);
    }

    void Place(string frame, (int X, int Y)? cell)
    {
        if (Ui.Find(frame) is not { } f) return;
        if (cell is not { } c || Ui.Find($"building_{c.X}_{c.Y}") is not { } node) { f.Visible = false; return; }
        f.Visible = true;
        f.GlobalPosition = node.GlobalPosition + (node.Size - f.Size) / 2;
    }

    readonly Dictionary<string, string> _jogPresets = new();

    void SetJogButton(string name, string preset, string? tooltip)
    {
        if (_jog.Find(name) is not { } btn) return;
        if (_jogPresets.TryGetValue(name, out var had) && had == preset) return;
        _jogPresets[name] = preset;
        var path = $"UI/Common/JogDial/Buttons/{preset}.(JogDialButton).xdb";
        if (Game.Instance.Fs.Exists(path)) UiDrawableHelper.Apply(btn, Game.Instance.Xdb.Load(path).Root!, path);
        if (tooltip != null) btn.SetTooltip(TownUi.Text(tooltip, ""));
    }

    /// <summary>
    /// 0065a4c0: the selection keeps the cell's building only when town vf 0xac gives code 0 for it (buildable),
    /// else it is cleared; jog button 2 is enabled when 00658da0 holds (the player owns the town and player vf 0x44,
    /// +0x634, the turn state, is 0 — <see cref="TownScreen.OwnsTown"/>) and the selection is not empty.
    /// </summary>
    BuildingDef? SelectedBuildable() =>
        Owner.OwnsTown() && _selected is { } c && CellDef(c) is { } def && Owner.Town.Level(def.Type) < def.Level
        && TownData.Check(Owner.Session, Owner.Town, def) == BuildDenial.CanBuild ? def : null;

    void ClickCell((int X, int Y) cell)
    {
        if (CellDef(cell) == null) return;
        _selected = cell;
        Refresh();
    }

    /// <summary>
    /// 0065c530 case 2: the build command (005e91f0) for the selection, then the selection is cleared, jog button 2
    /// disabled, the town refreshed (town vf 0xd4) and a sound played (00818c40 / 0081af90); the screen stays open. The
    /// +0x1ac object (0065dc00, vtable 00e2d934) is the cells' blink timer: 0065d3c0 (each frame from 0065b680) swaps
    /// its two highlight sets once a second (GetTickCount, +0x40) or at once when +0x3c is set — the build sets it so
    /// the blink restarts at once; 0065bab0 clears it. The blink itself is not drawn here (GAP).
    /// </summary>
    void Build()
    {
        if (SelectedBuildable() is not { } def) return;
        if (Apply(new BuildCmd(Owner.Town.Object, def.Type))) { _selected = null; Refresh(); }
    }
}

/// <summary>
/// The magic guild (UI/MageGuild, N_MAGIC_GUILD_SCREEN_ID; setup 00631110): the pages of FUN_006309d0
/// (<see cref="H5.Adventure.Engine.Adventure.GuildScreenPages"/>), one per bookmark; a page's spells fill
/// SpellsPanel_0N with N = min(count, panels), a rune page fills the RunesPanel_0N whose Rune slot count equals the rune
/// count (00631110 / FUN_00632340); page selection FUN_0062fe60 (caption → CircleView circle_name); "button" message
/// → page by bookmark name (00630320); the first page shown is the one named by the opener ("Button01" from the guild,
/// FUN_006303c0; "Button07" from the Fortress special, FUN_00630450), else page 0 (00630270).
/// </summary>
public sealed partial class MageGuildScreen : TownSubScreen
{
    const string Texts = "UI/MageGuild2/Main.(WindowRelatedTexts).xdb";
    readonly List<H5.Adventure.Engine.GuildPage> _pages;
    int _page;

    public MageGuildScreen(TownScreen owner, string initial = "Button01") : base(owner, "UI/MageGuild.(WindowScreen).xdb")
    {
        _pages = owner.Session.Game.GuildScreenPages(owner.Town, n => Ui.Find(n) != null);
        foreach (var n in new[] { "Button", "OrcsButton" })
            for (int i = 1; Ui.Find($"{n}{i:00}") is { } b; i++) b.Visible = false;
        foreach (var pg in _pages)
            if (Ui.Find(pg.Bookmark) is { } b) b.Visible = pg.Shown;
        Ui.Bus.On("button", (_, src) =>
        {
            int i = _pages.FindIndex(pg => string.Equals(pg.Bookmark, src?.Window.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { _page = i; Refresh(); }
        });
        _page = Math.Max(0, _pages.FindIndex(pg => pg.Bookmark == initial));
        // FUN_0062fbe0: T_ORCS_HEADER / T_ORCS_HEADER_TOOLTIP in a Stronghold (town type 10), else T_HEADER / T_HEADER_TOOLTIP.
        bool orcs = owner.Town.Type == "TOWN_STRONGHOLD";
        if (Ui.Find("Header") is { } header)
        {
            if (UiGameRoot.RelatedTextAt(Texts, orcs ? "T_ORCS_HEADER" : "T_HEADER") is { } h) header.SetText(h);
            if (UiGameRoot.RelatedTextAt(Texts, orcs ? "T_ORCS_HEADER_TOOLTIP" : "T_HEADER_TOOLTIP") is { } tt) header.SetTooltip(tt);
        }
    }

    static int Slots(UiNode panel, string item)
    {
        int n = 0;
        while (panel.Screen?.Find($"{panel.Window.Name}/{item}_{n + 1:00}") != null) n++;
        return n;
    }

    public override void Refresh()
    {
        base.Refresh();
        var db = Game.Instance.Db;
        var spellPanels = new List<UiNode>();
        for (int n = 1; Ui.Find($"SpellsPanel/SpellsPanel_{n:00}") is { } sp; n++) spellPanels.Add(sp);
        var runePanels = new List<UiNode>();
        for (int n = 1; Ui.Find($"RunesPanel/RunesPanel_{n:00}") is { } rp; n++) runePanels.Add(rp);
        foreach (var x in spellPanels.Concat(runePanels)) x.Visible = false;
        Ui.SetVisible("SpellsPanel", false);
        Ui.SetVisible("RunesPanel", false);
        if (_page >= _pages.Count) return;
        var page = _pages[_page];
        for (int i = 0; i < _pages.Count; i++)
            if (Ui.Find(_pages[i].Bookmark) is { } b) b.SetState(i == _page ? 1 : 0);
        Ui.Find("CircleView")?.SetValue("circle_name", (UiGameRoot.RelatedTextAt(Texts, page.Caption) ?? "").Trim());
        UiNode? panel = page.Runes
            ? runePanels.FirstOrDefault(rp => Slots(rp, "Rune") == page.Spells.Count)
            : page.Spells.Count > 0 && spellPanels.Count > 0 ? spellPanels[Math.Min(page.Spells.Count, spellPanels.Count) - 1] : null;
        if (panel == null) return;
        panel.Visible = true;
        Ui.SetVisible(page.Runes ? "RunesPanel" : "SpellsPanel", true);
        string item = page.Runes ? "Rune" : "Spell";
        for (int i = 0; i < page.Spells.Count; i++)
        {
            var spell = db.Spell(page.Spells[i]);
            var node = Ui.Find($"{panel.Window.Name}/{item}_{i + 1:00}");
            if (node == null) break;
            if (spell == null) continue;
            var icon = (string?)Game.Instance.Xdb.Load(spell.SourcePath).Root?.Element("Texture")?.Attribute("href");
            TownUi.SetPicture(node.FindChild("SpellIcon", true, false) as Control,
                string.IsNullOrEmpty(icon) ? null : Game.Instance.LoadTexture(XdbRef.Parse(icon, spell.SourcePath).Path));
            (node.FindChild("SpellView", true, false) as UiNode)?.SetValue("spell_name", spell.Name);
            node.TooltipText = UiResources.StripMarkup(spell.Description);
        }
    }
}

/// <summary>The blacksmith (UI/BuyWarMachine): ballista, first aid tent, ammo cart at <see cref="H5.Adventure.Engine.Adventure.WarMachinePrice"/>.</summary>
public sealed partial class BlacksmithScreen : TownSubScreen
{
    static readonly string[] Machines = { "WAR_MACHINE_BALLISTA", "WAR_MACHINE_FIRST_AID_TENT", "WAR_MACHINE_AMMO_CART" };
    const string Texts = "UI/BuyWarMachine/Texts.(WindowRelatedTexts).xdb";
    /// <summary>No choice on opening: 006ed200 fills the description with the Blacksmith's own texts (initial selection UNVERIFIED).</summary>
    int _choice = -1;

    public BlacksmithScreen(TownScreen owner) : base(owner, "UI/BuyWarMachine.(WindowScreen).xdb")
    {
        // Authored as a centred box over the screen.
        Ui.Bus.On("choice_click", (_, src) => { if (src?.Name.ToString() is { } n && n.StartsWith("Choice_")) { _choice = n[^1] - '0'; Refresh(); } });
        Ui.Bus.On("buy_war_machine", Buy);
    }

    H5.Adventure.State.HeroState? Hero() =>
        Owner.Session.S.Heroes.FirstOrDefault(h => h.Alive && h.InTown == Owner.Town.Object && !h.Garrisoned)
        ?? Owner.Session.S.Heroes.FirstOrDefault(h => h.Alive && h.InTown == Owner.Town.Object);

    /// <summary>
    /// The choice record (006ee160): the hero's machines (hero vf 0x70), its owner's gold (+0x18 of player vf 0xd8, −1
    /// without a hero) and town vf 0xe4 = 00cd07c0 (blacksmith built and a visiting hero, town +0x40). 006ee270: a choice
    /// is enabled only with gold ≥ price, the machine not owned and that flag; its state is 1 when selected; the price
    /// label is ID_PRICE_LABEL_TEXT_NORMAL or _NOT_ENOUGH_GOLD (gold &lt; price).
    /// </summary>
    bool Buyable(int i, out bool enoughGold)
    {
        var h = Hero();
        int gold = h == null ? -1 : Owner.Session.S.Players.First(p => p.Index == h.Owner).Purse[H5.Adventure.State.Purse.Gold];
        enoughGold = gold >= Owner.Session.Game.WarMachinePrice(Owner.Town, Machines[i]);
        bool has = h != null && Machines[i] switch
        {
            "WAR_MACHINE_BALLISTA" => h.Ballista, "WAR_MACHINE_FIRST_AID_TENT" => h.FirstAidTent, _ => h.AmmoCart,
        };
        bool visiting = Owner.Town.Level("TB_BLACKSMITH") > 0 && Owner.VisitingHero() != null;
        return enoughGold && !has && visiting;
    }

    void Buy()
    {
        if (_choice < 0 || Hero() is not { } h || !Buyable(_choice, out _)) return;
        Apply(new BuyWarMachineCmd(Owner.Town.Object, h.Id, Machines[_choice]));
    }

    public override void Refresh()
    {
        base.Refresh();
        // 006ed200, screen +0x1a8 = 1 (town): MainHeader_Blacksmith (0 = the map's War Machine Factory).
        Ui.Find("MainHeaderText")?.SetValue("Value", TownUi.Text("UI/BuyWarMachine/Texts/MainHeader_Blacksmith.txt", "Blacksmith").Trim());
        bool anyBuyable = false;
        for (int i = 0; i < Machines.Length; i++)
        {
            TownUi.SetPicture(Ui.Find($"Choice_{i}/Icon"), TownUi.CreatureIcon(Machines[i]));
            int price = Owner.Session.Game.WarMachinePrice(Owner.Town, Machines[i]);
            bool buyable = Buyable(i, out bool enoughGold);
            if (Ui.Find($"Price_{i}") is { } label)
            {
                label.SetText(TownUi.Text(enoughGold ? "UI/BuyArtefact/Main/ArtefactPriceNormal.txt" : "UI/BuyArtefact/Main/ArtefactPriceNotEnoughGold.txt", "<center><value=Price>").Trim());
                label.SetValue("Price", price.ToString());
            }
            if (Ui.Find($"Choice_{i}") is { } choice)
            {
                choice.SetState(buyable && i == _choice && choice.Window.ButtonStates.Count > 1 ? 1 : 0);
                choice.Enabled = buyable;
                choice.SetTooltip(ChoiceTooltip(i, buyable, enoughGold));
            }
            anyBuyable |= buyable;
        }
        // 006ee270: ButtonBuy is enabled when the selection is a valid choice and any choice is buyable.
        if (Ui.Find("ButtonBuy") is { } buy) buy.Enabled = _choice >= 0 && _choice < Machines.Length && anyBuyable;
        if (_choice < 0)
        {
            // 006ed200, screen +0x1a8 = 1: DescriptionHeader_Blacksmith / DescriptionText_Blacksmith.
            Ui.Find("DescriptionHeader")?.SetValue("Value", TownUi.Text("UI/BuyWarMachine/Texts/DescriptionHeader_Blacksmith.txt", "").Trim());
            Ui.Find("DescriptionText")?.SetValue("Value", TownUi.Text("UI/BuyWarMachine/Texts/DescriptionText_Blacksmith.txt", "").Trim());
            return;
        }
        var (name, desc) = MachineTexts(Machines[_choice]);
        Ui.Find("DescriptionHeader")?.SetValue("Value", name);
        Ui.Find("DescriptionText")?.SetValue("Value", UiResources.StripMarkup(desc));
    }

    /// <summary>
    /// 006ee270: each choice's tooltip (object 00585cb0, vtable 00e1564c) holds the machine's name, description and,
    /// for a disabled choice, one denial reason — assigned in turn (0040e960), so the last applying one stays:
    /// ID_DENIAL_REASON_NOT_ENOUGH_GOLD (gold short and a hero, gold ≥ 0), _ALREDY_HAVE, _ONLY_VISITING_HERO_CAN_BUY.
    /// The tooltip window's layout is not traced; the three parts are stacked here.
    /// </summary>
    string ChoiceTooltip(int i, bool buyable, bool enoughGold)
    {
        var (name, desc) = MachineTexts(Machines[i]);
        string reason = "";
        if (!buyable)
        {
            var h = Hero();
            bool has = h != null && Machines[i] switch
            {
                "WAR_MACHINE_BALLISTA" => h.Ballista, "WAR_MACHINE_FIRST_AID_TENT" => h.FirstAidTent, _ => h.AmmoCart,
            };
            if (!enoughGold && h != null) reason = UiGameRoot.RelatedTextAt(Texts, "ID_DENIAL_REASON_NOT_ENOUGH_GOLD") ?? "";
            if (has) reason = UiGameRoot.RelatedTextAt(Texts, "ID_DENIAL_REASON_ALREDY_HAVE") ?? "";
            if (!(Owner.Town.Level("TB_BLACKSMITH") > 0 && Owner.VisitingHero() != null))
                reason = UiGameRoot.RelatedTextAt(Texts, "ID_DENIAL_REASON_ONLY_VISITING_HERO_CAN_BUY") ?? "";
        }
        return string.Join("<br>", new[] { name, desc, reason }.Where(x => x.Trim().Length > 0).Select(x => x.Trim()));
    }

    /// <summary>Name and description from the machine's CreatureVisual (GameMechanics/CreatureVisual/WarMachines).</summary>
    internal static (string Name, string Description) MachineTexts(string id)
    {
        var file = id switch
        {
            "WAR_MACHINE_BALLISTA" => "Ballista", "WAR_MACHINE_FIRST_AID_TENT" => "FirstAidTent", _ => "AmmoCart",
        };
        var path = $"GameMechanics/CreatureVisual/WarMachines/{file}.xdb";
        if (!Game.Instance.Fs.Exists(path)) return (file, "");
        var root = Game.Instance.Xdb.Load(path).Root!;
        string T(string el) => Game.Instance.Xdb.ReadText((string?)root.Element(el)?.Attribute("href"), path)?.Trim() ?? "";
        return (T("CreatureNameFileRef") is { Length: > 0 } n ? n : file, T("DescriptionFileRef"));
    }
}

/// <summary>
/// The town marketplace (UI/MarketplaceAndMail, market page), driven by <see cref="MarketController"/>
/// with the exe's exchange-rate tables and the town owner's marketplace count.
/// </summary>
public sealed partial class MarketScreen : TownSubScreen
{
    readonly MarketController _market;

    public MarketScreen(TownScreen owner) : base(owner, "UI/MarketplaceAndMail.(WindowScreen).xdb")
    {
        // The exchange refreshes this screen's own resource bar as well as the (hidden) town HUD.
        _market = new MarketController(this, Ui, owner.Session, () => Me, -1, () => { TownUi.SetResourceBar(Ui, Me); Owner.Refresh(); }, Close);
    }

    public override void _Ready()
    {
        base._Ready();
        Callable.From(_market.DevClicks).CallDeferred();
    }

    public override void Refresh()
    {
        base.Refresh();
        _market?.Refresh();
    }
}
