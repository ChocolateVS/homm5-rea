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
        Campaign.ScriptUiBridge.Attach(this);
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

    public void ShowScenarioInfo()
    {
        if (_modal > 0) return;
        var m = Session.Map;
        var goal = m.Objectives.FirstOrDefault(o => o.Primary && o.Kind != "OBJECTIVE_KIND_SCENARIO_INFO")?.Kind ?? "OBJECTIVE_KIND_DEFEAT_ALL";
        Message(m.Name, $"{m.Description}<br><br>Day {Session.S.DayOfWeek}, week {Session.S.WeekOfMonth}, month {Session.S.Month}.<br>Goal: {GoalText(goal)}");
    }

    static string GoalText(string kind) => kind switch
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

    void Plot(HeroState hero, Tile dest)
    {
        var path = Session.Game.FindPath(hero, dest);
        if (Verbose) GD.Print($"[H5] plot hero {hero.Id} at {hero.Pos} → {dest}: {(path == null ? "no path" : $"{path.Count} steps")}");
        if (path == null || path.Count == 0) { World.ClearPath(); _plotted = null; return; }
        _plotted = dest;
        bool fight = IsFight(hero, dest);
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
        if (tile is not { } t || InputLocked || Selected is not { } hero || !World.IsExplored(t)) { AdvCursors.Set(AdvCursor.Default); return; }
        var s = Session.S;
        if (s.Heroes.Any(h => h.Alive && !h.Garrisoned && h.Pos == t && h.Owner == hero.Owner))
        {
            AdvCursors.Set(s.Heroes.First(h => h.Alive && h.Pos == t).Id == hero.Id ? AdvCursor.Default : AdvCursor.MeetHeroes);
            return;
        }
        var path = Session.Game.FindPath(hero, t);
        if (path == null) { AdvCursors.Set(AdvCursor.Default); return; }
        int days = DaysTo(hero, path);
        if (IsFight(hero, t)) AdvCursors.Set(AdvCursor.Attack, days);
        else if (Session.Game.TownAt(t) is { } town) AdvCursors.Set(town.Owner == hero.Owner ? AdvCursor.Town : AdvCursor.Attack, days);
        else if (Session.Map.Objects.Any(o => o.Kind != ObjectKind.Static && o.ActiveTiles.Contains(t) && !(s.Objects.TryGetValue(o.Index, out var os) && os.Removed)))
            AdvCursors.Set(AdvCursor.Use, days);
        else AdvCursors.Set(AdvCursor.Move, days);
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
        if (tile is { } t) Click(t, false);
    }

    void DrawFakeCursor()
    {
        if (FakeMouse is not { } p) return;
        _fakeCursor ??= new TextureRect { Name = "FakeCursor", MouseFilter = MouseFilterEnum.Ignore, ZIndex = 100 };
        if (_fakeCursor.GetParent() == null) AddChild(_fakeCursor);
        var (c, days) = AdvCursors.Current;
        if (AdvCursors.Texture(c, days) is { } cur)
        {
            _fakeCursor.Texture = cur.Tex;
            _fakeCursor.Size = cur.Tex.GetSize();
            _fakeCursor.Position = p - cur.Hot;
        }
    }

    public override void _Process(double delta)
    {
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
                Hud.Refresh();
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
                if (HumanCaused(player) && Session.Map.Objects[oc.Object].Kind is ObjectKind.Mine or ObjectKind.Dwelling)
                    FlyText(oc.Object, ObjectName(oc.Object));
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
                await Dialog(done => Message("Artifact", $"You found the {ArtifactName(ag.Artifact)}.", ArtifactIcon(ag.Artifact), done));
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
                        Hud.Screen.SetVisible("HotSeatBlackBg", true);
                        var colour = s.Players.Find(p => p.Index == ts.Player)?.Colour ?? "";
                        var name = AdvTexts.Get($"Text/Game/Player/Colors/{AdvColors.TextName(colour)}_player.txt");
                        await Dialog(done => Message("", name, null, done));
                        _hotSeat.Confirm();
                        Hud.Screen.SetVisible("HotSeatBlackBg", false);
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
                    // Stock: "Start of Week: <value=weektype>" with the moon week name and its description.
                    var week = AdvTexts.WeekName(ws.MoonWeek);
                    await Dialog(done => Message(AdvTexts.Get(AdvTexts.StartOfWeek, ("weektype", week)), AdvTexts.WeekDescription(ws.MoonWeek), null, done));
                }
                break;
            case PlayerEliminated pe:
                if (pe.Player == Session.Human.Index)
                    await Dialog(done => Message("", AdvTexts.Get(AdvTexts.Defeat), null, () => { done(); ReturnToMainMenu(); }));
                break;
            case H5.Adventure.Engine.GameEnded ge:
                bool won = ge.WinnerTeam == Session.Human.Team;
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
        if (Autoplay) GetTree().CreateTimer(0.6).Timeout += () =>
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
        float speed = World.HeroSpeed(m.HeroId) * (Autoplay ? 4 : 1);
        if (hero.Has("move")) hero.Play("move", loop: true);
        foreach (var t in m.Path)
        {
            var target = World.StandPoint(t);
            var dir = target - hero.Position;
            if (dir.LengthSquared() < 1e-4f) continue;
            // Original models face local +Z in Godot space (see ArenaGrid.FacingYaw).
            hero.Rotation = new Vector3(0, Mathf.Atan2(dir.X, dir.Z), 0);
            var tw = CreateTween();
            tw.TweenProperty(hero, "position", target, dir.Length() / speed);
            await ToSignal(tw, Tween.SignalName.Finished);
            if (HumanCaused(player)) World.Camera.CenterOn(t);
        }
        if (hero.Has("idle00")) hero.Play("idle00", loop: true);
    }

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
                case "join_free":
                    await Dialog(done => AdvDialogs.YesNo(_dialogLayer, ObjectName(c.Object), AdvTexts.Get(AdvTexts.CreaturesJoin), null, y => { accept = y; done(); }));
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
        var combat = new AdventureCombatSession(state, seed, new[] { 0 });
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
