using Godot;
using H5.Adventure.Engine;
using H5Reborn.Interaction;

namespace H5Reborn.Town;

/// <summary>
/// Town screen keyboard: input_a2.cfg "town_screen" section (B hall, R fort, G mage guild,
/// T tavern, M market, Q special, X blacksmith, Y artifacts, Z shipyard, F town info,
/// Esc/Enter/E close, N/P next/previous town, U upgrade creatures, Space swap heroes,
/// A/I/S/V/H hero pages, C spellbook). Action names are the ones H5_Game.exe's town screen
/// observes (SFLB_AddObservers @0061bcf0), dispatched to the HUD bus like the stock buttons.
/// Buildings open on DOUBLE click: the exe's click_pointer handler (@00613130) does nothing and
/// dblclick_pointer (@00619a30) opens the building (also manual p.20).
/// </summary>
public partial class TownScreen
{
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        // Sub-screens and dialogs handle their own sections.
        if (_subLayer.GetChildCount() > 0 || _dialogLayer.GetChildCount() > 0) return;
        var actions = KeyBindings.Actions("town_screen", k);
        foreach (var a in actions)
        {
            switch (a)
            {
                case "save" or "load" or "quicksave" or "quickload" or "close_flyby" or "set_camera_default_pos" or "shift":
                    continue;
                case "hero_attributes": OpenHeroPage("Army"); break;
                case "hero_equipment": OpenHeroPage("Inventory"); break;
                case "hero_skills": OpenHeroPage("Skills"); break;
                case "hero_abilities": OpenHeroPage("Abilities"); break;
                case "hero_biography": OpenHeroPage("Biography"); break;
                case "input_swap_heroes": SwapHeroes(); break;
                case "input_upgrade_creatures": _hud.Bus.Send("upgrade_creatures"); break;
                case "input_enter_special": _hud.Bus.Send("enter_special"); break;
                default: _hud.Bus.Send(a); break;
            }
            GetViewport().SetInputAsHandled();
        }
    }

    void OpenHeroPage(string page)
    {
        var h = VisitingHero() ?? GarrisonHero();
        if (h == null) return;
        OpenHero(h);
        if (_subLayer.GetChildCount() > 0 && _subLayer.GetChild(_subLayer.GetChildCount() - 1) is Hero.HeroScreen hs)
            Callable.From(() => hs.ShowPageByName(page)).CallDeferred();
    }

    /// <summary>Space (input_swap_heroes): moves the visiting hero into the garrison or the garrison hero out.</summary>
    void SwapHeroes()
    {
        var visiting = VisitingHero();
        var garrison = GarrisonHero();
        if (visiting != null && garrison == null) Apply(new GarrisonHeroCmd(Town.Object, visiting.Id, true) { Player = Town.Owner });
        else if (garrison != null && visiting == null) Apply(new GarrisonHeroCmd(Town.Object, garrison.Id, false) { Player = Town.Owner });
        // Both present: the engine has no single swap command yet (AUDIT_INPUT.md GAP).
        Refresh();
    }
}

/// <summary>
/// Town sub-screens close with their own input_a2.cfg section keys (fort_screen R/Esc,
/// mage_guild_screen G/Esc, tavern_screen Esc/T/E, market_screen M, buy_war_machine_screen
/// Esc/Enter/E, town_build) — never with a right click (that shows descriptions in the original).
/// </summary>
public abstract partial class TownSubScreen
{
    string Section => this switch
    {
        FortScreen => "fort_screen",
        MageGuildScreen => "mage_guild_screen",
        TavernScreen => "tavern_screen",
        MarketScreen => "market_screen",
        BlacksmithScreen => "buy_war_machine_screen",
        TownBuildScreen => "town_build",
        _ => "",
    };

    /// <summary>Returns true when the key closed the screen or was otherwise consumed.</summary>
    protected bool HandleSubInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return false;
        var actions = KeyBindings.Actions(Section, k);
        if (actions.Count == 0) actions = KeyBindings.Actions(KeyBindings.Global, k);
        foreach (var a in actions)
        {
            if (a is "close" or "close_window" or "esc_pressed" || a.EndsWith("_close"))
            {
                Close();
                GetViewport().SetInputAsHandled();
                return true;
            }
            // jd_button_N: jog-dial buttons of the screen; forwarded by name to the screen's bus.
            if (a.StartsWith("jd_button_") || a.StartsWith("input_page_"))
            {
                Ui.Bus.Send(a);
                GetViewport().SetInputAsHandled();
                return true;
            }
        }
        return false;
    }
}
