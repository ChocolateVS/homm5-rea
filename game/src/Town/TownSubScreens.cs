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
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape } || e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
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
/// highlight, selection, the price strip and the town level. Clicking a selected buildable cell builds it.
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

    public TownBuildScreen(TownScreen owner) : base(owner, "UI/TownBuildNew.(WindowScreen).xdb")
    {
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

    /// <summary>The building level a cell stands for now: the next level to build there, else the highest built.</summary>
    BuildingDef? CellDef((int X, int Y) cell)
    {
        if (!_cells.TryGetValue(cell, out var list)) return null;
        var db = Owner.Session.Db;
        var t = Owner.Town;
        BuildingDef? builtTop = null;
        foreach (var (type, level) in list.OrderBy(e => e.Level))
        {
            var def = db.Building(t.Type, type, level);
            if (def == null || level > t.MaxLevel.GetValueOrDefault(type)) continue;
            if (t.Level(type) >= level) { builtTop = def; continue; }
            return def;
        }
        return builtTop;
    }

    public override void Refresh()
    {
        base.Refresh();
        var t = Owner.Town;
        Ui.Find("town_level")?.SetValue("level", t.DevLevel.ToString());
        for (int x = 1; x <= 5; x++)
            for (int y = 1; y <= 6; y++)
            {
                if (Ui.Find($"building_{x}_{y}") is not { } cellNode) continue;
                var def = CellDef((x, y));
                cellNode.Visible = def != null;
                if (def == null || Ui.Find($"building_{x}_{y}/button") is not { } btn) continue;
                var view = TownData.View(def);
                TownUi.SetPicture(btn, view.IconHref != null ? Game.Instance.LoadTexture(view.IconHref) : null);
                var state = TownData.Check(Owner.Session, t, def);
                // Built and buildable cells show in full colour; the rest are dimmed.
                // TODO(exe): confirm the exact visual of unavailable cells in FUN_0065bab0.
                btn.Modulate = state is TownData.BuildState.Built or TownData.BuildState.CanBuild ? Colors.White : new Color(0.55f, 0.55f, 0.55f);
                btn.TooltipText = Tooltip(view, state);
            }
        var show = _selected ?? _hover;
        var sel = show is { } s ? CellDef(s) : null;
        TownUi.SetCostStrip(Ui.Find("CreatureCountHolder"),
            sel != null && TownData.Check(Owner.Session, t, sel) != TownData.BuildState.Built ? CostArray(sel) : null, Me);
        UpdateFrames();
    }

    static int[] CostArray(BuildingDef def) =>
        new[] { def.Cost.Wood, def.Cost.Ore, def.Cost.Mercury, def.Cost.Crystal, def.Cost.Sulfur, def.Cost.Gem, def.Cost.Gold };

    string Tooltip(BuildingView v, TownData.BuildState state)
    {
        string reason = state switch
        {
            TownData.BuildState.Built => "already_build",
            TownData.BuildState.CanBuild => "can_build",
            TownData.BuildState.NoResources => "no_money",
            TownData.BuildState.BuiltToday => "build_thisturn",
            TownData.BuildState.Forbidden => "limit_upgrade",
            _ => v.Def.Type == "TB_GRAIL" ? "grail" : "prereq",
        };
        var text = TownUi.Text($"UI/TownBuildNew/Texts/{reason}.txt", reason);
        var lines = new List<string> { v.Name, UiResources.StripMarkup(v.Description), UiResources.StripMarkup(text) };
        if (state is TownData.BuildState.Requirements)
        {
            var db = Owner.Session.Db;
            var missing = v.Def.Dependencies.Where(d => Owner.Town.Level(d.Type) < d.Level)
                .Select(d => db.Building(Owner.Town.Type, d.Type, d.Level)?.Name ?? d.Type).ToList();
            if (missing.Count > 0) lines.Add(string.Join(", ", missing));
            if (Owner.Town.DevLevel < v.Def.DevLevelNeeded) lines.Add($"Town level {v.Def.DevLevelNeeded}");
        }
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

    void ClickCell((int X, int Y) cell)
    {
        var def = CellDef(cell);
        if (def == null) return;
        if (_selected == cell && TownData.Check(Owner.Session, Owner.Town, def) == TownData.BuildState.CanBuild)
        {
            if (Apply(new BuildCmd(Owner.Town.Object, def.Type))) _selected = null;
            return;
        }
        _selected = cell;
        Refresh();
    }
}

/// <summary>
/// The tavern (NUI tavern screen, UI/TavernScreen; messages click_hero1/2, rumors, thiefs, close, exe 00634de0):
/// the two heroes on offer with face, name, attributes, starting army and price. The stock screen has no hire
/// button (hiring is done from the town strip); here double-clicking the face hires the shown hero.
/// </summary>
public sealed partial class TavernScreen : TownSubScreen
{
    int _slot;

    public TavernScreen(TownScreen owner) : base(owner, "UI/TavernScreen.(WindowScreen).xdb")
    {
        Ui.Bus.On("click_hero1", () => { _slot = 0; Refresh(); });
        Ui.Bus.On("click_hero2", () => { _slot = 1; Refresh(); });
        Ui.Bus.On("rumors", () => Owner.Notice("Tavern", "There are no rumours this week."));
        Ui.Bus.On("thiefs", () => Owner.Notice("Thieves' Guild", "The thieves have nothing to tell yet."));
        Ui.SetVisible("tavern-thieves-window", false);
        if (Ui.Find("HeroInfo/Face") is { } face)
        {
            face.MouseFilter = MouseFilterEnum.Stop;
            face.TooltipText = "Double-click to hire this hero";
            face.GuiInput += e => { if (e is InputEventMouseButton { DoubleClick: true, ButtonIndex: MouseButton.Left }) Hire(); };
        }
    }

    void Hire()
    {
        if (Apply(new HireHeroCmd(Owner.Town.Object, _slot))) Close();
    }

    public override void Refresh()
    {
        base.Refresh();
        var defId = _slot < Me.Tavern.Length ? Me.Tavern[_slot] : null;
        var db = Game.Instance.Db;
        Ui.Find("price")?.SetValue("price", H5.Adventure.Engine.Adventure.HeroHireCost.ToString());
        if (defId == null || !db.Heroes.TryGetValue(defId, out var def))
        {
            Ui.SetVisible("HeroInfo", false);
            return;
        }
        Ui.SetVisible("HeroInfo", true);
        TownUi.SetPicture(Ui.Find("HeroInfo/Face"),
            Combat.CombatIcons.Hero(defId, true) is { } fp ? Game.Instance.LoadTexture(fp) : null);
        var cls = Owner.Session.Db.SkillProbs.ContainsKey(def.Class) ? def.Class.Replace("HERO_CLASS_", "").Replace('_', ' ') : def.Class;
        Ui.Find("HeroInfo/Name")?.SetValue("HeroName", def.Name);
        Ui.Find("HeroInfo/Name")?.SetValue("HeroClass", System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(cls.ToLowerInvariant()));
        Ui.Find("HeroInfo/Name")?.SetValue("HeroLevel", "1");
        foreach (var (key, value) in new[] { ("Attack", def.Attack), ("Defence", def.Defence), ("SpellPower", def.SpellPower), ("Knowledge", def.Knowledge) })
            Ui.Find($"HeroAttributes/{key}")?.SetValue("Attribute", value.ToString());
        Ui.Find("HeroAttributes/Morale")?.SetValue("Attribute", "0");
        Ui.Find("HeroAttributes/Luck")?.SetValue("Attribute", "0");
        Ui.Find("HeroAttributes/Mana")?.SetValue("Attribute", (def.Knowledge * 10).ToString());
        // The starting army is rolled when the hero is hired (Adventure.GiveStartingArmy), so the slots stay empty.
        for (int i = 0; i < 7; i++)
        {
            var slot = Ui.Find($"Slot_{i + 1}");
            TownUi.SetPicture(slot?.FindChild("Pic", true, false) as Control, null);
            if (slot?.FindChild("Text", true, false) is UiNode txt) txt.Visible = false;
        }
    }
}

/// <summary>The magic guild (UI/MageGuild): the spells of each built circle (SpellsPanel_0N holds N spells).</summary>
public sealed partial class MageGuildScreen : TownSubScreen
{
    int _circle;

    public MageGuildScreen(TownScreen owner) : base(owner, "UI/MageGuild.(WindowScreen).xdb")
    {
        int built = Math.Min(owner.Town.Level("TB_MAGIC_GUILD"), 5);
        // The bookmark buttons select a circle; only built circles are shown.
        for (int i = 1; i <= 5; i++)
            if (Ui.Find($"Button0{i}") is { } b)
            {
                int circle = i - 1;
                b.Visible = i <= built;
                b.Clicked += _ => { _circle = circle; Refresh(); };
            }
        Ui.Bus.On("button", (_, src) => { if (src?.Name.ToString() is { } n && n.StartsWith("Button0")) { _circle = n[^1] - '1'; Refresh(); } });
        Ui.SetVisible("RunesPanel", false);
    }

    public override void Refresh()
    {
        base.Refresh();
        var spells = _circle < Owner.Town.MageGuild.Count ? Owner.Town.MageGuild[_circle] : new List<string>();
        Ui.Find("CircleView")?.SetValue("circle_name", $"Circle {_circle + 1}");
        for (int n = 1; n <= 4; n++) Ui.SetVisible($"SpellsPanel/SpellsPanel_0{n}", n == Math.Clamp(spells.Count, 1, 4));
        for (int i = 0; i < spells.Count && i < 4; i++)
        {
            var spell = Game.Instance.Db.Spell(spells[i]);
            var node = Ui.Find($"SpellsPanel/SpellsPanel_0{Math.Clamp(spells.Count, 1, 4)}/Spell_0{i + 1}");
            if (node == null || spell == null) continue;
            var icon = (string?)Game.Instance.Xdb.Load(spell.SourcePath).Root?.Element("Texture")?.Attribute("href");
            TownUi.SetPicture(node.FindChild("SpellIcon", true, false) as Control,
                string.IsNullOrEmpty(icon) ? null : Game.Instance.LoadTexture(XdbRef.Parse(icon, spell.SourcePath).Path));
            (node.FindChild("SpellView", true, false) as UiNode)?.SetValue("spell_name", spell.Name);
            node.TooltipText = UiResources.StripMarkup(spell.Description);
        }
    }
}

/// <summary>The blacksmith (UI/BuyWarMachine): three war machines, the town's native one at normal price, the rest ×3.</summary>
public sealed partial class BlacksmithScreen : TownSubScreen
{
    static readonly string[] Machines = { "WAR_MACHINE_BALLISTA", "WAR_MACHINE_FIRST_AID_TENT", "WAR_MACHINE_AMMO_CART" };
    int _choice;

    public BlacksmithScreen(TownScreen owner) : base(owner, "UI/BuyWarMachine.(WindowScreen).xdb")
    {
        // Authored as a centred box over the screen.
        Ui.Bus.On("choice_click", (_, src) => { if (src?.Name.ToString() is { } n && n.StartsWith("Choice_")) { _choice = n[^1] - '0'; Refresh(); } });
        Ui.Bus.On("buy_war_machine", Buy);
    }

    H5.Adventure.State.HeroState? Hero() =>
        Owner.Session.S.Heroes.FirstOrDefault(h => h.Alive && h.InTown == Owner.Town.Object && !h.Garrisoned)
        ?? Owner.Session.S.Heroes.FirstOrDefault(h => h.Alive && h.InTown == Owner.Town.Object);

    void Buy()
    {
        if (Hero() is not { } h) { Owner.Notice("Blacksmith", "A hero must be in the town to buy a war machine."); return; }
        Apply(new BuyWarMachineCmd(Owner.Town.Object, h.Id, Machines[_choice]));
    }

    public override void Refresh()
    {
        base.Refresh();
        var db = Game.Instance.Db;
        var native = Owner.Session.Db.NativeWarMachine.GetValueOrDefault(Owner.Town.Type);
        Ui.Find("MainHeaderText")?.SetValue("Value", "Blacksmith");
        for (int i = 0; i < Machines.Length; i++)
        {
            TownUi.SetPicture(Ui.Find($"Choice_{i}/Icon"), TownUi.CreatureIcon(Machines[i]));
            int price = db.WarMachines.TryGetValue(Machines[i], out var wm) ? wm.Cost.Gold * (Machines[i] == native ? 1 : 3) : 0;
            Ui.Find($"Price_{i}")?.SetValue("Price", price.ToString());
            Ui.Find($"Choice_{i}")?.SetState(i == _choice && (Ui.Find($"Choice_{i}")?.Window.ButtonStates.Count ?? 0) > 1 ? 1 : 0);
        }
        var (name, desc) = MachineTexts(Machines[_choice]);
        Ui.Find("DescriptionHeader")?.SetValue("Value", name);
        Ui.Find("DescriptionText")?.SetValue("Value", UiResources.StripMarkup(desc));
    }

    /// <summary>Name and description from the machine's CreatureVisual (GameMechanics/CreatureVisual/WarMachines).</summary>
    static (string Name, string Description) MachineTexts(string id)
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
/// The marketplace (UI/MarketplaceAndMail, market page). The original exchange-rate formula has not been
/// traced in the exe yet, so trading is disabled here rather than guessed.
/// </summary>
public sealed partial class MarketScreen : TownSubScreen
{
    public MarketScreen(TownScreen owner) : base(owner, "UI/MarketplaceAndMail.(WindowScreen).xdb")
    {
        Ui.SetVisible("MarketMainWindow", true);
        Ui.SetVisible("MailMainWindow", false);
        for (int i = 0; i < 7; i++)
        {
            Ui.Find($"TreasureButton_{i}")?.SetText($"<center>{TownUi.ResourceNames[i]}");
            Ui.Find($"RateButton_{i}")?.SetText($"<center>{TownUi.ResourceNames[i]}");
        }
        Ui.Find("LeftButtonPanelHeader")?.SetText("<center>You give");
        Ui.Find("RightButtonPanelHeader")?.SetText("<center>You get");
    }

    public override void _Ready()
    {
        base._Ready();
        Owner.Notice("Marketplace", "Trading is not available yet: the original exchange rates have not been recovered.");
    }
}
