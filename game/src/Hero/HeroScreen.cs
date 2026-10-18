using Godot;
using H5.Adventure.Engine;
using H5.Core.Data;
using H5.Core.Xdb;
using H5Reborn.Adventure;
using H5Reborn.Render;
using H5Reborn.Town;
using H5Reborn.Ui;
using HeroState = H5.Adventure.State.HeroState;

namespace H5Reborn.Hero;

/// <summary>
/// The hero screen (UI/HeroScreen3) over the 3D stand Arenas/Interface/Hero_screen (the hero's adventure
/// character at ph_hero). Single mode shows HeroPage_Self with Bookmarks_HeroSingle. Meeting mode (two own
/// heroes meet on the map) shows the stock side-by-side layout: HeroPage_Self2 + Bookmarks_HeroSingle for the
/// visiting hero on the left and HeroPage_Meet + Bookmarks_HeroMeet for the met hero on the right; creature
/// stacks drag between the two armies and clicking an artifact passes it to the other hero's backpack.
/// Bookmarks switch the Army (attributes, stacks with drag-and-drop/split, war machines), Inventory (paper
/// doll and a paged 10-slot backpack; click to wear/take off), Skills, Abilities and Biography pages of their
/// panel. Esc or right click closes it.
/// </summary>
public partial class HeroScreen : Control
{
    static readonly (string Window, string Slot)[] PaperDoll =
    {
        ("Helm", "HEAD"), ("Necklace", "NECK"), ("Cloak", "SHOULDERS"), ("Cirase", "CHEST"), ("Weapon", "PRIMARY"),
        ("Shield", "SECONDARY"), ("Ring_1", "FINGER"), ("Ring_2", "FINGER"), ("Boots", "FEET"), ("Additional", "MISCSLOT1"),
    };
    static readonly string[] Pages = { "Army", "Inventory", "Skills", "Abilities", "Biography" };

    /// <summary>One hero's page in the screen (one in single mode, two when heroes meet).</summary>
    sealed class Panel(HeroState hero, UiNode page, string bookmarks)
    {
        public HeroState Hero = hero;
        public readonly UiNode Page = page;
        public readonly string Bookmarks = bookmarks;
        public int BackpackPage;
        public Panel? Other;
    }

    readonly AdventureScreen _adv;
    readonly TownScreen? _town;
    readonly HeroState _hero;
    readonly HeroState? _meet;
    UiScreen _ui = null!;
    ArmySlots _army = null!;
    readonly List<Panel> _panels = new();

