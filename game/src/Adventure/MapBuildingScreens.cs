using Godot;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;
using H5Reborn.Town;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>Shared plumbing of the full-screen map-building windows opened while a hero visits the object.</summary>
public abstract partial class MapBuildingScreen : Control
{
    protected readonly AdventureScreen Adv;
    protected readonly UiScreen Ui;
    protected readonly int HeroId;
    protected readonly int Object;

    protected MapBuildingScreen(AdventureScreen adv, string screenXdb, int heroId, int obj)
    {
        Adv = adv;
        HeroId = heroId;
        Object = obj;
        Name = GetType().Name;
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Stop;
        Ui = new UiScreen(screenXdb);
        AddChild(Ui);
        foreach (var msg in new[] { "close", "cancel", "close_window", "timeout_forced_close" })
            Ui.Bus.On(msg, Close);
    }

    protected HeroState Hero => Adv.Session.S.Hero(HeroId)!;
    protected PlayerState Me => Adv.Session.S.Players.First(p => p.Index == Hero.Owner);

    protected bool Apply(AdvCommand cmd)
    {
        var r = Adv.Session.Apply(cmd with { Player = Me.Index });
        if (!r.Ok) AdvDialogs.Message(this, Adv.ObjectTitle(Object), Capitalise(r.Error ?? "Not possible."), null);
        Refresh();
        Adv.Hud.Refresh();
        return r.Ok;
    }

    static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..] + ".";

    public virtual void Refresh() { if (Ui.Find("resource-bar") != null) TownUi.SetResourceBar(Ui, Me); }

    public override void _Ready() => Refresh();

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape or Key.Enter or Key.KpEnter })
        {
            GetViewport().SetInputAsHandled();
            Close();
        }
    }

    protected void Close() => QueueFree();

    /// <summary>"1250 Gold, 2 Crystal" in resource order, or "free".</summary>
    public static string CostText(Resources c)
    {
        int[] v = { c.Wood, c.Ore, c.Mercury, c.Crystal, c.Sulfur, c.Gem, c.Gold };
        var parts = new[] { 6, 0, 1, 2, 3, 4, 5 }.Where(i => v[i] > 0).Select(i => $"{v[i]} {AdvTexts.ResourceName(i)}").ToList();
        return parts.Count == 0 ? "0" : string.Join(", ", parts);
    }
}

/// <summary>
/// Hill Fort (UI/FortHill): the visiting hero's army; each stack's button opens the stock choose-upgrade
/// item (UI/UpgradeCreatures/AlternativeUpgrade/ChooseUpgradeItem) with the normal and alternative upgrade,
/// priced by <see cref="H5.Adventure.Engine.Adventure.UpgradeQuote"/> at the Hill Fort rate.
/// </summary>
public sealed partial class HillFortScreen : MapBuildingScreen
{
    UiScreen? _choose;

    public HillFortScreen(AdventureScreen adv, int heroId, int hillFort) : base(adv, "UI/FortHill.(WindowScreen).xdb", heroId, hillFort)
    {
        Ui.Bus.On("CloseFortHill", Close);
        Ui.SetVisible("FortHill/creaturesholder", true);
        for (int i = 0; i < H5.Adventure.State.Army.Slots; i++)
        {
            int slot = i;
            if (Ui.Find($"FortHill/creaturesholder/creature{i + 1}/button") is { } b) b.Clicked += _ => OpenChoice(slot);
        }
    }

    public override void _Ready()
    {
        base._Ready();
        // Dev: H5_HILLFORT_CHOICE=<slot> opens that stack's choose-upgrade item (screenshots).
        if (int.TryParse(System.Environment.GetEnvironmentVariable("H5_HILLFORT_CHOICE"), out int dev))
            Callable.From(() => OpenChoice(dev)).CallDeferred();
    }

