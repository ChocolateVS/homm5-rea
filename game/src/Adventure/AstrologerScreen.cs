using Godot;
using H5.Adventure.Engine;
using H5Reborn.Town;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// Tower of Astrologer (UI/TowerOfAstrologer, ID_TOWER_OF_ASTROLOGER_SCREEN; CTowerOfAstrologerScreen, setup 007b88c0).
/// Fill 007b81a0: PresentWeekIcon / NextWeekIcon / FutureWeekIcon show the current week and the next two of the
/// calendar (CWorld vfunc 0xb0 + 0xb8(2)) at alpha 1.0 / 0.4 / 0.25; RandomWeekIcon1..3 the candidates (vfunc 0xbc),
/// hidden past the list or when the status is 2. An icon is the creature icon of a creature week (007b7ff0) over
/// WeekIcons creature_week / magic_week / empty_week_100_alfa (007b7d70: creature week, else 00bce040 neutral → empty),
/// tooltip ASTROLOGER_ICON_TOOLTIP_TEMPLATE (WeekName, WeekDesc). Refresh 007b7970 sets the ChangeButton tooltip and
/// state from the statuses (no candidates → _NO_ELEMENTS; 1 _ON_SUNDAY, 2 _TWICE_ON_WEEK, 3 disabled, 0 → purse
/// check 007b7790, _NO_MONEY) and puts the Selection frame on the first candidate; clicking a candidate moves it there
/// (007b7810). change_week (007b8680) shows WARNING_CAPTION / WARNING_TEXT and sends CChangeMoonWeekCmd when the
/// chosen week's status is 0.
/// </summary>
public sealed partial class AstrologerScreen : MapBuildingScreen
{
    const string Dir = "UI/TowerOfAstrologer/";
    const string Texts = Dir + "Texts.(WindowRelatedTexts).xdb";
    static readonly string[] Random = { "RandomWeekIcon1", "RandomWeekIcon2", "RandomWeekIcon3" };
    static readonly string[] Calendar = { "PresentWeekIcon", "NextWeekIcon", "FutureWeekIcon" };
    static readonly float[] CalendarAlpha = { 1f, 0.4f, 0.25f };
    int _choice;
    bool _closing;

