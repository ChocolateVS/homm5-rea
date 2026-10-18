using Godot;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5Reborn.Adventure;
using H5Reborn.Audio;
using H5Reborn.Hero;
using H5Reborn.Ui;
using HeroState = H5.Adventure.State.HeroState;

namespace H5Reborn.Town;

/// <summary>
/// The town screen (NUI::CTownScreen, UI/TownScreen): the faction's 3D interior with the
/// original HUD on top — resource bar, building name line (BuildingDesc), the two jog dials
/// (enter_hall/fort/tavern/market/magic_guild/blacksmith/shipyard/special, buy_artifacts,
/// next/prev town, close_town, upgrade_creatures, show_town_info, exe 0061bcf0) and the army
/// strip (visiting hero row over the garrison row). Hovering a building highlights it and names
/// it; clicking opens its screen. Right-drag orbits the camera, the wheel zooms (CameraLimits).
/// It is shown over the adventure map (in its dialog layer, which locks map input).
/// </summary>
public partial class TownScreen : Control
{
    public static TownScreen? Current { get; private set; }

    public AdventureScreen Adv { get; }
    public AdventureSession Session => Adv.Session;
    public TownState Town { get; private set; }

    SubViewport _vp = null!;
    SubViewportContainer _vpc = null!;
    TownScene3D _scene = null!;
    UiScreen _hud = null!;
    Control _subLayer = null!, _dialogLayer = null!;
    ArmySlots _army = null!;
    string? _hoverBuilding;
    Vector2? _pickAt;
    bool _rotating;
    Node? _music;

    TownScreen(AdventureScreen adv, TownState town)
    {
        Adv = adv;
        Town = town;
        Name = "TownScreen";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public static TownScreen Open(AdventureScreen adv, TownState town)
    {
        var s = new TownScreen(adv, town);
        Current = s;
        adv.World.Visible = false;
        adv.Hud.Visible = false;
        adv.DialogLayer.AddChild(s);
        return s;
    }

    public override void _Ready()
    {
        Audio.TownAudio.Attach(this);
        _vpc = new SubViewportContainer { Name = "Town3D", AnchorRight = 1, AnchorBottom = 1, Stretch = true, MouseFilter = MouseFilterEnum.Stop };
        _vp = new SubViewport { OwnWorld3D = true, HandleInputLocally = false, PhysicsObjectPicking = false };
        _vpc.AddChild(_vp);
        AddChild(_vpc);
        _vpc.GuiInput += On3DInput;

        _hud = new UiScreen("UI/TownScreen.(WindowScreen).xdb");
        AddChild(_hud);
        _subLayer = new Control { Name = "SubScreens", AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_subLayer);
        _dialogLayer = new Control { Name = "Dialogs", AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_dialogLayer);

        WireHud();
        LoadTown(Town);
    }

    void LoadTown(TownState town)
    {
        Town = town;
        _scene?.QueueFree();
        var desc = TownData.InteriorOf(town.Type);
        if (desc != null)
        {
            _scene = new TownScene3D(desc);
            _vp.AddChild(_scene);
            Callable.From(SyncBuildings).CallDeferred();
            if (TownLauncher.OrbitBy is { } o) Callable.From(() => _scene.Orbit(o.X, o.Y, 1)).CallDeferred();
            PlayMusic(desc);
        }
        Refresh();
    }

    void PlayMusic(string desc)
    {
        _music?.QueueFree();
        var href = (string?)Game.Instance.Xdb.Load(desc).Root?.Element("Music")?.Attribute("href");
        if (string.IsNullOrEmpty(href)) return;
        var music = Game.Instance.Xdb.Load(H5.Core.Xdb.XdbRef.Parse(href, desc).Path).Root;
        var track = (string?)music?.Descendants("Track").FirstOrDefault()?.Attribute("href")
                    ?? (string?)music?.Descendants().FirstOrDefault(e => e.Attribute("href") != null && ((string?)e.Attribute("href"))!.Contains("Sound"))?.Attribute("href");
        if (!string.IsNullOrEmpty(track)) _music = SoundLibrary.Play(H5.Core.Xdb.XdbRef.Parse(track, desc).Path, this);
    }

    /// <summary>Shows each building at the level built in the town (ModObjectName → upgrade index).</summary>
    public void SyncBuildings()
    {
        if (_scene == null) return;
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in TownData.Defs(Session.Db, Town))
        {
            var v = TownData.View(def);
            if (v.ModObjectName.Length == 0) continue;
            levels.TryAdd(v.ModObjectName, 0);
            if (Town.Level(def.Type) >= def.Level) levels[v.ModObjectName] = Math.Max(levels[v.ModObjectName], def.Level);
        }
        foreach (var (name, level) in levels) _scene.ShowLevel(name, level);
    }