    public override void Refresh()
    {
        base.Refresh();
        var h = Hero;
        Ui.Find("FortHill/HeroName")?.SetValue("heroname", h.Name);
        TownUi.SetPicture(Ui.Find("FortHill/HeroIcon"), TownUi.HeroFace(h, large: false));
        var game = Adv.Session.Game;
        for (int i = 0; i < H5.Adventure.State.Army.Slots; i++)
        {
            var s = h.Army[i];
            var icon = Ui.Find($"FortHill/creaturesholder/creature{i + 1}/icon");
            if (icon != null) icon.Visible = s is { Count: > 0 };
            TownUi.SetPicture(icon, s is { Count: > 0 } ? TownUi.CreatureIcon(s.Creature) : null);
            (icon?.FindChild("Text", false, false) as UiNode)?.SetValue("NumberOfCreatures", s?.Count.ToString() ?? "");
            if (Ui.Find($"FortHill/creaturesholder/creature{i + 1}/button") is { } b)
                b.Enabled = s is { Count: > 0 } && (game.UpgradeQuote(s.Creature, 1, s.Count, true).Target != null
                                                   || game.UpgradeQuote(s.Creature, 2, s.Count, true).Target != null);
        }
    }

    void OpenChoice(int slot)
    {
        _choose?.QueueFree();
        _choose = null;
        var s = Hero.Army[slot];
        if (s is not { Count: > 0 } || Ui.Find($"FortHill/creaturesholder/creature{slot + 1}") is not { } cell) return;
        _choose = UpgradeChooser.Open(this, cell, Adv.Session.Game, Me, s.Creature, s.Count, hillFort: true, _ => true, upgrade =>
        {
            _choose = null;
            if (Apply(new UpgradeCreatureCmd(HeroId, -1, slot, upgrade, Object)))
                Audio.AudioSystem.PlayNamed("FORTHILL_UPGRADE_CREATURES_SOUND");
        });
    }

    public override void _GuiInput(InputEvent e)
    {
        // A click beside the pop-up item dismisses it.
        if (_choose != null && e is InputEventMouseButton { Pressed: true }) { _choose.QueueFree(); _choose = null; }
    }
}

/// <summary>War Machine Factory (UI/BuyWarMachine): the three machines at their standard prices.</summary>
public sealed partial class WarMachineFactoryScreen : MapBuildingScreen
{
    static readonly string[] Machines = { "WAR_MACHINE_BALLISTA", "WAR_MACHINE_FIRST_AID_TENT", "WAR_MACHINE_AMMO_CART" };
    int _choice;

    public WarMachineFactoryScreen(AdventureScreen adv, int heroId, int factory) : base(adv, "UI/BuyWarMachine.(WindowScreen).xdb", heroId, factory)
    {
        Ui.Bus.On("choice_click", (_, src) => { if (src?.Name.ToString() is { } n && n.StartsWith("Choice_")) { _choice = n[^1] - '0'; Refresh(); } });
        Ui.Bus.On("buy_war_machine", () =>
        {
            if (Apply(new BuyFactoryWarMachineCmd(HeroId, Object, Machines[_choice]))) Audio.AudioSystem.PlayNamed("BUY_WAR_MACHINE_SOUND");
        });
    }

    public override void Refresh()
    {
        base.Refresh();
        var db = Game.Instance.Db;
        Ui.Find("MainHeaderText")?.SetValue("Value", Adv.ObjectTitle(Object));
        for (int i = 0; i < Machines.Length; i++)
        {
            TownUi.SetPicture(Ui.Find($"Choice_{i}/Icon"), TownUi.CreatureIcon(Machines[i]));
            int price = db.WarMachines.TryGetValue(Machines[i], out var wm) ? wm.Cost.Gold : 0;
            Ui.Find($"Price_{i}")?.SetText($"<center><value_value>{price}");
            Ui.Find($"Choice_{i}")?.SetState(i == _choice && (Ui.Find($"Choice_{i}")?.Window.ButtonStates.Count ?? 0) > 1 ? 1 : 0);
        }
        var (name, desc) = BlacksmithScreen.MachineTexts(Machines[_choice]);
        Ui.Find("DescriptionHeader")?.SetValue("Value", name);
        Ui.Find("DescriptionText")?.SetValue("Value", UiResources.StripMarkup(desc));
    }
}

