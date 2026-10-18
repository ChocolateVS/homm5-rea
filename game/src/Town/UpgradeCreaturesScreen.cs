using Godot;
using H5.Adventure.Engine;
using H5.Adventure.State;
using H5Reborn.Adventure;
using H5Reborn.Ui;

namespace H5Reborn.Town;

/// <summary>
/// Town creature upgrades (UI/UpgradeCreature): the garrison (row 0) and the visiting hero's army (row 1);
/// a stack's button pops up the stock choose-upgrade item with the upgrades the town's upgraded dwelling
/// offers, priced as upgraded cost − base cost per creature (exe FUN_004bb570).
/// </summary>
public sealed partial class UpgradeCreaturesScreen : TownSubScreen
{
    UiScreen? _choose;

    public UpgradeCreaturesScreen(TownScreen owner) : base(owner, "UI/UpgradeCreature.(WindowScreen).xdb")
    {
        Ui.Bus.On("confirm", Close);
        Ui.SetVisible("dndicon", false);
        for (int row = 0; row < 2; row++)
            for (int i = 0; i < Army.Slots; i++)
            {
                int r = row, slot = i;
                if (Ui.Find($"armyholder{row}/creature{i}/button") is { } b) b.Clicked += _ => OpenChoice(r, slot);
            }
    }

    HeroState? RowHero(int row) => row == 1 ? Owner.VisitingHero() : null;

    Stack?[]? RowArmy(int row) => row == 0 ? Owner.Town.Garrison : RowHero(1)?.Army;

    bool Allowed(string creature, string target)
    {
        var game = Owner.Session.Game;
        int tier = Game.Instance.Db.Creatures.TryGetValue(creature, out var cd) ? cd.Tier : 0;
        return game.DwellingCreature(Owner.Town, tier, 1) == target || game.DwellingCreature(Owner.Town, tier, 2) == target;
    }

    public override void Refresh()
    {
        base.Refresh();
        var game = Owner.Session.Game;
        Ui.Find("heroicon0")!.Visible = Owner.GarrisonHero() is not null;
        if (Owner.GarrisonHero() is { } gh) TownUi.SetPicture(Ui.Find("heroicon0"), TownUi.HeroFace(gh, large: false));
        Ui.Find("heroicon1")!.Visible = Owner.VisitingHero() is not null;
        if (Owner.VisitingHero() is { } vh) TownUi.SetPicture(Ui.Find("heroicon1"), TownUi.HeroFace(vh, large: false));
        for (int row = 0; row < 2; row++)
        {
            var army = RowArmy(row);
            Ui.Find($"armyholder{row}")!.Visible = army != null;
            for (int i = 0; i < Army.Slots; i++)
            {
                var s = army?[i];
                var icon = Ui.Find($"armyholder{row}/creature{i}/icon");
                TownUi.SetPicture(icon, s is { Count: > 0 } ? TownUi.CreatureIcon(s.Creature) : null);
                (icon?.FindChild("NumberOfCreatures", false, false) as UiNode)?.SetValue("NumberOfCreatures", s is { Count: > 0 } ? s.Count.ToString() : "");
                Ui.SetVisible($"armyholder{row}/creature{i}/selection", false);
                if (Ui.Find($"armyholder{row}/creature{i}/button") is { } b)
                    b.Enabled = s is { Count: > 0 } && Enumerable.Range(1, 2).Any(u =>
                        game.UpgradeQuote(s.Creature, u, s.Count, false).Target is { } t && Allowed(s.Creature, t));
            }
        }
    }

    void OpenChoice(int row, int slot)
    {
        _choose?.QueueFree();
        _choose = null;
        var s = RowArmy(row)?[slot];
        if (s is not { Count: > 0 } || Ui.Find($"armyholder{row}/creature{slot}") is not { } cell) return;
        var hero = RowHero(row);
        _choose = UpgradeChooser.Open(this, cell, Owner.Session.Game, Me, s.Creature, s.Count, hillFort: false, t => Allowed(s.Creature, t), upgrade =>
        {
            _choose = null;
            Apply(new UpgradeCreatureCmd(hero?.Id ?? -1, Owner.Town.Object, slot, upgrade) { Player = Me.Index });
        });
    }

    public override void _GuiInput(InputEvent e)
    {
        if (_choose != null && e is InputEventMouseButton { Pressed: true }) { _choose.QueueFree(); _choose = null; }
    }
}
