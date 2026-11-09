using Godot;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Ui;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// The hire screen's caravan tabs (TotE; 006e23a0 builds them, 006e0b70 / 006e0f70 the tab bar):
///   • hire_from_castles / hire_from_dwellings: the sources of 00ce6110 split into towns and dwellings, one
///     caravan_hire_item_template row per source dwelling level (town: base / upgrade / alternative columns, as the
///     town's own rows; dwelling: one creature), definition caravan_hire_definition (count, +growth, Days: the source's
///     days, 006db6b0); the dial buys into a caravan to this town (CPurchaseCreaturesToCaravan 00428f20, HireAll =
///     CPurchaseAllCreaturesToCaravan 0042cb30 over the selected row's source);
///   • caravans_info: the owner's caravans heading here, caravan_info_item_template rows, definition
///     caravan_info_definition with days_text + days_travel or caravan_cant_move (days −1, 006db6b0).
/// Header T_CARAVANS_FROM_CASTLES / _FROM_DWELLINGS / _INFO (Header.(WindowRelatedTexts)); a tab with no rows is disabled
/// with hire_towns_disabled_status / hire_dwellings_disable_status / caravans_info_disable_status (006d9a60).
/// UNVERIFIED: the per-row split of a town source into its levels and the info row's creature columns (the first three
/// stacks of the caravan); 006db6b0's row descriptor fill is not fully decompiled.
/// </summary>
public sealed partial class TownHireScreen
{
    const string Hire = "UI/HireCreatures/";
    const string CaravanWindow = "hire_to_caravan_window";

    sealed record CaravanRow(int Source, bool IsTown, int Tier, string?[] Creatures, int Days);

    /// <summary>0 town rows, 1 creature info, 2 hire in castles, 3 hire in dwellings, 4 caravans statistics.</summary>
    int _tab;
    readonly List<CaravanRow> _castles = new(), _dwellings = new();
    readonly List<CaravanState> _caravans = new();
    readonly List<UiNode> _rowNodes = new();
    int _crow, _ccol;

    /// <summary>Dev only (TownLauncher `--town-caravans=`): the caravan tab opened with the screen.</summary>
    public static string? DevTab;

    bool InCaravanHire => _tab is 2 or 3;
    List<CaravanRow> CaravanRows => _tab == 2 ? _castles : _dwellings;

    void InitCaravanTabs()
    {
        foreach (var w in new[] { "tabs/hire_from_castles", "tabs/hire_from_dwellings", "tabs/caravans_info" }) Ui.SetVisible(w, true);
        Ui.SetVisible(CaravanWindow, false);
        foreach (var t in new[] { "caravan_hire_item_template", "caravan_info_item_template" }) Ui.SetVisible($"{CaravanWindow}/{t}", false);
        Ui.Bus.On("hire_from_castles", () => ShowCaravanTab(2));
        Ui.Bus.On("hire_from_dwellings", () => ShowCaravanTab(3));
        Ui.Bus.On("caravans_info", () => ShowCaravanTab(4));
        BuildCaravanLists();
        SetTabStatus("tabs/hire_from_castles", _castles.Count > 0, "hire_towns_disabled_status");
        SetTabStatus("tabs/hire_from_dwellings", _dwellings.Count > 0, "hire_dwellings_disable_status");
        SetTabStatus("tabs/caravans_info", _caravans.Count > 0, "caravans_info_disable_status");
    }

    static string HireText(string key) => UiGameRoot.RelatedTextAt(Hire + "Texts.(WindowRelatedTexts).xdb", key) ?? "";
    static string HeaderText(string key) => UiGameRoot.RelatedTextAt(Hire + "Header.(WindowRelatedTexts).xdb", key) ?? "";

    void SetTabStatus(string tab, bool enabled, string status)
    {
        if (Ui.Find(tab) is not { } b) return;
        b.Enabled = enabled;
        if (!enabled) b.SetTooltip(HireText(status));
    }

