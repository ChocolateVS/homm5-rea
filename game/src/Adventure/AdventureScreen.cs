using Godot;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using BattleState = H5.Combat.BattleState;
using H5Reborn.Combat;
using H5Reborn.Render;
using H5Reborn.Ui;
using HeroState = H5.Adventure.State.HeroState;

namespace H5Reborn.Adventure;

/// <summary>
/// The adventure map screen: 3D map (<see cref="AdvWorld"/>), original HUD
/// (<see cref="AdvHud"/>), the stock mouse behaviour (hover plots a path with the
/// original markers and day-count cursors; clicking a plotted destination again, or
/// Space, sends the hero), sequential playback of engine events (hero walks, pickups,
/// captures, level-ups, choices, battles on the combat screen) and the turn cycle
/// against computer players.
/// </summary>
public partial class AdventureScreen : Control
{
    public AdventureSession Session { get; }
    public AdvWorld World { get; private set; } = null!;
    public AdvHud Hud { get; private set; } = null!;
    public int SelectedHeroId { get; private set; } = -1;

    Tile? _plotted;               // destination of the currently shown path
    Tile? _hover;
    bool _playing;                // an event is being animated
    int _modal;                   // open dialogs
    Control _dialogLayer = null!;
    /// <summary>Layer for modal screens (town, hero); anything in it locks map input.</summary>
    public Control DialogLayer => _dialogLayer;
    Control _flyLayer = null!;

    /// <summary>Dev/test: the local player's turns are played by the bot (`--adventure-autoplay`).</summary>
    public static bool Autoplay;
    /// <summary>Dev runs that script a visit (`--adventure-visit-test`): message boxes close by themselves like in autoplay
    /// (the first day's start-of-week notice would otherwise hold the visit).</summary>
    public static bool DevAutoCloseBoxes;
    /// <summary>Dev/test: stop after this many days (`--adventure-days=N`).</summary>
    public static int AutoplayDays = 3;

    public AdventureScreen(AdventureSession session)
    {
        Session = session;
        Name = "AdventureScreen";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public static AdventureScreen Open(AdventureSession session)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.CurrentScene?.QueueFree();
        var screen = new AdventureScreen(session);
        tree.Root.AddChild(screen);
        tree.CurrentScene = screen;
        return screen;
    }

    bool _built;

    public override void _Ready()
    {
        // Like the other original screens, the HUD is laid out in real pixels.
        H5Reborn.Settings.VideoSettings.ApplyScaling();
        if (_built) return;
        _built = true;
        Audio.AdventureAudio.Attach(this);

        World = new AdvWorld(Session);
        AddChild(World);
        Hud = new AdvHud(this);
        AddChild(Hud);
        _flyLayer = new Control { Name = "FlyTexts", AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_flyLayer);
        _dialogLayer = new Control { Name = "Dialogs", AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_dialogLayer);
        _dialogLayer.ChildEnteredTree += _ => _modal++;
        _dialogLayer.ChildExitingTree += _ => _modal--;

        var first = MyHeroes().FirstOrDefault();
        if (first != null) Select(first);
        else if (MyTowns().FirstOrDefault() is { } town) World.Camera.CenterOn(town.Pos);
        Hud.Refresh();
        // Map scripts and their UI (messages, dialog scenes, objectives); idempotent, campaigns attach too.
        // Map scripts run where the game is simulated: never on a network client (the host runs them).
        if (!Session.IsRemote) Campaign.ScriptUiBridge.Attach(this);
        // Quicksave/quickload, save/load keys and autosave (game/src/Save).
        Save.SaveHotkeys.Attach(this);
    }

    // ---------------------------------------------------------------- state helpers

    public List<HeroState> MyHeroes() =>
        Session.S.Heroes.Where(h => h.Alive && h.Owner == Session.Human.Index && !h.Garrisoned).ToList();

    public List<TownState> MyTowns() =>
        Session.S.Towns.Where(t => t.Owner == Session.Human.Index).ToList();

    HeroState? Selected => SelectedHeroId >= 0 ? Session.S.Hero(SelectedHeroId) : null;

    bool InputLocked => _playing || _modal > 0 || !Session.IsHumanTurn || Session.PendingEvents > 0 || Autoplay;

    void Select(HeroState h)
    {
        SelectedHeroId = h.Id;
        _plotted = null;
        World.ClearPath();
        if (h.Pos.Floor != World.Floor) World.SetFloor(h.Pos.Floor);
        World.Camera.CenterOn(h.Pos);
        World.ShowSelection(h);
        Hud.Refresh();
    }

    public void SelectHeroAt(int i)
    {
        var heroes = MyHeroes();
        if (i >= 0 && i < heroes.Count) Select(heroes[i]);
    }

    public void SelectTownAt(int i)
    {
        var towns = MyTowns();
        if (i < 0 || i >= towns.Count) return;
        var t = towns[i];
        if (t.Pos.Floor != World.Floor) World.SetFloor(t.Pos.Floor);
        World.Camera.CenterOn(t.Pos);
    }

    // ---------------------------------------------------------------- jog dial actions

    public void NextHero()
    {
        if (InputLocked) return;
        var heroes = MyHeroes();
        if (heroes.Count == 0) return;
        int i = heroes.FindIndex(h => h.Id == SelectedHeroId);
        // Next hero that can still move, like the original's MB_Next_Hero.
        for (int k = 1; k <= heroes.Count; k++)
        {
            var h = heroes[(i + k + heroes.Count) % heroes.Count];
            if (h.MovePoints > 0 || k == heroes.Count) { Select(h); return; }
        }
    }

    public void MoveSelectedHero()
    {
        if (InputLocked || Selected is not { } h || _plotted is not { } dest) return;
        SendMove(h, dest);
    }

    public void FlipFloor()
    {
        if (_modal > 0 || !Session.Map.HasUnderground) return;
        World.SetFloor(1 - World.Floor);
        World.ClearPath();
        _plotted = null;
        Hud.Refresh();
    }

    public void EndTurn()
    {
        if (InputLocked) return;
        World.ClearPath();
        _plotted = null;
        var r = Session.Apply(new EndTurnCmd());
        if (!r.Ok) return;
        RunComputerTurns();
    }

    void RunComputerTurns()
    {
        if (Session.S.Finished || Session.IsHumanTurn) return;
        Hud.ShowEnemyTurn(Session.Game.Current.Index);
        // Computer turns are computed at once; their events then play back in order.
        Session.RunComputerTurns();
    }

    public void ShowEscMenu()
    {
        if (_modal > 0) return;
        // The stock in-game menu (UI/EscMenu) with Save/Load (game/src/Save).
        Save.InGameMenu.Show(this, ReturnToMainMenu);
    }

    /// <summary>The information box (UI/MissionObjectives) on its objectives page.</summary>
    public void ShowScenarioInfo() => ShowMissionBox("TabObjectives");

    /// <summary>Opens UI/MissionObjectives on a tab (TabObjectives, TabLog or TabKingdom).</summary>
    public void ShowMissionBox(string tab)
    {
        if (_modal > 0 || GetNodeOrNull("MissionBox") != null) return;
        AddChild(new MissionBoxScreen(this, tab));
    }

    /// <summary>The puzzle map of the Tear of Asha (UI/PuzzleMap).</summary>
    public void ShowPuzzleMap()
    {
        if (_modal > 0 || GetNodeOrNull("PuzzleMap") != null) return;
        AddChild(new PuzzleMapScreen(this));
    }

    /// <summary>adv_dig: asks (stock dig_graal texts), then digs with the selected hero.</summary>
    public void DigWithSelected()
    {
        if (_modal > 0 || Selected is not { } hero || !Session.IsHumanTurn) return;
        string T(string file, string fallback) => AdvTexts.Get($"UI/AdventureScreen-heavy/{file}") is { Length: > 0 } t ? t : fallback;
        AdvDialogs.YesNo(_dialogLayer, T("dig_graal_header.txt", "Dig for the Tear of Asha"), T("dig_graal_confirmation.txt", "Do you want to dig?"), null, yes =>
        {
            if (!yes) return;
            var r = Session.Apply(new DigCmd(hero.Id) { Player = Session.Human.Index });
            // Refusals carry the exe's RPGRoot text id (GRAAL_TRY_ON_CLEAR, GRAAL_NOT_ENOUGH_MOVEMENT).
            if (!r.Ok) Message(T("dig_graal_header.txt", "Dig for the Tear of Asha"), AdvTexts.Rpg(r.Error ?? "") is { Length: > 0 } why ? why : r.Error ?? "");
            Hud.Refresh();
        });
    }

    internal static string GoalText(string kind) => kind switch
    {
        "OBJECTIVE_KIND_DEFEAT_ALL" => "Defeat all enemies",
        _ => kind.Replace("OBJECTIVE_KIND_", "").Replace('_', ' ').ToLowerInvariant(),
    };

    public void Message(string title, string text, Texture2D? face = null, Action? onClose = null) =>
        AdvDialogs.Message(_dialogLayer, title, text, face, onClose);

    void ReturnToMainMenu()
    {
        AdvCursors.Reset();
        GetTree().ChangeSceneToFile("res://scenes/MainMenu.tscn");
    }

    // ---------------------------------------------------------------- mouse