    // ------------------------------------------------------------------ HUD

    void WireHud()
    {
        var bus = _hud.Bus;
        bus.On("close_town", Close);
        bus.On("next_town", () => CycleTown(+1));
        bus.On("prev_town", () => CycleTown(-1));
        bus.On("enter_hall", () => OpenSub("hall"));
        bus.On("enter_fort", () => OpenSub("fort"));
        bus.On("upgrade_creatures", () => OpenSub("fort"));
        bus.On("enter_tavern", () => OpenSub("tavern"));
        bus.On("enter_market", () => OpenSub("market"));
        bus.On("enter_magic_guild", () => OpenSub("guild"));
        bus.On("enter_blacksmith", () => OpenSub("blacksmith"));
        bus.On("enter_shipyard", () => Notice("Shipyard", "Ships are built at the town's shipyard on the adventure map."));
        bus.On("enter_special", () => OpenSub("special"));
        bus.On("buy_artifacts", () => Notice("Artifact Merchant", "The artifact merchant is not open yet."));
        bus.On("show_town_info", ShowTownInfo);
        bus.On("click_pointer", (_, src) => { if (src?.Name == "Hero" && VisitingHero() is { } h) OpenHero(h); });
        // The dndicon template is only used by the original drag code.
        _hud.SetVisible("dndicon", false);

        _army = new ArmySlots(_dialogLayer, () => Apply);
        _army.Changed += Refresh;
        for (int k = 1; k <= 7; k++)
        {
            if (_hud.Find($"ListBase/List_{k}/HeroContainer/Hero") is { } top)
                _army.Add(new ArmySlotView { Node = top, Slot = k - 1 });
            if (_hud.Find($"ListBase/List_{k}/TownContainer/Town") is { } bottom)
                _army.Add(new ArmySlotView { Node = bottom, Slot = k - 1 });
        }
    }

    HeroState? VisitingHero() => Session.S.Heroes.FirstOrDefault(h => h.Alive && h.InTown == Town.Object && !h.Garrisoned);
    HeroState? GarrisonHero() => Session.S.Heroes.FirstOrDefault(h => h.Alive && h.InTown == Town.Object && h.Garrisoned);

    bool Apply(AdvCommand cmd)
    {
        var r = Session.Apply(cmd);
        if (!r.Ok) GD.Print($"[H5] town: {cmd.GetType().Name} rejected: {r.Error}");
        Refresh();
        return r.Ok;
    }

    public void Refresh()
    {
        var me = Session.S.Players.First(p => p.Index == Town.Owner);
        TownUi.SetResourceBar(_hud, me);
        var visiting = VisitingHero();
        var garrison = GarrisonHero();
        // Rebind the army rows: top = visiting hero, bottom = garrison hero's army or the town garrison.
        foreach (var node in Enumerable.Range(1, 7))
        {
            if (_hud.Find($"ListBase/List_{node}/HeroContainer/Hero") is { } top) Bind(top, visiting?.Id ?? -1, -1, node - 1, visiting != null);
            if (_hud.Find($"ListBase/List_{node}/TownContainer/Town") is { } bottom)
                Bind(bottom, garrison?.Id ?? -1, garrison == null ? Town.Object : -1, node - 1, true);
        }
        TownUi.SetPicture(_hud.Find("ListBase/List_0/HeroContainer/Hero/PicBox/Pic") is { } hp ? hp : null, visiting != null ? TownUi.HeroFace(visiting, false) : null);
        if (_hud.Find("ListBase/List_0/HeroContainer/Hero/PicBox/Pic") is { } hpic) hpic.Visible = visiting != null;
        var townPic = _hud.Find("ListBase/List_0/TownContainer/Town/PicBox/Pic");
        if (townPic != null)
        {
            townPic.Visible = true;
            TownUi.SetPicture(townPic, garrison != null ? TownUi.HeroFace(garrison, false) : TownIcon());
        }
        _army.Refresh(Session.S);
        _hud.Find("BuildingDesc")?.SetValue("value", _hoverBuilding ?? Town.Name);
        _hud.Find("BuildingDesc")?.SetValue("name", _hoverBuilding ?? Town.Name);
        if (_hud.Find("BuildingDesc") is { } bd && _hoverBuilding == null) bd.SetText($"<h0><color_header><outline><center>{Town.Name}");
        SyncBuildings();
    }

