using Godot;
using H5Reborn.Adventure;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// The Trading Post map object (BUILDING_TRADING_POST): the market page of UI/MarketplaceAndMail with the
/// "Trading Post" header (Market/Texts/MainHeaderAdventure) and the 3-marketplace rates, opened over the
/// adventure map while the visiting hero stands on it. Esc or the jog dial's Exit closes it.
/// </summary>
public sealed partial class TradingPostScreen : Control
{
    readonly MarketController _market;

    public TradingPostScreen(AdventureScreen adv, int playerIndex, int tradingPost)
    {
        Name = "TradingPost";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Stop;
        var ui = new UiScreen("UI/MarketplaceAndMail.(WindowScreen).xdb");
        AddChild(ui);
        var session = adv.Session;
        _market = new MarketController(this, ui, session, () => session.S.Players.First(p => p.Index == playerIndex), tradingPost,
            () => { TownUi.SetResourceBar(ui, session.S.Players.First(p => p.Index == playerIndex)); adv.Hud.Refresh(); }, Close);
        TownUi.SetResourceBar(ui, session.S.Players.First(p => p.Index == playerIndex));
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            GetViewport().SetInputAsHandled();
            Close();
        }
    }

    void Close() => QueueFree();
}