    public override void _UnhandledInput(InputEvent e)
    {
        // Audited input (game/src/Input/AdventureScreen.Input.cs): input_a2.cfg keys, manual click table.
        if (HandleAdvInput(e)) return;
        if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            switch (k.Keycode)
            {
                case Key.Space: MoveSelectedHero(); break;
                case Key.N or Key.Tab: NextHero(); break;
                case Key.U or Key.T when !k.CtrlPressed: FlipFloor(); break;
                case Key.Enter or Key.KpEnter or Key.E when _modal == 0: EndTurn(); break;
                case Key.Escape or Key.F10 when _modal == 0: ShowEscMenu(); break;
                case >= Key.Key1 and <= Key.Key8 when !k.CtrlPressed: SelectHeroAt((int)(k.Keycode - Key.Key1)); break;
                case >= Key.Key1 and <= Key.Key8 when k.CtrlPressed: SelectTownAt((int)(k.Keycode - Key.Key1)); break;
            }
            return;
        }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb && !mb.CtrlPressed)
        {
            if (TileAt(mb.Position) is { } tile) Click(tile, mb.DoubleClick);
        }
    }

    Tile? TileAt(Vector2 screen) =>
        World.TileAtRay(World.Camera.ProjectRayOrigin(screen), World.Camera.ProjectRayNormal(screen));

    AdvNet.HotSeatController? _hotSeat;

    static readonly string[] MapSizeEnums =
        { "E_MAP_TINY_SIZE", "E_MAP_SMALL_SIZE", "E_MAP_MEDIUM_SIZE", "E_MAP_LARGE_SIZE", "E_MAP_EXTRALARGE_SIZE", "E_MAP_HUGE_SIZE", "E_MAP_IMPOSSIBLE_SIZE" };

    /// <summary>
    /// Single-player (non-campaign, non-network, single human) victories enter the Hall of Fame. The map size
    /// (from its width, RMG size table 72..320) and difficulty factors come from UI/UIGameRoot. TODO(exe): the
    /// original base score was not recovered from H5_Game.exe (only the HallOfFame data readers at
    /// 0051dd20/0051fdc0/005194a0 were located); a fixed base of 1000 is used, so ranking follows size and difficulty.
    /// </summary>
    void RecordHallOfFame()
    {
        if (Session.IsRemote || Session.IsHotSeat || Campaign.CampaignFlow.Active != null) return;
        var tiles = H5.Rmg.RmgSizes.Tiles;
        int idx = Array.FindIndex(tiles, t => Session.Map.TilesX <= t);
        var size = MapSizeEnums[idx < 0 ? MapSizeEnums.Length - 1 : idx];
        var hof = Profiles.HallOfFame.Data;
        int points = hof.Score(1000, size, Session.S.Difficulty);
        Profiles.HallOfFame.Record(new Profiles.HallOfFame.Entry
        {
            Name = Profiles.ProfileManager.CurrentName,
            Days = Session.S.Day,
            Points = points,
            Scenario = $"{(hof.MapSizes.TryGetValue(size, out var s) ? s.Short : size)} - {Session.Map.Name}",
        });
    }

    /// <summary>An aimed adventure spell waiting for its target tile (Instant Travel).</summary>
    string? _pendingSpell;

    /// <summary>
    /// The adventure spellbook (the stock 3D book opened on its adventure tab) for the selected hero.
    /// Town Portal and Summon Creatures cast at once (Summon Creatures takes the whole garrison of the
    /// nearest own town); Instant Travel waits for a click on the destination.
    /// </summary>
    public void OpenAdventureSpellbook()
    {
        if (InputLocked || Selected is not { } h) return;
        var g = Session.Game;
        var options = h.Spells
            .Where(sp => Game.Instance.Db.Spell(sp)?.School == "MAGIC_SCHOOL_ADVENTURE" && h.Mana >= g.AdventureSpellCost(sp))
            .Select(sp => new H5.Combat.SpellOption { Spell = sp, ManaCost = g.AdventureSpellCost(sp) }).ToList();
        var book = new Combat.SpellBook3D(h.Spells, options, h.Mana, g.MaxMana(h), warcries: false, adventure: true);
        book.Chosen += opt =>
        {
            if (opt.Spell == H5.Adventure.Engine.Adventure.SpellDimensionDoor) { _pendingSpell = opt.Spell; return; }
            CastAdventureSpell(h, opt.Spell, null);
        };
        book.Closed += () => book.QueueFree();
        _dialogLayer.AddChild(book);
    }

    void CastAdventureSpell(HeroState h, string spell, Tile? target)
    {
        var r = Session.Apply(new CastAdventureSpellCmd(h.Id, spell, target) { Player = Session.Human.Index });
        if (!r.Ok)
        {
            var cant = spell switch
            {
                H5.Adventure.Engine.Adventure.SpellSummonCreatures => AdvTexts.Get("Text/Game/Spells/Adventure/Summon_Creatures/cant_cast.txt"),
                H5.Adventure.Engine.Adventure.SpellSummonBoat => AdvTexts.Get("Text/Game/Spells/Adventure/Summon_Boat/cant_cast.txt"),
                _ => "",
            };
            if (cant.Length > 0) Message("", cant);
            if (Verbose) GD.Print($"[H5] adventure spell {spell} rejected: {r.Error}");
        }
    }

    void Click(Tile tile, bool doubleClick)
    {
        if (InputLocked) return;
        var s = Session.S;
        if (_pendingSpell is { } aimed)
        {
            _pendingSpell = null;
            if (Selected is { } caster) CastAdventureSpell(caster, aimed, tile);
            return;
        }
        // Own hero: select it.
        if (s.Heroes.FirstOrDefault(h => h.Alive && !h.Garrisoned && h.Pos == tile && h.Owner == Session.Human.Index) is { } mine)
        {
            if (mine.Id != SelectedHeroId) { Select(mine); return; }
            if (doubleClick) Hero.HeroScreen.Open(this, mine);
            return;
        }
        // Own town without the hero standing at its entrance: show the town (screen not ported yet).
        if (Session.Game.TownAt(tile) is { } town && town.Owner == Session.Human.Index && Selected?.Pos != tile && _plotted != tile)
        {
            if (doubleClick || Selected == null) { Town.TownScreen.Open(this, town); return; }
        }
        if (Selected is not { } hero) return;
        if (_plotted == tile) { SendMove(hero, tile); return; }
        Plot(hero, tile);
    }

    void Plot(HeroState hero, Tile dest, bool? fightAtEnd = null)
    {
        var path = Session.Game.FindPath(hero, dest);
        if (Verbose) GD.Print($"[H5] plot hero {hero.Id} at {hero.Pos} → {dest}: {(path == null ? "no path" : $"{path.Count} steps")}");
        if (path == null || path.Count == 0) { World.ClearPath(); _plotted = null; return; }
        _plotted = dest;
        bool fight = fightAtEnd ?? IsFight(hero, dest);
        int mp = hero.MovePoints;
        var steps = new List<(Tile, AdvWorld.Marker)>();
        var prev = hero.Pos;
        bool today = true;
        foreach (var t in path)
        {
            if (t == hero.Pos) continue;
            int cost = Session.Game.StepCost(hero, prev, t);
            if (today && cost > mp) today = false;
            if (today) mp -= cost;
            bool last = t == path[^1];
            steps.Add((t, last && fight ? AdvWorld.Marker.Contested : today ? AdvWorld.Marker.Available : AdvWorld.Marker.Gray));
            prev = t;
        }
        World.ShowPath(hero.Town is { Length: > 0 } town ? town : HeroTown(hero), steps);
    }

    static string HeroTown(HeroState h) =>
        Game.Instance.Db.Heroes.TryGetValue(h.DefId, out var def) && def.Town is { Length: > 0 } t ? t : "TOWN_HEAVEN";

    bool IsFight(HeroState hero, Tile dest)
    {
        var s = Session.S;
        if (s.Heroes.Any(h => h.Alive && !h.Garrisoned && h.Pos == dest && h.Owner != hero.Owner)) return true;
        if (Session.Game.GuardingMonster(dest) != null) return true;
        if (Session.Game.TownAt(dest) is { } t && t.Owner != hero.Owner && t.Garrison.StackCount() > 0) return true;
        return false;
    }

    /// <summary>Number of days (1 = today) the hero needs to reach a tile, for the cursor variants.</summary>
    int DaysTo(HeroState hero, List<Tile> path)
    {
        int days = 1, mp = hero.MovePoints, prev = 0;
        var from = hero.Pos;
        foreach (var t in path)
        {
            if (t == hero.Pos) continue;
            int cost = Session.Game.StepCost(hero, from, t);
            if (cost > mp) { days++; mp = Math.Max(hero.MaxMovePoints, 1); }
            mp -= cost;
            from = t;
            prev++;
        }
        return days;
    }

    void SendMove(HeroState hero, Tile dest)
    {
        World.ClearPath();
        _plotted = null;
        Session.Apply(new MoveHeroCmd(hero.Id, dest));
    }

    void UpdateHover()
    {
        if (_modal > 0) { AdvCursors.Set(AdvCursor.Default); return; }
        var edge = World.Camera.EdgeScroll;
        if (edge != Vector2I.Zero)
        {
            AdvCursors.Set((edge.X, edge.Y) switch
            {
                (-1, -1) => AdvCursor.ScrollLeftUp, (1, -1) => AdvCursor.ScrollRightUp,
                (-1, 1) => AdvCursor.ScrollLeftDown, (1, 1) => AdvCursor.ScrollRightDown,
                (-1, _) => AdvCursor.ScrollLeft, (1, _) => AdvCursor.ScrollRight,
                (_, -1) => AdvCursor.ScrollUp, _ => AdvCursor.ScrollDown,
            });
            return;
        }
        var mouse = FakeMouse ?? GetViewport().GetMousePosition();
        // Over the HUD the default cursor applies.
        if (FakeMouse == null && GetViewport().GuiGetHoveredControl() is { } hovered && hovered != this && hovered.MouseFilter == MouseFilterEnum.Stop)
        {
            AdvCursors.Set(AdvCursor.Default);
            return;
        }
        var tile = TileAt(mouse);
        _hover = tile;
        // 0059e1f0: input locked → CURSOR_WAIT (actions 1/2), spell aiming → CURSOR_CAST (action 9),
        // no pick (outside the map) → CURSOR_CANT_CAST.
        if (InputLocked) { AdvCursors.Set(AdvCursor.Wait); return; }
        if (_pendingSpell != null) { AdvCursors.Set(AdvCursor.Cast); return; }
        if (tile is not { } t || !World.IsExplored(t)) { AdvCursors.Set(AdvCursor.CantGo); return; }
        var target = HoverTarget(mouse, t);
        AdvCursors.Set(CursorFor(CursorAction(target.Action, Selected?.InBoat == true)), target.Days + 1);
    }

    // ---------------------------------------------------------------- frame loop / playback

    /// <summary>Dev/test (`--adventure-fake-mouse=x,y`): hover as if the mouse were there and draw the cursor in-frame.</summary>
    public static Vector2? FakeMouse;
    /// <summary>Dev (`--adventure-verbose`): logs plotting and clicks.</summary>
    public static bool Verbose;
    TextureRect? _fakeCursor;

    /// <summary>Test helper: a left click at a screen position (plots, then a second call moves).</summary>
    public void SimulateClick(Vector2 screen)
    {
        var tile = TileAt(screen);
        if (Verbose) GD.Print($"[H5] click {screen} → tile {tile?.ToString() ?? "none"}, selected hero {SelectedHeroId}, locked {InputLocked}");
        if (tile is { } t) AdvClick(t, false, screen);
    }

    void DrawFakeCursor()
    {
        if (FakeMouse is not { } p) return;
        _fakeCursor ??= new TextureRect { Name = "FakeCursor", MouseFilter = MouseFilterEnum.Ignore, ZIndex = 100 };
        if (_fakeCursor.GetParent() == null) AddChild(_fakeCursor);
        var (c, days, red) = AdvCursors.Current;
        if (AdvCursors.Texture(c, days, red) is { } cur)
        {
            _fakeCursor.Texture = cur.Tex;
            _fakeCursor.Size = cur.Tex.GetSize();
            _fakeCursor.Position = p - cur.Hot;
        }
    }

    public override void _Process(double delta)
    {
        DevHoverStep();
        UpdateHover();
        DrawFakeCursor();
        if (_playing || _modal > 0) return;
        if (Session.TryDequeue(out var ev, out var player))
        {
            Play(ev, player);
            return;
        }
        // Idle: drive whoever is to move.
        if (Session.S.Finished) return;
        if (Session.S.Battle != null && Session.Game.Current.Human && !Session.IsRemote) { StartBattle(); return; }
        if (!Session.IsHumanTurn) { if (!Session.IsRemote) RunComputerTurns(); return; }
        Hud.ShowEnemyTurn(-1);
        if (Autoplay) AutoplayStep();
    }

    async void Play(AdvEvent ev, int player)
    {
        _playing = true;
        try { await PlayEvent(ev, player); }
        catch (Exception e) { GD.PushError($"[H5] adventure playback {ev.GetType().Name}: {e}"); }
        _playing = false;
    }

    bool HumanCaused(int player) => player == Session.Human.Index;

    async Task PlayEvent(AdvEvent ev, int player)
    {
        Audio.AdventureEventAudio.OnEvent(this, ev, player);
        var s = Session.S;
        switch (ev)
        {
            case ScriptResync:
                // After script-made changes have played: bring every model in line with the state
                // (covers objects the script created or moved without a dedicated event).
                foreach (var h in s.Heroes) World.SyncHero(h);
                foreach (var o in Session.Map.Objects) World.SyncObject(o.Index);
                Hud.Refresh();
                Hud.UpdateMinimap();
                break;
            case ObjectVisited { What: "border_guard_closed" } bc when HumanCaused(player) && !Autoplay:
            {
                // BorderguardBox without the key: ID_TITLE_TEXT_n / ID_DESCRIPTION_NEG_n, no Accept button (006ab6f0).
                int col = Session.Map.Objects[bc.Object].Shared.Color;
                await Dialog(done => AdvDialogs.Message(_dialogLayer, BorderGuardText($"ID_TITLE_TEXT_{col}"), BorderGuardText($"ID_DESCRIPTION_NEG_{col}"),
                    Game.Instance.LoadTexture("Textures/Interface/Borderguard/Face_Texture.xdb"), done));
                break;
            }
            case ObjectVisited ov when HumanCaused(player) && !Autoplay && VisitMessage(ov) is { Length: > 0 } visitText:
                await Dialog(done => Message("", visitText, null, done));
                break;
            case ObjectVisited bb when bb.What.StartsWith("battlebonus:") && HumanCaused(player) && !Autoplay && s.Hero(bb.HeroId) is { } blessed:
            {
                // Next-battle bonus buildings (00af9a10 & co.): one flyoff per changed field over the hero (009c9120 with
                // ICON_LUCK / ICON_MORALE / ICON_OFFENCE / ICON_DEFENCE / ICON_HITPOINTS / ICON_INITIATIVE / ICON_SPEED);
                // hit points use RPGRoot APPLY_HIT_POINTS_EFFECT_FORMAT. A repeat visit flies messagesFileRef[3].
                var at = World.TileCenter(blessed.Pos);
                if (bb.What == "battlebonus:again") { FlyTextAt(at, ObjectMessage(bb.Object, 3)); break; }
                foreach (var part in bb.What["battlebonus:".Length..].Split(','))
                {
                    var kv = part.Split('=');
                    int field = int.Parse(kv[0]), amount = int.Parse(kv[1]);
                    FlyTextAt(at, field == 4 ? AdvTexts.Get(AdvTexts.RpgPath("APPLY_HIT_POINTS_EFFECT_FORMAT"), ("hit_points", amount)) : $"+{amount}");
                }
                Hud.Refresh();
                break;
            }
            case ObjectVisited { What: "BUILDING_TRADING_POST" } tp when HumanCaused(player) && !Autoplay && s.Hero(tp.HeroId) is { } trader:
            {
                // Trading Post: the marketplace screen at the 3-marketplace rates while the hero stands on it.
                Hud.Visible = false;
                var post = new Town.TradingPostScreen(this, trader.Owner, tp.Object);
                AddChild(post);
                await ToSignal(post, Node.SignalName.TreeExited);
                Hud.Visible = true;
                Hud.Refresh();
                break;
            }
            case ObjectVisited { What: "BUILDING_HILL_FORT" or "BUILDING_WARMACHINE_FACTORY" or "BUILDING_BLACK_MARKET" or H5.Adventure.Engine.Adventure.SacrificialAltar or H5.Adventure.Engine.Adventure.SpellShop or H5.Adventure.Engine.Adventure.AstrologerTower } bv when HumanCaused(player) && !Autoplay && s.Hero(bv.HeroId) is { } visitor:
            {
                // Hill Fort / War Machine Factory / Artifact Merchant: their stock windows while the hero stands at the object.
                Hud.Visible = false;
                Control screen = bv.What switch
                {
                    "BUILDING_HILL_FORT" => new HillFortScreen(this, visitor.Id, bv.Object),
                    "BUILDING_BLACK_MARKET" => new BlackMarketScreen(this, visitor.Id, bv.Object),
                    H5.Adventure.Engine.Adventure.SacrificialAltar => new SacrificeAltarScreen(this, visitor.Id, bv.Object),
                    H5.Adventure.Engine.Adventure.SpellShop => new SpellShopScreen(this, visitor.Id, bv.Object),
                    H5.Adventure.Engine.Adventure.AstrologerTower => new AstrologerScreen(this, visitor.Id, bv.Object),
                    _ => new WarMachineFactoryScreen(this, visitor.Id, bv.Object),
                };
                AddChild(screen);
                await ToSignal(screen, Node.SignalName.TreeExited);
                Hud.Visible = true;
                Hud.Refresh();
                break;
            }
            case ObjectVisited { What: H5.Adventure.Engine.Adventure.MapTavern or H5.Adventure.Engine.Adventure.DenOfThieves or H5.Adventure.Engine.Adventure.DenOfThieves + ":captured" } tv when HumanCaused(player) && !Autoplay && s.Hero(tv.HeroId) is { } guest:
            {
                // Map tavern / den of thieves: the tavern screen (00639580 map mode / 00639740 thieves mode); a den
                // changing owner first shows messagesFileRef[2] (00bca4f0).
                bool den = tv.What != H5.Adventure.Engine.Adventure.MapTavern;
                if (tv.What.EndsWith(":captured") && ObjectMessage(tv.Object, 2) is { Length: > 0 } capturedText)
                    await Dialog(done => Message(ObjectTitle(tv.Object), capturedText, null, done));
                Hud.Visible = false;
                var tavern = new MapTavernScreen(this, guest.Id, tv.Object, den);
                AddChild(tavern);
                await ToSignal(tavern, Node.SignalName.TreeExited);
                Hud.Visible = true;
                Hud.Refresh();
                break;
            }
            case ObjectVisited { What: H5.Adventure.Engine.Adventure.MemoryMentor } mv when HumanCaused(player) && !Autoplay && s.Hero(mv.HeroId) is { } pupil:
            {
                // Memory Mentor: the hero screen in its unlearn mode (006c4460); forgotten points are then spent on
                // relearn offers (CRelearnHeroSkillCmd; the exe's relearn UI is not traced — the level-up box here).
                var mentor = Hero.HeroScreen.OpenMentor(this, pupil);
                await ToSignal(mentor, Node.SignalName.TreeExited);
                while (pupil.UnlearnedPoints > 0 && pupil.RelearnOffer is { } offer)
                {
                    int pick = 0;
                    await Dialog(done => AdvDialogs.LevelUp(_dialogLayer, pupil, offer, p => { pick = p; done(); }));
                    if (!Session.Apply(new H5.Adventure.Engine.RelearnHeroSkillCmd(pupil.Id, pick) { Player = pupil.Owner }).Ok) break;
                }
                Hud.Refresh();
                break;
            }
            case ObjectVisited { What: "garrison" } gv when HumanCaused(player) && !Autoplay && s.Hero(gv.HeroId) is { } keeper:
            {
                // Own garrison / mine: leave or take troops (exe 005a5d90 case 0x1c).
                Hud.Visible = false;
                var garrison = new GarrisonScreen(this, keeper.Id, gv.Object);
                AddChild(garrison);
                await ToSignal(garrison, Node.SignalName.TreeExited);
                Hud.Visible = true;
                Hud.Refresh();
                break;
            }
            case ObjectVisited { What: "dwelling" } dv when HumanCaused(player) && !Autoplay && s.Hero(dv.HeroId) is { } recruiter:
            {
                // Map dwelling: the hire screen while the hero stands at it (exe 005a5d90 case 3).
                Hud.Visible = false;
                var hire = new DwellingHireScreen(this, recruiter.Id, dv.Object);
                AddChild(hire);
                await ToSignal(hire, Node.SignalName.TreeExited);
                Hud.Visible = true;
                Hud.Refresh();
                break;
            }
            case ObjectVisited wv when wv.What.StartsWith("BUILDING_WITCH_HUT:") && HumanCaused(player) && !Autoplay:
            {
                // BUILDING_WITCH_HUT:<learned|known|full>:<skill>, told with the hut's stock texts.
                var parts = wv.What.Split(':');
                var msgs = Session.Map.Objects[wv.Object].Shared.Messages;
                string Msg(int i, string fallback) => i < msgs.Count && UiResources.Instance.Text(msgs[i]) is { Length: > 0 } t ? t.Trim() : fallback;
                var skill = parts.Length > 2 ? parts[2] : "";
                var mastery = s.Hero(wv.HeroId)?.MasteryOf(skill) ?? H5.Core.Data.Mastery.None;
                var text = parts[1] switch
                {
                    "learned" => Msg(2, "The witch teaches you %s.").Replace("%s", AdvDialogs.SkillName(skill, (int)mastery)),
                    "full" => Msg(4, "You cannot learn any more skills."),
                    _ => Msg(3, "The Skill is already known"),
                };
                var icon = skill.Length > 0 ? AdvDialogs.SkillIcon(skill, Math.Max(1, (int)mastery)) : null;
                await Dialog(done => AdvDialogs.Message(_dialogLayer, ObjectName(wv.Object), text, icon, done));
                Hud.Refresh();
                break;
            }
            case ObjectVisited tk when tk.What.StartsWith("BUILDING_TREE_OF_KNOWLEDGE") && HumanCaused(player) && !Autoplay && s.Hero(tk.HeroId) is { } learner:
            {
                string T(string file) => AdvTexts.Get($"Text/Interface/TreeOfKnowledge/{file}");
                var title = T("Title_Text.txt") is { Length: > 0 } tt ? tt : ObjectName(tk.Object);
                if (tk.What.EndsWith(":visited"))
                {
                    var msgs = Session.Map.Objects[tk.Object].Shared.Messages;
                    var already = msgs.Count > 3 && UiResources.Instance.Text(msgs[3]) is { Length: > 0 } av ? av.Trim() : "Already visited.";
                    await Dialog(done => AdvDialogs.Message(_dialogLayer, title, already, null, done));
                    break;
                }
                var price = Session.Game.TreeOfKnowledgePrice(tk.Object);
                var me = Session.Human;
                bool afford = me.Purse.CanAfford(price);
                int maxLevel = Session.Map.HeroMaxLevel > 0 ? Session.Map.HeroMaxLevel : 40;
                var desc = T(price.Gold > 0 ? "desc_gold_text.txt" : price.Gem > 0 ? "desc_gems_text.txt" : "desc_free_text.txt");
                if (!afford) desc += "<br>" + T(price.Gold > 0 ? "NoGold_Text.txt" : "NoGems_Text.txt");
                if (learner.Level >= maxLevel) desc += "<br>" + T("level-cap-reached.txt");
                Texture2D? Tex(string path) => Game.Instance.LoadTexture(path);
                int pick = 1;
                await Dialog(done => AdvDialogs.Choose2(_dialogLayer, title, desc, null,
                    (Tex("Textures/Interface/TreeOfKnowledge/exp_texture.xdb"), T("Accept_Text.txt")),
                    (Tex("Textures/Interface/TreeOfKnowledge/Decline_Texture.xdb"), T("Decline_Text.txt")),
                    c => { pick = c; done(); }));
                if (pick != 0 || !afford || learner.Level >= maxLevel) break;
                Session.Apply(new UseTreeOfKnowledgeCmd(learner.Id, tk.Object) { Player = me.Index });
                Hud.Refresh();
                break;
            }
            case ObjectVisited { Object: -1 } dg when dg.What.StartsWith("dig:") && HumanCaused(player) && !Autoplay:
            {
                // 009920a0: GRAAL_NAME / GRAAL_CONGRATULATIONS when found, else GRAAL_HAS_HOLE, a GRAAL_HINT_* or GRAAL_NOTHING_HERE.
                var title = AdvTexts.Get("UI/AdventureScreen-heavy/dig_graal_header.txt") is { Length: > 0 } dt ? dt : "";
                bool found = dg.What == "dig:grail";
                if (found && AdvTexts.Rpg("GRAAL_NAME") is { Length: > 0 } gn) title = gn;
                var text = AdvTexts.Rpg(found ? "GRAAL_CONGRATULATIONS" : dg.What[4..]);
                var face = found ? ArtifactIcon(H5.Adventure.Engine.Adventure.GrailArtifact) : null;
                await Dialog(done => AdvDialogs.Message(_dialogLayer, title, text, face, done));
                Hud.Refresh();
                break;
            }
            // A finished quest opens no box (exe 005a5d90 case 0x1d: only quest states 1 and 2 open one).
            case ObjectVisited { What: "seerhut" or "seerhut:offer" } sh when HumanCaused(player) && !Autoplay && s.Hero(sh.HeroId) is { } seeker:
                await Dialog(done => SeerHutBox.Open(this, _dialogLayer, seeker, sh.Object, sh.What == "seerhut:offer", done));
                break;
            case SphinxRiddlePosed sp when HumanCaused(player) && !Autoplay && s.Obj(sp.Object).Riddle is { } riddle:
            {
                // Sphinx (009cb070 → UI/Sphinx): pose, riddle and reward pages while the hero stands at it.
                Hud.Visible = false;
                var screen = new SphinxScreen(this, sp.HeroId, sp.Object, riddle);
                AddChild(screen);
                await ToSignal(screen, Node.SignalName.TreeExited);
                Hud.Visible = true;
                Hud.Refresh();
                break;
            }
            case ObjectVisited { What: "sphinx:refuse" } sr when HumanCaused(player) && !Autoplay:
            {
                // A used Sphinx (or one without a riddle) says its messagesFileRef[3] text.
                var msgs = Session.Map.Objects[sr.Object].Shared.Messages;
                var text = msgs.Count > 3 ? UiResources.Instance.Text(msgs[3])?.Trim() : null;
                if (!string.IsNullOrEmpty(text)) await Dialog(done => AdvDialogs.Message(_dialogLayer, ObjectName(sr.Object), text, null, done));
                break;
            }
            case ObjectVisited { What: "BUILDING_CARTOGRAPHER" } cv when HumanCaused(player) && !Autoplay && s.Hero(cv.HeroId) is { } buyer:
            {
                var me = Session.Human;
                var msgs = Session.Map.Objects[cv.Object].Shared.Messages;
                string Msg(int i, string fallback) => i < msgs.Count && UiResources.Instance.Text(msgs[i]) is { Length: > 0 } t ? t.Trim() : fallback;
                if (Session.Game.CartographerBought(me, cv.Object))
                {
                    await Dialog(done => AdvDialogs.Message(_dialogLayer, ObjectName(cv.Object), Msg(3, "Already visited."), null, done));
                    break;
                }
                int cost = Session.Game.CartographerCost(cv.Object);
                // The stock offer text names the default price.
                var offer = Msg(2, $"Would you like to buy a map for {cost} gold?").Replace("1000", cost.ToString());
                bool yes = false;
                await Dialog(done => AdvDialogs.YesNo(_dialogLayer, ObjectName(cv.Object), offer, null, y => { yes = y; done(); }));
                if (!yes) break;
                var r = Session.Apply(new BuyCartographerMapCmd(buyer.Id, cv.Object) { Player = me.Index });
                if (!r.Ok) await Dialog(done => AdvDialogs.Message(_dialogLayer, ObjectName(cv.Object), AdvTexts.Get("Text/Game/BuildingsCommon/NotEnoughGold.txt") is { Length: > 0 } ng ? ng : "Not enough gold.", null, done));
                World.RefreshShroud();
                Hud.Refresh();
                Hud.UpdateMinimap();
                break;
            }
            case ObjectVisited { Object: -1 } mv when HumanCaused(player) && !Autoplay && mv.What.StartsWith("meet:")
                                                    && int.TryParse(mv.What[5..], out var metId)
                                                    && s.Hero(mv.HeroId) is { } visitor && s.Hero(metId) is { } met
                                                    && met.Owner == visitor.Owner:
                // Two of the player's heroes meet: the stock side-by-side hero screen (army/artifact exchange).
                var meeting = Hero.HeroScreen.OpenMeeting(this, visitor, met);
                await ToSignal(meeting, Node.SignalName.TreeExited);
                foreach (var h in s.Heroes) World.SyncHero(h);
                Hud.Refresh();
                break;
            case HeroMoved m:
                await AnimateMove(m, player);
                if (s.Hero(m.HeroId) is { } hm) World.SyncHero(hm);
                World.Boats?.Sync();
                Hud.Refresh();
                break;
            case HeroEmbarked he:
                // The boarding step: walk onto the ship, then the ship takes the hero aboard.
                await AnimateMove(new HeroMoved(he.HeroId, new[] { he.From, he.To }, he.MovePointsLeft), player);
                if (s.Hero(he.HeroId) is { } hb) World.SyncHero(hb);
                World.Boats?.Sync();
                Hud.Refresh();
                break;
            case HeroDisembarked hd:
                World.Boats?.Sync();
                await AnimateMove(new HeroMoved(hd.HeroId, new[] { hd.From, hd.To }, hd.MovePointsLeft), player);
                if (s.Hero(hd.HeroId) is { } hl) World.SyncHero(hl);
                World.Boats?.Sync();
                Hud.Refresh();
                break;
            case BoatCreated or BoatMoved:
                World.Boats?.Sync();
                Hud.UpdateMinimap();
                break;
            case TilesRevealed tr when tr.Player == Session.Human.Index:
                World.RefreshShroud();
                Hud.UpdateMinimap();
                break;
            case ObjectRemoved or_:
                World.SyncObject(or_.Object);
                Hud.UpdateMinimap();
                break;
            case ObjectCaptured oc:
                World.SyncObject(oc.Object);
                Hud.UpdateMinimap();
                // Mines and Dwarven Warrens carry their capture flyoff (messagesFileRef[2] FLYOFF_Captured).
                if (HumanCaused(player) && Session.Map.Objects[oc.Object].Kind is ObjectKind.Mine or ObjectKind.DwarvenWarren
                    && ObjectMessage(oc.Object, 2) is { Length: > 0 } flyoff)
                    FlyText(oc.Object, flyoff);
                break;
            case ResourcesChanged rc when rc.Player == Session.Human.Index:
                Hud.Refresh();
                if (HumanCaused(player) && Selected is { } sel)
                    for (int i = 0; i < 7; i++)
                        if (rc.Delta[i] > 0) FlyTextAt(World.TileCenter(sel.Pos), $"+{rc.Delta[i]} {AdvTexts.ResourceName(i)}");
                break;
            case BuildingBuilt bb:
                World.SyncObject(bb.TownObject);
                Hud.Refresh();
                break;
            case HeroHired or ArmyChanged or HeroStatChanged or ExperienceGained:
                foreach (var h in s.Heroes) World.SyncHero(h);
                Hud.Refresh();
                break;
            case HeroRemoved hr:
                if (s.Heroes.Find(h => h.Id == hr.HeroId) is { } dead) World.SyncHero(dead);
                if (hr.HeroId == SelectedHeroId) { SelectedHeroId = -1; World.ShowSelection(null); if (MyHeroes().FirstOrDefault() is { } nh) Select(nh); }
                Hud.Refresh();
                break;
            case ArtifactGained ag when HumanCaused(player):
                // The artifact's own name and description (Artifacts.xdb) with its icon, instead of an invented sentence.
                await Dialog(done => Message(ArtifactName(ag.Artifact),
                    Game.Instance.Db.Artifacts.TryGetValue(ag.Artifact, out var adef) ? adef.Description : "", ArtifactIcon(ag.Artifact), done));
                break;
            case SpellLearned sl when HumanCaused(player):
                FlyTextAt(Selected is { } hs ? World.TileCenter(hs.Pos) : World.Camera.GlobalPosition, Game.Instance.Db.Spell(sl.Spell)?.Name ?? sl.Spell);
                break;
            case LevelUpOffered lu when HumanCaused(player) && s.Hero(lu.HeroId) is { } lh && lh.PendingLevelUps.Count > 0:
                await LevelUp(lh);
                break;
            case ChoiceOffered co when HumanCaused(player) && s.Choice != null:
                await AnswerChoice(co);
                break;
            case Message { Key: "already_visited" or "already_visited_week" } av when HumanCaused(player):
                // Stock wording (Text/Game/BuildingsCommon) instead of the engine's placeholder.
                await Dialog(done => Message("", AdvTexts.Get(av.Key == "already_visited" ? "Text/Game/BuildingsCommon/AlreadyVisited.txt" : AdvTexts.AlreadyVisited), null, done));
                break;
            case Message msg when HumanCaused(player) && !string.IsNullOrWhiteSpace(msg.Text):
                await Dialog(done => Message("", msg.Text, null, done));
                break;
            case H5.Adventure.Engine.TurnStarted ts:
                if (Session.IsHotSeat)
                {
                    // Hot-seat hand-over (AdvNet.HotSeatController): between two humans the stock
                    // HotSeatBlackBg panel covers the map until the next player confirms.
                    _hotSeat ??= new AdvNet.HotSeatController(s.Players.Where(p => p.Human).Select(p => p.Index).ToHashSet());
                    if (_hotSeat.OnTurn(ts.Player))
                    {
                        var cover = AdvNet.HotSeatHandOver.Cover();
                        _dialogLayer.AddChild(cover);
                        _dialogLayer.MoveChild(cover, 0);
                        var next = s.Players.First(p => p.Index == ts.Player);
                        await Dialog(done => Message(AdvNet.HotSeatHandOver.Header, AdvNet.HotSeatHandOver.Text(next), null, done));
                        _hotSeat.Confirm();
                        cover.QueueFree();
                    }
                    World.RefreshShroud();
                    Hud.UpdateMinimap();
                }
                Hud.ShowEnemyTurn(ts.Player == Session.Human.Index ? -1 : ts.Player);
                if (ts.Player == Session.Human.Index)
                {
                    foreach (var h in s.Heroes) World.SyncHero(h);
                    if (Selected is { } h2) World.Camera.CenterOn(h2.Pos);
                    else if (MyHeroes().FirstOrDefault() is { } fh) Select(fh);
                    Hud.Refresh();
                }
                break;
            case WeekStarted ws when s.Players.Count > 0:
                if (Session.IsHumanTurn)
                {
                    // 009946e0: START_OF_WEEK with weektype = the week's name and its description; a creature week
                    // shows the creature's icon (00bcd640).
                    var week = AdvTexts.WeekName(ws.MoonWeek);
                    int cw = H5.Adventure.Engine.MoonCalendar.CreatureOf(Session.Db.MoonWeekIndex(ws.MoonWeek));
                    var icon = cw != 0 && Session.Db.EnumName("CreatureType", cw) is { } cid ? H5Reborn.Town.TownUi.CreatureIcon(cid) : null;
                    await Dialog(done => Message(AdvTexts.Get(AdvTexts.StartOfWeek, ("weektype", week)), AdvTexts.WeekDescription(ws.MoonWeek), icon, done));
                }
                break;
            case PlayerEliminated pe:
                if (pe.Player == Session.Human.Index)
                    await Dialog(done => Message("", AdvTexts.Get(AdvTexts.Defeat), null, () => { done(); ReturnToMainMenu(); }));
                break;
            case H5.Adventure.Engine.GameEnded ge:
                bool won = ge.WinnerTeam == Session.Human.Team;
                if (won) RecordHallOfFame();
                await Dialog(done => Message("", AdvTexts.Get(won ? AdvTexts.Victory : AdvTexts.Defeat), null, () => { done(); ReturnToMainMenu(); }));
                break;
            case BattleRequested:
                // The battle starts once playback is idle (see _Process).
                break;
        }
    }

    Task Dialog(Action<Action> open)
    {
        var tcs = new TaskCompletionSource();
        open(() => tcs.TrySetResult());
        // Automated runs close boxes by themselves after a moment (so screenshots still catch them).
        if (Autoplay || DevAutoCloseBoxes) GetTree().CreateTimer(0.6).Timeout += () =>
        {
            foreach (var c in _dialogLayer.GetChildren()) c.QueueFree();
            tcs.TrySetResult();
        };
        return tcs.Task;
    }

    async Task AnimateMove(HeroMoved m, int player)
    {
        var hero = World.HeroNode(m.HeroId);
        if (hero == null || m.Path.Count == 0) return;
        bool visible = m.Path.Any(World.IsExplored) || HumanCaused(player);
        if (!visible) return;
        var h = Session.S.Hero(m.HeroId);
        if (h != null && m.Path[0].Floor != World.Floor && HumanCaused(player)) World.SetFloor(m.Path[0].Floor);
        bool human = HumanCaused(player);
        // adventure_speed_human / adventure_speed_ai (defaults 1 and 2) scale the walk; the exe reads
        // them per side as max(value, 0) (FUN_008da900; setvars registered in FUN_008e45c0).
        float mult = Math.Max(H5Reborn.Options.OptionsStore.GetFloat(human ? "adventure_speed_human" : "adventure_speed_ai", human ? 1 : 2), 0.25f);
        if (Autoplay) mult *= 4;
        // Ground speed = clip MovementSpeed × SpeedFactor; the clip's own forward drift is stripped
        // at build time (CharacterBuilder.AddInPlaceTrack), so the node carries the travel.
        float ground = hero.GroundSpeeds.TryGetValue("move", out var gs) && gs > 0.1f ? gs : World.HeroSpeed(m.HeroId);
        float speed = ground * mult;
        if (hero.Has("move"))
        {
            hero.Play("move", loop: true);
            hero.Player.SpeedScale = hero.SpeedFactor("move") * mult;
        }

        // One continuous route through the tile centres, corners rounded, walked at constant speed
        // (previously one tween per tile: a stop, a rotation snap and a camera jump at every tile).
        var start = AdvWorld.WorldToMap(hero.Position);
        var pts = new List<Vector2> { start };
        var floors = new List<int> { h?.Pos.Floor ?? m.Path[0].Floor };
        foreach (var t in m.Path)
        {
            var c = new Vector2(t.X + 0.5f, t.Y + 0.5f);
            if (c.DistanceSquaredTo(pts[^1]) < 1e-4f) continue;
            pts.Add(c);
            floors.Add(t.Floor);
        }
        if (pts.Count < 2) { if (hero.Has("idle00")) hero.Play("idle00", loop: true); return; }
        var curve = new Curve2D { BakeInterval = 0.05f };
        for (int i = 0; i < pts.Count; i++)
        {
            // Bezier handles along the corner bisector, a quarter of the shorter neighbouring
            // segment long: straight runs stay straight, 45°/90° turns are rounded off.
            var hIn = Vector2.Zero; var hOut = Vector2.Zero;
            if (i > 0 && i < pts.Count - 1)
            {
                var a = pts[i] - pts[i - 1]; var b = pts[i + 1] - pts[i];
                var tangent = (a.Normalized() + b.Normalized()).Normalized();
                float len = 0.25f * Math.Min(a.Length(), b.Length());
                hIn = -tangent * len; hOut = tangent * len;
            }
            curve.AddPoint(pts[i], hIn, hOut);
        }
        float total = curve.GetBakedLength();
        float yaw = hero.Rotation.Y;

        // Map +y is Godot −Z; original models face local +Z (see ArenaGrid.FacingYaw).
        static float YawOf(Vector2 d) => Mathf.Atan2(d.X, -d.Y);

        // Turn on the spot first when the route starts behind the hero, instead of walking a
        // backwards-curving arc.
        float wantYaw = YawOf(curve.SampleBaked(Math.Min(0.3f, total), true) - start);
        float turn = Mathf.Abs(Mathf.AngleDifference(yaw, wantYaw));
        if (turn > Mathf.Pi / 2)
        {
            float from = yaw, elapsed = 0, turnTime = turn / (TurnRate * mult);
            while (elapsed < turnTime)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!IsInstanceValid(hero)) return;
                elapsed += (float)GetProcessDeltaTime();
                yaw = Mathf.LerpAngle(from, wantYaw, Math.Min(1, elapsed / turnTime));
                hero.Rotation = new Vector3(0, yaw, 0);
            }
        }

        float s = 0;
        while (s < total)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!IsInstanceValid(hero)) return;
            float dt = (float)GetProcessDeltaTime();
            // The route is in tiles, the clip's ground speed in world units (AdvWorld.Unit per tile).
            s = Math.Min(total, s + speed * dt / AdvWorld.Unit);
            var p = curve.SampleBaked(s, true);
            // Floor of the leg being walked (a subterranean gate changes floor at a path tile).
            int leg = Math.Clamp((int)(s / total * (pts.Count - 1)) + 1, 1, pts.Count - 1);
            // Height sampled continuously along the way, not interpolated between tile centres.
            hero.Position = World.StandPointAt(p.X, p.Y, floors[leg]);
            var ahead = curve.SampleBaked(Math.Min(total, s + 0.25f), true) - p;
            if (ahead.LengthSquared() > 1e-6f)
            {
                yaw = Mathf.LerpAngle(yaw, YawOf(ahead), 1f - Mathf.Exp(-TurnEase * dt));
                hero.Rotation = new Vector3(0, yaw, 0);
            }
            if (human) World.Camera.Follow(hero.Position);
            if (Verbose) GD.Print($"[H5] walk t={Time.GetTicksMsec()} s={s:F3}/{total:F3} pos=({hero.Position.X:F3},{hero.Position.Y:F3},{hero.Position.Z:F3}) yaw={Mathf.RadToDeg(yaw):F1} dt={dt:F4}");
        }
        hero.Position = World.StandPoint(m.Path[^1]);
        if (human) World.Camera.StopFollow();
        if (hero.Has("idle00")) hero.Play("idle00", loop: true);
    }

    /// <summary>On-the-spot turn rate (rad/s) and the easing rate for turning while walking.</summary>
    const float TurnRate = 2.5f * Mathf.Pi, TurnEase = 14f;

    async Task LevelUp(HeroState h)
    {
        while (h.PendingLevelUps.Count > 0)
        {
            var offer = h.PendingLevelUps[0];
            int pick = 0;
            if (Autoplay) pick = 0;
            else await Dialog(done => AdvDialogs.LevelUp(_dialogLayer, h, offer, p => { pick = p; done(); }));
            if (!Session.Apply(new ChooseLevelUpCmd(h.Id, pick)).Ok) break;
        }
    }

    async Task AnswerChoice(ChoiceOffered co)
    {
        var c = Session.S.Choice!;
        bool accept = true;
        int option = 0;
        if (!Autoplay)
        {
            switch (c.Kind)
            {
                case "chest":
                    await Dialog(done => AdvDialogs.Choose2(_dialogLayer, AdvTexts.Get(AdvTexts.ChestTitle), AdvTexts.Get(AdvTexts.ChestDesc), null,
                        (null, AdvTexts.Get(AdvTexts.ChestGold, ("gold", c.Gold))), (null, AdvTexts.Get(AdvTexts.ChestExp, ("exp", c.Experience))), p => { option = p; done(); }));
                    break;
                case "bank_search":
                    await Dialog(done => AdvDialogs.YesNo(_dialogLayer, ObjectName(c.Object), ObjectMessage(c.Object, 2), null, y => { accept = y; done(); }));
                    break;
                case "border_guard":
                {
                    // BorderguardBox (006ab6f0, 005a5d90 case 0x13): ID_BORDERGUARD texts by key colour, Accept / Refuse.
                    int col = Session.Map.Objects[c.Object].Shared.Color;
                    string Bg(string id) => BorderGuardText(id);
                    Texture2D? Tex(string name) => Game.Instance.LoadTexture($"Textures/Interface/Borderguard/{name}.xdb");
                    int pick = 1;
                    await Dialog(done => AdvDialogs.Choose2(_dialogLayer, Bg($"ID_TITLE_TEXT_{col}"), Bg($"ID_DESCRIPTION_POS_{col}"), Tex("Face_Texture"),
                        (Tex("Accept_Texture"), Bg("ID_ACCEPT_TEXT")), (Tex("Decline_Texture"), Bg("ID_DECLINE_TEXT")), p => { pick = p; done(); }, Bg("ID_HEADER_TEXT")));
                    accept = pick == 0;
                    break;
                }
                case "attack_or_defence_2" or "attack_or_defence_1" or "spellpower_or_knowledge_1" when SelectAttributeBox(c.Object) is { } sel:
                    // CSelectAttribBuildingLogEntry (005a5d90 case 0x15 → 006af7f0 / 006af4b0): DefaultStats
                    // SelectAttributeBuildings of the building type — title, choice header, description, the hero's
                    // face and the two variants' icons and texts.
                    await Dialog(done => AdvDialogs.Choose2(_dialogLayer, sel.Title, sel.Desc, Session.S.Hero(c.Hero) is { } sh ? AdvDialogs.HeroFace(sh) : null,
                        sel.V1, sel.V2, p => { option = p; done(); }, sel.Header));
                    break;
                case "monster_flee" when Session.S.Choice?.Creatures is { Count: > 0 } fleeing:
                    // CFleeFightDialogLogEntry (008e1a80): Text/Interface/JoinFlee Flee_Title / Flee_Desc; yes = Pursue!, no = Leave them.
                    await Dialog(done => AdvDialogs.YesNo(_dialogLayer, AdvTexts.Get("Text/Interface/JoinFlee/Flee_Title_Text.txt"),
                        AdvTexts.Get("Text/Interface/JoinFlee/Flee_Desc_Text.txt", ("count", fleeing.Sum(f => f.Count)), ("name", CreatureName(fleeing[0].Creature))),
                        null, y => { accept = y; done(); }));
                    break;
                case "join_free":
                    await Dialog(done => AdvDialogs.YesNo(_dialogLayer, ObjectName(c.Object), AdvTexts.Get(AdvTexts.CreaturesJoin), null, y => { accept = y; done(); }));
                    break;
                case "shipyard":
                    await Dialog(done => AdvDialogs.Shipyard(_dialogLayer, Session.S.Players.First(p => p.Index == Session.S.Hero(c.Hero)!.Owner), Session.Game.ShipyardStatus(c.Object), y => { accept = y; done(); }));
                    break;
                case "join_paid":
                    await Dialog(done => AdvDialogs.YesNo(_dialogLayer, ObjectName(c.Object), AdvTexts.Get(AdvTexts.CreaturesJoinMoney) + $"<br>{c.Gold}", null, y => { accept = y; done(); }));
                    break;
                default:
                    await Dialog(done => AdvDialogs.YesNo(_dialogLayer, ObjectName(c.Object), c.Kind.Replace('_', ' '), null, y => { accept = y; done(); }));
                    break;
            }
        }
        Session.Apply(new AnswerChoiceCmd(accept, option));
    }

    // ---------------------------------------------------------------- battles

    void StartBattle()
    {
        var pb = Session.S.Battle!;
        if (Autoplay && !AutoplayRealCombat)
        {
            Session.QuickCombat();
            return;
        }
        var state = Session.CreatePendingBattle(out var seed);
        // CCombatArenaScreen's ctor (FUN_0054c2f0, 0x0054ce2a) runs combat scripts only outside multiplayer and hotseat.
        var scriptHost = GetNodeOrNull<Campaign.ScriptUiBridge>("ScriptUiBridge")?.Host;
        Func<H5.Combat.Battle, H5.Scripting.Runtime.CombatScripting?>? scripts =
            scriptHost != null && H5.Scripting.Runtime.CombatScripting.ScriptsAllowed(Session.IsRemote, Session.IsHotSeat)
                ? battle => scriptHost.CreateCombatScripts(battle) : null;
        var combat = new AdventureCombatSession(state, seed, H5Reborn.AdvNet.HotSeatBattles.HumanSides(Session.S, pb), scripts: scripts);
        // Keep the adventure screen alive while the combat screen owns the scene.
        var tree = GetTree();
        var placeholder = new Node { Name = "AdventurePause" };
        tree.Root.AddChild(placeholder);
        tree.Root.RemoveChild(this);
        tree.CurrentScene = placeholder;
        AdvCursors.Reset();
        CombatScreen.ReturnHandler = finished => ReturnFromBattle((AdventureCombatSession)finished);
        GD.Print($"[H5] adventure battle: hero {pb.AttackerHero} vs {(pb.DefenderHero >= 0 ? $"hero {pb.DefenderHero}" : $"object {pb.DefenderObject}")} at {pb.Where}, seed {seed}");
        if (AutoplayRealCombat)
        {
            // Test mode: the combat AI plays both sides on the real combat screen, then hands back.
            CombatScreen.Autoplay = true;
            tree.Root.AddChild(new CombatCloser(combat) { Name = "CombatCloser" });
        }
        CombatScreen.Open(combat);
    }

    void ReturnFromBattle(AdventureCombatSession finished)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.CurrentScene?.QueueFree();
        tree.Root.AddChild(this);
        tree.CurrentScene = this;
        var st = finished.Battle.State;
        bool fled = st.Winner == 1 && (st.EndReason.Contains("flee", StringComparison.OrdinalIgnoreCase) || st.EndReason.Contains("surrender", StringComparison.OrdinalIgnoreCase) || st.EndReason.Contains("retreat", StringComparison.OrdinalIgnoreCase));
        Session.ResolveBattle(st, fled);
        foreach (var h in Session.S.Heroes) World.SyncHero(h);
        Hud.Refresh();
    }

    // ---------------------------------------------------------------- autoplay (tests)

    readonly SimpleAdventureBot _autoBot = new();
    int _startDay = -1;

    void AutoplayStep()
    {
        if (_startDay < 0) _startDay = Session.S.Day;
        if (Session.S.Day - _startDay >= AutoplayDays)
        {
            Autoplay = false;
            GD.Print($"[H5] adventure autoplay finished on day {Session.S.Day}, hash {Session.S.ComputeHash()}");
            AutoplayFinished?.Invoke();
            return;
        }
        _autoBot.PlayTurn(Session.Game, Session.Seed ^ (ulong)Session.S.Day);
    }

    public static event Action? AutoplayFinished;

    /// <summary>Test mode (`--adventure-real-combat`): autoplay battles run on the combat screen instead of quick combat.</summary>
    public static bool AutoplayRealCombat;

    /// <summary>
    /// Test helper (`--adventure-test-attack`): sends the selected hero at the nearest monster it
    /// can reach today; the battle then plays on the combat screen (AI on both sides) and returns.
    /// </summary>
    /// <summary>Dev: hires a hero in the first own town (if needed) and opens the meeting screen for two own heroes.</summary>
    /// <summary>Dev: teaches the selected hero the adventure spells (full mana), then opens the book or casts <paramref name="spell"/>.</summary>
    public void DevSpellbook(string? spell)
    {
        if (Selected is not { } h && MyHeroes().FirstOrDefault() is { } first) Select(first);
        if (Selected is not { } hero) return;
        foreach (var sp in new[] { H5.Adventure.Engine.Adventure.SpellTownPortal, H5.Adventure.Engine.Adventure.SpellDimensionDoor, H5.Adventure.Engine.Adventure.SpellSummonCreatures, H5.Adventure.Engine.Adventure.SpellSummonBoat })
            if (!hero.Spells.Contains(sp)) hero.Spells.Add(sp);
        hero.Mana = Session.Game.MaxMana(hero) + 100;
        if (spell == null) OpenAdventureSpellbook();
        else
        {
            CastAdventureSpell(hero, spell, null);
            foreach (var h2 in Session.S.Heroes) World.SyncHero(h2);
            if (Selected is { } moved) World.Camera.CenterOn(moved.Pos);
            Hud.Refresh();
        }
    }

    /// <summary>
    /// Dev switch `--adventure-visit-test=TYPE`: puts the selected hero on the first object of that shared type
    /// (e.g. BUILDING_HILL_FORT) and plays its visit, for screenshots of the building windows.
    /// </summary>
    public void DevVisit(string type)
    {
        var s = Session.S;
        // TYPE:own — hand the object to the human player first, without guards (own-building visits: garrison screen).
        bool own = type.EndsWith(":own");
        if (own) type = type[..^4];
        var hero = Selected ?? MyHeroes().FirstOrDefault();
        var o = Session.Map.Objects.FirstOrDefault(x => (x.Shared.Type == type || x.Kind.ToString() == type) && !(s.Objects.TryGetValue(x.Index, out var os) && os.Removed));
        if (hero == null || o == null) { GD.PushWarning($"[H5] visit test: no hero or no {type} on this map"); return; }
        if (own) { var os = s.Obj(o.Index); os.Owner = Session.Human.Index; os.Guards.Clear(); }
        var target = o.ActiveTiles.FirstOrDefault(o.Pos);
        // :own also clears wandering monsters guarding the object (dev screenshots).
        if (own)
            foreach (var m in Session.Map.Objects.Where(m => m.Kind == H5.Adventure.Map.ObjectKind.Monster && Math.Abs(m.Pos.X - target.X) <= 2 && Math.Abs(m.Pos.Y - target.Y) <= 2))
                s.Obj(m.Index).Removed = true;
        // A varied army so upgrade prices show for several tiers.
        var db = Game.Instance.Db;
        for (int tier = 1; tier <= 7; tier++)
            if (db.Creatures.Values.Where(c => c.Town == hero.Town && c.Tier == tier && !c.Upgrade && c.Upgrades.Count > 0).OrderBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault() is { } cd)
                hero.Army[tier - 1] = new Stack(cd.Id, 8 - tier);
        Session.Human.Purse = new Purse(new H5.Core.Data.Resources(50, 50, 50, 50, 50, 50, 60000));
        // Step onto the object from a free neighbouring tile, so the engine runs the real visit.
        hero.MovePoints = 5000;
        foreach (var (dx, dy) in new[] { (0, -1), (0, 1), (-1, 0), (1, 0), (-1, -1), (1, -1), (-1, 1), (1, 1) })
        {
            var from = new Tile(target.X + dx, target.Y + dy, target.Floor);
            var floor = Session.Map.Floor(from.Floor);
            if (floor == null || from.X < 0 || from.Y < 0 || from.X >= floor.Width || from.Y >= floor.Height || floor.IsBlocked(from.X, from.Y)) continue;
            hero.Pos = from;
            World.SyncHero(hero);
            World.Camera.CenterOn(from);
            Hud.Refresh();
            Session.Apply(new MoveHeroCmd(hero.Id, target) { Player = Session.Human.Index });
            return;
        }
        GD.PushWarning($"[H5] visit test: no free tile next to {type}");
    }

    public void MeetTest()
    {
        var s = Session.S;
        if (MyHeroes().Count() < 2 && s.Towns.FirstOrDefault(t => t.Owner == Session.Human.Index) is { } town)
            Session.Apply(new HireHeroCmd(town.Object, 0));
        var mine = MyHeroes().Take(2).ToList();
        // Layout check when only one own hero exists: pair it with any other hero on the map.
        if (mine.Count == 1 && s.Heroes.FirstOrDefault(h => h.Alive && h.Id != mine[0].Id) is { } any) mine.Add(any);
        if (mine.Count == 2) Hero.HeroScreen.OpenMeeting(this, mine[0], mine[1]);
        else GD.PushWarning("[H5] meet test: need two heroes");
    }

    public void TestAttackNearest()
    {
        if (Selected is not { } hero) { GD.Print("[H5] test attack: no hero"); return; }
        AutoplayRealCombat = true;
        var g = Session.Game;
        Tile? best = null;
        int bestCost = int.MaxValue;
        foreach (var o in Session.Map.OfKind(ObjectKind.Monster))
        {
            if (o.Pos.Floor != hero.Pos.Floor || Session.S.Objects.TryGetValue(o.Index, out var os) && os.Removed) continue;
            if (Math.Max(Math.Abs(o.Pos.X - hero.Pos.X), Math.Abs(o.Pos.Y - hero.Pos.Y)) > 25) continue;
            var path = g.FindPath(hero, o.Pos);
            if (path == null) continue;
            int cost = 0;
            var prev = hero.Pos;
            foreach (var t in path) { if (t == hero.Pos) continue; cost += g.StepCost(hero, prev, t); prev = t; }
            if (cost <= hero.MovePoints && cost < bestCost) { bestCost = cost; best = o.Pos; }
        }
        if (best is not { } dest) { GD.Print("[H5] test attack: no monster in reach"); return; }
        GD.Print($"[H5] test attack: hero {hero.Id} → monster at {dest} (cost {bestCost}/{hero.MovePoints})");
        Plot(hero, dest);
        GetTree().CreateTimer(1.0).Timeout += () => SendMove(hero, dest);
    }

    /// <summary>Test helper: once the battle has ended and its results are up, leaves the combat screen.</summary>
    sealed partial class CombatCloser(CombatSession session) : Node
    {
        double _after = -1;
        public override void _Process(double delta)
        {
            if (!session.Ended) return;
            if (_after < 0) _after = 2.5;
            _after -= delta;
            if (_after > 0) return;
            if (GetTree().CurrentScene is CombatScreen cs) cs.ReturnToMenu();
            QueueFree();
        }
    }

    // ---------------------------------------------------------------- texts

    /// <summary>The object's stock name (first messagesFileRef entry), for building windows.</summary>
    public string ObjectTitle(int index) => ObjectName(index);

    string ObjectName(int index)
    {
        if (index < 0 || index >= Session.Map.Objects.Count) return "";
        var o = Session.Map.Objects[index];
        if (o.Kind == ObjectKind.Town && Session.S.Town(index) is { } t && t.Name.Length > 0) return t.Name;
        var first = o.Shared.Raw?.Element("messagesFileRef")?.Elements("Item").FirstOrDefault();
        if ((string?)first?.Attribute("href") is { Length: > 0 } href && UiResources.Instance.Text(href) is { Length: > 0 } name)
            return UiResources.StripMarkup(name).Trim();
        return o.Shared.Type.Replace("BUILDING_", "").Replace('_', ' ');
    }

    /// <summary>DefaultStats SelectAttributeBuildings entry of an object's building type (Arena, School of Magic, War Academy).</summary>
    (string Title, string Header, string Desc, (Texture2D? Icon, string Text) V1, (Texture2D? Icon, string Text) V2)? SelectAttributeBox(int obj)
    {
        const string stats = "GameMechanics/RPGStats/DefaultStats.xdb";
        var type = Session.Map.Objects[obj].Shared.Type;
        var item = Game.Instance.Xdb.Load(stats).Root?.Element("SelectAttributeBuildings")?.Elements("Item").FirstOrDefault(i => (string?)i.Element("BuildingType") == type);
        if (item == null) return null;
        string T(System.Xml.Linq.XElement? e, string name) => (string?)e?.Element(name)?.Attribute("href") is { Length: > 0 } h ? UiResources.Instance.Text(H5.Core.Xdb.XdbRef.Parse(h, stats).Path) ?? "" : "";
        (Texture2D?, string) V(string name)
        {
            var v = item.Element(name);
            var icon = (string?)v?.Element("Icon64")?.Attribute("href");
            return (string.IsNullOrEmpty(icon) ? null : Game.Instance.LoadTexture(H5.Core.Xdb.XdbRef.Parse(icon, stats).Path), T(v, "TextFileRef"));
        }
        return (T(item, "TitleFileRef"), T(item, "ChoiceHeaderFileRef"), T(item, "DescFileRef"), V("Variant1"), V("Variant2"));
    }

    /// <summary>A text of UI/BorderGuard/Texts (UIGameRoot ID_BORDERGUARD) by its TextName.</summary>
    static string BorderGuardText(string name)
    {
        const string table = "UI/BorderGuard/Texts.(WindowRelatedTexts).xdb";
        var item = Game.Instance.Xdb.Load(table).Root?.Element("texts")?.Elements("Item").FirstOrDefault(i => (string?)i.Element("TextName") == name);
        var href = (string?)item?.Element("text")?.Attribute("href");
        return string.IsNullOrEmpty(href) ? "" : UiResources.Instance.Text(H5.Core.Xdb.XdbRef.Parse(href, table).Path) ?? "";
    }

    static string ArtifactName(string id) => Game.Instance.Db.Artifacts.TryGetValue(id, out var a) && a.Name.Length > 0 ? a.Name : id;

    static Texture2D? ArtifactIcon(string id)
    {
        const string table = "GameMechanics/RefTables/Artifacts.xdb";
        var item = Game.Instance.Xdb.Load(table).Root?.Element("objects")?.Elements("Item").FirstOrDefault(i => (string?)i.Element("ID") == id);
        var href = (string?)item?.Element("obj")?.Element("Icon")?.Attribute("href");
        return string.IsNullOrEmpty(href) ? null : Game.Instance.LoadTexture(H5.Core.Xdb.XdbRef.Parse(href, table).Path);
    }

    static string HeroSummary(HeroState h) =>
        $"Level {h.Level} {h.Class.Replace("HERO_CLASS_", "").ToLowerInvariant()}<br>Attack {h.Attack}  Defense {h.Defence}  Spell Power {h.SpellPower}  Knowledge {h.Knowledge}<br>" +
        string.Join("<br>", h.Army.Live().Select(st => $"{st.Count} {CreatureName(st.Creature)}"));

    string TownSummary(TownState t) =>
        $"{t.Type.Replace("TOWN_", "").ToLowerInvariant()} town, level {t.DevLevel}.<br>The town screen is not available yet.";

    static string CreatureName(string id) => Game.Instance.Db.Creatures.TryGetValue(id, out var c) && c.Name.Length > 0 ? c.Name : id;

    // ---------------------------------------------------------------- floating texts

    void FlyText(int objectIndex, string text)
    {
        var o = Session.Map.Objects[objectIndex];
        FlyTextAt(World.TileCenter(o.Pos), text);
    }

    /// <summary>A rising caption over a map position (the original's "flyoff" messages).</summary>
    void FlyTextAt(Vector3 world, string text)
    {
        // Stock sign style: Text/Interface/FlyingSignFormat_Text.txt ("<flying_signs>").
        var label = new BitmapText { Markup = $"<center>{AdvTexts.Get(AdvTexts.FlyingSignFormat)}{text}", Size = new Vector2(240, 24) };
        _flyLayer.AddChild(label);
        var start = world + new Vector3(0, 2.5f, 0);
        double t = 0;
        void Place()
        {
            var cam = World.Camera;
            if (cam.IsPositionBehind(start)) { label.Visible = false; return; }
            label.Position = cam.UnprojectPosition(start) - new Vector2(120, 24 + (float)t * 30);
        }
        Place();
        var tw = label.CreateTween();
        tw.TweenMethod(Callable.From<double>(v => { t = v; Place(); label.Modulate = new Color(1, 1, 1, (float)Math.Min(1, 2 * (1 - v))); }), 0.0, 1.0, 1.6);
        tw.TweenCallback(Callable.From(label.QueueFree));
    }
}
