using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// Hero/town list double clicks. The stock list buttons (UI/AdventureScreen-heavy/TownHeroPanel
/// Hero/Town WindowMSButtonShared) send hero_click / town_click on click and hero_dbl_click /
/// town_dbl_click on double click (commandsOnLDblKlick → HeroDblClick/TownDblClick reactions).
/// H5_Game.exe (@005ec7e0) handles both through the same select-command with a double-click flag;
/// per the stock manual a double click on a hero portrait opens the hero screen (p.17) and on a
/// town icon the town screen (p.20). The single click still selects and focuses the camera.
/// </summary>
public partial class AdvHud
{
    bool _inputAuditRegistered;

    /// <summary>When false, the mana bars under the hero portraits are hidden (show_heroes_mana, key M).</summary>
    public bool ShowHeroesMana { get; private set; } = true;

    internal void RegisterInputAudit()
    {
        if (_inputAuditRegistered) return;
        _inputAuditRegistered = true;
        var bus = Screen.Bus;
        bus.On("hero_dbl_click", (cmd, src) =>
        {
            if (ColumnOf(src) is not int c) return;
            var heroes = _screen.MyHeroes();
            int i = _heroScroll + c;
            if (i < 0 || i >= heroes.Count || _screen.DialogLayer.GetChildCount() > 0) return;
            _screen.SelectHeroAt(i);
            Hero.HeroScreen.Open(_screen, heroes[i]);
        });
        bus.On("town_dbl_click", (cmd, src) =>
        {
            if (ColumnOf(src) is not int c) return;
            var towns = _screen.MyTowns();
            int i = _townScroll + c;
            if (i < 0 || i >= towns.Count || _screen.DialogLayer.GetChildCount() > 0) return;
            _screen.SelectTownAt(i);
            Town.TownScreen.Open(_screen, towns[i]);
        });
        bus.On("show_heroes_mana", () => { ShowHeroesMana = !ShowHeroesMana; Refresh(); });
    }

    /// <summary>Applies the input toggles after a refresh (mana bar visibility).</summary>
    void ApplyInputAuditToggles()
    {
        var heroes = _screen.MyHeroes();
        for (int i = 0; i < Columns; i++)
        {
            var col = Column(i);
            if (!ShowHeroesMana && col?.FindChild("ManaPanel", true, false) is Godot.Control c) c.Visible = false;
            // Zzz (HeroSleeping) marks heroes put to sleep with Z / sleep_wake_hero.
            int h = _heroScroll + i;
            if (col?.FindChild("HeroSleeping", true, false) is Godot.Control z)
                z.Visible = h < heroes.Count && _screen.IsHeroSleeping(heroes[h].Id);
        }
    }
}