/// <summary>
/// The stock choose-upgrade item (UI/UpgradeCreatures/AlternativeUpgrade/ChooseUpgradeItem): pops up over a
/// stack with its normal and alternative upgrade; a choice asks for confirmation with the price
/// (AlternativeUpgradeBox TDescription "Upgrade cost:").
/// </summary>
public static class UpgradeChooser
{
    public static UiScreen Open(Control host, Control cell, H5.Adventure.Engine.Adventure game, PlayerState me, string creature, int count,
        bool hillFort, Func<string, bool> allowed, Action<int> confirmed)
    {
        var choose = new UiScreen("UI/UpgradeCreatures/AlternativeUpgrade/ChooseUpgradeItem.(WindowSimple).xdb");
        host.AddChild(choose);
        // The item pops up over the stack it upgrades (authored at 0,0).
        if (choose.NodeOf(choose.Root) is { } item)
            item.GlobalPosition = cell.GlobalPosition + new Vector2((cell.Size.X - item.Size.X) / 2, -item.Size.Y);
        string[] icons = { "norm_upg_icon", "alt_upg_icon" }, buttons = { "upg_button", "alt_upg_button" };
        for (int u = 1; u <= 2; u++)
        {
            var (target, cost) = game.UpgradeQuote(creature, u, count, hillFort);
            TownUi.SetPicture(choose.Find(icons[u - 1]), target != null ? TownUi.CreatureIcon(target) : null);
            if (choose.Find(buttons[u - 1]) is not { } b) continue;
            b.Enabled = target != null && allowed(target) && me.Purse.CanAfford(cost);
            if (target == null) continue;
            string name = Game.Instance.Db.Creatures.TryGetValue(target, out var td) ? td.Name : target;
            b.SetTooltip($"{name}<br>{MapBuildingScreen.CostText(cost)}");
            int upgrade = u;
            b.Clicked += _ =>
            {
                choose.QueueFree();
                var text = TownUi.Text("UI/UpgradeCreatures/AlternativeUpgradeBox/TDescription.txt", "Choose the upgrade for this creature.<br><br>Upgrade cost:");
                AdvDialogs.YesNo(host, name, $"{text} {MapBuildingScreen.CostText(cost)}", TownUi.CreatureIcon(target), yes => { if (yes) confirmed(upgrade); });
            };
        }
        return choose;
    }
}

/// <summary>The map's Artifact Merchant wagon (BUILDING_BLACK_MARKET): UI/BuyArtefact for the visiting hero.</summary>
public sealed partial class BlackMarketScreen : MapBuildingScreen
{
    readonly ArtifactMerchantController _merchant;

    public BlackMarketScreen(AdventureScreen adv, int heroId, int market) : base(adv, "UI/BuyArtefact.(WindowScreen).xdb", heroId, market)
    {
        _merchant = new ArtifactMerchantController(Ui, adv.Session, () => Me, () => Hero, market, () => adv.Hud.Refresh(),
            text => AdvDialogs.Message(this, adv.ObjectTitle(market), text, null));
    }

    public override void Refresh()
    {
        base.Refresh();
        _merchant?.Refresh();
    }
}

/// <summary>
/// The Seer Hut box (UI/SeerHut/Main, exe 006f09b0 / fill 006f0000): the quest's caption and description, the quest
/// item (006ef730) and the reward (006efb10) with the ID_SEER_HUT related *_FORMAT texts. Mode 1 (quest not taken):
/// Accept takes it. Mode 2 (taken, not fulfilled): Accept closes. Mode 3 (fulfilled): Complete Quest and Cancel
/// replace Accept (SetVisible on mode == 3).
/// </summary>
public static class SeerHutBox
{
    const string Texts = "ID_SEER_HUT";