    /// <summary>00ce6110 → 006e08f0 / 006e0a10: the sources by kind, and the owner's caravans to this town.</summary>
    void BuildCaravanLists()
    {
        _castles.Clear(); _dwellings.Clear(); _caravans.Clear();
        var town = Owner.Town;
        foreach (var s in G.CaravanSources(town))
        {
            if (s.IsTown && G.S.Town(s.Object) is { } src)
            {
                for (int tier = 1; tier <= 7; tier++)
                {
                    var cs = new string?[3];
                    for (int v = 0; v < 3; v++) cs[v] = G.DwellingCreature(src, tier, v);
                    if (cs.Any(c => c != null)) _castles.Add(new CaravanRow(s.Object, true, tier, cs, s.Days));
                }
            }
            else if (!s.IsTown)
            {
                var list = G.Map.Objects[s.Object].Shared.DwellingCreatures;
                foreach (int row in G.DwellingRows(s.Object))
                    if (row < list.Count) _dwellings.Add(new CaravanRow(s.Object, false, row + 1, new string?[] { list[row], null, null }, s.Days));
            }
        }
        var at = G.EntranceOf(town);
        _caravans.AddRange(G.S.Caravans.Where(c => c.Owner == town.Owner && c.Dest == at));
    }

    void ShowCaravanTab(int tab)
    {
        _tab = tab;
        Ui.SetVisible("hire_window", false);
        Ui.SetVisible("creature_info", false);
        Ui.SetVisible(CaravanWindow, true);
        _crow = 0; _ccol = FirstColumn(0);
        FillCaravanRows();
        SetCount(MaxCount());
    }

    void HideCaravanWindow(int tab)
    {
        _tab = tab;
        Ui.SetVisible(CaravanWindow, false);
    }

    int FirstColumn(int row) => row < CaravanRows.Count ? Math.Max(0, Array.FindIndex(CaravanRows[row].Creatures, c => c != null)) : 0;

    int[]? CaravanPool(CaravanRow r) => r.IsTown ? G.S.Town(r.Source)?.Available : G.S.Objects.TryGetValue(r.Source, out var os) ? os.Available : null;
    int CaravanPoolCount(CaravanRow r) => CaravanPool(r) is { } p && r.Tier - 1 < p.Length ? p[r.Tier - 1] : 0;
    string? CaravanCreature() => InCaravanHire && _crow < CaravanRows.Count ? CaravanRows[_crow].Creatures[_ccol] : null;

    void FillCaravanRows()
    {
        foreach (var n in _rowNodes) Ui.RemoveInstance(n);
        _rowNodes.Clear();
        Ui.Find($"{CaravanWindow}/header")?.SetText(HeaderText(_tab switch { 2 => "T_CARAVANS_FROM_CASTLES", 3 => "T_CARAVANS_FROM_DWELLINGS", _ => "T_CARAVANS_INFO" }));
        if (Ui.Find($"{CaravanWindow}/elements_container_template") is not UiScrollableContainer box) return;
        box.ClearItems();
        if (Ui.Find($"{CaravanWindow}/{(_tab == 4 ? "caravan_info_item_template" : "caravan_hire_item_template")}") is not { } template) return;
        int count = _tab == 4 ? _caravans.Count : CaravanRows.Count;
        for (int i = 0; i < count; i++)
        {
            var node = Ui.Instantiate(template.Window, box.Window, new Int2(0, 0), $"caravan_row{i}");
            node.Window.Placement = template.Window.Placement;
            node.Visible = true;
            _rowNodes.Add(node);
            if (_tab == 4) FillInfoRow(node, _caravans[i]); else FillHireRow(node, i);
            box.AddItem(node);
        }
        UpdateCaravanSelection();
    }

    /// <summary>The selected row's creature_upgrade_selection frame (as on the town rows: shown on the selected row).</summary>
    void UpdateCaravanSelection()
    {
        for (int i = 0; i < _rowNodes.Count; i++)
            if (Child(_rowNodes[i], "creature_upgrade_selection") is { } sel) sel.Visible = InCaravanHire && i == _crow;
    }

    static UiNode? Child(UiNode row, string name) => row.FindChild(name, true, false) as UiNode;

