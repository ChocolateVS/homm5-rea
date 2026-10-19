using Godot;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5Reborn.Interaction;
using H5Reborn.Ui;
using HeroState = H5.Adventure.State.HeroState;

namespace H5Reborn.Adventure;

/// <summary>
/// Adventure-map input, audited against the original (docs/rules/AUDIT_INPUT.md):
/// keys come from the install's input_a2.cfg "adventure_screen" section and are dispatched
/// by action name to the same handlers the HUD buttons use; mouse clicks follow the stock
/// manual's control table (p.46) and H5_Game.exe's adventure observers (SFLB_AddObservers
/// @0059a210: win_left_button_dblclk → FUN_00597640, win_left_button_up → FUN_00597360).
/// </summary>
public partial class AdventureScreen
{
    const string AdvSection = "adventure_screen";

    /// <summary>Heroes put to sleep with Z (sleep_wake_hero, exe @0058c3a0). Sleeping heroes are skipped by Next Hero.</summary>
    readonly HashSet<int> _sleepingHeroes = new();

    public bool IsHeroSleeping(int heroId) => _sleepingHeroes.Contains(heroId);

    bool _advInputRegistered;

    public override void _EnterTree()
    {
        // Register the input_a2.cfg actions once the HUD exists (it is built in _Ready).
        Callable.From(RegisterAdvInput).CallDeferred();
        InputTestDriver.AttachIfRequested(this);
    }

    /// <summary>Registers the input_a2.cfg action names this screen answers to (idempotent).</summary>
    void RegisterAdvInput()
    {
        if (_advInputRegistered || Hud == null) return;
        _advInputRegistered = true;
        var bus = Hud.Screen.Bus;
        // MB_Next_Hero skips sleeping heroes (sleep_wake_hero): replaces the HUD's handler.
        bus.On("MB_Next_Hero", NextAwakeHero);
        bus.On("input_esc_menu", OnEscape);
        bus.On("input_game_options", OpenGameOptions);
        for (int i = 1; i <= 8; i++)
        {
            int idx = i - 1;
            bus.On($"input_select_hero_{i:00}", () => { if (!InputLocked) SelectHeroAt(idx); });
            bus.On($"input_select_town_{i:00}", () => { if (!InputLocked) SelectTownAt(idx); });
        }
        bus.On("hero_attributes", () => OpenSelectedHeroPage("Army"));
        bus.On("hero_equipment", () => OpenSelectedHeroPage("Inventory"));
        bus.On("hero_skills", () => OpenSelectedHeroPage("Skills"));
        bus.On("hero_abilities", () => OpenSelectedHeroPage("Abilities"));
        bus.On("hero_biography", () => OpenSelectedHeroPage("Biography"));
        bus.On("input_adv_show_objectives", ShowScenarioInfo);
        bus.On("input_adv_show_objectives_log", ShowScenarioInfo);
        bus.On("adv_show_town", ShowSelectedHeroTown);
        bus.On("sleep_wake_hero", ToggleSleepSelected);
        Hud.RegisterInputAudit();
        AddChild(new Interaction.AdvTooltip(this));
    }

