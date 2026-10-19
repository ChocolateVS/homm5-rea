using Godot;
using H5.Combat;
using H5.Core.Ui;
using H5.Core.Xdb;
using H5Reborn.Ui;

namespace H5Reborn.Combat;

/// <summary>
/// The stock combat HUD (UI/CombatScreen-Heavy): jog dial wired by the data's
/// message names, the ATB bar with original portraits in predicted turn order,
/// the prediction line and combat log, the tactics placement bar, plus unit info,
/// spell list and results panels drawn with original fonts and textures.
/// </summary>
public partial class CombatHud : Control
{
    readonly CombatScreen _screen;
    public UiScreen Screen { get; }
    public CombatLog Log { get; }
    /// <summary>Luck roll seen just before the next damage event (for the log wording).</summary>
    public int PendingLuck;

    readonly List<AtbIcon> _icons = new();
    Label? _desync;
    Control? _spellPanel, _infoPanel, _resultsPanel;
    BattlePhase _phase = BattlePhase.Tactics;
    bool _logVisible;
    int _activeActor = int.MinValue;

    const int IconCount = 9, IconStep = 61;

    static readonly string[] CombatButtons =
        { "OnJog-Wait", "OnJog-Defend", "OnJog-Spellbook", "OnJog-AutoCombat", "OnJog-Flee", "OnJog-Menu" };
    static readonly string[] TacticsButtons = { "OnJog-StartCombat", "OnJog-NextCreature" };

    public CombatHud(CombatScreen screen)
    {
        _screen = screen;
        Name = "Hud";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;

        Screen = new UiScreen("UI/CombatScreen-Heavy.(WindowScreen).xdb");
        AddChild(Screen);
        foreach (var hidden in new[] { "RunesBar", "RunesBarSnapBottomPlace", "dndicon", "cursor_prediction", "Face", "TacticPhaseMarker", "OnJog-AtbToggle", "LogWindow", "OnJog-Special" })
            Screen.SetVisible(hidden, false);

        Log = new CombatLog(Screen);
        var bus = Screen.Bus;
        bus.On("Combat_Defend", _screen.CmdDefend);
        bus.On("Combat_SpellBook", ToggleSpells);
        bus.On("Combat_Retreat", _screen.CmdSurrender);
        bus.On("Combat_AutoCombat", _screen.CmdAutoCombat);
        bus.On("Combat_OptionsMenu", ShowOptions);
        bus.On("Combat_SpecialAbility", _screen.CmdSpecial);
        bus.On("autoplace_army", _screen.CmdAutoplace);   // OnJog-StartCombat: "Auto place creatures"
        bus.On("autoplace_army_force", _screen.CmdAutoplace);
        bus.On("confirm_placement", _screen.CmdEndTactics);
        bus.On("switch_atb_log", ToggleLog);
        // ATB scroll buttons (ATBBar/ScrollLeft|Right → UISRunReaction atb_scroll_left|right).
        bus.On("atb_scroll_left", () => AtbScrollMessage(true, false));
        bus.On("atb_scroll_right", () => AtbScrollMessage(false, true));
        bus.On("atb_scroll_left_reverse", () => AtbScrollMessage(false, false));
        bus.On("atb_scroll_right_reverse", () => AtbScrollMessage(false, false));
        UiMessageBus.Unhandled += OnUnhandled;

        BuildAtbIcons();
    }

    public override void _ExitTree() => UiMessageBus.Unhandled -= OnUnhandled;

    int _retreatCode = -1;
    bool? _autoState;

    double _devClock;
    int _devHoldStage;

