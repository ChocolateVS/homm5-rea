using Godot;
using H5.Adventure.Engine;
using H5Reborn.Town;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// The caravan meet screen (CMeetCaravanCmd 0042f870 → CCreateCaravanMeetScreen 0043cdb0 → CCaravanMeet, vtable
/// 00e42bd4): the garrison screen UI/Garrison (ID_GARRISON_SCREEN, 007b6330) with the caravan as the second army.
/// Its description is T_SCREEN_CARAVAN_DESC (007b5c00, ScreenCaravanDesc.txt), the header tooltip
/// T_HEADER_TOOLTIP_CARAVAN (007b5cf0, HeaderTooltipCaravan.txt). Row 0 is the meeting hero, row 1 the caravan; a click on
/// a stack and then on another slot swaps, moves or merges them (SwapStacksCmd with FromCaravan / ToCaravan, the same
/// garrison exchange as <see cref="GarrisonScreen"/>); OK closes. UNVERIFIED: the second row taken as the meeting hero
/// (0043cdb0 keeps the caravan and the player only); the header text as the caravan's name (Caravan_Name.txt).
/// </summary>
public sealed partial class CaravanMeetScreen : MapBuildingScreen
{
    readonly int _caravan;
    (int Row, int Slot)? _picked;

    public CaravanMeetScreen(AdventureScreen adv, int heroId, int caravan) : base(adv, "UI/Garrison.(WindowScreen).xdb", heroId, -1)
    {
        _caravan = caravan;
        Ui.Bus.On("confirm", Close);
        for (int row = 0; row < 2; row++)
            for (int i = 0; i < H5.Adventure.State.Army.Slots; i++)
            {
                int r = row, slot = i;
                if (Ui.Find($"CreatureSwapMain/armyholder{row}/creature{i}") is { } b) b.Clicked += _ => Pick(r, slot);
            }
        var header = Ui.Find("CreatureSwapMain/HeaderView");
        header?.SetText("<h1><center>" + CaravanLayer.SharedText(0));
        header?.SetTooltip(TownUi.Text("UI/Garrison/HeaderTooltipCaravan.txt"));
        Ui.Find("CreatureSwapMain/CreatureSwapView")?.SetValue("desc", TownUi.Text("UI/Garrison/ScreenCaravanDesc.txt"));
    }

    H5.Adventure.State.CaravanState? Car => Adv.Session.S.Caravans.Find(c => c.Id == _caravan);

    H5.Adventure.State.Stack?[] Row(int row) => row == 0 ? Hero.Army : Car?.Army ?? H5.Adventure.State.Army.Empty();

    void Pick(int row, int slot)
    {
        if (_picked is not { } from)
        {
            if (Row(row)[slot] is { Count: > 0 }) _picked = (row, slot);
            Refresh();
            return;
        }
        _picked = null;
        if (from != (row, slot))
            Apply(new SwapStacksCmd(from.Row == 0 ? HeroId : -1, -1, from.Slot, row == 0 ? HeroId : -1, -1, slot)
            {
                FromCaravan = from.Row == 1 ? _caravan : -1,
                ToCaravan = row == 1 ? _caravan : -1,
            });
        else Refresh();
    }

    public override void Refresh()
    {
        base.Refresh();
        TownUi.SetPicture(Ui.Find("CreatureSwapMain/heroicon0"), TownUi.HeroFace(Hero, large: false));
        if (Ui.Find("CreatureSwapMain/heroicon1") is { } ci) TownUi.SetPicture(ci, CaravanLayer.Icon());
        for (int row = 0; row < 2; row++)
        {
            var army = Row(row);
            for (int i = 0; i < H5.Adventure.State.Army.Slots; i++)
            {
                var s = army[i];
                var icon = Ui.Find($"CreatureSwapMain/armyholder{row}/creature{i}/icon");
                if (icon == null) continue;
                icon.Visible = s is { Count: > 0 };
                TownUi.SetPicture(icon, s is { Count: > 0 } ? TownUi.CreatureIcon(s.Creature) : null);
                (icon.FindChild("NumberOfCreatures", false, false) as UiNode)?.SetValue("NumberOfCreatures", s?.Count.ToString() ?? "");
                icon.Modulate = _picked == (row, i) ? new Color(1.4f, 1.4f, 1f) : Colors.White;
            }
        }
    }
}