    void Bind(UiNode node, int hero, int town, int slot, bool visible)
    {
        node.Visible = visible;
        foreach (var v in _army.Views.Where(v => v.Node == node)) { v.Hero = hero; v.Town = town; v.Slot = slot; }
    }

    Texture2D? TownIcon()
    {
        const string table = "GameMechanics/RefTables/TownTypesInfo.xdb";
        var item = Game.Instance.Xdb.Load(table).Root?.Element("objects")?.Elements("Item")
            .FirstOrDefault(i => (string?)i.Element("ID") == Town.Type);
        var icons = item?.Element("obj")?.Element("kingdomTownIcons")?.Element("textures")?.Elements("Item").ToList();
        if (icons is not { Count: > 0 }) return null;
        int lvl = Math.Clamp(Town.Level("TB_TOWN_HALL") - 1, 0, icons.Count - 1);
        var href = (string?)icons[lvl].Attribute("href");
        return string.IsNullOrEmpty(href) ? null : Game.Instance.LoadTexture(H5.Core.Xdb.XdbRef.Parse(href, table).Path);
    }

    void CycleTown(int dir)
    {
        var towns = Adv.MyTowns();
        if (towns.Count < 2) return;
        int i = towns.IndexOf(Town);
        LoadTown(towns[((i + dir) % towns.Count + towns.Count) % towns.Count]);
    }

    void ShowTownInfo()
    {
        var lines = new List<string>
        {
            $"{TownTypeName()}<br>Town level: {Town.DevLevel}",
            "Recruitable this week:",
        };
        for (int tier = 1; tier <= 7; tier++)
            if (Session.Game.DwellingCreature(Town, tier, 0) is { } c)
                lines.Add($"{Game.Instance.Db.Creatures[c].Name}: {Town.Available[tier - 1]}");
        Notice(Town.Name, string.Join("<br>", lines));
    }

    string TownTypeName() => TownUi.Text($"Text/Game/Towns/Types/{Town.Type.Replace("TOWN_", "").ToLowerInvariant()}.txt", Town.Type);

    public void Notice(string title, string text) => AdvDialogs.Message(_dialogLayer, title, text, null);

    // ------------------------------------------------------------------ sub-screens

    /// <summary>Opens a town sub-screen by key (also used by `--town-goto=`).</summary>
    public void OpenSub(string key)
    {
        foreach (var c in _subLayer.GetChildren()) c.QueueFree();
        Control? sub = key switch
        {
            "hall" => new TownBuildScreen(this),
            "fort" or "recruit" => new FortScreen(this),
            "tavern" => Town.Level("TB_TAVERN") > 0 ? new TavernScreen(this) : null,
            "guild" => Town.Level("TB_MAGIC_GUILD") > 0 ? new MageGuildScreen(this) : null,
            "blacksmith" => Town.Level("TB_BLACKSMITH") > 0 ? new BlacksmithScreen(this) : null,
            "market" => Town.Level("TB_MARKETPLACE") > 0 ? new MarketScreen(this) : null,
            "hero" => VisitingHero() is { } h ? new HeroScreen(Adv, h, this) : GarrisonHero() is { } g ? new HeroScreen(Adv, g, this) : null,
            "special" => null,
            _ => null,
        };
        if (sub == null)
        {
            var reason = key switch
            {
                "special" => "This building's screen is not available yet.",
                "hero" => "No hero is in the town.",
                _ => "This building has not been built yet.",
            };
            Notice(Town.Name, reason);
            return;
        }
        _hud.Visible = sub is not HeroScreen;
        _subLayer.AddChild(sub);
    }

