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
    public override void _EnterTree()
    {
        // After _Ready's WireHud: the exe's click_pointer handler (@00613130) does nothing; dblclick_pointer
        // (@00619a30) opens the hero screen when the pointer is on the visiting (List_0 top) or garrison
        // (List_0 bottom) hero portrait (FUN_00619320). Stack double clicks open creature info (ArmySlots).
        Callable.From(() =>
        {
            _hud.Bus.On("click_pointer", () => { });
            _hud.Bus.On("dblclick_pointer", (_, src) =>
            {
                var list0 = _hud.Find("ListBase/List_0");
                if (src == null || list0 == null || !list0.IsAncestorOf(src)) return;
                var h = src.Name == "Hero" ? VisitingHero() : src.Name == "Town" ? GarrisonHero() : null;
                if (h != null) OpenHero(h);
            });
            _vpc.GuiInput += OnBuildingTooltipInput;
        }).CallDeferred();
    }

    Ui.UiNode? _buildingTip;

    /// <summary>
    /// Right button held over a building shows its tooltip (UIGameRoot ID_TOWN_HALL_TOOLTIP =
    /// UI/Tooltips/TownHall/BuildingToolTip, text UI/Tooltips/TownHall/nameDesc.txt: name + description).
    /// </summary>
    void OnBuildingTooltipInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Right } rb) return;
        _buildingTip?.QueueFree();
        _buildingTip = null;
        if (!rb.Pressed || _hoverBuilding == null || ViewFor(_hoverBuilding) is not { } view) return;
        ShowBuildingTooltip(view, rb.GlobalPosition);
    }

    void ShowBuildingTooltip(BuildingView view, Vector2 at)
    {
        // Name and description are original markup texts: inserted as is, height measured with the stock fonts.
        var markup = (Ui.UiResources.Instance.Text("UI/Tooltips/TownHall/nameDesc.txt") ?? "")
            .Replace("<value=name>", view.Name).Replace("<value=desc>", TooltipDescription(view));
        var text = Interaction.AdvTooltip.Text(markup, 200);
        _buildingTip = Interaction.AdvTooltip.Frame("UI/Tooltips/TownHall/BuildingToolTip.(WindowGathering).xdb", text);
        AddChild(_buildingTip);
        var vp = GetViewportRect().Size;
        var p = at + new Vector2(16, 16);
        if (p.X + _buildingTip.Size.X > vp.X) p.X = at.X - _buildingTip.Size.X - 4;
        if (p.Y + _buildingTip.Size.Y > vp.Y) p.Y = at.Y - _buildingTip.Size.Y - 4;
        _buildingTip.Position = p;
    }

    /// <summary>
    /// The description part of 0061a5d0 (tooltip, param_3 ≠ 0): the built level's description (00cd4070 = level
    /// record +0x50; the name is 00cd3fc0 = +0x44), then by the building's town ability (town vf 0x48):
    /// 0x1c (Dungeon Ritual Pit) appends ritual_pit_info_witches / _minotaurs (UI/TownScreen texts, &lt;value=val&gt;)
    /// for the town's vf 0x94 / 0x98 bonuses when non-zero; otherwise building type 5 (Blacksmith) takes 00664aa0's
    /// text: the description with &lt;value=warmachine&gt; = the name of the town type's machine (town vf 0xe8 =
    /// 00cceac0: type data +0xc4, 0 → 1; SWarMachine +0x9c visual, +0x44 name). Name, description and an empty third
    /// string go to the 0x48 tooltip object (00585d40). GAP: abilities 0xc / 0xd (mystic pond) and 0x1d (trade guild)
    /// use the 0x74 resource tooltip (0061f2b0, UI/Tooltips/TownHall/TradeGuild); the shipyard (type 3) appends
    /// shipyard_deserted after a test (00e95610-side interface vf 0xc) that is not identified.
    /// </summary>
    string TooltipDescription(BuildingView view)
    {
        string desc = view.Description;
        if (view.Def.Type == H5.Adventure.Engine.Adventure.RitualPit && Town.Type == "TOWN_DUNGEON")
        {
            var (witches, minotaurs) = H5.Adventure.Engine.Adventure.RitualPitBonus(Town.SacrificedHitPoints);
            // UI/TownScreen/texts.(WindowRelatedTexts): ritual_pit_info_witches / _minotaurs.
            if (witches != 0) desc += TownUi.Text("UI/TownScreen/ritual_pit_info_witches.txt", "").Replace("<value=val>", witches.ToString());
            if (minotaurs != 0) desc += TownUi.Text("UI/TownScreen/ritual_pit_info_minotaurs.txt", "").Replace("<value=val>", minotaurs.ToString());
        }
        else if (view.Def.Type == "TB_BLACKSMITH")
        {
            var machine = Session.Db.NativeWarMachine.GetValueOrDefault(Town.Type) is { Length: > 0 } m ? m : "WAR_MACHINE_BALLISTA";
            desc = desc.Replace("<value=warmachine>", Combat.CombatLogText.WarMachineName(machine) ?? "");
        }
        return desc;
    }

    /// <summary>Dev (`--town-tip=TB_…`): the tooltip of that building's built level, as on a right click at the screen centre.</summary>
    public void DevBuildingTooltip(string type)
    {
        // Dev: a building the map does not allow is granted at the type's maximum level.
        if (Town.Level(type) <= 0 && Session.Db.MaxLevel(Town.Type, type) is > 0 and var max) Town.Built[type] = max;
        var view = TownData.Defs(Session.Db, Town).Where(d => d.Type == type && Town.Level(type) >= d.Level)
            .OrderByDescending(d => d.Level).Select(TownData.View).FirstOrDefault();
        if (view == null) { GD.PushWarning($"[H5] --town-tip: {type} not built"); return; }
        ShowBuildingTooltip(view, GetViewportRect().Size / 2);
    }

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
                case "save" or "load" or "quicksave" or "quickload" or "close_flyby" or "shift":
                    continue;
                // HOME: the default camera pose (00616300 → 006153e0).
                case "set_camera_default_pos": _scene?.DefaultPos(); break;
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

    /// <summary>
    /// Space (input_swap_heroes 00619790): one town hero command (00435cc0 / 004f58e0) — the visiting hero into the
    /// garrison, a garrison hero out as the visitor; when it fails, a message box with the town screen texts
    /// T_NOT_ENOUGH_ROOM and T_MSGBOX_TITLE.
    /// </summary>
    void SwapHeroes()
    {
        var visiting = VisitingHero();
        var garrison = GarrisonHero();
        bool ok = visiting != null ? Apply(new GarrisonHeroCmd(Town.Object, visiting.Id, true) { Player = Town.Owner })
            : garrison == null || Apply(new GarrisonHeroCmd(Town.Object, garrison.Id, false) { Player = Town.Owner });
        if (!ok)
            Notice(TownUi.Text("UI/TownScreen/townmsgboxtitle.txt", "Town"), TownUi.Text("UI/TownScreen/nonenoughroom.txt", ""));
        Refresh();
    }
}

