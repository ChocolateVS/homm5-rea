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
            var scene = _scene;
            Callable.From(() =>
            {
                scene.StartTownCamera(Options.OptionsStore.GetBool("town_enable_flyby", true) && !OS.GetCmdlineUserArgs().Contains("--town-no-flyby"));
                if (TownLauncher.OrbitBy is { } o) scene.Orbit(o.X, o.Y, 1);
            }).CallDeferred();
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
        bus.On("upgrade_creatures", () => OpenSub("upgrade"));
        bus.On("enter_tavern", () => OpenSub("tavern"));
        bus.On("enter_market", () => OpenSub("market"));
        bus.On("enter_magic_guild", () => OpenSub("guild"));
        bus.On("enter_blacksmith", () => OpenSub("blacksmith"));
        bus.On("enter_shipyard", OpenShipyard);
        bus.On("enter_special", () => OpenSub("special"));
        bus.On("buy_artifacts", () => OpenSub("artifacts"));
        bus.On("show_town_info", () => OpenSub("info"));
        // click_pointer (00613130) does nothing; dblclick_pointer (00619a30) over either portrait (+0x2a0 / +0x2a4)
        // opens the hero screen (00619320).
        foreach (var path in new[] { "ListBase/List_0/HeroContainer/Hero", "ListBase/List_0/TownContainer/Town" })
            if (_hud.Find(path) is { } portrait)
            {
                portrait.MouseFilter = MouseFilterEnum.Stop;
                portrait.GuiInput += e => { if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true }) OpenTownHeroes(); };
            }
        // The dndicon template is only used by the original drag code.
        _hud.SetVisible("dndicon", false);

        _army = new ArmySlots(_dialogLayer, () => ApplyArmyMove);
        _army.Changed += Refresh;
        for (int k = 1; k <= 7; k++)
        {
            if (_hud.Find($"ListBase/List_{k}/HeroContainer/Hero") is { } top)
                _army.Add(new ArmySlotView { Node = top, Slot = k - 1 });
            if (_hud.Find($"ListBase/List_{k}/TownContainer/Town") is { } bottom)
                _army.Add(new ArmySlotView { Node = bottom, Slot = k - 1 });
        }
    }

    internal HeroState? VisitingHero() => Session.S.Heroes.FirstOrDefault(h => h.Alive && h.InTown == Town.Object && !h.Garrisoned);
    internal HeroState? GarrisonHero() => Session.S.Heroes.FirstOrDefault(h => h.Alive && h.InTown == Town.Object && h.Garrisoned);

    /// <summary>
    /// The town army rows' drop handler (the code at 0061591b..006159ff): moving a stack from a hero's army into the other
    /// army when the hero holds fewer than two stacks (FUN_0045a320 &lt; 2) shows T_LEFT_LAST_CREATURE / T_MSGBOX_TITLE
    /// (00679150) and sends nothing. The guard's flag ([esp+0x13]) is read here as "the stack would leave the hero"
    /// (empty or same-creature destination) — UNVERIFIED.
    /// </summary>
    bool ApplyArmyMove(AdvCommand cmd)
    {
        if (cmd is SwapStacksCmd m && m.FromHero >= 0 && m.FromHero != m.ToHero && Session.S.Hero(m.FromHero) is { } h
            && h.Army.StackCount() < 2)
        {
            Stack?[]? to = m.ToHero >= 0 ? Session.S.Hero(m.ToHero)?.Army : m.ToTown >= 0 ? Session.S.Town(m.ToTown)?.Garrison : null;
            var dest = to?[m.ToSlot];
            if (dest is not { Count: > 0 } || dest.Creature == h.Army[m.FromSlot]?.Creature)
            {
                var texts = "UI/TownScreen/texts.(WindowRelatedTexts).xdb";
                Notice(UiGameRoot.RelatedTextAt(texts, "T_MSGBOX_TITLE") ?? "", UiGameRoot.RelatedTextAt(texts, "T_LEFT_LAST_CREATURE") ?? "");
                return false;
            }
        }
        return Apply(cmd);
    }

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
        // Army rows (FUN_0061c5b0 / 00615d10): the upper row (HeroContainer, portrait +0x2a4, "GARRISON_SLOT") is the
        // garrison — the garrison hero's army or the town's own; the lower row (TownContainer, portrait +0x2a0,
        // "VISITING_SLOT") is the visiting hero.
        foreach (var node in Enumerable.Range(1, 7))
        {
            if (_hud.Find($"ListBase/List_{node}/HeroContainer/Hero") is { } top)
                Bind(top, garrison?.Id ?? -1, garrison == null ? Town.Object : -1, node - 1, true);
            // 0061c5b0 shows both rows' PicBoxes (vf 0x44(1)) for List_1..7 whatever the heroes; 00615d10 hides only an
            // empty slot's picture.
            if (_hud.Find($"ListBase/List_{node}/TownContainer/Town") is { } bottom) Bind(bottom, visiting?.Id ?? -1, -1, node - 1, true);
        }
        var visitPic = _hud.Find("ListBase/List_0/TownContainer/Town/PicBox/Pic");
        TownUi.SetPicture(visitPic, visiting != null ? TownUi.HeroFace(visiting, false) : null);
        if (visitPic != null) visitPic.Visible = visiting != null;
        var garrisonPic = _hud.Find("ListBase/List_0/HeroContainer/Hero/PicBox/Pic");
        if (garrisonPic != null)
        {
            garrisonPic.Visible = true;
            // 00615d10: an empty garrison slot shows UIGameRoot CDBID_TEXTURE_GARRISON (Textures/TownScreen/GarHero) with
            // the GARRISON_SLOT tooltip; an empty visiting slot is cleared and hidden.
            TownUi.SetPicture(garrisonPic, garrison != null ? TownUi.HeroFace(garrison, false) : Game.Instance.LoadTexture("Textures/TownScreen/GarHero.xdb"));
            if (garrisonPic is UiNode gp) gp.SetTooltip(garrison == null ? TownUi.Text("Text/Interface/TownScreen/GarrisonHeroSlot.txt", "") : null);
        }
        _army.Refresh(Session.S);
        _hud.Find("BuildingDesc")?.SetValue("value", _hoverBuilding ?? Town.Name);
        _hud.Find("BuildingDesc")?.SetValue("name", _hoverBuilding ?? Town.Name);
        if (_hud.Find("BuildingDesc") is { } bd && _hoverBuilding == null) bd.SetText($"<h0><color_header><outline><center>{Town.Name}");
        SyncBuildings();
        UpdateBuildingButtons();
    }

    /// <summary>
    /// 0061b1c0 → 00614440 per HUD button: enabled (007c5b60 → window vf 0x3c) when FUN_00614290 holds — the town's
    /// owner check 00613c00 and town vf 0x34(building) &gt; 0 — EnterMarket (2), EnterTavern (4), EnterShipyard (3),
    /// EnterBlacksmith (5), MagicGuild (006145a0); the window's "deny" value (vf 0x80, `&lt;value=deny&gt;` in each
    /// tooltip) is empty, else UI/TownScreen texts "building_not_present".
    /// </summary>
    void UpdateBuildingButtons()
    {
        foreach (var (window, building) in new[]
                 {
                     ("EnterMarket", "TB_MARKETPLACE"), ("EnterTavern", "TB_TAVERN"), ("EnterShipyard", "TB_SHIPYARD"),
                     ("EnterBlacksmith", "TB_BLACKSMITH"),
                 })
            BuildingButton(window, building, null);
        // 00614d00: MagicGuild's tooltip is TT_HALL_OF_TRIAL when 006145a0 gives TB_SPECIAL_1 (a Stronghold), else TT_MAGE_GUILD.
        var guild = H5.Adventure.Engine.Adventure.GuildScreenBuilding(Town);
        BuildingButton("MagicGuild", guild, HudText(guild == "TB_MAGIC_GUILD" ? "TT_MAGE_GUILD" : "TT_HALL_OF_TRIAL"));
        UpdateArtifactsButton();
        // 0061b1c0 (BL = the owner check 00613c00): EnterFort enabled with 0061a390 — a dwelling (buildings 7..0xd) of
        // level > 0 (its caravan part, other dwellings of the player, is not modelled); the upgrade button (+0x38c, vf
        // 0x3c) with 00613090 — a dwelling of level > 1; EnterHall enabled for the owner, its button state (vf 0xc) = town
        // vf 0x9c (00cce4e0: a build is still possible today).
        bool Dwellings(int min) => Enumerable.Range(1, 7).Any(k => Town.Level($"TB_DWELLING_{k}") >= min);
        if (_hud.Find("EnterFort") is { } fort) fort.Enabled = Dwellings(1);
        if (_hud.Find("UpgradeCreatures") is { } upgrade) upgrade.Enabled = Dwellings(2);
        if (_hud.Find("EnterHall") is { } hall)
        {
            hall.Enabled = true;
            hall.SetState(Town.BuiltToday ? 0 : 1);
        }
        UpdateSpecialButton();
    }

    static string HudText(string key) => UiGameRoot.RelatedTextAt("UI/TownScreen/texts.(WindowRelatedTexts).xdb", key) ?? "";

    /// <summary>
    /// 00614440(window, building): enabled (007c5b60) when FUN_00614290 holds — the owner check 00613c00 and town
    /// vf 0x34(building) &gt; 0; vf 0x80 sets the tooltip's deny value to "" or "building_not_present".
    /// <paramref name="tooltip"/> replaces the window's own tooltip text (vf 0x68) when given.
    /// </summary>
    bool BuildingButton(string window, string building, string? tooltip)
    {
        bool built = Town.Level(building) > 0;
        if (_hud.Find(window) is not { } button) return built;
        button.Enabled = built;
        SetDeny(button, tooltip, built ? "" : HudText("building_not_present"));
        return built;
    }

    static void SetDeny(UiNode button, string? tooltip, string deny)
    {
        var tip = tooltip ?? UiResources.Instance.Text(button.Window.TooltipPath) ?? "";
        if (tip.Length == 0) return;
        button.SetTooltip(tip.Replace("<value=deny>", deny.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// buy_artifacts (00614960): tooltip TT_TRAVELLER_SHELTER_TOOLTIP in a Stronghold (town type 10), else
    /// TT_BUY_ARTIFACTS_TOOLTIP. Building 00614600: Academy and Stronghold TB_SPECIAL_3, Dungeon TB_SPECIAL_4, else none
    /// (0x1a) → disabled with "unavailable_in_this_town". A Stronghold whose building is built (00614290) but has no
    /// Barbarian hero (00613c60) → disabled with "traveller_shelter_anavaliable". Otherwise 00614440.
    /// </summary>
    void UpdateArtifactsButton()
    {
        bool stronghold = Town.Type == "TOWN_STRONGHOLD";
        var tip = HudText(stronghold ? "TT_TRAVELLER_SHELTER_TOOLTIP" : "TT_BUY_ARTIFACTS_TOOLTIP");
        string? building = Town.Type switch
        {
            "TOWN_ACADEMY" or "TOWN_STRONGHOLD" => "TB_SPECIAL_3", "TOWN_DUNGEON" => "TB_SPECIAL_4", _ => null,
        };
        if (_hud.Find("buy_artifacts") is not { } button) return;
        if (building == null)
        {
            button.Enabled = false;
            SetDeny(button, tip, HudText("unavailable_in_this_town"));
            return;
        }
        if (stronghold && Town.Level(building) > 0 && !BarbarianInTown())
        {
            button.Enabled = false;
            SetDeny(button, tip, HudText("traveller_shelter_anavaliable"));
            return;
        }
        BuildingButton("buy_artifacts", building, tip);
    }

    /// <summary>00613c60: the visiting hero's race (hero vf 0x258) is 8 (Stronghold), else the garrison hero's is.</summary>
    bool BarbarianInTown() => VisitingHero() is { Town: "TOWN_STRONGHOLD" } || GarrisonHero() is { Town: "TOWN_STRONGHOLD" };

    /// <summary>
    /// EnterSpecial (00614e60, jump table 006151c8 on the town type 3..10): building and required skill — Haven
    /// TB_SPECIAL_1 / Training (0xd), Preserve TB_SPECIAL_0 / Avenger (0x10), Academy TB_SPECIAL_2 / Artificer (0x11,
    /// the visiting hero only), Dungeon TB_SPECIAL_3, Necropolis TB_SPECIAL_2, Inferno and Stronghold TB_SPECIAL_5,
    /// Fortress town vf 0x4c(0x20) (ability 0x20's building, TB_SPECIAL_1). The tooltip's special value is the
    /// building's name (00cd3fc0(1)); 00614440 enables it when built; then, with a skill, the visiting hero (vf 0x18(0))
    /// or the garrison hero (vf 0x18(1), not for the Academy) must have it (hero vf 0x174 &gt; 0), else 0061f750
    /// disables it with "skill_not_present" (its skill value = the skill's name).
    /// </summary>
    void UpdateSpecialButton()
    {
        (string Building, string? Skill, bool Both) spec = Town.Type switch
        {
            "TOWN_HEAVEN" => ("TB_SPECIAL_1", "HERO_SKILL_TRAINING", true),
            "TOWN_PRESERVE" => ("TB_SPECIAL_0", "HERO_SKILL_AVENGER", true),
            "TOWN_ACADEMY" => ("TB_SPECIAL_2", "HERO_SKILL_ARTIFICIER", false),
            "TOWN_DUNGEON" => ("TB_SPECIAL_3", null, true),
            "TOWN_NECROMANCY" => ("TB_SPECIAL_2", null, true),
            "TOWN_INFERNO" or "TOWN_STRONGHOLD" => ("TB_SPECIAL_5", null, true),
            "TOWN_FORTRESS" => ("TB_SPECIAL_1", null, true),
            _ => ("", null, false),
        };
        if (spec.Building.Length == 0 || _hud.Find("EnterSpecial") is not { } button) return;
        var name = Session.Db.Building(Town.Type, spec.Building, 1)?.Name ?? "";
        var tip = (UiResources.Instance.Text(button.Window.TooltipPath) ?? "").Replace("<value=special>", name, StringComparison.OrdinalIgnoreCase);
        if (!BuildingButton("EnterSpecial", spec.Building, tip) || spec.Skill is not { } skillId) return;
        bool Has(HeroState? h) => h != null && h.MasteryOf(skillId) != H5.Core.Data.Mastery.None;
        if (Has(VisitingHero()) || (spec.Both && Has(GarrisonHero()))) return;
        button.Enabled = false;
        var skill = UiResources.StripMarkup(SkillCommonName(skillId) ?? skillId).Trim();
        SetDeny(button, tip, HudText("skill_not_present").Replace("<value=skill>", skill, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The skill's CommonNameFileRef text (Skills.xdb); the record field the exe reads (008481f0 → +0x18) is taken to be it.</summary>
    static string? SkillCommonName(string skill)
    {
        const string table = "GameMechanics/RefTables/Skills.xdb";
        var item = Game.Instance.Xdb.Load(table).Root?.Element("objects")?.Elements("Item").FirstOrDefault(i => (string?)i.Element("ID") == skill);
        var href = (string?)item?.Element("obj")?.Element("CommonNameFileRef")?.Attribute("href");
        return string.IsNullOrEmpty(href) ? null : UiResources.Instance.Text(H5.Core.Xdb.XdbRef.Parse(href, table).Path);
    }

    void Bind(UiNode node, int hero, int town, int slot, bool visible)
    {
        node.Visible = visible;
        foreach (var v in _army.Views.Where(v => v.Node == node)) { v.Hero = hero; v.Town = town; v.Slot = slot; }
    }

    internal Texture2D? TownIcon()
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

    string TownTypeName() => TownUi.Text($"Text/Game/Towns/Types/{Town.Type.Replace("TOWN_", "").ToLowerInvariant()}.txt", Town.Type);

    public void Notice(string title, string text) => AdvDialogs.Message(_dialogLayer, title, text, null);

    /// <summary>
    /// The town shipyard (enter_shipyard → exe 0061a1c0 / 00619af0): nothing happens until the shipyard is built;
    /// then the stock shipyard box (<see cref="AdvDialogs.Shipyard"/>) for this town's slipway and, if accepted,
    /// <see cref="BuildShipCmd"/> — the boat appears on the town's slipway.
    /// </summary>
    void OpenShipyard()
    {
        if (Town.Level("TB_SHIPYARD") <= 0) return;
        var me = Session.S.Players.First(p => p.Index == Town.Owner);
        AdvDialogs.Shipyard(_dialogLayer, me, Session.Game.ShipyardStatus(Town.Object), yes =>
        {
            if (!yes) return;
            var r = Session.Apply(new BuildShipCmd(Town.Object) { Player = me.Index });
            if (!r.Ok) GD.PushWarning($"[H5] build ship: {r.Error}");
            Refresh();
        });
    }

    // ------------------------------------------------------------------ sub-screens

    /// <summary>Opens a town sub-screen by key (also used by `--town-goto=`).</summary>
    public void OpenSub(string key)
    {
        // Every building / HUD action ends the fly-up first (00618170 … set screen +0x294, 00616bf0).
        _scene?.EndFlyby();
        if (key == "shipyard") { OpenShipyard(); return; }
        foreach (var c in _subLayer.GetChildren()) c.QueueFree();
        Control? sub = key switch
        {
            "hall" => new TownBuildScreen(this),
            "fort" or "recruit" => new TownHireScreen(this),
            var d when d.StartsWith("dwelling_") && int.TryParse(d[9..], out var tier) => new TownHireScreen(this, tier),
            "tavern" => Town.Level("TB_TAVERN") > 0 ? new TavernScreen(this) : null,
            // FUN_00619d70: town vfunc 0x34(FUN_006145a0 = TB_SPECIAL_1 in a Stronghold, else TB_MAGIC_GUILD) ≠ 0 → "Button01".
            "guild" => Town.Level(H5.Adventure.Engine.Adventure.GuildScreenBuilding(Town)) > 0 ? new MageGuildScreen(this) : null,
            "blacksmith" => Town.Level("TB_BLACKSMITH") > 0 ? new BlacksmithScreen(this) : null,
            "market" => Town.Level("TB_MARKETPLACE") > 0 ? new MarketScreen(this) : null,
            "hero" => VisitingHero() is { } h ? new HeroScreen(Adv, h, this) : GarrisonHero() is { } g ? new HeroScreen(Adv, g, this) : null,
            "upgrade" => new UpgradeCreaturesScreen(this),
            "info" => new TownInfoScreen(this),
            // 00618a90: only when the town has a CBlackMarket (town vf 0x10c, abilities 0x17 / 0x1d).
            // buy_artifacts (0061a260): a Stronghold opens the Walker's Hut (00618920), other towns the merchant (00618a90).
            "artifacts" when Town.Type == "TOWN_STRONGHOLD" => WalkersHutScreen.TryOpen(this),
            "artifacts" => H5.Adventure.Engine.Adventure.TownHasArtifactMerchant(Town) ? new ArtifactMerchantScreen(this) : null,
            "special" => SpecialScreen(Town.Type),
            "training" => HavenTrainingScreen.CanOpen(this) ? new HavenTrainingScreen(this) : null,
            _ => null,
        };
        // Nothing opens and nothing is shown when the screen's condition fails: the HUD handlers (SFLB_AddObservers
        // 0061bcf0) end in 006183c0 market / 006186b0 tavern / 00619c20 blacksmith / 00619d70 guild, which return
        // when town vf 0x34(building) = 0; buy_artifacts 0061a260 → 00618920 (no Barbarian) / 00618a90 (no
        // CBlackMarket); enter_special 0061b570; the hero screen 00619320 returns without a town hero.
        if (sub == null) return;
        // The hero screen, the marketplace, the hire screen, the tavern and the build screen (own resource bar and jog
        // dial, 006590a0) replace the town HUD.
        _hud.Visible = sub is not HeroScreen and not MarketScreen and not TownHireScreen and not TavernScreen and not TownBuildScreen;
        _subLayer.AddChild(sub);
    }

    /// <summary>
    /// enter_special (FUN_0061b570): switch on the town type 3..10 — Haven FUN_006184f0 (training), Preserve
    /// FUN_00618ff0 (favoured enemies), Academy FUN_00618c10 (creature artifacts), Dungeon FUN_006187c0 (ritual
    /// pit), Necropolis FUN_00619e80 (transform), Inferno FUN_00618870 (sacrificial pit), Fortress FUN_00618d10,
    /// Stronghold FUN_00618dc0 (slave market).
    /// </summary>
    Control? SpecialScreen(string townType) => townType switch
    {
        "TOWN_HEAVEN" => HavenTrainingScreen.CanOpen(this) ? new HavenTrainingScreen(this) : null,
        "TOWN_PRESERVE" => FavoredEnemyScreen.CanOpen(this) ? new FavoredEnemyScreen(this) : null,
        "TOWN_NECROMANCY" => new NecropolisTransformScreen(this),
        "TOWN_INFERNO" => new SacrificeScreen(this, growth: false),
        "TOWN_DUNGEON" => new SacrificeScreen(this, growth: true),
        "TOWN_STRONGHOLD" => SlaveMarketScreen.CanOpen(this) ? new SlaveMarketScreen(this) : null,
        // FUN_00618c10 → FUN_00640850 (UI/AcademyCreatureArtifacts) with the town's hero; nothing without one.
        "TOWN_ACADEMY" => ArcaneForgeScreen.HeroFor(this) is { } forgeHero ? new ArcaneForgeScreen(this, forgeHero) : null,
        // FUN_00618d10: ability 0x20 (Runic Shrine) → FUN_00630450 = the magic guild screen (N_MAGIC_GUILD_SCREEN_ID)
        // with "Button07" (the rune page of FUN_006309d0).
        "TOWN_FORTRESS" => Town.Level("TB_SPECIAL_1") > 0 ? new MageGuildScreen(this, "Button07") : null,
        _ => null,
    };

    /// <summary>
    /// Screen of a special building double-clicked in the 3D town (FUN_0061b760: the building's town ability
    /// from the table at 00f7c998 → handler; abilities without a screen show the building's info box).
    /// </summary>
    static string? SpecialBuildingKey(string townType, string building) => (townType, building) switch
    {
        ("TOWN_HEAVEN", "TB_SPECIAL_1") => "training",
        ("TOWN_PRESERVE", "TB_SPECIAL_0") => "special",
        ("TOWN_NECROMANCY", "TB_SPECIAL_2") => "special",
        ("TOWN_INFERNO", "TB_SPECIAL_5") => "special",
        ("TOWN_DUNGEON", "TB_SPECIAL_3") => "special",
        ("TOWN_STRONGHOLD", "TB_SPECIAL_5") => "special",
        ("TOWN_STRONGHOLD", "TB_SPECIAL_3") => "artifacts",   // town ability 0x2b, the Walker's Hut (00618920)
        ("TOWN_STRONGHOLD", "TB_SPECIAL_1") => "guild",   // abilities 0x27..0x29 → FUN_00619d70 (the guild screen)
        ("TOWN_FORTRESS", "TB_SPECIAL_1") => "special",
        ("TOWN_ACADEMY", "TB_SPECIAL_2") => "special",   // town ability 0x16, the Arcane Forge
        ("TOWN_ACADEMY", "TB_SPECIAL_3") => "artifacts", // 0061b760 cases 0x17 / 0x1d → 00618a90 (artifact merchant)
        ("TOWN_DUNGEON", "TB_SPECIAL_4") => "artifacts",
        _ => null,
    };

    /// <summary>
    /// 00619320: the hero screen for the town's heroes — both (the two-hero page) when the visiting and the garrison
    /// hero are present, else the one there is.
    /// </summary>
    void OpenTownHeroes()
    {
        var visiting = VisitingHero();
        var garrison = GarrisonHero();
        if (visiting == null && garrison == null) return;
        foreach (var c in _subLayer.GetChildren()) c.QueueFree();
        _hud.Visible = false;
        _subLayer.AddChild(visiting != null && garrison != null
            ? new HeroScreen(Adv, visiting, this, meet: garrison)
            : new HeroScreen(Adv, visiting ?? garrison!, this));
    }

    public void OpenHero(HeroState h)
    {
        _scene?.EndFlyby();
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

    // Mouse axes in the exe's input units (bindconfigure MOUSE_AXIS_X/Y 1.2, MOUSE_AXIS_Z 160; the final scale
    // is the combat camera's, docs/rules/CAMERA.md open point).
    const float MouseAxisPerPixel = 1.2f / 1000f, WheelInputPerNotch = 120f / 160f;

    void On3DInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion mm:
                // input_a2.cfg common camera section, consumed by CTownOperator (007d5550):
                // +camera_rotate_mouse / −camera_pitch_mouse = right button + X / Y;
                // −camera_strafe_mouse / +camera_forward_mouse = middle button (or CTRL + right) + X / Y.
                var d = mm.Relative * MouseAxisPerPixel;
                if (_middleDown || (_rotating && Input.IsKeyPressed(Key.Ctrl))) _scene?.AddInput(-d.X, 0, d.Y, 0);
                else if (_rotating) _scene?.AddInput(d.X, -d.Y, 0, 0);
                else _pickAt = mm.Position;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } rb:
                _rotating = rb.Pressed;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } mb:
                _middleDown = mb.Pressed;
                break;
            // −camera_zoom_mouse MOUSE_AXIS_Z: wheel up zooms in.
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                _scene?.AddInput(0, 0, 0, -WheelInputPerNotch); break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                _scene?.AddInput(0, 0, 0, WheelInputPerNotch); break;
            // The first left button press during the fly-up only ends it (win_left_button_down 0061bc00).
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } when _scene?.FlybyActive == true:
                _scene.EndFlyby();
                break;
            // Buildings open on double click (exe click_pointer @00613130 is a no-op, dblclick_pointer @00619a30 opens).
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true }:
                if (_hoverBuilding != null) ClickBuilding(_hoverBuilding);
                break;
        }
    }

    bool _middleDown;

    /// <summary>
    /// Ends the fly-up on any left button press or release over the town screen (win_left_button_down 0061bc00 /
    /// win_left_button_up 006199f0 set screen +0x294 and switch to the hand camera).
    /// </summary>
    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } && _scene?.FlybyActive == true && _subLayer.GetChildCount() == 0)
            _scene.EndFlyby();
    }

    /// <summary>
    /// Held camera keys (input_a2.cfg common section): UP/DOWN ±camera_forward, RIGHT/LEFT ±camera_strafe,
    /// CTRL+DOWN/UP ±camera_pitch, CTRL+RIGHT/LEFT ±camera_rotate, PG_DOWN/PG_UP ±camera_zoom; level × frame time.
    /// </summary>
    public override void _Process(double delta)
    {
        if (_scene == null || _scene.FlybyActive || _subLayer.GetChildCount() > 0 || _dialogLayer.GetChildCount() > 0) return;
        if (GetViewport().GuiGetFocusOwner() is LineEdit) return;
        float dt = (float)delta, rot = 0, pitch = 0, fwd = 0, zoom = 0;
        bool ctrl = Input.IsKeyPressed(Key.Ctrl);
        float Axis(Key plus, Key minus) => (Input.IsKeyPressed(plus) ? 1 : 0) - (Input.IsKeyPressed(minus) ? 1 : 0);
        if (ctrl) { pitch = Axis(Key.Down, Key.Up); rot = Axis(Key.Right, Key.Left); }
        else { fwd = Axis(Key.Up, Key.Down); rot = Axis(Key.Right, Key.Left); }
        zoom = Axis(Key.Pagedown, Key.Pageup);
        if (rot != 0 || pitch != 0 || fwd != 0 || zoom != 0) _scene.AddInput(rot * dt, pitch * dt, fwd * dt, zoom * dt);
    }

    public override void _PhysicsProcess(double delta)
    {
        // No building hover while the fly-up runs (0061b140: screen +0x294 = 0 → plain cursor, no pick).
        if (_scene?.FlybyActive == true) { _pickAt = null; return; }
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

    // Hover highlight: 0061a5d0 (hover, param_3 = 0) hands the scene highlighter (screen +0x174, vcall +4) the colour
    // (0.3961, 0.2196, 0, 0) for the building's CArenaTownModObject; drawn here as an additive overlay of that colour.
    static readonly StandardMaterial3D Highlight = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = new Color(0.3961f, 0.2196f, 0f, 1f),
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
            var t when t.StartsWith("TB_DWELLING_") => "dwelling_" + t["TB_DWELLING_".Length..],
            _ => null,
        };
        key ??= SpecialBuildingKey(Town.Type, view.Def.Type);
        // Ability 0 (Training Grounds): when the screen cannot open, the building's info box (FUN_0061b760 default).
        if (key == "training" && !HavenTrainingScreen.CanOpen(this)) key = null;
        if (view.Def.Type == "TB_SHIPYARD") OpenShipyard();
        else if (key != null) OpenSub(key);
        else Notice(view.Name, view.Description);
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }
}
