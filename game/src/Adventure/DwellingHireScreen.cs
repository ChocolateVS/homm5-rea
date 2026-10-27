using Godot;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5Reborn.Town;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// Map dwelling visit (exe 005a5d90 case 3, CHeroVisitDwellingDialogLogEntry → deferred 006db170 → 006e7e50 → 006dd290 /
/// 006e23a0): the HIRE_CREATURES screen (UI/HireCreatures) over the terrain's dwelling flyby scene. One hire_window row
/// (item_N, filled by 006ddb60) per creature of the dwelling: dwelling icon, the creature button with the available count,
/// definition "count / +growth" (UI/HireCreatures/HireWindow/Item/definition.txt); name_header = the dwelling's name; the
/// creature tab shows creature_info. The jog dial (00782b70 → 007826c0 → 007824a0) is UI/Common/JogDial/EditJogDial with
/// jd_editbox: button_0 Exit, button_1 Minus, button_2 Buy, button_3 Plus, button_4 HireAll, tooltips from
/// UI/Common/JogDial/TownJogDial (Plus/Minus/Buy/BuyAllCreatures). Recruits go into the visiting hero (RecruitCmd on the dwelling).
/// The rows are the same CHireItem as the town's hire screen (<see cref="H5Reborn.Town.TownHireScreen"/>):
///   • clicking a creature (006def90 → 006dd9d0) selects the row and sets the count to min(pool, affordable);
///   • double-clicking (006d8a40, row flag +0x3c from 006ddb60) presses Buy with that count;
///   • HireAll (006e3050 case 4 → 006da5c0 → CHireAllCreaturesCmd 0042b870 → FUN_009a52a0) hires every row, last row
///     first, with the free-slot rule (<see cref="H5.Adventure.Engine.Adventure.DwellingHireAllPlan"/>), after the
///     buy_all_header / buy_all_text_format box with the total cost.
/// </summary>
public sealed partial class DwellingHireScreen : MapBuildingScreen
{
    const string Item = "UI/HireCreatures/HireWindow/Item/";
    const string Jog = "UI/Common/JogDial/TownJogDial/";
    readonly UiScreen _jog;
    int _row, _count;