    public void OpenHero(HeroState h)
    {
        foreach (var c in _subLayer.GetChildren()) c.QueueFree();
        _hud.Visible = false;
        _subLayer.AddChild(new HeroScreen(Adv, h, this));
    }

    /// <summary>Called by sub-screens when they close.</summary>
    public void SubClosed()
    {
        _hud.Visible = true;
        Refresh();
    }

    void Close()
    {
        Current = null;
        Adv.World.Visible = true;
        Adv.Hud.Visible = true;
        Adv.World.SyncObject(Town.Object);
        foreach (var h in Session.S.Heroes) Adv.World.SyncHero(h);
        Adv.Hud.Refresh();
        QueueFree();
    }

    // ------------------------------------------------------------------ 3D picking & camera

    void On3DInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion mm:
                if (_rotating) _scene?.Orbit(mm.Relative.X * 0.3f, -mm.Relative.Y * 0.2f, 1);
                else _pickAt = mm.Position;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } rb:
                _rotating = rb.Pressed;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                _scene?.Orbit(0, 0, 0.9f); break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                _scene?.Orbit(0, 0, 1.1f); break;
            // Buildings open on double click (exe click_pointer @00613130 is a no-op, dblclick_pointer @00619a30 opens).
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true }:
                if (_hoverBuilding != null) ClickBuilding(_hoverBuilding);
                break;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_pickAt is not { } pos || _scene?.Camera == null || !_scene.IsInsideTree()) return;
        _pickAt = null;
        var cam = _scene.Camera;
        var from = cam.ProjectRayOrigin(pos);
        var to = from + cam.ProjectRayNormal(pos) * 3000;
        var hit = _scene.GetWorld3D().DirectSpaceState.IntersectRay(new PhysicsRayQueryParameters3D { From = from, To = to, CollisionMask = 1 << 1 });
        string? name = hit.Count > 0 && hit["collider"].As<Node>() is { } n && n.HasMeta("building") ? n.GetMeta("building").AsString() : null;
        if (name == _hoverBuilding) return;
        SetHighlight(_hoverBuilding, false);
        _hoverBuilding = name;
        SetHighlight(name, true);
        var view = name != null ? ViewFor(name) : null;
        _hud.Find("BuildingDesc")?.SetText($"<h0><color_header><outline><center>{view?.Name ?? (name == null ? Town.Name : "")}");
    }

    /// <summary>The built building level shown by a scene object.</summary>
    BuildingView? ViewFor(string modObject)
    {
        BuildingView? best = null;
        foreach (var def in TownData.Defs(Session.Db, Town))
        {
            var v = TownData.View(def);
            if (!string.Equals(v.ModObjectName, modObject, StringComparison.OrdinalIgnoreCase)) continue;
            if (Town.Level(def.Type) >= def.Level && (best == null || def.Level > best.Def.Level)) best = v;
        }
        return best;
    }

    static readonly StandardMaterial3D Highlight = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = new Color(1, 0.9f, 0.6f, 0.18f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
    };

    void SetHighlight(string? building, bool on)
    {
        if (building == null || _scene == null || !_scene.Buildings.TryGetValue(building, out var b) || b.Current == null) return;
        foreach (var mi in b.Current.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
            mi.MaterialOverlay = on ? Highlight : null;
    }

    void ClickBuilding(string modObject)
    {
        var view = ViewFor(modObject);
        if (view == null) return;
        string? key = view.Def.Type switch
        {
            "TB_TOWN_HALL" => "hall",
            "TB_FORT" => "fort",
            "TB_TAVERN" => "tavern",
            "TB_MAGIC_GUILD" => "guild",
            "TB_MARKETPLACE" => "market",
            "TB_BLACKSMITH" => "blacksmith",
            var t when t.StartsWith("TB_DWELLING_") => "fort",
            _ => null,
        };
        if (key != null) OpenSub(key);
        else Notice(view.Name, view.Description);
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }
}