    public HeroScreen(AdventureScreen adv, HeroState hero, TownScreen? town = null, HeroState? meet = null)
    {
        _adv = adv;
        _hero = hero;
        _town = town;
        _meet = meet;
        Name = "HeroScreen";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>Opens the screen over the adventure map (double-click on an own hero).</summary>
    public static HeroScreen Open(AdventureScreen adv, HeroState hero)
    {
        var s = new HeroScreen(adv, hero);
        adv.DialogLayer.AddChild(s);
        return s;
    }

    /// <summary>Opens the meeting screen for two heroes of the same player standing next to each other.</summary>
    public static HeroScreen OpenMeeting(AdventureScreen adv, HeroState visitor, HeroState met)
    {
        var s = new HeroScreen(adv, visitor, meet: met);
        adv.DialogLayer.AddChild(s);
        return s;
    }

    public override void _Ready()
    {
        Add3DStand();
        _ui = new UiScreen("UI/HeroScreen3.(WindowScreen).xdb");
        AddChild(_ui);
        _ui.SetVisible("Bookmarks_HeroSingle", true);
        _ui.SetVisible("Bookmarks_HeroMeet", _meet != null);
        _ui.SetVisible("HeroPage_Self", _meet == null);
        _ui.SetVisible("HeroPage_Self2", _meet != null);
        _ui.SetVisible("HeroPage_Meet", _meet != null);
        if (_meet == null)
            _panels.Add(new Panel(_hero, _ui.Find("HeroPage_Self")!, "Bookmarks_HeroSingle"));
        else
        {
            var left = new Panel(_hero, _ui.Find("HeroPage_Self2")!, "Bookmarks_HeroSingle");
            var right = new Panel(_meet, _ui.Find("HeroPage_Meet")!, "Bookmarks_HeroMeet");
            (left.Other, right.Other) = (right, left);
            _panels.Add(left);
            _panels.Add(right);
        }
        foreach (var p in _panels) p.Page.Visible = true;
        _ui.Bus.On("bookmark", (_, src) =>
        {
            var name = src?.Name.ToString() ?? "Army";
            var group = src?.GetParent()?.Name.ToString() ?? "Bookmarks_HeroSingle";
            if (_panels.Find(p => p.Bookmarks == group) is { } panel) ShowPage(panel, name == "Invertory" ? "Inventory" : name);
        });
        _ui.Bus.On("cast_spell", OpenSpellbook);
        _ui.Bus.On("slot_click", (_, src) => { if (src != null) ClickArtifactSlot(src); });
        // The stock arrows send both *_self and *_meet; the panel is the one holding the button.
        _ui.Bus.On("inv_left_self", (_, src) => TurnBackpack(src, -1));
        _ui.Bus.On("inv_right_self", (_, src) => TurnBackpack(src, +1));

        // One ArmySlots for both panels so stacks drag between the two heroes (TransferStackCmd /
        // SwapStacksCmd accept a neighbouring own hero as the target).
        _army = new ArmySlots(this, () => cmd => Apply(cmd));
        _army.Changed += Refresh;
        foreach (var p in _panels)
            for (int i = 1; i <= 7; i++)
                if (Find(p, $"Army/ArmyStacks/Slot_{i}") is { } slot)
                    _army.Add(new ArmySlotView { Node = slot, Hero = p.Hero.Id, Slot = i - 1 });
        foreach (var p in _panels) ShowPage(p, "Army");
    }

    void Add3DStand()
    {
        const string stand = "Arenas/Interface/Hero_screen/Hero_screen.(ArenaDesc).xdb";
        if (!Game.Instance.Fs.Exists(stand)) return;
        var vpc = new SubViewportContainer { Name = "Hero3D", AnchorRight = 1, AnchorBottom = 1, Stretch = true, MouseFilter = MouseFilterEnum.Ignore };
        var vp = new SubViewport { OwnWorld3D = true, HandleInputLocally = false };
        vpc.AddChild(vp);
        AddChild(vpc);
        var scene = new TownScene3D(stand);
        vp.AddChild(scene);
        Callable.From(() =>
        {
            if (!Game.Instance.Db.Heroes.TryGetValue(_hero.DefId, out var def) || !Game.Instance.Fs.Exists(def.SourcePath)) return;
            var root = Game.Instance.Xdb.Load(def.SourcePath).Root!;
            var href = (string?)root.Element("HeroCharacterAdventure")?.Attribute("href");
            if (string.IsNullOrEmpty(href)) return;
            try
            {
                var c = CharacterBuilder.Build(XdbRef.Parse(href, def.SourcePath).Path, adventureMap: true);
                if (scene.Locators.TryGetValue("ph_hero", out var at)) c.Transform = at;
                scene.AddChild(c);
            }
            catch (Exception e) { GD.PushWarning($"[H5] hero stand: {e.Message}"); }
        }).CallDeferred();
    }

    void TurnBackpack(Node? src, int delta)
    {
        var panel = _panels.Find(p => src != null && p.Page.IsAncestorOf(src)) ?? _panels[0];
        panel.BackpackPage = Math.Max(0, panel.BackpackPage + delta);
        Refresh();
    }

    static UiNode? Find(Panel p, string path) => p.Page.FindChild(path.Split('/')[0], true, false) is UiNode first
        ? path.Contains('/') ? first.GetNodeOrNullByNames(path.Split('/')[1..]) : first
        : null;

    bool Apply(AdvCommand cmd)
    {
        var r = _adv.Session.Apply(cmd);
        if (!r.Ok) GD.Print($"[H5] hero screen: {cmd.GetType().Name} rejected: {r.Error}");
        Refresh();
        _town?.Refresh();
        return r.Ok;
    }

    void ShowPage(Panel panel, string page)
    {
        foreach (var p in Pages) if (panel.Page.FindChild(p, false, false) is UiNode n) n.Visible = p == page;
        // The page-level attribute panel replaces the face/name block on the Inventory page (the Army
        // page shows attributes inside its own panel).
        if (panel.Page.FindChild("Attributes", false, false) is UiNode top) top.Visible = page == "Inventory";
        if (panel.Page.FindChild("HeroInfo", false, false) is UiNode info) info.Visible = page != "Inventory";
        // The inventory page has its own attribute panel at the top; the army page shows attributes inside.
        foreach (var bm in new[] { "Army", "Invertory", "Skills", "Abilities", "Biography" })
            if (_ui.Find($"{panel.Bookmarks}/{bm}") is { } b && b.Window.ButtonStates.Count > 1)
                b.SetState((bm == "Invertory" ? "Inventory" : bm) == page ? 1 : 0);
        Refresh();
    }

    public void Refresh()
    {
        if (_adv.Session.S.Players.FirstOrDefault(p => p.Index == _hero.Owner) is { } owner) TownUi.SetResourceBar(_ui, owner);
        _army.Refresh(_adv.Session.S);
        foreach (var p in _panels)
        {
            if (_adv.Session.S.Hero(p.Hero.Id) is { } fresh) p.Hero = fresh;
            RefreshPanel(p);
        }
    }

    void RefreshPanel(Panel panel)
    {
        var h = panel.Hero;
        var db = Game.Instance.Db;
        TownUi.SetPicture(Find(panel, "HeroInfo/Face"), TownUi.HeroFace(h));
        var name = Find(panel, "HeroInfo/Name");
        name?.SetValue("HeroName", h.Name);
        name?.SetValue("HeroLevel", h.Level.ToString());
        name?.SetValue("HeroClass", ClassName(h.Class));
        long next = Game.Instance.Db.Experience.For(h.Level + 1);
        if (Find(panel, "HeroInfo/Experience") is { } bar) bar.TooltipText = $"{h.Experience} / {next}";

        var (morale, luck) = (h.NextBattleMorale + h.ScriptMorale, h.NextBattleLuck + h.ScriptLuck);
        foreach (var attrs in panel.Page.FindChildren("Attributes", "", true, false).OfType<UiNode>())
        {
            Set(attrs, "Attack", h.Attack);
            Set(attrs, "Defence", h.Defence);
            Set(attrs, "SpellPower", h.SpellPower);
            Set(attrs, "Knowledge", h.Knowledge);
            Set(attrs, "Morale", morale);
            Set(attrs, "Luck", luck);
            if (attrs.FindChild("Mana", true, false) is UiNode mana)
            {
                mana.SetValue("Attribute", h.Mana.ToString());
                mana.SetValue("AttributeMax", _adv.Session.Game.MaxMana(h).ToString());
            }
        }

        foreach (var (window, has, machine) in new[] { ("Ballista", h.Ballista, "WAR_MACHINE_BALLISTA"), ("FirstAidTent", h.FirstAidTent, "WAR_MACHINE_FIRST_AID_TENT"), ("AmmoCart", h.AmmoCart, "WAR_MACHINE_AMMO_CART") })
            if (Find(panel, $"Army/WarMachinesStacks/{window}") is { } wm)
                TownUi.SetPicture(wm.FindChild("Pic", true, false) as Control, has ? TownUi.CreatureIcon(machine) : null);

        // Paper doll and backpack.
        var worn = h.Artifacts.ToList();
        foreach (var (window, slot) in PaperDoll)
        {
            if (Find(panel, $"Inventory/{window}") is not { } node) continue;
            string? art = null;
            var ofSlot = worn.Where(a => db.Artifacts.TryGetValue(a, out var d) && d.Slot == slot).ToList();
            if (window == "Ring_2") art = ofSlot.Skip(1).FirstOrDefault(); else art = ofSlot.FirstOrDefault();
            node.SetMeta("artifact", art ?? "");
            node.SetMeta("backpack", false);
            node.SetMeta("hero", h.Id);
            TownUi.SetPicture(node, art != null ? ArtifactIcon(art) : null);
            node.TooltipText = art != null ? ArtifactName(art) : "";
        }
        int pages = Math.Max(1, (h.Backpack.Count + 9) / 10);
        panel.BackpackPage = Math.Clamp(panel.BackpackPage, 0, pages - 1);
        for (int i = 0; i < 10; i++)
        {
            if (Find(panel, $"Inventory/Slot_{i + 1}") is not { } node) continue;
            int idx = panel.BackpackPage * 10 + i;
            var art = idx < h.Backpack.Count ? h.Backpack[idx] : null;
            node.SetMeta("artifact", art ?? "");
            node.SetMeta("backpack", true);
            node.SetMeta("hero", h.Id);
            TownUi.SetPicture(node, art != null ? ArtifactIcon(art) : null);
            node.TooltipText = art != null ? ArtifactName(art) : "";
        }

        // Skills table (6 cells) and racial / specialisation row.
        var skills = h.Skills.Where(s => s.Mastery > Mastery.None).ToList();
        for (int i = 0; i < 6; i++)
        {
            if (Find(panel, $"Skills/Cell_{i + 1}") is not { } cell) continue;
            var s = i < skills.Count ? skills[i] : null;
            cell.Visible = true;
            TownUi.SetPicture(cell.FindChild("Icon", true, false) as Control, s != null ? AdvDialogs.SkillIcon(s.SkillId, (int)s.Mastery) : null);
            (cell.FindChild("Desc", true, false) as UiNode)?.SetText(s != null ? $"<body_bright>{AdvDialogs.SkillName(s.SkillId, (int)s.Mastery)}" : "");
        }
        if (Find(panel, "Skills/Racial_1") is { } racial)
        {
            racial.Visible = true;
            TownUi.SetPicture(racial.FindChild("Icon", true, false) as Control, SpecializationIcon(h));
        }
        Find(panel, "Skills/Racial_Desc")?.SetText($"<body_bright>{SpecializationName(h)}");

        // Abilities: each learned skill with up to three of its perks.
        var perkDefs = _adv.Session.Db.Perks;
        for (int row = 0; row < 6; row++)
        {
            if (Find(panel, $"Abilities/Abilities{row}") is not { } line) continue;
            var s = row < skills.Count ? skills[row] : null;
            line.Visible = s != null;
            if (s == null) continue;
            TownUi.SetPicture(line.FindChild("Skill", false, false)?.FindChild("Icon", false, false) as Control, AdvDialogs.SkillIcon(s.SkillId, (int)s.Mastery));
            var perks = h.Perks.Where(p => perkDefs.TryGetValue(p, out var pd) && pd.BasicSkill == s.SkillId).Take(3).ToList();
            for (int k = 0; k < 3; k++)
            {
                var host = line.FindChild($"Perk{k}", false, false)?.FindChild("Icon", false, false) as Control;
                TownUi.SetPicture(host, k < perks.Count ? AdvDialogs.SkillIcon(perks[k], 1) : null);
                if (host != null) host.TooltipText = k < perks.Count ? AdvDialogs.SkillName(perks[k], 1) : "";
            }
        }

        // Biography.
        if (Find(panel, "Biography") is { } bio && Game.Instance.Db.Heroes.TryGetValue(h.DefId, out var hd) && Game.Instance.Fs.Exists(hd.SourcePath))
        {
            var root = Game.Instance.Xdb.Load(hd.SourcePath).Root!;
            var text = Game.Instance.Xdb.ReadText((string?)root.Element("BiographyFileRef")?.Attribute("href"), hd.SourcePath) ?? "";
            foreach (var tv in bio.FindChildren("*", "", true, false).OfType<UiNode>().Where(n => n.Window.Type == "WindowTextView"))
            {
                tv.SetValue("biography", text);
                tv.SetValue("spec_name", SpecializationName(h));
                tv.SetValue("spec_desc", hd.SpecializationDescription);
            }
        }
    }

    static void Set(UiNode attrs, string name, int value)
    {
        if (attrs.FindChild(name, true, false) is UiNode n) n.SetValue("Attribute", value.ToString());
    }

    void ClickArtifactSlot(UiNode node)
    {
        var art = node.HasMeta("artifact") ? node.GetMeta("artifact").AsString() : "";
        if (art.Length == 0) return;
        bool fromBackpack = node.GetMeta("backpack").AsBool();
        int heroId = node.HasMeta("hero") ? node.GetMeta("hero").AsInt32() : _hero.Id;
        if (_panels.Find(p => p.Hero.Id == heroId)?.Other is { } other)
            // Meeting: the artifact goes to the other hero's backpack.
            Apply(new MoveArtifactCmd(heroId, art, fromBackpack, other.Hero.Id, ToBackpack: true));
        else
            Apply(new MoveArtifactCmd(heroId, art, fromBackpack, heroId, ToBackpack: !fromBackpack));
    }

    void OpenSpellbook()
    {
        var book = new Combat.SpellBook3D(_hero.Spells, Array.Empty<H5.Combat.SpellOption>(), _hero.Mana, _adv.Session.Game.MaxMana(_hero),
            warcries: _hero.Class == "HERO_CLASS_BARBARIAN");
        book.Closed += () => book.QueueFree();
        AddChild(book);
    }

    static string ClassName(string cls)
    {
        const string table = "GameMechanics/RefTables/HeroClass.xdb";
        var item = Game.Instance.Xdb.Load(table).Root?.Element("objects")?.Elements("Item").FirstOrDefault(i => (string?)i.Element("ID") == cls);
        var text = Game.Instance.Xdb.ReadText((string?)item?.Element("obj")?.Element("NameFileRef")?.Attribute("href"), table);
        return string.IsNullOrWhiteSpace(text)
            ? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(cls.Replace("HERO_CLASS_", "").Replace('_', ' ').ToLowerInvariant())
            : text.Trim();
    }

    static Texture2D? SpecializationIcon(HeroState h)
    {
        if (!Game.Instance.Db.Heroes.TryGetValue(h.DefId, out var def) || !Game.Instance.Fs.Exists(def.SourcePath)) return null;
        var href = (string?)Game.Instance.Xdb.Load(def.SourcePath).Root?.Element("SpecializationIcon")?.Attribute("href");
        return string.IsNullOrEmpty(href) ? null : Game.Instance.LoadTexture(XdbRef.Parse(href, def.SourcePath).Path);
    }

    static string SpecializationName(HeroState h) =>
        Game.Instance.Db.Heroes.TryGetValue(h.DefId, out var def) ? def.SpecializationName : "";

    const string ArtifactTable = "GameMechanics/RefTables/Artifacts.xdb";

    static System.Xml.Linq.XElement? ArtifactItem(string id) =>
        Game.Instance.Xdb.Load(ArtifactTable).Root?.Element("objects")?.Elements("Item").FirstOrDefault(i => (string?)i.Element("ID") == id)?.Element("obj");

    static Texture2D? ArtifactIcon(string id)
    {
        var href = (string?)ArtifactItem(id)?.Element("Icon")?.Attribute("href");
        return string.IsNullOrEmpty(href) ? null : Game.Instance.LoadTexture(XdbRef.Parse(href, ArtifactTable).Path);
    }

    static string ArtifactName(string id) =>
        Game.Instance.Db.Artifacts.TryGetValue(id, out var a) && a.Name.Length > 0 ? a.Name : id;

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape } || e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
        {
            GetViewport().SetInputAsHandled();
            QueueFree();
            if (_town != null) _town.SubClosed();
            else { _adv.Hud.Refresh(); foreach (var x in _adv.Session.S.Heroes) _adv.World.SyncHero(x); }
        }
    }
}

static class NodeFind
{
    /// <summary>Follows a chain of descendant names (each looked up recursively under the previous).</summary>
    public static UiNode? GetNodeOrNullByNames(this Node start, IEnumerable<string> names)
    {
        Node? cur = start;
        foreach (var n in names) cur = cur?.FindChild(n, true, false);
        return cur as UiNode;
    }
}
