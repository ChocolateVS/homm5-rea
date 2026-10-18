using Godot;
using H5Reborn.Interaction;

namespace H5Reborn.Hero;

/// <summary>
/// Hero screen keyboard (input_a2.cfg "hero_screen" section: A army/attributes, I inventory,
/// V abilities, H biography; global close_window = Esc) and closing rules. The stock screen is
/// closed with Esc or its close button — a right click is the original "show description"
/// gesture, never a close (manual p.8 Tooltips: "right-click on all interactive elements …
/// to call a detailed description").
/// </summary>
public partial class HeroScreen
{
    /// <summary>Shows the given page (Army, Inventory, Skills, Abilities, Biography) on every panel.</summary>
    public void ShowPageByName(string page)
    {
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
                case "hero_attributes": ShowPageByName("Army"); break;
                case "hero_equipment": ShowPageByName("Inventory"); break;
                case "hero_skills": ShowPageByName("Skills"); break;
                case "hero_abilities": ShowPageByName("Abilities"); break;
                case "hero_biography": ShowPageByName("Biography"); break;
                case "close_window" or "esc_pressed": CloseHeroScreen(); break;
                default: continue;
            }
            GetViewport().SetInputAsHandled();
            return true;
        }
        return false;
    }

    void CloseHeroScreen()
    {
        if (!IsInsideTree() || IsQueuedForDeletion()) return;
        QueueFree();
        if (_town != null) _town.SubClosed();
        else { _adv.Hud.Refresh(); foreach (var x in _adv.Session.S.Heroes) _adv.World.SyncHero(x); }
    }
}
