using H5.Adventure.Engine;
using H5Reborn.Adventure;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// Stronghold Slave Market (UI/StrongholdSlaveMarket, CStrongholdSlaveMarket@NUI vtable 0xe43174; enter_special →
/// FUN_00618dc0 (only with TB_SPECIAL_5 built) → FUN_007b9fc0). Button switch FUN_007b99c0: stack not removable →
/// state 2 + T_BUTTON_RED_UNREMOVEABLE; not sellable → state 2 + T_BUTTON_RED_UNSELLABLE; else state 0 +
/// T_BUTTON_GREEN (button enabled in every case); the creature tooltip shows sell_gain. Click (FUN_007b9d30) →
/// <see cref="SellCreatureCmd"/>.
/// </summary>
public sealed partial class SlaveMarketScreen : CreatureTransformScreen
{
    const string Texts = "UI/StrongholdSlaveMarket/Texts.(WindowRelatedTexts).xdb";

    public SlaveMarketScreen(TownScreen owner) : base(owner, "UI/StrongholdSlaveMarket.(WindowScreen).xdb") { }

    public static bool CanOpen(TownScreen owner) =>
        owner.Town.Type == "TOWN_STRONGHOLD" && owner.Town.Level(H5.Adventure.Engine.Adventure.SlaveMarket) > 0;

    protected override (int State, bool Enabled, string? Tip) ButtonFor(int row, int slot)
    {
        var q = Owner.Session.Game.SlaveMarketQuoteFor(Owner.Town, RowHero(row), slot);
        if (!q.Removable) return (2, true, UiGameRoot.RelatedTextAt(Texts, "T_BUTTON_RED_UNREMOVEABLE"));
        if (!q.Sellable) return (2, true, UiGameRoot.RelatedTextAt(Texts, "T_BUTTON_RED_UNSELLABLE"));
        var gain = TownUi.Text("UI/StrongholdSlaveMarket/ArmyHolder/Creature/sell_gain.txt", "Sum will be paid:");
        return (0, true, $"{UiGameRoot.RelatedTextAt(Texts, "T_BUTTON_GREEN")}<br>{gain} {MapBuildingScreen.CostText(q.Price)}");
    }

    protected override void Clicked(int row, int slot)
    {
        var hero = RowHero(row);
        var q = Owner.Session.Game.SlaveMarketQuoteFor(Owner.Town, hero, slot);
        if (!q.Removable || !q.Sellable) return;
        Apply(new SellCreatureCmd(Owner.Town.Object, hero?.Id ?? -1, slot) { Player = Me.Index });
    }
}
