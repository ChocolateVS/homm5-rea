using Godot;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Ui;
using H5.Core.Xdb;
using H5Reborn.Town;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// The tavern screen (UI/TavernScreen, NUI tavern screen 00637b50) as the map tavern (BUILDING_TAVERN, controller
/// 00639580 → 00638970) and the den of thieves (BUILDING_DEN_OF_THIEVES, controller 00639740 → 00638670 with the
/// tavern flag +0x260 off) open it. Bookmarks Hero1/Hero2 (click_hero1/2), Rumors (rumors), Thiefs (thiefs) switch
/// the hire / rumours / thieves windows; Hero1/2 and Rumors are enabled only in tavern mode, Thiefs only when a
/// thieves' guild level &gt; 0 was passed (only the den passes one), so the map tavern has heroes and rumours and the
/// den shows the thieves page alone (+0x184 = 3). The generic jog dial (00783f10) carries Exit (button 0,
/// exit_tavern), the action button 2 (preset Buy; hire_hero on a hero page, pay otherwise; enabled when the hire
/// status 004f46a0 is 0 and a free tile is left, else its tooltip is hire_result_*) and HeroInfo (button 4).
/// Handler 00636c30: 0 → close, 2 → 006350b0 hire / 00635320 rumour / 00635440 thieves, 4 → hero info 006c44f0.
/// </summary>
public sealed partial class MapTavernScreen : MapBuildingScreen
{
    const string Texts = "UI/Tavern.(WindowRelatedTexts).xdb";
    const string ThievesTexts = "UI/Tavern/Thieves.(WindowRelatedTexts).xdb";
    static readonly string[] PlayerColors = { "Red", "Blue", "Green", "Yellow", "Orange", "Teal", "Purple", "Brown" };
    static readonly string[] Tabs = { "Hero1Bookmark", "Hero2Bookmark", "RumorsBookmark", "ThiefsBookmark" };

    readonly bool _den;
    readonly ThievesInfo? _thieves;
    readonly UiScreen _jog;
    readonly List<UiNode> _lines = new();
    int _tab;

    public MapTavernScreen(AdventureScreen adv, int heroId, int obj, bool den) : base(adv, "UI/TavernScreen.(WindowScreen).xdb", heroId, obj)
    {
        _den = den;
        // 00b04c72: the den passes the player's dens owned + 1 (player vfunc 0x138 + 1); taverns pass 0.
        if (den) _thieves = adv.Session.Game.BuildThievesInfo(adv.Session.Game.ThievesDensOwned(Me) + 1);
        Ui.Bus.On("click_hero1", () => SetTab(0));
        Ui.Bus.On("click_hero2", () => SetTab(1));
        Ui.Bus.On("rumors", () => SetTab(2));
        Ui.Bus.On("thiefs", () => SetTab(3));

        _jog = new UiScreen("UI/Common/JogDial/JogDial.xdb", Ui.Bus);
        AddChild(_jog);
        SetJogButton("button_0", "Exit", T("exit_tavern"));
        SetJogButton("button_2", "Buy", T("hire_hero"));
        SetJogButton("button_4", "HeroInfo", T("hero_info"));
        foreach (var unused in new[] { "button_1", "button_3" }) _jog.SetVisible(unused, false);
        Ui.Bus.On("jd_button_0_reaction", Close);
        Ui.Bus.On("jd_button_2_reaction", Action);

        // 00637b50: the den starts on the thieves page; the tavern on the first hero, else on rumours.
        _tab = den ? 3 : Offers()[0] != null ? 0 : 2;
    }

    static string T(string key) => UiGameRoot.RelatedTextAt(Texts, key) ?? "";
    static string TT(string key) => UiGameRoot.RelatedTextAt(ThievesTexts, key) ?? "";

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

    /// <summary>The two offers (player vfunc 0x14c(0/1)); 00636a10 moves a lone second hero to the first bookmark. (raw slot per page)</summary>
    (string? Id, int Slot)[] OfferSlots()
    {
        var raw = Me.Tavern;
        var list = new List<(string?, int)>();
        for (int i = 0; i < raw.Length && i < 2; i++) if (raw[i] != null) list.Add((raw[i], i));
        while (list.Count < 2) list.Add((null, -1));
        return list.ToArray();
    }

    string?[] Offers() => OfferSlots().Select(o => o.Id).ToArray();