/// <summary>
/// Town sub-screens close with their own input_a2.cfg section keys (fort_screen R/Esc,
/// mage_guild_screen G/Esc, tavern_screen Esc/T/E, market_screen M, buy_war_machine_screen
/// Esc/Enter/E, town_build, hire_screen) — never with a right click (that shows descriptions in the original).
/// </summary>
public abstract partial class TownSubScreen
{
    string Section => this switch
    {
        TownHireScreen => "hire_screen",
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
        // Close actions win over jog-dial ones (Esc is bound to both jd_button_0 and close_window globally).
        if (actions.Any(a => a is "close" or "close_window" or "esc_pressed" || a.EndsWith("_close")))
        {
            Close();
            GetViewport().SetInputAsHandled();
            return true;
        }
        foreach (var a in actions)
        {
            // jd_button_N: the screen's jog-dial buttons, forwarded by name; jd_button_0 is the dial's
            // central (close/OK) button, so it closes when the screen does not handle it itself.
            if (a.StartsWith("jd_button_") || a.StartsWith("input_page_"))
            {
                bool handled = Ui.Bus.Dispatch(new H5.Core.Ui.UiCommand("Code", a), null);
                if (!handled && a == "jd_button_0") Close();
                GetViewport().SetInputAsHandled();
                return true;
            }
        }
        return false;
    }
}
