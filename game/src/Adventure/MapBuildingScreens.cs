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
        var s = Hero.Army[slot];
        if (s is not { Count: > 0 }) return;
        var game = Adv.Session.Game;
        _choose = new UiScreen("UI/UpgradeCreatures/AlternativeUpgrade/ChooseUpgradeItem.(WindowSimple).xdb");
        AddChild(_choose);
        // The item pops up over the stack it upgrades (authored at 0,0).
        if (Ui.Find($"FortHill/creaturesholder/creature{slot + 1}") is { } cell && _choose.NodeOf(_choose.Root) is { } item)
            item.GlobalPosition = cell.GlobalPosition + new Vector2((cell.Size.X - item.Size.X) / 2, -item.Size.Y);
        string[] icons = { "norm_upg_icon", "alt_upg_icon" }, buttons = { "upg_button", "alt_upg_button" };
        for (int u = 1; u <= 2; u++)
        {
            var (target, cost) = game.UpgradeQuote(s.Creature, u, s.Count, hillFort: true);
            TownUi.SetPicture(_choose.Find(icons[u - 1]), target != null ? TownUi.CreatureIcon(target) : null);
            if (_choose.Find(buttons[u - 1]) is not { } b) continue;
            b.Enabled = target != null && Me.Purse.CanAfford(cost);
            if (target == null) continue;
            string name = Game.Instance.Db.Creatures.TryGetValue(target, out var td) ? td.Name : target;
            b.SetTooltip($"{name}<br>{CostText(cost)}");
            int upgrade = u;
            b.Clicked += _ =>
            {
                _choose?.QueueFree();
                _choose = null;
                var text = TownUi.Text("UI/UpgradeCreatures/AlternativeUpgradeBox/TDescription.txt", "Choose the upgrade for this creature.<br><br>Upgrade cost:");
                AdvDialogs.YesNo(this, name, $"{text} {CostText(cost)}", TownUi.CreatureIcon(target), yes =>
                {
                    if (yes && Apply(new UpgradeCreatureCmd(HeroId, -1, slot, upgrade, Object)))
                        Audio.AudioSystem.PlayNamed("FORTHILL_UPGRADE_CREATURES_SOUND");
                });
            };
        }
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
