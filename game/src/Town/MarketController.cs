using System.Xml.Linq;
using Godot;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Xdb;
using H5Reborn.Adventure;
using H5Reborn.Ui;
using AdventureEngine = H5.Adventure.Engine.Adventure;

namespace H5Reborn.Town;

/// <summary>
/// The marketplace page of UI/MarketplaceAndMail (town Marketplace and the Trading Post map object),
/// driven like the original CMarketController/CMarketModel (NUI::NMarketAndMail):
/// <list type="bullet">
/// <item>left column TreasureButton_0..6: kingdom treasury, the selected one is what you give;</item>
/// <item>right column RateButton_0..6: rate for each resource you can get — "1/" + rate, or rate + "/1"
///   for gold, "-------" for the resource being given (FUN_0062e9c0, texts UI/Marketplace2/Resource);</item>
/// <item>YouGiveIcon/YouGetIcon (ResourcesInfo Icon), YouGiveText = amount given, YouGetEdit = amount
///   received, ScrollBar = received amount from 0 to the maximum affordable;</item>
/// <item>the generic 5-button jog dial (UI/Common/JogDial/JogDial.xdb, created by FUN_00783f10) with
///   Exit (button_0), Max (button_1, confirmation ExchangeMaximumMessage) and Exchange (button_2,
///   hotkey jd_button_2 per TTJogDialExchange).</item>
/// </list>
/// Rates and the exchange arithmetic come from the engine (AdventureEngine.MarketRates/MarketQuote,
/// tables from the exe); the exchange is an <see cref="ExchangeResourcesCmd"/>.
/// </summary>
public sealed class MarketController
{
    readonly UiScreen _ui;
    readonly AdventureSession _session;
    readonly Func<PlayerState> _me;
    readonly int _marketObject;
    readonly Action _changed;
    readonly Control _host;
    UiScreen? _jog;
    H5Reborn.Options.UiScrollerView? _scroll;
    int _give = -1, _get = -1, _received;

    const string ResTable = "GameMechanics/RefTables/ResourcesInfo.xdb";
    static readonly string[] Ids = { "E_WOOD", "E_ORE", "E_MERCURY", "E_CRYSTAL", "E_SULFUR", "E_GEM", "E_GOLD" };