    public DwellingHireScreen(AdventureScreen adv, int heroId, int dwelling) : base(adv, "UI/HireCreatures.(WindowScreen).xdb", heroId, dwelling)
    {
        // Caravans (TotE hire from castles / dwellings, caravan info) are not modelled: their tabs and window stay hidden.
        foreach (var w in new[] { "tabs/hire_from_castles", "tabs/hire_from_dwellings", "tabs/caravans_info", "hire_to_caravan_window", "creature_info", "CreaturepediaButton" })
            Ui.SetVisible(w, false);
        Ui.Bus.On("dwellings", () => { Ui.SetVisible("hire_window", true); Ui.SetVisible("creature_info", false); });
        Ui.Bus.On("creature", () => { Ui.SetVisible("hire_window", false); Ui.SetVisible("creature_info", true); });
        for (int i = 0; i < 7; i++)
        {
            int row = i;
            if (Ui.Find($"hire_window/item_{i}/creature") is not { } b) continue;
            b.Clicked += _ => Select(row);
            b.GuiInput += e =>
            {
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true })
                {
                    Select(row);
                    Recruit(_count);
                }
            };
        }

        _jog = new UiScreen("UI/Common/JogDial/EditJogDial.(WindowSimple).xdb", Ui.Bus);
        AddChild(_jog);
        SetJogButton("button_0", "Exit", "UI/Town/TTExit.txt");
        SetJogButton("button_1", "Minus", Jog + "MinusCreatures.txt");
        SetJogButton("button_2", "Buy", Jog + "BuyCreatures.txt");
        SetJogButton("button_3", "Plus", Jog + "PlusCreatures.txt");
        SetJogButton("button_4", "HireAll", Jog + "BuyAllCreatures.txt");
        Ui.Bus.On("jd_button_0_reaction", Close);
        Ui.Bus.On("jd_button_1_reaction", () => SetCount(_count - 1));
        Ui.Bus.On("jd_button_2_reaction", () => Recruit(_count));
        Ui.Bus.On("jd_button_3_reaction", () => SetCount(_count + 1));
        Ui.Bus.On("jd_button_4_reaction", AskHireAll);
        // input_a2.cfg hire_screen: jd_button_0 R, jd_button_2 Space, jd_button_4 A; add_creatures / remove_creatures ± 1.
        Ui.Bus.On("jd_button_0", Close);
        Ui.Bus.On("jd_button_2", () => Recruit(_count));
        Ui.Bus.On("jd_button_4", AskHireAll);
        if (_jog.Find("jd_editbox") is UiEditLine edit) edit.Changed += v => { if (int.TryParse(v, out var n)) SetCount(n, updateEdit: false); };
    }

    MapObject Dwelling => Adv.Session.Map.Objects[Object];
    List<string> Creatures => Dwelling.Shared.DwellingCreatures;
    int Available(int row) => Adv.Session.S.Objects.TryGetValue(Object, out var os) && row < os.Available.Length ? os.Available[row] : 0;

    public override void _Ready()
    {
        _row = Enumerable.Range(0, Creatures.Count).FirstOrDefault(r => Available(r) > 0);
        base._Ready();
        Select(_row);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } k)
            foreach (var a in H5Reborn.Interaction.KeyBindings.Actions("hire_screen", k))
                if (a is "add_creatures" or "remove_creatures")
                {
                    SetCount(_count + (a == "add_creatures" ? 1 : -1));
                    GetViewport().SetInputAsHandled();
                    return;
                }
        base._UnhandledInput(e);
    }

    void Select(int row)
    {
        if (row >= Creatures.Count) return;
        _row = row;
        SetCount(MaxAffordable());
    }

    void SetJogButton(string name, string preset, string tooltip)
    {
        if (_jog.Find(name) is not { } btn) return;
        var path = $"UI/Common/JogDial/Buttons/{preset}.(JogDialButton).xdb";
        if (Game.Instance.Fs.Exists(path)) UiDrawableHelper.Apply(btn, Game.Instance.Xdb.Load(path).Root!, path);
        btn.TooltipText = UiResources.StripMarkup(TownUi.Text(tooltip, preset));
    }

    /// <summary>006dd9d0: min(pool, purse / cost).</summary>
    int MaxAffordable()
    {
        if (_row >= Creatures.Count || !Game.Instance.Db.Creatures.TryGetValue(Creatures[_row], out var def)) return 0;
        return Math.Max(0, Math.Min(Available(_row), H5.Adventure.Engine.Adventure.Affordable(Me.Purse, def.Cost)));
    }

    void SetCount(int n, bool updateEdit = true)
    {
        _count = Math.Clamp(n, 0, MaxAffordable());
        if (updateEdit && _jog.Find("jd_editbox") is UiEditLine edit) edit.Value = _count.ToString();
        Refresh();
    }

    void Recruit(int n)
    {
        if (n <= 0 || _row >= Creatures.Count) return;
        if (Apply(new RecruitCmd(Object, _row + 1, n))) SetCount(MaxAffordable());
    }

    List<(int Row, string Creature, int Count)> HireAllPlan() =>
        Adv.Session.Game.DwellingVisitor(Object, Me.Index) is { } h ? Adv.Session.Game.DwellingHireAllPlan(Object, Me.Purse, h.Army) : new();

    void AskHireAll()
    {
        var plan = HireAllPlan();
        if (plan.Count == 0) return;
        var total = new H5.Adventure.State.Purse();
        foreach (var (_, creature, n) in plan) total.Add(Game.Instance.Db.Creatures[creature].Cost, n);
        var cost = new H5.Core.Data.Resources(total[0], total[1], total[2], total[3], total[4], total[5], total[6]);
        var text = TownUi.Text("UI/HireCreatures/buy_all_text_format.txt", "") + " " + CostText(cost);
        AdvDialogs.YesNo(this, TownUi.Text("UI/HireCreatures/buy_all_header.txt", ""), text, null, yes =>
        {
            if (yes && Apply(new HireAllCmd(Object))) SetCount(MaxAffordable());
        });
    }

    public override void Refresh()
    {
        base.Refresh();
        var db = Game.Instance.Db;
        Ui.Find("name_header")?.SetValue("name", Adv.ObjectTitle(Object));
        var icon = DwellingIcon();
        for (int i = 0; i < 7; i++)
        {
            var item = $"hire_window/item_{i}";
            bool used = i < Creatures.Count && db.Creatures.ContainsKey(Creatures[i]);
            Ui.SetVisible(item, used);
            if (!used) continue;
            // Map dwellings hold one creature per row: no upgrade columns.
            foreach (var w in new[] { "creature_upgraded", "creature_alt_upgraded", "creature_upg_back", "creature_alt_upg_back" })
                Ui.SetVisible($"{item}/{w}", false);
            Ui.SetVisible($"{item}/creature_upgrade_selection", i == _row);
            TownUi.SetPicture(Ui.Find($"{item}/dwelling"), icon);
            var cr = Ui.Find($"{item}/creature");
            TownUi.SetPicture(cr, TownUi.CreatureIcon(Creatures[i]));
            (cr?.FindChild("text", false, false) as UiNode)?.SetValue("value", Available(i).ToString());
            var def = Ui.Find($"{item}/definition");
            def?.SetText(UiResources.Instance.Text(Item + "definition.txt"));
            def?.SetValue("count", Available(i).ToString());
            def?.SetValue("growth", db.Creatures[Creatures[i]].WeeklyGrowth.ToString());
        }
        if (_row < Creatures.Count && db.Creatures.TryGetValue(Creatures[_row], out var cd))
        {
            TownUi.SetPicture(Ui.Find("creature_info/icon"), TownUi.CreatureIcon(Creatures[_row], large: true));
            if (Ui.Find("creature_info/icon_definition") is { } idef) { idef.SetValue("name", cd.Name); idef.SetValue("tier", cd.Tier.ToString()); }
            Ui.Find("creature_info/quantity")?.SetValue("value", Available(_row).ToString());
            // Cost of the selected count (CreatureCountHolder: wood … gold, red when not affordable).
            int[] unit = { cd.Cost.Wood, cd.Cost.Ore, cd.Cost.Mercury, cd.Cost.Crystal, cd.Cost.Sulfur, cd.Cost.Gem, cd.Cost.Gold };
            TownUi.SetCostStrip(Ui.Find("CreatureCountHolder"), unit.Select(u => u * Math.Max(1, _count)).ToArray(), Me);
        }
        if (_jog.Find("button_2") is { } buy) buy.Enabled = _count > 0;
        if (_jog.Find("button_4") is { } all) all.Enabled = HireAllPlan().Count > 0;
        if (_jog.Find("button_1") is { } minus) minus.Enabled = _count > 0;
        if (_jog.Find("button_3") is { } plus) plus.Enabled = _count < MaxAffordable();
    }

    Texture2D? DwellingIcon()
    {
        var href = (string?)Dwelling.Shared.Raw?.Element("Icon128")?.Attribute("href");
        return string.IsNullOrEmpty(href) ? null : Game.Instance.LoadTexture(H5.Core.Xdb.XdbRef.Parse(href, Dwelling.Shared.Path).Path);
    }
}