    public AstrologerScreen(AdventureScreen adv, int heroId, int tower) : base(adv, "UI/TowerOfAstrologer.(WindowScreen).xdb", heroId, tower)
    {
        Ui.Bus.On("change_week", Change);
        for (int i = 0; i < Random.Length; i++)
        {
            int idx = i;
            if (Ui.Find(Random[i]) is not { } n) continue;
            n.MouseFilter = MouseFilterEnum.Stop;
            n.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && !_closing) { _choice = idx; Refresh(); }
            };
        }
    }

    H5.Adventure.Engine.Adventure G => Adv.Session.Game;

    static string T(string key) => UiGameRoot.RelatedTextAt(Texts, key) ?? "";

    static Texture2D? WeekClassTexture(int week)
    {
        string name = MoonCalendar.CreatureOf(week) != 0 ? "CreatureWeek100Alfa" : MoonCalendar.IsNeutral(week) ? "EmptyWeek100Alfa" : "MagicWeek100Alfa";
        return Game.Instance.LoadTexture($"{Dir}Icons/{name}.(Texture).xdb");
    }

    Texture2D? CreatureTexture(int week)
    {
        int c = MoonCalendar.CreatureOf(week);
        return c != 0 && G.Db.EnumName("CreatureType", c) is { } id ? TownUi.CreatureIcon(id) : null;
    }

    string WeekTooltip(int week)
    {
        string id = G.Db.MoonWeekId(week);
        return T("ASTROLOGER_ICON_TOOLTIP_TEMPLATE").Replace("<value=WeekName>", AdvTexts.WeekName(id)).Replace("<value=WeekDesc>", AdvTexts.WeekDescription(id));
    }

    void SetIcon(UiNode n, int week, float alpha)
    {
        var bg = n.GetNodeOrNull<TextureRect>("_bg") ?? Add(n, "_bg");
        var fg = n.GetNodeOrNull<TextureRect>("_fg") ?? Add(n, "_fg");
        var cls = WeekClassTexture(week);
        bg.Texture = CreatureTexture(week) ?? cls;
        fg.Texture = cls;
        bg.Modulate = fg.Modulate = new Color(1, 1, 1, alpha);
    }

    static TextureRect Add(UiNode n, string name)
    {
        var r = new TextureRect
        {
            Name = name, AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
        };
        n.AddChild(r);
        return r;
    }

    public override void Refresh()
    {
        base.Refresh();
        var cands = G.AstrologerCandidates;
        int me = Me.Index;
        // 007b81a0: the calendar row.
        var row = new List<int> { G.CurrentMoonWeekIndex };
        row.AddRange(G.MoonForecast(2));
        for (int i = 0; i < Calendar.Length; i++)
            if (Ui.Find(Calendar[i]) is { } n && i < row.Count)
            {
                SetIcon(n, row[i], CalendarAlpha[i]);
                n.SetTooltip(WeekTooltip(row[i]));
            }
        for (int i = 0; i < Random.Length; i++)
        {
            if (Ui.Find(Random[i]) is not { } n) continue;
            bool show = i < cands.Count && G.AstrologerStatus(cands[i], me) != 2;
            n.Visible = show;
            if (!show) continue;
            SetIcon(n, cands[i], 1f);
            n.SetTooltip(WeekTooltip(cands[i]));
        }
        // 007b7970: button tooltip/state and the selection frame (the last candidate's status decides).
        var change = Ui.Find("ChangeButton");
        var sel = Ui.Find("Selection");
        bool selVisible;
        if (cands.Count == 0)
        {
            change?.SetTooltip(T("ASTROLOGER_CANT_CHANGE_NO_ELEMENTS"));
            if (change != null) change.Enabled = false;
            selVisible = false;
        }
        else
        {
            selVisible = true;
            foreach (int w in cands)
            {
                switch (G.AstrologerStatus(w, me))
                {
                    case 1: change?.SetTooltip(T("ASTROLOGER_CANT_CHANGE_ON_SUNDAY")); SetEnabled(change, false); selVisible = true; break;
                    case 2: change?.SetTooltip(T("ASTROLOGER_CANT_CHANGE_TWICE_ON_WEEK")); SetEnabled(change, false); selVisible = false; break;
                    case 3: SetEnabled(change, false); selVisible = true; break;
                    default:
                        if (!Me.Purse.CanAfford(G.AstrologerCost)) { change?.SetTooltip(T("ASTROLOGER_CANT_CHANGE_NO_MONEY")); SetEnabled(change, false); }
                        else SetEnabled(change, true);
                        selVisible = true;
                        break;
                }
            }
        }
        if (_choice >= cands.Count) _choice = 0;
        if (sel != null)
        {
            sel.Visible = selVisible && !_closing;
            // 007b7810: the frame is centred on the chosen icon.
            if (Ui.Find(Random[Math.Min(_choice, Random.Length - 1)]) is { } icon && icon.Visible)
                sel.Position = icon.Position + (icon.Size - sel.Size) / 2;
        }
    }

    static void SetEnabled(UiNode? n, bool on) { if (n != null) n.Enabled = on; }

    void Change()
    {
        var cands = G.AstrologerCandidates;
        if (_closing || cands.Count == 0) return;
        // 007b8110: the screen stops taking input (the selection is hidden); 00679150 shows the info box; the command
        // goes when the chosen week's status is 0; the screen closes with the box.
        _closing = true;
        int week = cands[Math.Min(_choice, cands.Count - 1)];
        AdvDialogs.Message(this, T("WARNING_CAPTION"), T("WARNING_TEXT"), null, Close);
        if (G.AstrologerStatus(week, Me.Index) == 0) Apply(new ChangeMoonWeekCmd(HeroId, week));
        else Refresh();
    }
}