    public MarketController(Control host, UiScreen ui, AdventureSession session, Func<PlayerState> me, int marketObject, Action changed, Action close)
    {
        _host = host;
        _ui = ui;
        _session = session;
        _me = me;
        _marketObject = marketObject;
        _changed = changed;

        ui.SetVisible("MarketMainWindow", true);
        // The per-resource count strip belongs to the mail/artifact pages; the market shows the treasury in its list.
        foreach (var hidden in new[] { "MailMainWindow", "Hero1ArtifactsWindow", "Hero2ArtifactsWindow", "CreatureCountHolder" }) ui.SetVisible(hidden, false);
        ui.Find("MainHeader")?.SetText(TownUi.Text(marketObject >= 0
            ? "UI/MarketplaceAndMail/Market/Texts/MainHeaderAdventure.txt" : "UI/Marketplace2/Main/MainHeader.txt", "Marketplace"));
        ui.Find("LeftButtonPanelHeader")?.SetText(TownUi.Text("UI/Marketplace2/Main/TreasuryHeader.txt", "Kingdom Treasury"));
        ui.Find("RightButtonPanelHeader")?.SetText(TownUi.Text("UI/Marketplace2/Main/RatesHeader.txt", "Exchange Rates"));
        ui.Find("LeftBigIconHeader")?.SetText(TownUi.Text("UI/Marketplace2/Main/YouGiveHeader.txt", "You Give"));
        ui.Find("RightBigIconHeader")?.SetText(TownUi.Text("UI/Marketplace2/Main/YouGetHeader.txt", "You Get"));

        // TreasureButton_N / RateButton_N (UI/Marketplace2/Resource/Treasure_N, Rate_N) are two-state buttons with
        // AutoChangeState false: state 0 (plain) carries the select_treasure / select_rate reaction, state 1 (the
        // Selection foreground) none. A click runs the current state's commands without advancing, so only an
        // unselected button selects. The controller (FUN_00629a90 / 0062e784) then calls the model: give
        // (0062b6c0) or get (0062b6e0) resource, each resetting the amount to 0; the other choice is kept.
        // UiNode advances multi-state buttons on click, so the click is read here from the state it left.
        for (int i = 0; i < 7; i++)
        {
            int k = i;
            if (ui.Find($"TreasureButton_{i}") is { } t) t.Clicked += n => { if (LeftState(n) == 0) SelectGive(k); else Refresh(); };
            if (ui.Find($"RateButton_{i}") is { } r) r.Clicked += n => { if (LeftState(n) == 0) SelectGet(k); else Refresh(); };
        }
        if (ui.Find("YouGetEdit") is UiEditLine edit)
            edit.Changed += v => { if (int.TryParse(v, out var n)) SetReceived(n, updateEdit: false); };

        if (ui.Find("ScrollBar") is { } bar)
        {
            _scroll = new H5Reborn.Options.UiScrollerView();
            bar.AddChild(_scroll);
            _scroll.PositionChanged += p => SetReceived(p, updateEdit: true, fromScroll: true);
        }

        _jog = new UiScreen("UI/Common/JogDial/JogDial.xdb", ui.Bus);
        host.AddChild(_jog);
        SetJogButton("button_0", "Exit", "UI/Town/TTExit.txt");
        SetJogButton("button_1", "Max", "UI/MarketplaceAndMail/Market/Texts/TTJogDialMaximum.txt");
        SetJogButton("button_2", "Exchange", "UI/MarketplaceAndMail/Market/Texts/TTJogDialExchange.txt");
        foreach (var unused in new[] { "button_3", "button_4" }) _jog.SetVisible(unused, false);
        ui.Bus.On("jd_button_0_reaction", close);
        ui.Bus.On("jd_button_1_reaction", AskMaximum);
        ui.Bus.On("jd_button_2_reaction", () => Exchange(_received));
        // Dev: H5_MARKET_PRESELECT=give,get (resource indices) for screenshot checks.
        if (OS.GetEnvironment("H5_MARKET_PRESELECT") is { Length: > 0 } pre && pre.Split(',') is { Length: 2 } pp
            && int.TryParse(pp[0], out var g0) && int.TryParse(pp[1], out var g1)) { _give = g0; _get = g1 == g0 ? -1 : g1; _received = MaxReceivable() / 2; }
        Refresh();
    }

    /// <summary>The state a clicked two-state button was in before UiNode advanced it.</summary>
    static int LeftState(UiNode n) => n.Window.ButtonStates.Count > 1 ? (n.State + n.Window.ButtonStates.Count - 1) % n.Window.ButtonStates.Count : 0;

    void SelectGive(int i)
    {
        _give = i;
        // A resource can't be exchanged for itself (its rate shows "-------"); the exe's setter keeps the other choice.
        if (_get == i) _get = -1;
        _received = 0;
        Refresh();
    }

    void SelectGet(int i)
    {
        if (i != _give) { _get = i; _received = 0; }
        Refresh();
    }

    /// <summary>Dev (screenshot checks): H5_MARKET_CLICK=TreasureButton_6,RateButton_0,set:5,jog:button_2 clicks (set:N = amount to get) in order.</summary>
    public void DevClicks()
    {
        if (OS.GetEnvironment("H5_MARKET_CLICK") is not { Length: > 0 } list) return;
        foreach (var name in list.Split(','))
        {
            if (name.StartsWith("set:") && int.TryParse(name[4..], out var n)) { SetReceived(n, updateEdit: true); continue; }
            var node = name.StartsWith("jog:") ? _jog?.Find(name[4..]) : _ui.Find(name);
            GD.Print($"[H5] market dev click {name}: {(node == null ? "not found" : "ok")}");
            node?.Activate();
            GD.Print($"[H5] market give={_give} get={_get} received={_received} cost={CostOf(_received)} max={MaxReceivable()} purse={string.Join(",", Enumerable.Range(0, 7).Select(i => _me().Purse[i]))}");
        }
    }

    /// <summary>Puts a JogDialButton preset's Active/Pushed/Disabled/Hover textures on a dial button.</summary>
    void SetJogButton(string name, string preset, string tooltip)
    {
        if (_jog?.Find(name) is not { } btn) return;
        var path = $"UI/Common/JogDial/Buttons/{preset}.(JogDialButton).xdb";
        if (!Game.Instance.Fs.Exists(path)) return;
        var root = Game.Instance.Xdb.Load(path).Root!;
        UiDrawableHelper.Apply(btn, root, path);
        btn.TooltipText = UiResources.StripMarkup(TownUi.Text(tooltip, preset));
    }

