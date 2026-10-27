using Godot;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5Reborn.Adventure;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// The town tavern (UI/TavernScreen; town screen 006186b0 → CCreateTavernScreen 006393e0 with the tavern flag +0x24
/// set → screen 00638670 with +0x1b0 = +0x260 = 1). Bookmarks (handlers 00634220..00634260): Hero1/Hero2 (pages 0/1,
/// enabled with an offer), Rumors (page 2, enabled by the tavern flag), Thiefs (page 3, needs a thieves' level &gt; 0).
/// The generic jog dial (00783f10) carries Exit (button 0), the action button 2 (hire_hero on a hero page, pay on the
/// rumours / thieves pages) and HeroInfo (button 4). Opening shows the first hero, else the rumours (00637b50).
/// </summary>
public sealed partial class TavernScreen : TownSubScreen
{
    const string Texts = "UI/Tavern.(WindowRelatedTexts).xdb";
    const string RumourTexts = "UI/Tavern/RumorsFormats.(WindowRelatedTexts).xdb";
    static readonly string[] Tabs = { "Hero1Bookmark", "Hero2Bookmark", "RumorsBookmark", "ThiefsBookmark" };

    readonly UiScreen _jog;
    int _tab;

    public TavernScreen(TownScreen owner) : base(owner, "UI/TavernScreen.(WindowScreen).xdb")
    {
        Ui.Bus.On("click_hero1", () => SetTab(0));
        Ui.Bus.On("click_hero2", () => SetTab(1));
        Ui.Bus.On("rumors", () => SetTab(2));
        Ui.Bus.On("thiefs", () => SetTab(3));
        // input_page_up / _down (00634270 / 006342a0) step the page by −1 / +1 modulo 4.
        Ui.Bus.On("input_page_up", () => Step(-1));
        Ui.Bus.On("input_page_down", () => Step(+1));

        _jog = new UiScreen("UI/Common/JogDial/JogDial.xdb", Ui.Bus);
        AddChild(_jog);
        SetJogButton("button_0", "Exit", T("exit_tavern"));
        SetJogButton("button_2", "Buy", T("hire_hero"));
        SetJogButton("button_4", "HeroInfo", T("hero_info"));
        foreach (var unused in new[] { "button_1", "button_3" }) _jog.SetVisible(unused, false);
        Ui.Bus.On("jd_button_0_reaction", Close);
        Ui.Bus.On("jd_button_2_reaction", Action);
        _tab = Offers()[0] != null ? 0 : 2;
        // Dev (screenshots): `--tavern-dev-rumours=N` buys N rumours and shows the rumours page.
        if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--tavern-dev-rumours=")) is { } dev && int.TryParse(dev.Split('=')[1], out var n))
        {
            for (int i = 0; i < n; i++) owner.Session.Apply(new BuyRumourCmd(owner.Town.Object) { Player = owner.Town.Owner });
            _tab = 2;
        }
    }

    static string T(string key) => UiGameRoot.RelatedTextAt(Texts, key) ?? "";

    void SetJogButton(string name, string preset, string tooltip)
    {
        if (_jog.Find(name) is not { } btn) return;
        var path = $"UI/Common/JogDial/Buttons/{preset}.(JogDialButton).xdb";
        if (Game.Instance.Fs.Exists(path)) UiDrawableHelper.Apply(btn, Game.Instance.Xdb.Load(path).Root!, path);
        btn.SetTooltip(tooltip);
    }

    void SetTab(int tab)
    {
        if (Ui.Find(Tabs[tab]) is { } b && !b.Enabled) return;
        _tab = tab;
        Refresh();
    }

    /// <summary>00634270 / 006342a0: (page ± 1) mod 4, skipping the rumours page when the tavern flag is off (never in a town).</summary>
    void Step(int dir)
    {
        _tab = ((_tab + dir) % 4 + 4) % 4;
        Refresh();
    }

    /// <summary>The town's offers after its hiring filter; a lone second hero moves to the first bookmark (00636a10).</summary>
    string?[] Offers()
    {
        var offers = H5.Adventure.Engine.Adventure.TownTavernOffers(Game.Instance.Db, Me, Owner.Town);
        return new[] { offers.Length > 0 ? offers[0] : null, offers.Length > 1 ? offers[1] : null };
    }

    /// <summary>
    /// The action button (handler 00636c30 → button 2): on a hero page 006350b0 (town: status 004f4570 must be 0, then
    /// the town hire command 00639060, HIRE_HERO_SOUND); on the rumours page 00635320 (CRerollRumourCmd, BUY_RUMORS_SOUND).
    /// </summary>
    void Action()
    {
        var game = Owner.Session.Game;
        switch (_tab)
        {
            case 0 or 1:
                if (game.TownHireStatus(Me, Owner.Town) != 0 || Offers()[_tab] == null) return;
                if (Apply(new HireHeroCmd(Owner.Town.Object, _tab) { Player = Me.Index }))
                {
                    Audio.AudioSystem.PlayNamed("HIRE_HERO_SOUND");
                }
                break;
            case 2:
                if (Me.Purse[Purse.Gold] < game.RumourCost) return;
                if (Apply(new BuyRumourCmd(Owner.Town.Object) { Player = Me.Index }))
                    Audio.AudioSystem.PlayNamed("BUY_RUMORS_SOUND");
                break;
        }
        Refresh();
    }

