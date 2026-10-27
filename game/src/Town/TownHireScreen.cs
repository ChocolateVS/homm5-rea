using Godot;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5Reborn.Adventure;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// The town's creature hire screen. enter_fort (0061b470 → 0061af30) and a dwelling double-click open the same
/// HIRE_CREATURES screen as a map dwelling (006db170 → 006dd290 / 006e23a0, UI/HireCreatures), not the H5 1.0
/// UI/FortScreen: one hire_window row (CHireItem) per built dwelling with the base / upgraded / alternative-upgrade
/// columns (creature, creature_upgraded, creature_alt_upgraded), the jog dial UI/Common/JogDial/EditJogDial
/// (00782b70; button_0 Exit, 1 Minus, 2 Buy, 3 Plus, 4 HireAll — screen handler 006e3050).
///   • Clicking a column (CHireItem 006def90) selects it; the dial range becomes 0..min(pool, affordable) and the
///     count is set to that maximum (006dd9d0).
///   • Double-clicking (CHireItem 006d8a40, enabled for the town) presses Buy: CHireCreaturesCmd with the dial count.
///   • HireAll (006e3050 case 4): the plan of FUN_009a52a0 (006dcfe0) with each row's selected column, its cost in the
///     buy_all_header / buy_all_text_format box, then CHireAllCreaturesCmd (<see cref="HireAllCmd"/>).
/// Creatures go to the garrison hero's army, else the town garrison (0061af30, <see cref="H5.Adventure.Engine.Adventure.TownRecruitArmy"/>).
/// </summary>
public sealed partial class TownHireScreen : TownSubScreen
{
    const string Item = "UI/HireCreatures/HireWindow/Item/";
    const string Jog = "UI/Common/JogDial/TownJogDial/";
    static readonly string[] Columns = { "creature", "creature_upgraded", "creature_alt_upgraded" };
    readonly UiScreen _jog;
    readonly List<int> _tiers = new();
    /// <summary>Selected column per tier (CHireItem selected window; FUN_006d9000 reads 0 when none was clicked).</summary>
    readonly int[] _column = new int[7];
    int _row, _count;

    H5.Adventure.Engine.Adventure G => Owner.Session.Game;