    int Markets() => _marketObject >= 0 ? 3 : _session.Game.MarketplaceCount(_me());

    int MaxReceivable()
    {
        if (_give < 0 || _get < 0) return 0;
        int have = _me().Purse[_give];
        int rate = AdventureEngine.MarketRates(_give, Markets())[_get];
        if (rate <= 0) return 0;
        return _get == H5.Adventure.State.Purse.Gold ? have * rate : have / rate;
    }

    /// <summary>Resources given for <paramref name="received"/> units (inverse of the exe's quote).</summary>
    int CostOf(int received)
    {
        if (_give < 0 || _get < 0 || received <= 0) return 0;
        int rate = AdventureEngine.MarketRates(_give, Markets())[_get];
        if (rate <= 0) return 0;
        return _get == H5.Adventure.State.Purse.Gold ? (received + rate - 1) / rate : received * rate;
    }

    void SetReceived(int n, bool updateEdit, bool fromScroll = false)
    {
        int max = MaxReceivable();
        // For gold the amount moves in whole units of the resource given (received = given × rate).
        if (_get == H5.Adventure.State.Purse.Gold && _give >= 0)
        {
            int rate = Math.Max(1, AdventureEngine.MarketRates(_give, Markets())[_get]);
            n = fromScroll ? n * rate : n / rate * rate;
        }
        _received = Math.Clamp(n, 0, max);
        Refresh(updateEdit, fromScroll);
    }

    void AskMaximum()
    {
        int max = MaxReceivable();
        if (max <= 0) return;
        var res = Ids.Select(ResName).ToArray();
        var text = TownUi.Text("UI/MarketplaceAndMail/Market/Texts/ExchangeMaximumMessage.txt",
                "Do you really want to exchange all <value=AmountToGive> of your <value=ResourceToGive> for <value=AmountToGet> <value=ResourceToGet>?")
            .Replace("<value=AmountToGive>", CostOf(max).ToString()).Replace("<value=ResourceToGive>", res[_give])
            .Replace("<value=AmountToGet>", max.ToString()).Replace("<value=ResourceToGet>", res[_get]);
        var header = TownUi.Text("UI/marketplace_max_exchange_question_header.txt", "Exchange all");
        AdvDialogs.YesNo(_host, UiResources.StripMarkup(header), text, null, yes => { if (yes) Exchange(max); });
    }

    void Exchange(int received)
    {
        if (_give < 0 || _get < 0 || received <= 0) return;
        var r = _session.Apply(new ExchangeResourcesCmd(_give, _get, CostOf(received), _marketObject) { Player = _me().Index });
        if (!r.Ok) GD.PushWarning($"[H5] exchange rejected: {r.Error}");
        _received = 0;
        _changed();
        Refresh();
    }

    public void Refresh(bool updateEdit = true, bool fromScroll = false)
    {
        var me = _me();
        int markets = Markets();
        var rates = _give >= 0 ? AdventureEngine.MarketRates(_give, markets) : new int[7];
        string prefix = TownUi.Text("UI/Marketplace2/Resource/ratePrefix.txt", "1/");
        string suffix = TownUi.Text("UI/Marketplace2/Resource/rateSuffix.txt", "/1");
        string disabled = TownUi.Text("UI/Marketplace2/Resource/rateDisabled.txt", "-------");
        for (int i = 0; i < 7; i++)
        {
            var t = _ui.Find($"TreasureButton_{i}");
            if (t != null)
            {
                TownUi.SetPicture(t.FindChild("Icon", true, false) as Control, SmallIcon(i));
                SetRowText(t, "", me.Purse[i].ToString(), "");
                t.SetState(i == _give && t.Window.ButtonStates.Count > 1 ? 1 : 0);
            }
            var r = _ui.Find($"RateButton_{i}");
            if (r != null)
            {
                TownUi.SetPicture(r.FindChild("Icon", true, false) as Control, SmallIcon(i));
                if (_give < 0 || i == _give || rates[i] <= 0) SetRowText(r, "", disabled, "");
                else if (i == H5.Adventure.State.Purse.Gold) SetRowText(r, "", rates[i].ToString(), suffix);
                else SetRowText(r, prefix, rates[i].ToString(), "");
                r.SetState(i == _get && r.Window.ButtonStates.Count > 1 ? 1 : 0);
            }
        }
        TownUi.SetPicture(_ui.Find("YouGiveIcon"), _give >= 0 ? BigIcon(_give) : null);
        TownUi.SetPicture(_ui.Find("YouGetIcon"), _get >= 0 ? BigIcon(_get) : null);
        _ui.Find("YouGiveText")?.SetValue("Amount", CostOf(_received).ToString());
        if (updateEdit && _ui.Find("YouGetEdit") is UiEditLine edit) edit.Value = _received.ToString();
        int max = MaxReceivable();
        if (_scroll != null)
        {
            bool gold = _get == H5.Adventure.State.Purse.Gold && _give >= 0;
            int rate = gold ? Math.Max(1, rates[_get]) : 1;
            int steps = gold ? max / rate : max;
            if (!fromScroll) _scroll.Setup(steps + 1, gold ? _received / rate : _received);
            _scroll.Enabled = steps > 0;
        }
        if (_jog?.Find("button_1") is { } maxBtn) maxBtn.Enabled = max > 0;
        if (_jog?.Find("button_2") is { } exBtn) exBtn.Enabled = _received > 0;
    }