    static string Fmt(string key, string desc, int number)
    {
        var t = UiGameRoot.RelatedText(Texts, key) ?? "";
        // 006efb10 inserts <value=number> only when it is positive.
        return t.Replace("<value=desc>", desc).Replace("<value=number>", number > 0 ? number.ToString() : "");
    }

    static readonly string[] ResourceFiles = { "Wood", "Ore", "Mercury", "Crystals", "Sulfur", "Gems", "Gold" };

    static Texture2D? ResourceIcon(int r) => Game.Instance.LoadTexture($"Textures/AdventureScreen/Resources/{ResourceFiles[Math.Clamp(r, 0, 6)]}.dds");

    static Texture2D? RootTexture(string id) => UiResources.Instance.Texture(UiGameRoot.Texture(id));

    public static void Open(AdventureScreen adv, Control layer, HeroState hero, int hut, bool offer, Action closed)
    {
        var game = adv.Session.Game;
        var o = adv.Session.Map.Objects[hut];
        var q = o.Quest!;
        // Centred over a modal blocker, like the stock message boxes (AdvDialogs).
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.35f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Stop };
        var box = new UiScreen("UI/SeerHut/Main.(WindowSimple).xdb");
        var size = new Vector2(box.Root.Rect.W, box.Root.Rect.H);
        box.AnchorRight = 0;
        box.AnchorBottom = 0;
        box.Size = size;
        dim.AddChild(box);
        layer.AddChild(dim);
        void Center() => box.Position = ((dim.Size - size) * 0.5f).Floor();
        dim.Resized += Center;
        Callable.From(Center).CallDeferred();
        box.Find("MainCaptionView")?.SetText($"<h1>{adv.ObjectTitle(hut)}");
        TownUi.SetPicture(box.Find("Hero"), TownUi.HeroFace(hero));
        // The description scrolls inside DescContainer (QUEST_FORMAT with the quest caption and description).
        var desc = box.Find("DescView");
        if (desc != null && box.Find("DescContainer") is { } area)
        {
            var markup = (UiGameRoot.RelatedText(Texts, "QUEST_FORMAT") ?? "")
                .Replace("<value=caption>", q.Objective.Caption ?? "").Replace("<value=desc>", q.Description ?? "");
            desc.Visible = false;
            area.ClipContents = true;
            var text = new BitmapText { Markup = markup, VerticalAlign = H5.Core.Ui.UiAlign.LowEnd, Size = new Vector2(desc.Size.X, 4000) };
            area.AddChild(text);
            float height = BitmapTextLayout.Build(markup, null, new Rect2(0, 0, desc.Size.X, 4000), H5.Core.Ui.UiAlign.LowEnd).Size.Y;
            area.MouseFilter = Control.MouseFilterEnum.Stop;
            area.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { Pressed: true } mb) return;
                float step = mb.ButtonIndex switch { MouseButton.WheelDown => -16, MouseButton.WheelUp => 16, _ => 0 };
                text.Position = new Vector2(0, Math.Clamp(text.Position.Y + step, Math.Min(0, area.Size.Y - height), 0));
            };
        }
        // The quest item (006ef730: a format per objective kind, else UNKNOWN_FORMAT with the riddle texture).
        var (kind, id, amount, name) = game.QuestRequirement(q);
        var db = Game.Instance.Db;
        string CreatureName(string c) => db.Creatures.TryGetValue(c, out var cd) ? cd.Name : c;
        (Texture2D? Icon, string Text) unknown = (RootTexture("ID_RIDDLE_CHOICE_TEXTURE"), Fmt("UNKNOWN_FORMAT", "", 0));
        (Texture2D? Icon, string Text) item = kind switch
        {
            "artifact" when id != null => (Hero.HeroScreen.ArtifactIcon(id), Fmt("ARTIFACT_FORMAT", Hero.HeroScreen.ArtifactName(id), 0)),
            "army" when id != null => (TownUi.CreatureIcon(id), Fmt("ASSEMBLEARMY_FORMAT", CreatureName(id), amount)),
            "resources" when int.TryParse(name, out int r) => (ResourceIcon(r), Fmt("RESOURCES_FORMAT", AdvTexts.ResourceName(r), amount)),
            "level" => (Game.Instance.LoadTexture("UI/SeerHut/AdvanceHeroesIcon.(Texture).xdb"), Fmt("ADVANCEHERO_FORMAT", "", amount)),
            "neutrals" => (unknown.Icon, Fmt("NEUTRALS_FORMAT", name ?? "", 0)),
            _ => unknown,
        };
        TownUi.SetPicture(box.Find("QuestItemPanel/Item"), item.Icon);
        box.Find("QuestItemPanel/ItemDescView")?.SetText(item.Text);
        // The reward (006efb10: EXP / RESOURCE / ARTIFACT / CREATURE / SKILL, anything else UNKNOWN_FORMAT).
        int res = H5.Adventure.Engine.Adventure.ShownRewardResource(q.Resources);
        (Texture2D? Icon, string Text) reward = q.AwardType switch
        {
            "AWARD_EXPERIENCE" => (RootTexture("CHEST_EXP_TEXTURE"), Fmt("EXP_FORMAT", "", q.Experience)),
            "AWARD_RESOURCE" when res >= 0 => (ResourceIcon(res), Fmt("RESOURCES_FORMAT", AdvTexts.ResourceName(res), q.Resources[res])),
            "AWARD_ARTIFACT" when q.Artifact != null => (Hero.HeroScreen.ArtifactIcon(q.Artifact), Fmt("ARTIFACT_FORMAT", Hero.HeroScreen.ArtifactName(q.Artifact), 0)),
            "AWARD_CREATURE" when q.Creature != null => (TownUi.CreatureIcon(q.Creature), Fmt("ARMY_FORMAT", CreatureName(q.Creature), q.CreatureCount)),
            "AWARD_SKILL" when q.Skill != null => (AdvDialogs.SkillIcon(q.Skill, 1), Fmt("SKILL_FORMAT", AdvDialogs.SkillName(q.Skill, 1), 0)),
            _ => unknown,
        };
        TownUi.SetPicture(box.Find("RewardPanel/Item"), reward.Icon);
        box.Find("RewardPanel/ItemDescView")?.SetText(reward.Text);
        int mode = offer ? 1 : game.SeerQuestMet(hero, hut) ? 3 : 2;
        box.SetVisible("CompleteQuestBtn", mode == 3);
        box.SetVisible("CancelBtn", mode == 3);
        box.SetVisible("AcceptBtn", mode != 3);
        void Close() { dim.QueueFree(); closed(); }
        box.Bus.On("seer_hut_accept", () =>
        {
            if (mode == 1)
            {
                var r = adv.Session.Apply(new AcceptSeerQuestCmd(hero.Id, hut) { Player = adv.Session.Human.Index });
                if (!r.Ok) GD.PushWarning($"[H5] seer hut accept: {r.Error}");
            }
            Close();
        });
        box.Bus.On("seer_hut_cancel", Close);
        box.Bus.On("timeout_forced_close", Close);
        box.Bus.On("seer_hut_complete_quest", () =>
        {
            var r = adv.Session.Apply(new CompleteSeerQuestCmd(hero.Id, hut) { Player = adv.Session.Human.Index });
            Close();
            if (!r.Ok) adv.Message(adv.ObjectTitle(hut), r.Error ?? "");
            adv.Hud.Refresh();
        });
    }
}