    void FillHireRow(UiNode node, int i)
    {
        var r = CaravanRows[i];
        TownUi.SetPicture(Child(node, "dwelling"), SourceIcon(r));
        int pool = CaravanPoolCount(r);
        for (int c = 0; c < 3; c++)
        {
            var btn = Child(node, Columns[c]);
            var cr = r.Creatures[c];
            if (c > 0 && Child(node, c == 1 ? "creature_upg_back" : "creature_alt_upg_back") is { } back) back.Visible = cr != null;
            if (btn == null) continue;
            btn.Visible = cr != null;
            if (cr == null) continue;
            TownUi.SetPicture(btn, TownUi.CreatureIcon(cr));
            (btn.FindChild("text", false, false) as UiNode)?.SetValue("value", pool.ToString());
            int row = i, col = c;
            btn.Clicked += _ => { _crow = row; _ccol = col; UpdateCaravanSelection(); SetCount(MaxCount()); };
            btn.GuiInput += e =>
            {
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true })
                { _crow = row; _ccol = col; SetCount(MaxCount()); BuyToCaravan(_count); }
            };
        }
        var def = Child(node, "definition");
        def?.SetText(UiResources.Instance.Text(Item + "caravan_hire_definition.txt"));
        def?.SetValue("count", pool.ToString());
        def?.SetValue("growth", (r.IsTown && G.S.Town(r.Source) is { } st ? G.WeeklyGrowthOf(st, r.Tier)
            : r.Creatures[0] is { } c0 && Game.Instance.Db.Creatures.TryGetValue(c0, out var cd) ? cd.WeeklyGrowth : 0).ToString());
        def?.SetValue("days_travel", r.Days.ToString());
        node.SetTooltip(TownUi.Text(Hire + "CaravanDaysTravel.txt", "").Replace("<value=count>", r.Days.ToString()));
    }

    void FillInfoRow(UiNode node, CaravanState c)
    {
        TownUi.SetPicture(Child(node, "dwelling"), c.Source >= 0 ? SourceIcon(new CaravanRow(c.Source, G.S.Town(c.Source) != null, 1, Array.Empty<string?>(), 0)) : null);
        var stacks = c.Army.Live().ToList();
        for (int k = 0; k < 3; k++)
        {
            var btn = Child(node, Columns[k]);
            if (k > 0 && Child(node, k == 1 ? "creature_upg_back" : "creature_alt_upg_back") is { } back) back.Visible = k < stacks.Count;
            if (btn == null) continue;
            btn.Visible = k < stacks.Count;
            if (k >= stacks.Count) continue;
            TownUi.SetPicture(btn, TownUi.CreatureIcon(stacks[k].Creature));
            (btn.FindChild("text", false, false) as UiNode)?.SetValue("value", stacks[k].Count.ToString());
        }
        var def = Child(node, "definition");
        def?.SetText(UiResources.Instance.Text(Item + "caravan_info_definition.txt"));
        if (c.DaysLeft == -1) { def?.SetValue("text", HireText("caravan_cant_move")); def?.SetValue("days_travel", ""); }
        else { def?.SetValue("text", HireText("days_text")); def?.SetValue("days_travel", c.DaysLeft.ToString()); }
    }

    /// <summary>The source's picture: the town's dwelling icon of that level, the map dwelling's Icon128.</summary>
    Texture2D? SourceIcon(CaravanRow r)
    {
        if (r.IsTown && G.S.Town(r.Source) is { } t)
        {
            var b = $"TB_DWELLING_{r.Tier}";
            var def = Owner.Session.Db.Building(t.Type, b, Math.Max(1, t.Level(b)));
            return def != null && TownData.View(def).IconHref is { } href ? Game.Instance.LoadTexture(href) : null;
        }
        var shared = G.Map.Objects[r.Source].Shared;
        var ih = (string?)shared.Raw?.Element("Icon128")?.Attribute("href");
        return string.IsNullOrEmpty(ih) ? null : Game.Instance.LoadTexture(H5.Core.Xdb.XdbRef.Parse(ih, shared.Path).Path);
    }

    int CaravanMaxCount()
    {
        if (CaravanCreature() is not { } c || !Game.Instance.Db.Creatures.TryGetValue(c, out var def)) return 0;
        return Math.Max(0, Math.Min(CaravanPoolCount(CaravanRows[_crow]), H5.Adventure.Engine.Adventure.Affordable(Me.Purse, def.Cost)));
    }

    void BuyToCaravan(int n)
    {
        if (n <= 0 || CaravanCreature() is not { } c) return;
        if (Apply(new BuyToCaravanCmd(CaravanRows[_crow].Source, Owner.Town.Object, c, n))) AfterCaravanChange();
    }

    void BuyAllToCaravan()
    {
        if (!InCaravanHire || _crow >= CaravanRows.Count) return;
        if (Apply(new BuyAllToCaravanCmd(CaravanRows[_crow].Source, Owner.Town.Object))) AfterCaravanChange();
    }

    void AfterCaravanChange()
    {
        BuildCaravanLists();
        SetTabStatus("tabs/caravans_info", _caravans.Count > 0, "caravans_info_disable_status");
        if (_crow >= CaravanRows.Count) _crow = 0;
        FillCaravanRows();
        SetCount(MaxCount());
    }
}