    static void SetRowText(UiNode row, string prefix, string amount, string suffix)
    {
        if (row.FindChild("Text", true, false) is not UiNode text) return;
        text.SetValue("Prefix", prefix);
        text.SetValue("Amount", amount);
        text.SetValue("Suffix", suffix);
    }

    static XElement? ResItem(int i) =>
        Game.Instance.Xdb.Load(ResTable).Root!.Element("objects")!.Elements("Item").FirstOrDefault(x => (string?)x.Element("ID") == Ids[i])?.Element("obj");

    static Texture2D? SmallIcon(int i) => Icon(i, "SmallIcon");
    static Texture2D? BigIcon(int i) => Icon(i, "Icon");

    static Texture2D? Icon(int i, string element)
    {
        var href = (string?)ResItem(i)?.Element(element)?.Attribute("href");
        return string.IsNullOrEmpty(href) ? null : Game.Instance.LoadTexture(XdbRef.Parse(href, ResTable).Path);
    }

    static string ResName(string id)
    {
        int i = Array.IndexOf(Ids, id);
        var href = (string?)ResItem(i)?.Element("NameFileRef")?.Attribute("href");
        return UiResources.StripMarkup(Game.Instance.Xdb.ReadText(href, ResTable) ?? TownUi.ResourceNames[i]).Trim();
    }
}

/// <summary>Builds button-state visuals from a (JogDialButton) preset (Active/Pushed/Disabled/Hover textures).</summary>
public static class UiDrawableHelper
{
    public static void Apply(UiNode button, XElement preset, string presetPath)
    {
        H5.Core.Ui.UiDrawable? D(string element)
        {
            var href = (string?)preset.Element(element)?.Attribute("href");
            if (string.IsNullOrEmpty(href)) return null;
            var tex = XdbRef.Parse(href, presetPath).Path;
            if (!Game.Instance.Fs.Exists(tex)) return null;
            var dest = (string?)Game.Instance.Xdb.Load(tex).Root?.Element("DestName")?.Attribute("href");
            return new H5.Core.Ui.UiDrawable
            {
                Kind = H5.Core.Ui.UiDrawableKind.ScalingTexture,
                TexturePath = tex,
                DdsPath = string.IsNullOrEmpty(dest) ? null : XdbRef.Parse(dest, tex).Path,
            };
        }
        var active = D("Active");
        var pushed = D("Pushed");
        var disabled = D("Disabled");
        var hover = D("Hover");
        if (button.Window.ButtonStates.Count == 0) button.Window.ButtonStates.Add(new H5.Core.Ui.UiButtonState());
        var visuals = button.Window.ButtonStates[0].Visuals;
        visuals["Normal"] = new H5.Core.Ui.UiVisualState { Background = active };
        visuals["MouseOver"] = new H5.Core.Ui.UiVisualState { Background = active, Foreground = hover };
        visuals["Pushed"] = new H5.Core.Ui.UiVisualState { Background = pushed ?? active };
        visuals["Disabled"] = new H5.Core.Ui.UiVisualState { Background = disabled ?? active };
        button.QueueRedraw();
    }
}
