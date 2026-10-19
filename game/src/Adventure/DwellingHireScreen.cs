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
            if (Ui.Find($"hire_window/item_{i}/creature") is { } b) b.Clicked += _ => { _row = row; SetCount(Math.Min(1, Available(row))); };
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
        if (_jog.Find("jd_editbox") is UiEditLine edit) edit.Changed += v => { if (int.TryParse(v, out var n)) SetCount(n, updateEdit: false); };
    }

    MapObject Dwelling => Adv.Session.Map.Objects[Object];
    List<string> Creatures => Dwelling.Shared.DwellingCreatures;
    int Available(int row) => Adv.Session.S.Objects.TryGetValue(Object, out var os) && row < os.Available.Length ? os.Available[row] : 0;

    public override void _Ready()
    {
        _row = Enumerable.Range(0, Creatures.Count).FirstOrDefault(r => Available(r) > 0);
        base._Ready();
        SetCount(Math.Min(1, Available(_row)));
    }

    void SetJogButton(string name, string preset, string tooltip)
    {
        if (_jog.Find(name) is not { } btn) return;
        var path = $"UI/Common/JogDial/Buttons/{preset}.(JogDialButton).xdb";
        if (Game.Instance.Fs.Exists(path)) UiDrawableHelper.Apply(btn, Game.Instance.Xdb.Load(path).Root!, path);
        btn.TooltipText = UiResources.StripMarkup(TownUi.Text(tooltip, preset));
    }

    int MaxAffordable()
    {
        if (_row >= Creatures.Count || !Game.Instance.Db.Creatures.TryGetValue(Creatures[_row], out var def)) return 0;
        int[] cost = { def.Cost.Wood, def.Cost.Ore, def.Cost.Mercury, def.Cost.Crystal, def.Cost.Sulfur, def.Cost.Gem, def.Cost.Gold };
        int n = Available(_row);
        for (int i = 0; i < 7; i++) if (cost[i] > 0) n = Math.Min(n, Me.Purse[i] / cost[i]);
        return Math.Max(0, n);
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
        if (Apply(new RecruitCmd(Object, _row + 1, n))) SetCount(Math.Min(1, Available(_row)));
    }

    void AskHireAll()
    {
        int n = MaxAffordable();
        if (n <= 0 || !Game.Instance.Db.Creatures.TryGetValue(Creatures[_row], out var def)) return;
        var text = TownUi.Text("UI/HireCreatures/buy_all_text_format.txt", "") + " " + CostText(H5.Adventure.State.Purse.Scale(def.Cost, n));
        AdvDialogs.YesNo(this, TownUi.Text("UI/HireCreatures/buy_all_header.txt", ""), text, null, yes => { if (yes) Recruit(n); });
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
        if (_jog.Find("button_4") is { } all) all.Enabled = MaxAffordable() > 0;
        if (_jog.Find("button_1") is { } minus) minus.Enabled = _count > 0;
        if (_jog.Find("button_3") is { } plus) plus.Enabled = _count < MaxAffordable();
    }

    Texture2D? DwellingIcon()
    {
        var href = (string?)Dwelling.Shared.Raw?.Element("Icon128")?.Attribute("href");
        return string.IsNullOrEmpty(href) ? null : Game.Instance.LoadTexture(H5.Core.Xdb.XdbRef.Parse(href, Dwelling.Shared.Path).Path);
    }
}