    public override void Refresh()
    {
        base.Refresh();
        if (_jog == null) return;
        var game = Owner.Session.Game;
        var offers = Offers();
        // 006372f3: an empty hero page moves on (page 0 → 1 if the second offer exists, else 2; page 1 → 0, else 2).
        if (_tab == 0 && offers[0] == null) _tab = offers[1] != null ? 1 : 2;
        else if (_tab == 1 && offers[1] == null) _tab = offers[0] != null ? 0 : 2;
        // 00637b50: heroes need the tavern flag and an offer, rumours the flag; the thieves level stays 0 here (GAP).
        SetBookmark(0, offers[0] != null, offers[0]);
        SetBookmark(1, offers[1] != null, offers[1]);
        SetBookmark(2, true, null);
        SetBookmark(3, false, null);
        Ui.SetVisible("tavern-hire-window", _tab <= 1);
        Ui.SetVisible("tavern-rumors-window", _tab == 2);
        Ui.SetVisible("tavern-thieves-window", _tab == 3);

        string? blocked = null;
        bool can = false;
        int price = 0;
        if (_tab <= 1)
        {
            price = game.HireCost(Me, Owner.Town);
            TavernHeroView.Show(Ui, offers[_tab]);
            // 00636d20 (town): enabled when 004f4570 is 0; 3 → hire_result_no_money, 5 → _max_heroes, 0/4/6 → _no_place.
            int status = game.TownHireStatus(Me, Owner.Town);
            blocked = status switch { 0 => null, 3 => T("hire_result_no_money"), 5 => T("hire_result_max_heroes"), _ => T("hire_result_no_place") };
            can = offers[_tab] != null && status == 0;
        }
        else if (_tab == 2)
        {
            // 006372f3 case 2: the price, the action enabled when price ≤ gold, the list as FIRST_RUMOR + RUMORS_FORMAT.
            price = game.RumourCost;
            can = price <= Me.Purse[Purse.Gold];
            FillRumours();
        }
        foreach (var w in new[] { "tavern-hire-window", "tavern-rumors-window", "tavern-thieves-window" })
            if (Ui.Find(w) is { } win && win.FindChild("price", true, false) is UiNode pr) pr.SetValue("price", price.ToString());
        if (_jog.Find("button_2") is { } act)
        {
            act.Enabled = can;
            act.SetTooltip(blocked ?? T(_tab <= 1 ? "hire_hero" : "pay"));
        }
        // GAP: hero info (006c44f0) shows the offered hero's screen; the engine has no hero object before hiring.
        if (_jog.Find("button_4") is { } info) info.Enabled = false;
    }

    void SetBookmark(int i, bool enabled, string? hero)
    {
        if (Ui.Find(Tabs[i]) is not { } b) return;
        b.Enabled = enabled;
        if (i <= 1)
            TownUi.SetPicture(Ui.Find($"{Tabs[i]}/Icon"), hero != null && Combat.CombatIcons.Hero(hero, false) is { } face ? Game.Instance.LoadTexture(face) : null);
        if (b.Window.ButtonStates.Count > 1) b.SetState(i == _tab ? 1 : 0);
    }

    /// <summary>
    /// The rumours text (006372f3): FIRST_RUMOR with cur_rumor = entry 0, then for each further entry RUMORS_FORMAT
    /// with prev_rumors = the text so far and cur_rumor = the entry.
    /// </summary>
    void FillRumours()
    {
        if (Ui.Find("tavern-rumors-window/RumorsText") is not { } template || Ui.Find("tavern-rumors-window/ScrollContainer") is not { } box) return;
        string first = UiGameRoot.RelatedTextAt(RumourTexts, "FIRST_RUMOR") ?? "<value=cur_rumor>";
        string next = UiGameRoot.RelatedTextAt(RumourTexts, "RUMORS_FORMAT") ?? "<value=prev_rumors><br><value=cur_rumor>";
        string text = "";
        var list = Owner.Town.Rumours;
        for (int i = 0; i < list.Count; i++)
        {
            var rumour = TownUi.Text(list[i]);
            text = i == 0 ? first.Replace("<value=cur_rumor>", rumour)
                : next.Replace("<value=prev_rumors>", text).Replace("<value=cur_rumor>", rumour);
        }
        // The screen puts RumorsText into ScrollContainer (vfunc 8, 00637b50) and scrolls it there.
        template.Visible = false;
        if (_text != null) Ui.RemoveInstance(_text);
        _text = Ui.Instantiate(template.Window, box.Window, new H5.Core.Ui.Int2(0, -_scroll), "RumorsTextShown");
        _text.SetText(text);
        box.ClipContents = true;
        if (!_scrollWired)
        {
            _scrollWired = true;
            box.MouseFilter = MouseFilterEnum.Stop;
            box.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } mb || _text == null) return;
                float max = Math.Max(0, _text.Size.Y - box.Size.Y);
                _scroll = (int)Math.Clamp(_scroll + (mb.ButtonIndex == MouseButton.WheelDown ? 40 : -40), 0, max);
                FillRumours();
            };
        }
    }

    UiNode? _text;
    int _scroll;
    bool _scrollWired;
}
