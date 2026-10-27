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
            if (Ui.Find($"FortHill/creaturesholder/creature{i + 1}/button") is { } b) SetButton(b, s);
        }
    }

    /// <summary>
    /// The stack's button per H5_Game.exe 006d4950 / 006d40c0 / 006d41f0: the slot's status is the best of its
    /// upgrade options' statuses (0 or 1 over 2 over 3; the later option wins a tie), 4 (button hidden) when the
    /// slot is empty or has no option. Status 0 → enabled, visual 0; 1 → enabled, 3; 2 → enabled, 4 for an
    /// upgraded creature (creature def +0x100) else 1; 3 → disabled, 3 / 0. Tooltip: action_tooltip_&lt;visual&gt;
    /// (CAN_UPGRADE, NO_RESOURCES, ALREADY_UPGRADED, CAN_REGRADE, NO_RESOURCES), for status 3
    /// action_tooltip_no_regrades / _no_upgrades. The option statuses come from CHillFortDialogLogEntry (not
    /// decoded): here 0 / 1 (upgrade / regrade) when affordable, else 2; no map forbids upgrades (status 3).
    /// </summary>
    void SetButton(UiNode b, H5.Adventure.State.Stack? s)
    {
        var game = Adv.Session.Game;
        int status = 4;
        bool upgraded = false;
        if (s is { Count: > 0 })
        {
            upgraded = Game.Instance.Db.Creatures.TryGetValue(s.Creature, out var cd) && cd.Upgrade;
            int best = -1;
            for (int u = 1; u <= 2; u++)
            {
                var (target, cost) = game.UpgradeQuote(s.Creature, u, s.Count, true);
                if (target == null) continue;
                int st = Me.Purse.CanAfford(cost) ? (upgraded ? 1 : 0) : 2;
                int rank = st <= 1 ? 2 : st == 2 ? 1 : 0;
                if (rank >= best) { best = rank; status = st; }
            }
        }
        b.Visible = status != 4;
        if (status == 4) return;
        int visual = status switch { 0 => 0, 1 => 3, 2 => upgraded ? 4 : 1, _ => upgraded ? 3 : 0 };
        b.Enabled = status != 3;
        if (b.Window.ButtonStates.Count > 1) b.SetState(visual);
        string key = status == 3 ? (upgraded ? "action_tooltip_no_regrades" : "action_tooltip_no_upgrades") : $"action_tooltip_{visual}";
        if (Ui.Find("FortHill")?.Window.RelatedTexts.TryGetValue(key, out var path) == true) b.SetTooltip(UiResources.Instance.Text(path));
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

/// <summary>
/// The Sphinx screen (UI/Sphinx, exe 006ebfd0; pages by 006eb640 / 006ebca0): 1 PoseRiddle (box2p: Accept /
/// Refuse, Ok), 2 Riddle (question and three answers, Ok or double click), 3 ChooseReward (gold / artifact /
/// experience). Refusing closes; a wrong answer ends the visit (the exe's battle is not built); the reward
/// page's choice is sent with the answer (<see cref="AnswerSphinxCmd"/>).
/// </summary>
public sealed partial class SphinxScreen : MapBuildingScreen
{
    const string Related = "UI/Sphinx/relatedTexts.(WindowRelatedTexts).xdb";
    const string PoseTexts = "UI/Sphinx/PoseRiddle.(WindowRelatedTexts).xdb";
    const string RewardTexts = "UI/Sphinx/ChooseReward.(WindowRelatedTexts).xdb";
    readonly PendingRiddle _riddle;
    int _page = 1, _accept, _answer = -1, _reward = -1;

    public SphinxScreen(AdventureScreen adv, int heroId, int sphinx, PendingRiddle riddle) : base(adv, "UI/Sphinx.(WindowScreen).xdb", heroId, sphinx)
    {
        _riddle = riddle;
        Ui.Bus.On("left", () => { _accept = 1; Refresh(); });
        Ui.Bus.On("right", () => { _accept = 2; Refresh(); });
        Ui.Bus.On("left_force", () => { _accept = 1; Confirm(); });
        Ui.Bus.On("right_force", () => { _accept = 2; Confirm(); });
        Ui.Bus.On("confirm", Confirm);
        Ui.Bus.On("choice_click", (_, src) => Choose(src, false));
        Ui.Bus.On("choice_double_click", (_, src) => Choose(src, true));
        Fill();
        // Dev (`--adventure-sphinx-page=2|3`): open on the riddle / reward page for screenshots.
        if (DevPage is 2 or 3) { _accept = 1; _page = DevPage; if (DevPage == 3) _answer = _riddle.Correct; }
    }

    public static int DevPage;

    void Choose(UiNode? src, bool confirm)
    {
        if (src?.Name.ToString() is not { } n || !n.StartsWith("Choice_")) return;
        int i = n[^1] - '0';
        if (_page == 2) _answer = i; else if (_page == 3) _reward = i; else return;
        if (confirm) Confirm(); else Refresh();
    }

    void Confirm()
    {
        // 006e95c0: Ok moves to the next page once the page has a selection; 006eb640 acts on the new page.
        if (_page == 1 && _accept == 0 || _page == 2 && _answer < 0 || _page == 3 && _reward < 0) return;
        _page++;
        if (_page == 2 && _accept == 2) { Close(); return; }
        if (_page == 3 && _answer != _riddle.Correct || _page == 4)
        {
            Apply(new AnswerSphinxCmd(HeroId, Object, _answer, Math.Max(0, _reward)));
            Close();
            return;
        }
        Refresh();
    }

    static Texture2D? RootTexture(string id) => UiResources.Instance.Texture(UiGameRoot.Texture(id));

    static void Icon(UiNode? host, Texture2D? tex) => TownUi.SetPicture(host, tex);

    void Fill()
    {
        // Pose page (006e9880): Face = ID_SPHINX_ICON_TEXTURE, Title = ID_SPHINX_HEADER_TEXT (T_HEADER_TOOLTIP),
        // the left/right choices with the accept/refuse textures and texts.
        Icon(Ui.Find("PoseRiddle/Face"), RootTexture("ID_SPHINX_ICON_TEXTURE"));
        Ui.Find("PoseRiddle/Title")?.SetText(UiGameRoot.RelatedTextAt(Related, "ID_SPHINX_HEADER_TEXT"));
        Ui.Find("PoseRiddle/Title")?.SetTooltip(UiGameRoot.RelatedTextAt(PoseTexts, "T_HEADER_TOOLTIP"));
        Icon(Ui.Find("PoseRiddle/ButtonLeft/Icon"), RootTexture("ID_POSE_RIDDLE_ACCEPT_TEXTURE"));
        Icon(Ui.Find("PoseRiddle/ButtonRight/Icon"), RootTexture("ID_POSE_RIDDLE_REFUSE_TEXTURE"));
        Ui.Find("PoseRiddle/ButtonLeft/Text")?.SetText(UiGameRoot.RelatedTextAt(Related, "ID_POSE_RIDDLE_ACCEPT_TEXT"));
        Ui.Find("PoseRiddle/ButtonRight/Text")?.SetText(UiGameRoot.RelatedTextAt(Related, "ID_POSE_RIDDLE_REFUSE_TEXT"));
        Ui.Find("PoseRiddle/ButtonLeft")?.SetTooltip(UiGameRoot.RelatedTextAt(PoseTexts, "T_ACCEPT_TOOLTIP"));
        Ui.Find("PoseRiddle/ButtonRight")?.SetTooltip(UiGameRoot.RelatedTextAt(PoseTexts, "T_REFUSE_TOOLTIP"));
        // Riddle page (006ea100): Sphinx icon, the question, the answers with the riddle-choice texture.
        Icon(Ui.Find("Riddle/Face"), RootTexture("ID_SPHINX_ICON_TEXTURE"));
        Ui.Find("Riddle/Description")?.SetText((UiGameRoot.RelatedTextAt(Related, "ID_RIDDLE_QUESTION_TEXT") ?? "").Replace("<value=Question>", _riddle.Question));
        var choiceText = UiResources.Instance.Text("UI/Sphinx/ChooseFrom3/Choice/Text.txt") ?? "<value=Text>";
        for (int i = 0; i < 3; i++)
        {
            Ui.Find($"Riddle/Choice_{i}/Text")?.SetText(choiceText.Replace("<value=Text>", _riddle.Answers[i]));
            Icon(Ui.Find($"Riddle/Choice_{i}/Icon"), RootTexture("ID_RIDDLE_CHOICE_TEXTURE"));
        }
        // Reward page (006ea940): Choice_0 gold, Choice_1 artifact, Choice_2 experience.
        Icon(Ui.Find("ChooseReward/Face"), RootTexture("ID_SPHINX_ICON_TEXTURE"));
        Ui.Find("ChooseReward/Description")?.SetText(UiGameRoot.RelatedTextAt(Related, "ID_CHOOSE_REWARD_DESCRIPTION_TEXT"));
        Ui.Find("ChooseReward/Choice_0/Text")?.SetText((UiGameRoot.RelatedTextAt(Related, "ID_CHOOSE_REWARD_GOLD_TEXT") ?? "").Replace("<value=Amount>", _riddle.Gold.ToString()));
        Icon(Ui.Find("ChooseReward/Choice_0/Icon"), Game.Instance.LoadTexture("Textures/AdventureScreen/Resources/Gold.dds"));
        Ui.Find("ChooseReward/Choice_0")?.SetTooltip(UiGameRoot.RelatedTextAt(RewardTexts, "T_REWARD_TOOLTIP_GOLD"));
        if (_riddle.Artifact is { } art)
        {
            Ui.Find("ChooseReward/Choice_1/Text")?.SetText(choiceText.Replace("<value=Text>", H5Reborn.Hero.HeroScreen.ArtifactName(art)));
            Icon(Ui.Find("ChooseReward/Choice_1/Icon"), H5Reborn.Hero.HeroScreen.ArtifactIcon(art));
        }
        Ui.Find("ChooseReward/Choice_1")?.SetTooltip(UiGameRoot.RelatedTextAt(RewardTexts, "T_REWARD_TOOLTIP_ARTEFACT"));
        Ui.Find("ChooseReward/Choice_2/Text")?.SetText((UiGameRoot.RelatedTextAt(Related, "ID_CHOOSE_REWARD_EXPERIENCE_TEXT") ?? "").Replace("<value=Amount>", _riddle.Experience.ToString()));
        Icon(Ui.Find("ChooseReward/Choice_2/Icon"), RootTexture("CHEST_EXP_TEXTURE"));
        Ui.Find("ChooseReward/Choice_2")?.SetTooltip(UiGameRoot.RelatedTextAt(RewardTexts, "T_REWARD_TOOLTIP_EXP"));
    }

    public override void Refresh()
    {
        if (Ui == null) return;
        // 006ebca0: one page at a time.
        Ui.SetVisible("PoseRiddle", _page == 1);
        Ui.SetVisible("Riddle", _page == 2);
        Ui.SetVisible("ChooseReward", _page == 3);
        // 006eb6d0: the left/right buttons show the pose choice; Ok needs one.
        Ui.Find("PoseRiddle/ButtonLeft")?.SetState(_accept == 1 ? 1 : 0);
        Ui.Find("PoseRiddle/ButtonRight")?.SetState(_accept == 2 ? 1 : 0);
        if (Ui.Find("PoseRiddle/ButtonOk") is { } ok1) ok1.Enabled = _accept != 0;
        // 006eb8c0 / 006ebab0: the selected answer / reward is pressed; Ok needs a selection.
        for (int i = 0; i < 3; i++)
        {
            Ui.Find($"Riddle/Choice_{i}")?.SetState(i == _answer ? 1 : 0);
            Ui.Find($"ChooseReward/Choice_{i}")?.SetState(i == _reward ? 1 : 0);
        }
        if (Ui.Find("Riddle/ButtonOk") is { } ok2) ok2.Enabled = _answer is >= 0 and <= 2;
        if (Ui.Find("ChooseReward/ButtonOk") is { } ok3) ok3.Enabled = _reward is >= 0 and <= 2;
    }
}

/// <summary>
/// Garrison screen (UI/Garrison, CGarrisonDialogLogEntry → 005a5d90 case 0x1c → 006d3010): the visiting hero's army
/// (armyholder0) and the troops left in the own garrison / mine (armyholder1, "Here you can leave some of your creatures
/// in order to control the building"); a click on a stack and then on another slot moves, swaps or merges them
/// (SwapStacksCmd with the building as the second army); OK (confirm) closes.
/// </summary>
public sealed partial class GarrisonScreen : MapBuildingScreen
{
    (int Row, int Slot)? _picked;

    public GarrisonScreen(AdventureScreen adv, int heroId, int building) : base(adv, "UI/Garrison.(WindowScreen).xdb", heroId, building)
    {
        Ui.Bus.On("confirm", Close);
        for (int row = 0; row < 2; row++)
            for (int i = 0; i < H5.Adventure.State.Army.Slots; i++)
            {
                int r = row, slot = i;
                if (Ui.Find($"CreatureSwapMain/armyholder{row}/creature{i}") is { } b) b.Clicked += _ => Pick(r, slot);
            }
        Ui.Find("CreatureSwapMain/HeaderView")?.SetText("<h1><center>" + adv.ObjectTitle(building));
        Ui.Find("CreatureSwapMain/CreatureSwapView")?.SetValue("desc", TownUi.Text("UI/Garrison/ScreenDesc.txt"));
    }

    H5.Adventure.State.Stack?[] Row(int row) => row == 0 ? Hero.Army : Adv.Session.S.Obj(Object).Army;

    void Pick(int row, int slot)
    {
        if (_picked is not { } from)
        {
            if (Row(row)[slot] is { Count: > 0 }) _picked = (row, slot);
            Refresh();
            return;
        }
        _picked = null;
        if (from != (row, slot))
            Apply(new SwapStacksCmd(from.Row == 0 ? HeroId : -1, from.Row == 1 ? Object : -1, from.Slot,
                row == 0 ? HeroId : -1, row == 1 ? Object : -1, slot));
        else Refresh();
    }

    public override void Refresh()
    {
        base.Refresh();
        TownUi.SetPicture(Ui.Find("CreatureSwapMain/heroicon0"), TownUi.HeroFace(Hero, large: false));
        for (int row = 0; row < 2; row++)
        {
            var army = Row(row);
            for (int i = 0; i < H5.Adventure.State.Army.Slots; i++)
            {
                var s = army[i];
                var icon = Ui.Find($"CreatureSwapMain/armyholder{row}/creature{i}/icon");
                if (icon == null) continue;
                icon.Visible = s is { Count: > 0 };
                TownUi.SetPicture(icon, s is { Count: > 0 } ? TownUi.CreatureIcon(s.Creature) : null);
                (icon.FindChild("NumberOfCreatures", false, false) as UiNode)?.SetValue("NumberOfCreatures", s?.Count.ToString() ?? "");
                icon.Modulate = _picked == (row, i) ? new Color(1.4f, 1.4f, 1f) : Colors.White;
            }
        }
    }
}

/// <summary>
/// Arcane Library (UI/BuySpell, ID_BUY_SPELL_SCREEN, exe 007bb850): six SpellButton_%d with SpellPrice_%d
/// (ResourceIcon + ResourceText); spell_select (007ba820) chooses an entry, buy_spell (007bad30) sends
/// CBuySpellShopSpellCmd for it, clears the choice and plays BUY_SPELL_SOUND; the cost strip CreatureCountHolder
/// (006d7ec0) shows the chosen price against the purse.
/// </summary>
public sealed partial class SpellShopScreen : MapBuildingScreen
{
    static readonly string[] ResourceFiles = { "Wood", "Ore", "Mercury", "Crystals", "Sulfur", "Gems", "Gold" };
    int _choice = -1;

    public SpellShopScreen(AdventureScreen adv, int heroId, int shop) : base(adv, "UI/BuySpell.(WindowScreen).xdb", heroId, shop)
    {
        Ui.Bus.On("spell_select", (_, src) => { if (src?.Name.ToString() is { } n && n.StartsWith("SpellButton_")) { _choice = n[^1] - '0'; Refresh(); } });
        Ui.Bus.On("buy_spell", Buy);
    }

    string?[] Stock => Adv.Session.S.Obj(Object).ShopSpells ?? Array.Empty<string?>();

    void Buy()
    {
        if (_choice is < 0 or >= 6) return;
        // 004299b0 refuses silently (returns 0); the screen only resets the choice.
        var r = Adv.Session.Apply(new BuySpellShopSpellCmd(HeroId, Object, _choice) { Player = Me.Index });
        _choice = -1;
        if (r.Ok) Audio.AudioSystem.PlayNamed("BUY_SPELL_SOUND");
        Refresh();
        Adv.Hud.Refresh();
    }

    static Texture2D? SpellIcon(SpellDef spell)
    {
        var icon = (string?)Game.Instance.Xdb.Load(spell.SourcePath).Root?.Element("Texture")?.Attribute("href");
        return string.IsNullOrEmpty(icon) ? null : Game.Instance.LoadTexture(H5.Core.Xdb.XdbRef.Parse(icon, spell.SourcePath).Path);
    }

    public override void Refresh()
    {
        base.Refresh();
        var stock = Stock;
        for (int i = 0; i < 6; i++)
        {
            var spell = i < stock.Length && stock[i] is { } id ? Game.Instance.Db.Spell(id) : null;
            TownUi.SetPicture(Ui.Find($"SpellButton_{i}/Icon"), spell != null ? SpellIcon(spell) : null);
            if (Ui.Find($"SpellButton_{i}") is { } b)
            {
                b.Enabled = spell != null;
                b.SetTooltip(spell != null ? $"<h3>{spell.Name}<br><body>{spell.Description}" : null);
                b.SetState(i == _choice && b.Window.ButtonStates.Count > 1 ? 1 : 0);
            }
            var cost = H5.Adventure.Engine.Adventure.SpellShopCost(spell);
            int[] v = { cost.Wood, cost.Ore, cost.Mercury, cost.Crystal, cost.Sulfur, cost.Gem, cost.Gold };
            int res = Array.FindIndex(v, x => x > 0);
            Ui.SetVisible($"SpellPrice_{i}", res >= 0);
            if (res >= 0)
            {
                TownUi.SetPicture(Ui.Find($"SpellPrice_{i}/ResourceIcon"), Game.Instance.LoadTexture($"Textures/AdventureScreen/Resources/{ResourceFiles[res]}.dds"));
                Ui.Find($"SpellPrice_{i}/ResourceText")?.SetText(v[res].ToString());
            }
        }
        var chosen = _choice >= 0 && _choice < stock.Length && stock[_choice] is { } c ? Game.Instance.Db.Spell(c) : null;
        var price = H5.Adventure.Engine.Adventure.SpellShopCost(chosen);
        int[] pv = { price.Wood, price.Ore, price.Mercury, price.Crystal, price.Sulfur, price.Gem, price.Gold };
        TownUi.SetCostStrip(Ui.Find("CreatureCountHolder"), chosen != null ? pv : null, Me);
        if (Ui.Find("BuyButton") is { } buy) buy.Enabled = chosen != null;
    }
}
