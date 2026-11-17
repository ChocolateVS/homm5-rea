using Godot;
using H5Reborn.Interaction;

namespace H5Reborn.Hero;

/// <summary>
/// Hero screen keyboard (input_a2.cfg "hero_screen" section: A army/attributes, I inventory,
/// V abilities, H biography — the key of the page shown closes the screen —, W / S previous / next page;
/// global close_window = Esc) and closing rules (exe hero screen observers 006c4b40). The stock screen is
/// closed with Esc or its close button — a right click is the original "show description"
/// gesture, never a close (manual p.8 Tooltips: "right-click on all interactive elements …
/// to call a detailed description").
/// </summary>
public partial class HeroScreen
{
    /// <summary>Shows the given page (Army, Inventory, Skills, Abilities, Biography) on every panel.</summary>
    public void ShowPageByName(string page)
    {
        _page = page;
        foreach (var p in _panels) ShowPage(p, page);
    }

    /// <summary>Returns true when the event was consumed.</summary>
    bool HandleHeroInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return false;
        var actions = KeyBindings.Actions("hero_screen", k);
        if (actions.Count == 0) actions = KeyBindings.Actions(KeyBindings.Global, k);
        foreach (var a in actions)
        {
            switch (a)
            {
                case "hero_attributes": PageKey("Army"); break;
                case "hero_equipment": PageKey("Inventory"); break;
                case "hero_skills": PageKey("Skills"); break;
                case "hero_abilities": PageKey("Abilities"); break;
                case "hero_biography": PageKey("Biography"); break;
                // input_hero_page_up / _down (W / S, 006c3160 / 006c31c0): the previous / next bookmark, wrapping 1..5.
                case "input_hero_page_up": ShowPageByName(Pages[(Array.IndexOf(Pages, _page) + Pages.Length - 1) % Pages.Length]); break;
                case "input_hero_page_down": ShowPageByName(Pages[(Array.IndexOf(Pages, _page) + 1) % Pages.Length]); break;
                case "close_window" or "esc_pressed": CloseHeroScreen(); break;
                default: continue;
            }
            GetViewport().SetInputAsHandled();
            return true;
        }
        return false;
    }

    /// <summary>
    /// A page key (006c2ec0 / 006c2f50 / 006c2fe0 / 006c30c0 / 006c30d0 → 006c1f70(page, 1)): on the page already shown it
    /// closes the screen (close_screen 006d0960), otherwise it shows that page.
    /// </summary>
    void PageKey(string page)
    {
        if (page == _page) CloseHeroScreen();
        else ShowPageByName(page);
    }

    void CloseHeroScreen()
    {
        if (!IsInsideTree() || IsQueuedForDeletion()) return;
        QueueFree();
        if (_preview) _town?.Refresh();
        else if (_town != null) _town.SubClosed();
        else { _adv.Hud.Refresh(); foreach (var x in _adv.Session.S.Heroes) _adv.World.SyncHero(x); }
    }
}