    void Action()
    {
        var game = Adv.Session.Game;
        switch (_tab)
        {
            case 0 or 1 when !_den:
                var (id, slot) = OfferSlots()[_tab];
                if (id == null) return;
                if (Apply(new HireHeroInTavernCmd(HeroId, Object, slot)))
                {
                    Audio.AudioSystem.PlayNamed("HIRE_HERO_SOUND");
                    foreach (var h in Adv.Session.S.Heroes) Adv.World.SyncHero(h);
                    if (Offers()[_tab] == null && Offers()[0] == null) _tab = 2;
                }
                break;
            case 3 when _thieves is { CanBuy: true }:
                // 00635440: gold − 250 ≥ 0 → the set-resource command, then one more line (vfunc 0x34).
                if (Apply(new PayThievesGuildCmd()))
                {
                    _thieves.Buy();
                    Audio.AudioSystem.PlayNamed("BUY_THIEVES_SOUND");
                }
                break;
        }
        Refresh();
    }

    public override void Refresh()
    {
        base.Refresh();
        var game = Adv.Session.Game;
        var offers = Offers();
        // Bookmarks (00637b50): heroes need the tavern flag and a hero; rumours the flag; thieves the flag and a level > 0.
        SetBookmark(0, !_den && offers[0] != null, offers[0]);
        SetBookmark(1, !_den && offers[1] != null, offers[1]);
        SetBookmark(2, !_den, null);
        SetBookmark(3, !_den && _thieves != null, null);
        Ui.SetVisible("tavern-hire-window", _tab <= 1);
        Ui.SetVisible("tavern-rumors-window", _tab == 2);
        Ui.SetVisible("tavern-thieves-window", _tab == 3);

        string? blocked = null;
        bool can = false;
        int price = 0;
        if (_tab <= 1)
        {
            price = game.HireCost(Me);
            TavernHeroView.Show(Ui, offers[_tab]);
            // 00636d20: status 004f46a0 (3 money, 5 heroes) and the tile list (no place).
            int status = game.MapTavernHireStatus(Me);
            bool place = Hero is { } v && game.MapTavernFreeTiles(v).Count > 0;
            blocked = status switch { 3 => T("hire_result_no_money"), 5 => T("hire_result_max_heroes"), _ => place ? null : T("hire_result_no_place") };
            can = offers[_tab] != null && blocked == null;
        }
        else if (_tab == 3 && _thieves != null)
        {
            price = ThievesInfo.Cost;
            can = _thieves.CanBuy && Me.Purse[Purse.Gold] >= price;
            FillThieves();
        }
        // GAP: rumours (CBuyRumorCmd 00639170, price RPGStats +0x54 via 00b4c260) are not modelled; the page shows its texts only.
        foreach (var w in new[] { "tavern-hire-window", "tavern-rumors-window", "tavern-thieves-window" })
            Ui.Find($"{w}/HirePrice/price")?.SetValue("price", price.ToString());
        foreach (var w in new[] { "tavern-rumors-window", "tavern-thieves-window" })
            if (Ui.Find(w) is { } win && win.FindChild("price", true, false) is UiNode pr) pr.SetValue("price", price.ToString());
        Ui.Find("price")?.SetValue("price", price.ToString());
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
    /// The thieves page (00635a60): one InfoLine per category that has visible entries, caption = the category text,
    /// one InfoBox per entry (006346b0: the player's colour, hidden players as UnknownPlayer; the hero face / creature
    /// icon in pic), tooltip TooltipFormat "&lt;name&gt;: &lt;number&gt;" with number = CountType%d / CrCountType%d, the exact
    /// value or CountTypeUnknown (00635540).
    /// </summary>
    void FillThieves()
    {
        // 00635a60: clear the container (slot 0x2c), then per category clone InfoLine (vfunc 0x28), set its caption,
        // clone InfoBox0 once per further entry into the Boxes gathering (00634580: InfoBox1.., relayout), fill the
        // boxes (006346b0 / 00635540), show the line and add it as the container's next item (slot 8).
        foreach (var l in _lines) Ui.RemoveInstance(l);
        _lines.Clear();
        if (Ui.Find("tavern-thieves-window/ScrollContainer") is not UiScrollableContainer box || Ui.Find("tavern-thieves-window/InfoLine") is not { } template) return;
        box.ClearItems();
        if (_thieves == null) return;
        // 00635a60 walks the categories in order and returns at the first list with < 1 entry (005056b0 clears every
        // category's vector and fills it only while the category is reached at the level).
        var cats = _thieves.Visible(keepEmpty: true).TakeWhile(c => c.Entries.Count > 0).ToList();
        for (int row = 0; row < cats.Count; row++)
        {
            var cat = cats[row];
            var line = Ui.Instantiate(template.Window, box.Window, new Int2(0, 0), $"line{row}");
            // The clone keeps the template's placement (EPA_MARGIN: the container's width, 100 high).
            line.Window.Placement = template.Window.Placement;
            _lines.Add(line);
            if (line.FindChild("InfoCaption", true, false) is UiNode cap) cap.SetValue("caption", TT(cat.Name));
            var boxes = line.FindChild("Boxes", true, false) as UiNode;
            var proto = boxes?.FindChild("InfoBox0", true, false) as UiNode;
            if (boxes != null && proto != null)
                for (int i = 0; i < cat.Entries.Count; i++)
                {
                    var e = cat.Entries[i];
                    var b = i == 0 ? proto : Ui.Instantiate(proto.Window, boxes.Window, new Int2(0, 0), $"InfoBox{i}");
                    if (i > 0) b.Window.Placement = proto.Window.Placement;
                    string color = e.Player is >= 1 and <= 8 ? PlayerColors[e.Player - 1] : "UnknownPlayer";
                    TownUi.SetPicture(b.FindChild("inner", true, false) as Control, Game.Instance.LoadTexture($"UI/Tavern/{color}.(Texture).dds"));
                    Texture2D? pic = e.Hero != null && Combat.CombatIcons.Hero(e.Hero, false) is { } hf ? Game.Instance.LoadTexture(hf)
                        : e.Creature != null ? TownUi.CreatureIcon(e.Creature) : null;
                    TownUi.SetPicture(b.FindChild("pic", true, false) as Control, pic);
                    string number = e.CountType switch
                    {
                        -2 => TT("CountTypeUnknown") is { Length: > 0 } u ? u : "???",
                        -1 => e.Value.ToString(),
                        _ => TT((cat.Name == "BestCreature" ? "CrCountType" : "CountType") + e.CountType),
                    };
                    var fmt = TT("TooltipFormat");
                    b.SetTooltip(fmt.Replace("<value=name>", TT(cat.Name + "Tooltip")).Replace("<value=number>", e.Player >= 1 || e.CountType != -2 ? number : "???"));
                }
            box.AddItem(line);
        }
    }
}

/// <summary>The hire page of the tavern screen (town and map): the offered hero's face, name, class, attributes.</summary>
public static class TavernHeroView
{
    public static void Show(UiScreen ui, string? defId)
    {
        var db = Game.Instance.Db;
        if (defId == null || !db.Heroes.TryGetValue(defId, out var def))
        {
            ui.SetVisible("HeroInfo", false);
            return;
        }
        ui.SetVisible("HeroInfo", true);
        TownUi.SetPicture(ui.Find("HeroInfo/Face"), Combat.CombatIcons.Hero(defId, true) is { } fp ? Game.Instance.LoadTexture(fp) : null);
        var cls = def.Class.Replace("HERO_CLASS_", "").Replace('_', ' ');
        ui.Find("HeroInfo/Name")?.SetValue("HeroName", def.Name);
        ui.Find("HeroInfo/Name")?.SetValue("HeroClass", System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(cls.ToLowerInvariant()));
        ui.Find("HeroInfo/Name")?.SetValue("HeroLevel", "1");
        foreach (var (key, value) in new[] { ("Attack", def.Attack), ("Defence", def.Defence), ("SpellPower", def.SpellPower), ("Knowledge", def.Knowledge) })
            ui.Find($"HeroAttributes/{key}")?.SetValue("Attribute", value.ToString());
        ui.Find("HeroAttributes/Morale")?.SetValue("Attribute", "0");
        ui.Find("HeroAttributes/Luck")?.SetValue("Attribute", "0");
        // ManaValue's text is minmaxvalue.txt "<value=Attribute>/<value=AttributeMax>" (current / maximum); an offered hero
        // has full mana: knowledge × HeroManaPerKnowledge, × Intelligence_ManaPointsBonus with the Intelligence perk
        // (the formula of Adventure.MaxMana).
        var adv = db.Stats.Root.Element("adventure");
        double mana = def.Knowledge * (H5.Core.Data.XmlHelpers.Int(adv, "HeroManaPerKnowledge", 10));
        if (def.Perks.Contains("HERO_SKILL_INTELLIGENCE")) mana *= H5.Core.Data.XmlHelpers.Dbl(adv?.Element("HeroSkills"), "Intelligence_ManaPointsBonus", 1.5);
        var manaNode = ui.Find("HeroAttributes/Mana");
        manaNode?.SetValue("Attribute", ((int)mana).ToString());
        manaNode?.SetValue("AttributeMax", ((int)mana).ToString());
        // GAP: the exe shows the offered hero's army (00638d90); the engine rolls it at hire (GiveStartingArmy).
        for (int i = 0; i < 7; i++)
        {
            var slot = ui.Find($"Slot_{i + 1}");
            TownUi.SetPicture(slot?.FindChild("Pic", true, false) as Control, null);
            if (slot?.FindChild("Text", true, false) is UiNode txt) txt.Visible = false;
        }
    }
}