    public TownHireScreen(TownScreen owner, int tier = 0) : base(owner, "UI/HireCreatures.(WindowScreen).xdb")
    {
        // Caravans (hire from castles / dwellings, caravan info) are not modelled: their tabs and window stay hidden.
        foreach (var w in new[] { "tabs/hire_from_castles", "tabs/hire_from_dwellings", "tabs/caravans_info", "hire_to_caravan_window", "creature_info", "CreaturepediaButton" })
            Ui.SetVisible(w, false);
        Ui.Bus.On("dwellings", () => { Ui.SetVisible("hire_window", true); Ui.SetVisible("creature_info", false); });
        Ui.Bus.On("creature", () => { Ui.SetVisible("hire_window", false); Ui.SetVisible("creature_info", true); });
        for (int t = 1; t <= 7; t++) if (G.DwellingCreature(owner.Town, t, 0) != null) _tiers.Add(t);
        for (int i = 0; i < 7; i++)
            for (int c = 0; c < 3; c++)
            {
                int row = i, col = c;
                if (Ui.Find($"hire_window/item_{i}/{Columns[c]}") is not { } b) continue;
                b.Clicked += _ => Select(row, col);
                b.GuiInput += e =>
                {
                    if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true })
                    {
                        Select(row, col);
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
        // input_a2.cfg hire_screen: jd_button_0 R, jd_button_2 Space, jd_button_4 A; add_creatures / remove_creatures
        // (num +/−, 006d8ae0 / 006d8b40: dial value ± 1).
        Ui.Bus.On("jd_button_0", Close);
        Ui.Bus.On("jd_button_2", () => Recruit(_count));
        Ui.Bus.On("jd_button_4", AskHireAll);
        if (_jog.Find("jd_editbox") is UiEditLine edit) edit.Changed += v => { if (int.TryParse(v, out var n)) SetCount(n, updateEdit: false); };
        _row = Math.Max(0, _tiers.IndexOf(tier));
    }

    public override void _Ready()
    {
        base._Ready();
        Select(_row, _column[TierOf(_row) - 1]);
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

    int TierOf(int row) => row < _tiers.Count ? _tiers[row] : 1;
    string? CreatureAt(int row, int col) => row < _tiers.Count ? G.DwellingCreature(Owner.Town, _tiers[row], col) : null;
    int Pool(int row) => row < _tiers.Count ? Owner.Town.Available[_tiers[row] - 1] : 0;

    void Select(int row, int col)
    {
        if (CreatureAt(row, col) == null) return;
        _row = row;
        _column[TierOf(row) - 1] = col;
        SetCount(MaxCount());
    }

    void SetJogButton(string name, string preset, string tooltip)
    {
        if (_jog.Find(name) is not { } btn) return;
        var path = $"UI/Common/JogDial/Buttons/{preset}.(JogDialButton).xdb";
        if (Game.Instance.Fs.Exists(path)) UiDrawableHelper.Apply(btn, Game.Instance.Xdb.Load(path).Root!, path);
        btn.TooltipText = UiResources.StripMarkup(TownUi.Text(tooltip, preset));
    }

    /// <summary>006dd9d0: min(pool, purse / cost).</summary>
    int MaxCount()
    {
        if (CreatureAt(_row, _column[TierOf(_row) - 1]) is not { } c || !Game.Instance.Db.Creatures.TryGetValue(c, out var def)) return 0;
        return Math.Max(0, Math.Min(Pool(_row), H5.Adventure.Engine.Adventure.Affordable(Me.Purse, def.Cost)));
    }

    void SetCount(int n, bool updateEdit = true)
    {
        _count = Math.Clamp(n, 0, MaxCount());
        if (updateEdit && _jog.Find("jd_editbox") is UiEditLine edit) edit.Value = _count.ToString();
        Refresh();
    }

    void Recruit(int n)
    {
        if (n <= 0 || _row >= _tiers.Count) return;
        if (Apply(new RecruitCmd(Owner.Town.Object, TierOf(_row), n, _column[TierOf(_row) - 1]))) SetCount(MaxCount());
    }

    int[] Selection() => (int[])_column.Clone();

    void AskHireAll()
    {
        var plan = G.HireAllPlan(Owner.Town, Me.Purse, G.TownRecruitArmy(Owner.Town), Selection());
        if (plan.Count == 0) return;
        var total = new Purse();
        foreach (var (_, _, creature, n) in plan) total.Add(Game.Instance.Db.Creatures[creature].Cost, n);
        var cost = new H5.Core.Data.Resources(total[0], total[1], total[2], total[3], total[4], total[5], total[6]);
        var text = TownUi.Text("UI/HireCreatures/buy_all_text_format.txt", "") + " " + MapBuildingScreen.CostText(cost);
        AdvDialogs.YesNo(this, TownUi.Text("UI/HireCreatures/buy_all_header.txt", ""), text, null, yes =>
        {
            if (yes && Apply(new HireAllCmd(Owner.Town.Object, Selection()))) SetCount(MaxCount());
        });
    }

    public override void Refresh()
    {
        base.Refresh();
        var db = Game.Instance.Db;
        Ui.Find("name_header")?.SetValue("name", Owner.Town.Name);
        for (int i = 0; i < 7; i++)
        {
            var item = $"hire_window/item_{i}";
            bool used = i < _tiers.Count;
            Ui.SetVisible(item, used);
            if (!used) continue;
            int tier = _tiers[i];
            TownUi.SetPicture(Ui.Find($"{item}/dwelling"), DwellingIcon(tier));
            for (int c = 0; c < 3; c++)
            {
                var cr = CreatureAt(i, c);
                var btn = Ui.Find($"{item}/{Columns[c]}");
                if (c > 0) Ui.SetVisible($"{item}/{(c == 1 ? "creature_upg_back" : "creature_alt_upg_back")}", cr != null);
                if (btn == null) continue;
                btn.Visible = cr != null;
                if (cr == null) continue;
                TownUi.SetPicture(btn, TownUi.CreatureIcon(cr));
                (btn.FindChild("text", false, false) as UiNode)?.SetValue("value", Pool(i).ToString());
            }
            Ui.SetVisible($"{item}/creature_upgrade_selection", i == _row);
            var def = Ui.Find($"{item}/definition");
            def?.SetText(UiResources.Instance.Text(Item + "definition.txt"));
            def?.SetValue("count", Pool(i).ToString());
            def?.SetValue("growth", G.WeeklyGrowthOf(Owner.Town, tier).ToString());
        }
        var sel = CreatureAt(_row, _column[TierOf(_row) - 1]);
        if (sel != null && db.Creatures.TryGetValue(sel, out var cd))
        {
            TownUi.SetPicture(Ui.Find("creature_info/icon"), TownUi.CreatureIcon(sel, large: true));
            if (Ui.Find("creature_info/icon_definition") is { } idef) { idef.SetValue("name", cd.Name); idef.SetValue("tier", cd.Tier.ToString()); }
            Ui.Find("creature_info/quantity")?.SetValue("value", Pool(_row).ToString());
            int[] unit = { cd.Cost.Wood, cd.Cost.Ore, cd.Cost.Mercury, cd.Cost.Crystal, cd.Cost.Sulfur, cd.Cost.Gem, cd.Cost.Gold };
            TownUi.SetCostStrip(Ui.Find("CreatureCountHolder"), unit.Select(u => u * Math.Max(1, _count)).ToArray(), Me);
        }
        int max = MaxCount();
        if (_jog.Find("button_2") is { } buy) buy.Enabled = _count > 0;
        if (_jog.Find("button_4") is { } all) all.Enabled = G.HireAllPlan(Owner.Town, Me.Purse, G.TownRecruitArmy(Owner.Town), Selection()).Count > 0;
        if (_jog.Find("button_1") is { } minus) minus.Enabled = _count > 0;
        if (_jog.Find("button_3") is { } plus) plus.Enabled = _count < max;
    }

    /// <summary>The dwelling's building icon (TownBuildingSharedStats Icon of its built level).</summary>
    Texture2D? DwellingIcon(int tier)
    {
        var b = $"TB_DWELLING_{tier}";
        var def = Owner.Session.Db.Building(Owner.Town.Type, b, Math.Max(1, Owner.Town.Level(b)));
        return def != null && TownData.View(def).IconHref is { } href ? Game.Instance.LoadTexture(href) : null;
    }
}