    /// <summary>
    /// Called first from <see cref="_UnhandledInput"/>. Returns true when the event was consumed.
    /// </summary>
    bool HandleAdvInput(InputEvent e)
    {
        RegisterAdvInput();
        if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            // Dialogs and sub-screens own the keyboard while open (their own sections).
            if (_modal > 0) return false;
            var actions = KeyBindings.Actions(AdvSection, k);
            if (actions.Count == 0) actions = KeyBindings.Actions(KeyBindings.Global, k);
            bool any = false;
            foreach (var a in actions)
            {
                // Save keys are owned by Save.SaveHotkeys; continuous camera keys by AdvCamera.
                if (a is "save" or "load" or "quicksave" or "quickload" or "set_camera_default_pos"
                    or "pause" or "screenshot" or "toggle_fullscreen" or "ctrl" or "shift" or "alt") continue;
                // Network chat keys only exist in network games (no chat line on this screen yet).
                if (a.StartsWith("enter_chat") || a.StartsWith("chat_to_")) continue;
                // Map-editor cheats bound in the stock file (Ctrl+Shift+T / Ctrl+D) stay disabled, as in retail.
                if (a is "adventure_teleport" or "adventure_remove_object") continue;
                // Global-section confirmations mean nothing on the map itself.
                if (a is "esc_pressed" or "enter_pressed" or "close_window" or "confirm_action" or "jd_button_0") continue;
                Hud.Screen.Bus.Dispatch(new H5.Core.Ui.UiCommand("GameMessage", a), null);
                any = true;
            }
            if (any) GetViewport().SetInputAsHandled();
            return true;
        }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb && !mb.CtrlPressed)
        {
            if (TileAt(mb.Position) is { } tile) AdvClick(tile, mb.DoubleClick, mb.Position);
            return true;
        }
        return false;
    }

    /// <summary>Esc: cancel an aimed spell, then clear the plotted route, otherwise open the game menu (manual p.46).</summary>
    void OnEscape()
    {
        if (_modal > 0) return;
        if (_pendingSpell != null) { _pendingSpell = null; return; }
        if (_plotted != null) { _plotted = null; World.ClearPath(); return; }
        ShowEscMenu();
    }

    void OpenGameOptions()
    {
        if (_modal > 0) return;
        Options.GameOptionsScreen? options = null;
        options = new Options.GameOptionsScreen(() => options!.QueueFree()) { MouseFilter = MouseFilterEnum.Stop };
        _dialogLayer.AddChild(options);
    }

    void NextAwakeHero()
    {
        if (InputLocked) return;
        var heroes = MyHeroes();
        if (heroes.Count == 0) return;
        int i = heroes.FindIndex(h => h.Id == SelectedHeroId);
        // The next awake hero that can still move; falls back to the next awake hero.
        for (int pass = 0; pass < 2; pass++)
            for (int k = 1; k <= heroes.Count; k++)
            {
                var h = heroes[(i + k + heroes.Count) % heroes.Count];
                if (_sleepingHeroes.Contains(h.Id)) continue;
                if (pass == 1 || h.MovePoints > 0) { Select(h); return; }
            }
    }

    void ToggleSleepSelected()
    {
        if (InputLocked || Selected is not { } h) return;
        if (!_sleepingHeroes.Add(h.Id)) _sleepingHeroes.Remove(h.Id);
        Hud.Refresh();
    }

    void OpenSelectedHeroPage(string page)
    {
        if (InputLocked || Selected is not { } h) return;
        Hero.HeroScreen.Open(this, h).ShowPageByName(page);
    }

    /// <summary>adv_show_town (exe @0058c320 → @00604990): opens the town the selected hero is visiting.</summary>
    void ShowSelectedHeroTown()
    {
        if (InputLocked || Selected is not { } h) return;
        var town = Session.S.Towns.FirstOrDefault(t => t.Owner == Session.Human.Index && t.Object == h.InTown);
        if (town != null) Town.TownScreen.Open(this, town);
    }

    /// <summary>
    /// Left click on the map (manual p.46 "Mission map" table):
    /// <list type="bullet">
    /// <item>own hero, double click → that hero's management screen (manual p.17);</item>
    /// <item>own hero, single click with no hero selected → select it; with another own hero selected →
    /// set it as destination to approach and open the exchange screen (MeetHeroes cursor);</item>
    /// <item>own town, double click → town screen (manual p.20);</item>
    /// <item>anything else: first click sets the destination (route shown), clicking it again or
    /// double clicking moves the hero.</item>
    /// </list>
    /// </summary>
    void AdvClick(Tile tile, bool doubleClick, Vector2 screen)
    {
        if (InputLocked) return;
        var s = Session.S;
        if (_pendingSpell is { } aimed)
        {
            _pendingSpell = null;
            if (Selected is { } caster) CastAdventureSpell(caster, aimed, tile);
            return;
        }
        if (s.Heroes.FirstOrDefault(h => h.Alive && !h.Garrisoned && h.Pos == tile && h.Owner == Session.Human.Index) is { } mine)
        {
            if (doubleClick)
            {
                if (mine.Id != SelectedHeroId) Select(mine);
                Hero.HeroScreen.Open(this, mine);
                return;
            }
            if (Selected is not { } current) { Select(mine); return; }
            if (mine.Id == current.Id) return;
            if (_plotted == tile) { SendMove(current, tile); return; }
            Plot(current, tile);
            return;
        }
        if (Session.Game.TownAt(tile) is { } town && town.Owner == Session.Human.Index && Selected?.Pos != tile)
        {
            if (doubleClick) { World.ClearPath(); _plotted = null; Town.TownScreen.Open(this, town); return; }
            if (Selected == null) { World.Camera.CenterOn(town.Pos); return; }
        }
        if (Selected is not { } hero) return;
        ClickTarget(hero, tile, screen);
    }
}