    public override void _Process(double delta)
    {
        UpdateAtbScroll(delta);
        if (CombatLauncher.AtbHold is { } hold && _devHoldStage < 2)
        {
            _devClock += delta;
            if (Screen.Find(hold.Button) is { } btn)
            {
                var center = btn.GetGlobalRect().GetCenter();
                if (_devHoldStage == 0 && _devClock >= hold.Start)
                {
                    _devHoldStage = 1;
                    Input.ParseInputEvent(new InputEventMouseMotion { Position = center, GlobalPosition = center });
                    Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = center, GlobalPosition = center });
                    GD.Print($"[H5] atb-hold press {hold.Button} index={_atbScroll} max={_atbMaxScroll}");
                }
                else if (_devHoldStage == 1 && _devClock >= hold.Start + hold.Hold)
                {
                    _devHoldStage = 2;
                    Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = center, GlobalPosition = center });
                    GD.Print($"[H5] atb-hold release {hold.Button} index={_atbScroll} pos={_atbPos}");
                }
            }
        }
        // Retreat button availability and tooltip (exe FUN_00540090 on FUN_0053feb0's code).
        int code = RetreatDialog.RefusalCode(_screen, _screen.IsPlaying);
        if (code != _retreatCode && Screen.Find("OnJog-Flee") is { } flee)
        {
            _retreatCode = code;
            RetreatDialog.ApplyButton(flee, code);
        }
        // The auto-combat jog button is a two-state toggle (Combat_AutoCombat in both states):
        // its state shows whether auto-combat is running.
        bool auto = CombatScreen.Autoplay;
        if (auto != _autoState && Screen.Find("OnJog-AutoCombat") is { } ab)
        {
            _autoState = auto;
            ab.SetState(auto ? 1 : 0);
        }
    }

    void OnUnhandled(UiCommand cmd)
    {
        // OnJog-Wait sends "input_Combat_Wait0" through UISSendGameMessage.
        if (cmd.Argument.Contains("Combat_Wait", StringComparison.OrdinalIgnoreCase)) _screen.CmdWait();
    }

    // ---------------------------------------------------------------- phase / layout

    public void OnPhase(BattlePhase phase)
    {
        _phase = phase;
        Refresh();
    }

    public void Refresh()
    {
        var s = _screen.Session.Battle.State;
        _phase = s.Phase;
        bool tactics = _phase == BattlePhase.Tactics;
        foreach (var b in CombatButtons) Screen.SetVisible(b, !tactics);
        foreach (var b in TacticsButtons) Screen.SetVisible(b, tactics);
        Screen.SetVisible("PlacementBar", tactics);
        Screen.SetVisible("ATBBar", !tactics && !_logVisible);
        Screen.SetVisible("LogWindow", !tactics && _logVisible);
        bool special = !tactics && _screen.Session.Battle.GetLegalActions() is { Abilities.Count: > 0 } la
                       && _screen.Session.LocalSides.Contains(la.Side);
        Screen.SetVisible("OnJog-Special", special);
        Screen.SetVisible("OnJog-Spellbook", !tactics && !special);
        if (tactics) FillPlacementBar();
        UpdateAtb();
    }

    internal void ToggleLogPublic() => ToggleLog();
    internal void ToggleSpellsPublic() => ToggleSpells();

    UiScreen? _escMenu;

    /// <summary>
    /// The stock in-game menu (UI/EscMenu, opened by the jog dial's menu button and
    /// ESC/F4/F10): Return to game, Surrender, Main menu, Quit to Windows, Options.
    /// Save/Load/Restart mission have no meaning in a duel and are disabled.
    /// </summary>
    public void ShowOptions()
    {
        if (_escMenu != null) { CloseOptions(); return; }
        var menu = new UiScreen("UI/EscMenu.(WindowScreen).xdb") { MouseFilter = MouseFilterEnum.Stop };
        foreach (var name in new[] { "SaveBtn", "LoadBtn", "RestartMissionBtn" })
            if (menu.Find(name) is { } b) b.Enabled = false;
        menu.Bus.On("return_to_game", CloseOptions);
        menu.Bus.On("surrender", () => { CloseOptions(); _screen.CmdSurrender(); });
        menu.Bus.On("main_menu", () => { CloseOptions(); _screen.ReturnToMenu(); });
        menu.Bus.On("exit_to_windows", () => GetTree().Quit());
        menu.Bus.On("options", () =>
        {
            // The stock data-driven Options screen (UI/uiconsts GameOptions), as on the adventure map.
            CloseOptions();
            Options.GameOptionsScreen? options = null;
            options = new Options.GameOptionsScreen(() => options!.QueueFree()) { MouseFilter = MouseFilterEnum.Stop };
            AddChild(options);
        });
        _escMenu = menu;
        AddChild(menu);
    }

    /// <summary>esc_menu bindsection: close_menu on ESC or E while the menu is open.</summary>
    internal bool EscMenuOpen => _escMenu != null;
    internal void CloseEscMenu() => CloseOptions();

    void CloseOptions()
    {
        _escMenu?.QueueFree();
        _escMenu = null;
    }

    void ToggleLog()
    {
        _logVisible = !_logVisible;
        Refresh();
    }

    public bool IsOverHud(Vector2 mouse)
    {
        foreach (var name in new[] { "Jogdial", "ATBBar", "PlacementBar", "PredictionBox", "LogWindow" })
            if (Screen.Find(name) is { } n && n.IsVisibleInTree() && n.GetGlobalRect().HasPoint(mouse)) return true;
        foreach (var p in new[] { _spellPanel, _infoPanel, _resultsPanel })
            if (p != null && IsInstanceValid(p) && p.IsVisibleInTree() && p.GetGlobalRect().HasPoint(mouse)) return true;
        return false;
    }

    /// <summary>
    /// Hover prediction (exe 00545ae0): the PredictionBox line in PredictionFormat
    /// (UI/CombatArena-Heavy/PredictionFormat.txt, "&lt;body_bright&gt;") — or the last log line
    /// when nothing is predicted — and the cursor_prediction label next to the cursor.
    /// </summary>
    public void SetPrediction(CombatPredictions.Prediction? p)
    {
        string format = UiResources.Instance.Text("UI/CombatArena-Heavy/PredictionFormat.txt")?.Trim() ?? "<body_bright>";
        var line = p != null ? format + p.BoxMarkup : Log.Last ?? "";
        Screen.Find("PredictionText")?.SetText(line);
        SetCursorPrediction(p?.CursorMarkup);
    }

    BitmapText? _cursorPrediction;

    /// <summary>
    /// The cursor_prediction window (UI/CombatArena-Heavy/CursorPrediction.(WindowTextView).xdb:
    /// 200 px wide, grows with its text, cursor-prediction.txt markup). It sits centred under the
    /// cursor (placement relative to the cursor inferred; the exe positions it at run time).
    /// </summary>
    void SetCursorPrediction(string? markup)
    {
        if (markup == null)
        {
            if (_cursorPrediction != null) _cursorPrediction.Visible = false;
            return;
        }
        if (_cursorPrediction == null)
        {
            _cursorPrediction = new BitmapText { Wrap = false, ZIndex = 50 };
            AddChild(_cursorPrediction);
        }
        string template = UiResources.Instance.Text("UI/CombatArena-Heavy/cursor-prediction.txt")?.Trim()
                          ?? "<center><system><font size=14><color_white><outline><value=prediction>";
        _cursorPrediction.Markup = template.Replace("<value=prediction>", markup);
        _cursorPrediction.Visible = true;
        UpdateCursorPrediction();
    }

    Vector2 _cursorPos;

    public void UpdateCursorPrediction(Vector2? mouse = null)
    {
        if (mouse is { } m) _cursorPos = m;
        if (_cursorPrediction is not { Visible: true } cp) return;
        float s = Screen.UiScale;
        cp.Scale = new Vector2(s, s);
        cp.Size = new Vector2(200, 48);
        cp.Position = _cursorPos + new Vector2(-100 * s, 32 * s);
    }

    public void SetDesync(bool desynced)
    {
        if (!desynced) { if (_desync != null) _desync.Visible = false; return; }
        _desync ??= AddWarning();
        _desync.Visible = true;
    }

    Label AddWarning()
    {
        var l = new Label
        {
            Text = "Desync detected: this client's battle state differs from the server's.",
            Position = new Vector2(16, 16), Modulate = new Color(1, 0.4f, 0.3f),
        };
        AddChild(l);
        return l;
    }

    // ---------------------------------------------------------------- ATB bar

    sealed class AtbIcon
    {
        public Control Root = null!;
        public TextureRect Background = null!, Person = null!, Selection = null!;
        public BitmapText Count = null!;
        /// <summary>Original "Blind" marker window (UI/CombatArena-Heavy/ATBBar/Icon_Blind).</summary>
        public Control? Blind;
    }

    // ATB bar scrolling, as the exe's bar view (FUN_0055dce0 / FUN_0055f880 / FUN_0055f810):
    // atb_scroll_left/right (the scroll buttons' Pushed sub-state reaction) set a continuous scroll flag,
    // the *_reverse reaction on release clears it. The strip slides toward index × pitch at
    // ATBBar_ScrollSpeed (4.0) × 0.001 × pitch pixels per ms, i.e. 4 icons a second; whenever it reaches
    // (or is within one step of) the target while a flag is still set, the index advances again, so
    // holding a button scrolls continuously. Left is enabled from index 1, right while icons remain hidden.
    /// <summary>How many icons the ATB bar is scrolled ahead (the exe's view index, +0x48).</summary>
    int _atbScroll;
    /// <summary>Current strip offset in pixels (the exe's +0x44), sliding toward _atbScroll × IconStep.</summary>
    float _atbPos;
    bool _scrollLeft, _scrollRight;
    /// <summary>Upcoming icons available beyond the first (prediction length minus the visible ones).</summary>
    int _atbMaxScroll;
    const int MaxAtbScroll = 20;
    const float AtbScrollSpeed = 4.0f;   // exe DAT_00f32c28 "ATBBar_ScrollSpeed" default (FUN_00561f80)
    Control? _atbClip;
    Vector2 _atbIconOrigin;
    List<int> _atbUpcoming = new();

    void AtbScrollMessage(bool left, bool right) { _scrollLeft = left; _scrollRight = right; }

    void UpdateAtbScroll(double delta)
    {
        float target = _atbScroll * IconStep;
        int before = _atbScroll;
        if (_atbPos == target)
        {
            if (_scrollLeft) { if (_atbScroll > 0) _atbScroll--; }
            else if (_scrollRight && _atbScroll < _atbMaxScroll) _atbScroll++;
        }
        else
        {
            float step = (float)(delta * 1000.0) * IconStep * AtbScrollSpeed * 0.001f;
            if (Math.Abs(_atbPos - target) <= step)
            {
                if (_scrollLeft) { if (_atbScroll > 0) _atbScroll--; }
                else if (_scrollRight && _atbScroll < _atbMaxScroll) _atbScroll++;
                target = _atbScroll * IconStep;
            }
            _atbPos = _atbPos <= target ? Math.Min(target, _atbPos + step) : Math.Max(target, _atbPos - step);
        }
        if (_atbScroll != before) UpdateAtb();
        else LayoutAtbIcons();
        if (Screen.Find("ScrollLeft") is { } l) l.Enabled = _atbScroll >= 1;
        if (Screen.Find("ScrollRight") is { } r) r.Enabled = _atbScroll < _atbMaxScroll;
    }

    void BuildAtbIcons()
    {
        if (Screen.Find("IconContainer") is not { } container || Screen.Root.Find("IconPrototype") is not { } proto) return;
        var protoRect = proto.Rect;
        var containerRect = container.Window.Rect;
        var protoTex = UiResources.Instance.Texture("UI/CombatArena-Heavy/ATBBar/IconPrototype.(Texture).dds");
        var selTex = UiResources.Instance.Texture("UI/CombatArena-Heavy/ATBBar/Selection.(Texture).dds");
        container.ClipContents = false;
        // The strip (IconContainer) slides inside the IconView window, which clips it (FUN_0055df40 binds
        // "IconView"/"IconContainer"; the scroll buttons draw over its ends at Priority 2).
        var view = Screen.Find("IconView")?.Window.Rect ?? containerRect;
        _atbClip = new Control
        {
            Name = "AtbClip", ClipContents = true, MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2(view.X - containerRect.X, view.Y - containerRect.Y), Size = new Vector2(view.W, view.H),
        };
        container.AddChild(_atbClip);
        _atbIconOrigin = new Vector2(protoRect.X - view.X, protoRect.Y - view.Y);
        // One extra icon so a partly scrolled strip has no gap at its right edge.
        for (int i = 0; i < IconCount + 1; i++)
        {
            var icon = new AtbIcon();
            icon.Root = new Control
            {
                Position = _atbIconOrigin + new Vector2(i * IconStep, 0),
                Size = new Vector2(protoRect.W, protoRect.H), MouseFilter = MouseFilterEnum.Ignore,
            };
            icon.Root.AddChild(new TextureRect { Texture = protoTex, Size = icon.Root.Size, MouseFilter = MouseFilterEnum.Ignore });
            icon.Background = new TextureRect
            {
                Position = new Vector2(3, 3), Size = new Vector2(55, 55), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore,
            };
            icon.Person = new TextureRect
            {
                Position = new Vector2(3, 3), Size = new Vector2(55, 55), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore,
            };
            icon.Count = new BitmapText { Position = new Vector2(12, 40), Size = new Vector2(45, 22), Wrap = false };
            icon.Selection = new TextureRect { Texture = selTex, Size = icon.Root.Size, Visible = false, MouseFilter = MouseFilterEnum.Ignore };
            icon.Root.AddChild(icon.Background);
            icon.Root.AddChild(icon.Person);
            icon.Root.AddChild(icon.Count);
            icon.Root.AddChild(icon.Selection);
            try
            {
                var blind = UiResources.Instance.LoadWindow("UI/CombatArena-Heavy/ATBBar/Icon_Blind.(WindowSimple).xdb");
                UiLayout.Apply(blind);
                icon.Blind = new UiNode(blind) { MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(blind.Rect.X, blind.Rect.Y), Size = new Vector2(blind.Rect.W, blind.Rect.H), Visible = false };
                icon.Root.AddChild(icon.Blind);
            }
            catch (Exception e) { GD.PushWarning($"[H5] ATB blind marker: {e.Message}"); }
            _atbClip.AddChild(icon.Root);
            _icons.Add(icon);
        }
    }

    static Texture2D? SideBackground(int side, bool large) =>
        UiResources.Instance.Texture(side == 0 ? "UI/CombatArena-Heavy/ATBBar/Red_128.(Texture).dds" : "UI/CombatArena-Heavy/ATBBar/Blue_128.(Texture).dds");

    public void OnTurnStarted(int actor)
    {
        _activeActor = actor;
        _atbScroll = 0;
        _atbPos = 0;
        Refresh();
    }

    void UpdateAtb()
    {
        var b = _screen.Session.Battle;
        var order = TurnOrder.Predict(b, IconCount + 1 + MaxAtbScroll);
        int current = b.State.Active ?? (order.Count > 0 ? order[0] : int.MinValue);
        SetLarge(current);
        _atbUpcoming = (order.Count > 0 && order[0] == current ? order.Skip(1) : order).ToList();
        _atbMaxScroll = Math.Max(0, _atbUpcoming.Count - IconCount);
        if (_atbScroll > _atbMaxScroll) { _atbScroll = _atbMaxScroll; _atbPos = Math.Min(_atbPos, _atbScroll * IconStep); }
        LayoutAtbIcons();
    }

    /// <summary>Places the icon strip at the current scroll offset and fills each slot from the prediction.</summary>
    void LayoutAtbIcons()
    {
        var b = _screen.Session.Battle;
        int first = (int)Math.Floor(_atbPos / IconStep);
        float frac = _atbPos - first * IconStep;
        for (int i = 0; i < _icons.Count; i++)
        {
            var icon = _icons[i];
            int k = first + i;
            icon.Root.Position = _atbIconOrigin + new Vector2(i * IconStep - frac, 0);
            if (k < 0 || k >= _atbUpcoming.Count) { icon.Root.Visible = false; continue; }
            int a = _atbUpcoming[k];
            icon.Root.Visible = true;
            icon.Background.Texture = SideBackground(SideOf(a), false);
            icon.Person.Texture = Portrait(a, large: false);
            icon.Count.Markup = Actor.IsHero(a) ? "" : $"<creatures_count>{CountOf(a)}";
            if (icon.Blind != null) icon.Blind.Visible = !Actor.IsHero(a) && b.State.Unit(a)?.HasEffect("SPELL_BLIND") == true;
        }
    }

    void SetLarge(int actor)
    {
        if (Screen.Find("LargeIcon") is not { } large) return;
        large.Visible = actor != int.MinValue;
        if (actor == int.MinValue) return;
        SetTex(Screen.Find("LargeIcon/Background"), SideBackground(SideOf(actor), true));
        SetTex(Screen.Find("LargeIcon/Person"), Portrait(actor, large: true));
        Screen.Find("LargeIcon/Count")?.SetValue("count", Actor.IsHero(actor) ? "" : CountOf(actor).ToString());
    }

    static void SetTex(UiNode? node, Texture2D? tex)
    {
        if (node == null) return;
        var existing = node.GetNodeOrNull<TextureRect>("_tex");
        if (existing == null)
        {
            existing = new TextureRect
            {
                Name = "_tex", AnchorRight = 1, AnchorBottom = 1, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore,
                // Drawn behind the window so its own text (stack counts) stays on top.
                ShowBehindParent = true,
            };
            node.AddChild(existing);
            node.MoveChild(existing, 0);
        }
        existing.Texture = tex;
    }

    int SideOf(int actor) =>
        Actor.IsHero(actor) ? Actor.HeroSide(actor) : _screen.Session.Battle.State.Unit(actor)?.Side ?? 0;

    int CountOf(int actor) => _screen.Session.Battle.State.Unit(actor)?.Count ?? 0;

    Texture2D? Portrait(int actor, bool large)
    {
        var s = _screen.Session.Battle.State;
        string? path = Actor.IsHero(actor)
            ? CombatIcons.Hero(s.Heroes[Actor.HeroSide(actor)].HeroId, large)
            : s.Unit(actor) is { } u ? CombatIcons.Creature(u.CreatureId, large) : null;
        return UiResources.Instance.Texture(path);
    }

    // ---------------------------------------------------------------- tactics placement bar

    void FillPlacementBar()
    {
        var s = _screen.Session.Battle.State;
        // The side currently deploying (hot-seat: player 1, then player 2), else the viewer's side.
        int side = _screen.Session.LocalSides.Where(sd => !s.TacticsDone[sd]).Cast<int?>().FirstOrDefault() ?? _screen.Session.ViewSide;
        var units = s.Units.Where(u => u.Side == side && u.Alive && !u.IsWarMachine && !u.IsObject).ToList();
        for (int i = 1; i <= 7; i++)
        {
            var slot = Screen.Find($"PlacementBar/Slot_{i}");
            var pic = Screen.Find($"PlacementBar/Slot_{i}/Pic");
            if (slot == null || pic == null) continue;
            if (i - 1 < units.Count)
            {
                var u = units[i - 1];
                SetTex(pic, UiResources.Instance.Texture(CombatIcons.Creature(u.CreatureId, false)));
                pic.SetValue("count", u.Count.ToString());
                slot.Visible = true;
            }
            else slot.Visible = false;
        }
        var h = s.Heroes[side];
        if (h.Present) SetTex(Screen.Find("PlacementBar/Hero"), UiResources.Instance.Texture(CombatIcons.Hero(h.HeroId, true)));
    }

    // ---------------------------------------------------------------- spells

    void ToggleSpells()
    {
        if (_spellPanel != null && IsInstanceValid(_spellPanel)) { ((SpellBook3D)_spellPanel).Close(); return; }
        var b = _screen.Session.Battle;
        var la = _screen.MyLegalActions();
        if (la == null) return;
        // The original opens the 3D spellbook for the acting hero (all known spells; ones that
        // can't be cast now are drawn in the "can not cast" style) or for a caster creature.
        IReadOnlyCollection<string> known;
        int mana, maxMana;
        bool warcries;
        if (la.IsHero)
        {
            var h = b.State.Heroes[Actor.HeroSide(la.Actor)];
            known = h.Spells;
            (mana, maxMana) = (h.Mana, h.MaxMana);
            warcries = h.Spells.Count > 0 && h.Spells.All(s => Game.Instance.Db.Spell(s)?.School == "MAGIC_SCHOOL_WARCRIES");
        }
        else if (b.State.Unit(la.Actor) is { } u)
        {
            known = u.Spells.Select(k => k.Spell).ToList();
            (mana, maxMana) = (u.Mana, u.MaxMana);
            warcries = false;
        }
        else return;
        if (known.Count == 0) { Log.AddLine("<body>No spells can be cast now."); return; }
        var book = new SpellBook3D(known, la.Spells, mana, maxMana, warcries);
        book.Chosen += _screen.ChooseSpell;
        book.Closed += () => _spellPanel = null;
        _spellPanel = book;
        AddChild(book);
    }

    // ---------------------------------------------------------------- unit info

    const string InfoTexts = "UI/CreatureInfo/CreatureInfoWindow/Texts/";

    /// <summary>The original creature info screen (UI/CreatureInfo) filled from the unit's current state.</summary>
    public void ShowUnitInfo(Unit u)
    {
        _infoPanel?.QueueFree();
        if (u.IsWarMachine || u.IsObject) return;
        var b = _screen.Session.Battle;
        var def = Game.Instance.Db.Creatures.GetValueOrDefault(u.CreatureId);
        var info = new UiScreen("UI/CreatureInfo.(WindowScreen).xdb") { MouseFilter = MouseFilterEnum.Stop };
        info.SetVisible("upgrade_tooltip", false);
        info.SetVisible("to_left", false);
        info.SetVisible("to_right", false);
        SetTex(info.Find("creature_info/icon"), UiResources.Instance.Texture(CombatIcons.Creature(u.CreatureId, true)));
        if (info.Find("icon_definition") is { } idef)
        {
            idef.SetValue("name", CombatLogText.Escape(def?.Name is { Length: > 0 } n ? n : CombatLogText.WarMachineName(u.CreatureId) ?? u.Name));
            idef.SetValue("tier", u.Tier.ToString());
            idef.SetValue("town", TownName(u.Town));
        }
        info.Find("quantity")?.SetValue("value", u.Count.ToString());

        string Single(double v, double baseV) => Fmt("single_value", ("value_normal", Math.Abs(v - baseV) < 1e-6 ? Num(v) : ""),
            ("value_red", v < baseV - 1e-6 ? Num(v) : ""), ("value_green", v > baseV + 1e-6 ? Num(v) : ""));
        string Pair(double a, double baseA, double bb, double baseB) => Fmt("double_value",
            ("value1_normal", Math.Abs(a - baseA) < 1e-6 ? Num(a) : ""), ("value1_red", a < baseA - 1e-6 ? Num(a) : ""), ("value1_green", a > baseA + 1e-6 ? Num(a) : ""),
            ("value2_normal", Math.Abs(bb - baseB) < 1e-6 ? Num(bb) : ""), ("value2_red", bb < baseB - 1e-6 ? Num(bb) : ""), ("value2_green", bb > baseB + 1e-6 ? Num(bb) : ""));
        string Double(int a, int bb) => Pair(a, a, bb, bb);
        // Effective values (spells, hero, artifacts, auras) coloured against the creature's base
        // stats, as the stock info window does: green above base, red below.
        var (minDmg, maxDmg) = b.DamageRangeOf(u);
        var rows = new (int Slot, string Value)[]
        {
            (0, Single(b.AttackOf(u), u.BaseAttack)),
            (1, Single(b.DefenceOf(u), u.BaseDefence)),
            (2, Pair(Math.Round(minDmg, 1), u.BaseMinDamage, Math.Round(maxDmg, 1), u.BaseMaxDamage)),
            (3, Double(u.TopHealth, u.MaxHealthCache)),
            (4, Single(b.SpeedOf(u), u.BaseSpeed)),
            (5, Single(Math.Round(b.InitiativeOf(u), 1), u.BaseInitiative)),
            (6, Single(b.MoraleOf(u), 0)),
            (7, Single(b.LuckOf(u), 0)),
        };
        // Stat icons: the stock param icon window has no texture (Param/Icon/Back is empty)
        // and the manual's info screenshots show text rows only, so they stay hidden.
        foreach (var (slot, value) in rows)
        {
            info.Find($"param_{slot}/name")?.SetText(UiResources.Instance.Text(InfoTexts + $"param_{slot}.txt"));
            info.Find($"param_{slot}/value")?.SetText(value);
            info.SetVisible($"param_{slot}/icon", false);
        }
        if (u.MaxShots > 0)
        {
            info.Find("param_8/name")?.SetText(UiResources.Instance.Text(InfoTexts + "Range.txt"));
            info.Find("param_8/value")?.SetText(Double(u.Shots, u.MaxShots));
            info.SetVisible("param_8/icon", false);
        }
        else info.SetVisible("param_8", false);

        // Description and abilities go in the scrollable container (the template window is placed by code).
        if (info.Find("description_container") is { } cont)
        {
            info.SetVisible("description_text", false);
            var abilities = u.Abilities.Select(a => Game.Instance.Db.Abilities.GetValueOrDefault(a)?.Name).Where(x => !string.IsNullOrEmpty(x));
            var markup = (UiResources.Instance.Text("UI/CreatureInfo/CreatureInfoWindow/description_text.txt") ?? "")
                .Replace("<value=description>", CombatLogText.Escape(def?.Description ?? ""))
                .Replace("<value=abilities>", CombatLogText.Escape(string.Join((UiResources.Instance.Text(InfoTexts + "separator.txt") ?? ",") + " ", abilities)));
            cont.ClipContents = true;
            cont.AddChild(new BitmapText { Markup = markup, AnchorRight = 1, AnchorBottom = 1, OffsetRight = -24 });
        }
        int e = 0;
        foreach (var fx in u.Effects.Where(x => x.SpellLevel > 0 || x.Positive).Select(x => x.Id).Distinct())
        {
            if (e >= 5) break;
            if (SpellEffectIcon(fx) is { } icon) SetTex(info.Find($"effect_{e++}"), UiResources.Instance.Texture(icon));
        }
        info.GuiInput += ev => { if (ev is InputEventMouseButton { Pressed: true }) { info.QueueFree(); _infoPanel = null; } };
        _infoPanel = info;
        AddChild(info);
    }

    static string Num(double v) => Math.Abs(v - Math.Round(v)) < 1e-6 ? ((int)Math.Round(v)).ToString() : v.ToString("0.#");

    static string Fmt(string template, params (string K, string V)[] values)
    {
        var t = UiResources.Instance.Text(InfoTexts + template + ".txt") ?? "";
        foreach (var (k, v) in values) t = t.Replace($"<value={k}>", v);
        return t;
    }

    static string TownName(string town) => town switch
    {
        "TOWN_HEAVEN" => "Haven", "TOWN_PRESERVE" => "Sylvan", "TOWN_ACADEMY" => "Academy", "TOWN_FORTRESS" => "Fortress",
        "TOWN_DUNGEON" => "Dungeon", "TOWN_INFERNO" => "Inferno", "TOWN_NECROMANCY" => "Necropolis", "TOWN_STRONGHOLD" => "Stronghold",
        _ => "Neutral",
    };

    static string? SpellEffectIcon(string spellId)
    {
        var def = Game.Instance.Db.Spell(spellId);
        if (def == null || !Game.Instance.Fs.Exists(def.SourcePath)) return null;
        var h = (string?)Game.Instance.Xdb.Load(def.SourcePath).Root!.Element("EffectTexture")?.Attribute("href");
        return string.IsNullOrEmpty(h) ? null : XdbRef.Parse(h, def.SourcePath).Path;
    }

    // ---------------------------------------------------------------- results

    const string ResultTexts = "UI/CombatResults/";

    /// <summary>The original combat results screen (UI/CombatResults) from the viewing side's perspective.</summary>
    public void ShowResults(int? winner, string reason)
    {
        _spellPanel?.QueueFree();
        _infoPanel?.QueueFree();
        var s = _screen.Session.Battle.State;
        int me = _screen.Session.ViewSide, enemy = 1 - me;
        var res = new UiScreen("UI/CombatResults.(WindowScreen).xdb") { MouseFilter = MouseFilterEnum.Stop };
        res.SetVisible("Replay", false);
        res.SetVisible("ReplaySave", false);
        bool won = winner == me;
        var header = UiResources.StripMarkup(UiResources.Instance.Text(ResultTexts + (won ? "Victory.txt" : "Defeat.txt")) ?? (won ? "Victory!" : "Defeat!"));
        res.Find("DescriptionHeader")?.SetValue("val", header);
        var r = reason.ToLowerInvariant();
        string outcome = won
            ? (r.Contains("surrender") ? "EnemySurrender" : r.Contains("fled") || r.Contains("retreat") ? "EnemyFled" : "EnemyDefated")
            : (r.Contains("surrender") ? "HeroSurrender" : r.Contains("fled") || r.Contains("retreat") ? "HeroFled" : "HeroDefeated");
        // Exe FUN_005836a0: DescriptionText_Format with the outcome text (type 1 defeated / 2 fled /
        // 3 surrendered) and, when the enemy was destroyed but the winner kept no troops, <value=no_army> =
        // WINNER_HAS_NO_ARMY; below it DescriptionExp_Format with HeroExp (own victory) or EnemyExp and the
        // winner's experience (only when the winning side has a hero).
        if (res.Find("HeaderCont") is { } cont)
        {
            string Txt(string n) => UiResources.Instance.Text(ResultTexts + n + ".txt") ?? "";
            bool destroyed = outcome is "EnemyDefated" or "HeroDefeated";
            bool winnerNoArmy = destroyed && winner is int ws && !s.Units.Any(u => u.Side == ws && u.Alive && u.Kind == UnitKind.Creature);
            var desc = (Txt("DescriptionText_Format") is { Length: > 0 } fmt ? fmt : "<body_bright><center><value=val>")
                .Replace("<value=player_hint>", "")
                .Replace("<value=val>", Txt(outcome))
                .Replace("<value=no_army>", winnerNoArmy ? Txt("WINNER_HAS_NO_ARMY") : "");
            long exp = winner is int wi ? _screen.Session.WinnerExperience(wi) : -1;
            if (exp >= 0)
            {
                var expLine = (won ? Txt("HeroExp") : Txt("EnemyExp")).Replace("<value=exp>", exp.ToString());
                desc += "<br>" + (Txt("DescriptionExp_Format") is { Length: > 0 } ef ? ef : "<body_bright><left><value=val>").Replace("<value=val>", expLine);
            }
            cont.AddChild(new BitmapText { Markup = desc, AnchorRight = 1, AnchorBottom = 1 });
        }
        var h = s.Heroes[me];
        if (h.Present) SetTex(res.Find("Header/Icon"), UiResources.Instance.Texture(CombatIcons.Hero(h.HeroId, true)));
        _lossOffsets.Clear();
        FillLosses(res, "LostUnits", me);
        FillLosses(res, "DestroyedUnits", enemy);
        // Left/Right scroll a list by one icon (exe FUN_00585920 / FUN_00585940, refreshed by FUN_00580ee0).
        void Scroll(UiNode? from, int step)
        {
            foreach (var (group, side) in new[] { ("LostUnits", me), ("DestroyedUnits", enemy) })
                if (from != null && res.Find(group + "/UnitsButtons") is { } ub && ub.IsAncestorOf(from))
                {
                    _lossOffsets[group] = _lossOffsets.GetValueOrDefault(group) + step;
                    FillLosses(res, group, side);
                }
        }
        res.Bus.On("Left", (_, n) => Scroll(n, -1));
        res.Bus.On("Right", (_, n) => Scroll(n, +1));
        res.Bus.On("Close", _screen.ReturnToMenu);
        _resultsPanel = res;
        AddChild(res);
    }

    readonly Dictionary<string, int> _lossOffsets = new();

    /// <summary>
    /// One results list (exe icon list FUN_005814d0 / FUN_00580ee0): CreatureIcon_0..n-1 show entries offset..offset+n-1,
    /// Left is enabled while offset &gt; 0, Right while offset &lt; entries − icons, and UnitsButtons is shown only when
    /// either can scroll.
    /// </summary>
    void FillLosses(UiScreen res, string group, int side)
    {
        var s = _screen.Session.Battle.State;
        var losses = s.Units.Where(u => u.Side == side && u.Kind == UnitKind.Creature && !u.Summoned)
            .Select(u => (u.CreatureId, Lost: u.InitialCount - Math.Max(0, u.Count))).Where(x => x.Lost > 0).ToList();
        int icons = 0;
        while (res.Find($"{group}/Units/CreatureIcon_{icons}") != null) icons++;
        int maxOffset = Math.Max(0, losses.Count - icons);
        int offset = Math.Clamp(_lossOffsets.GetValueOrDefault(group), 0, maxOffset);
        _lossOffsets[group] = offset;
        for (int i = 0; i < icons; i++)
        {
            var slot = res.Find($"{group}/Units/CreatureIcon_{i}")!;
            int k = offset + i;
            if (k < losses.Count)
            {
                SetTex(slot, UiResources.Instance.Texture(CombatIcons.Creature(losses[k].CreatureId, false)));
                res.Find($"{group}/Units/CreatureIcon_{i}/Number")?.SetValue("Number", losses[k].Lost.ToString());
                slot.Visible = true;
            }
            else slot.Visible = false;
        }
        bool left = offset > 0, right = offset < maxOffset;
        if (res.Find($"{group}/UnitsButtons/Left") is { } l) l.Enabled = left;
        if (res.Find($"{group}/UnitsButtons/Right") is { } r) r.Enabled = right;
        if (res.Find($"{group}/UnitsButtons") is { } ubs) ubs.Visible = left || right;
    }

    /// <summary>A centred dialog on the original parchment dialog texture.</summary>
    Control PanelBase(Vector2 size, string titleMarkup)
    {
        var vp = GetViewportRect().Size;
        var panel = new Control { Position = ((vp - size) / 2).Round() - new Vector2(0, 60), Size = size, MouseFilter = MouseFilterEnum.Stop };
        var bg = UiResources.Instance.Texture("UI/CombatArena-Heavy/CombatLog/Background.dds");
        panel.AddChild(new TextureRect
        {
            Texture = bg, Size = size, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore,
        });
        panel.AddChild(new ReferenceRect { BorderColor = new Color(0.62f, 0.52f, 0.33f), BorderWidth = 2, EditorOnly = false, Size = size, MouseFilter = MouseFilterEnum.Ignore });
        panel.AddChild(new BitmapText { Markup = $"<center><h2>{titleMarkup}", Position = new Vector2(0, 8), Size = new Vector2(size.X, 32), Wrap = false });
        var close = new Button { Text = "×", Position = new Vector2(size.X - 30, 4), Size = new Vector2(26, 26), Flat = true, FocusMode = FocusModeEnum.None };
        close.Pressed += () => { if (panel != _resultsPanel) panel.QueueFree(); };
        if (titleMarkup != "Victory!" && titleMarkup != "Defeat") panel.AddChild(close);
        return panel;
    }
}

/// <summary>The combat log: original LogWindow text area plus the one-line PredictionBox.</summary>
public sealed class CombatLog
{
    readonly UiScreen _screen;
    readonly List<string> _lines = new();
    public string? Last => _lines.Count > 0 ? _lines[^1] : null;

    public CombatLog(UiScreen screen) => _screen = screen;

    public void AddLine(string markup)
    {
        if (string.IsNullOrWhiteSpace(markup)) return;
        _lines.Add(markup);
        if (_lines.Count > 100) _lines.RemoveAt(0);
        GD.Print("[combat] " + UiResources.StripMarkup(markup));
        _screen.Find("TextLog")?.SetText(string.Join("<br>", _lines.TakeLast(6)));
        _screen.Find("PredictionText")?.SetText(markup);
    }
}
