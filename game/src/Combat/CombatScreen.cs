using Godot;
using H5.Assets.Model;
using H5.Combat;
using H5Reborn.Arena;
using H5Reborn.Render;
using H5Reborn.Ui;

namespace H5Reborn.Combat;

/// <summary>
/// The combat screen: an original arena with animated units, the stock combat HUD
/// (UI/CombatScreen-Heavy), mouse input with the original cursors and cell
/// highlights, and sequential playback of authoritative battle events. Works for
/// local vs-AI duels and networked duels through <see cref="CombatSession"/>.
/// </summary>
public partial class CombatScreen : Control
{
    /// <summary>Set before opening: when true, local AI also plays the local sides (automated tests).</summary>
    public static bool Autoplay;
    /// <summary>Optional: quit the application once the results are shown (automated tests).</summary>
    public static bool QuitWhenDone;

    public CombatSession Session { get; }
    public ArenaScene Arena { get; private set; } = null!;
    public CombatHud Hud { get; private set; } = null!;

    readonly Dictionary<int, UnitView> _units = new();
    readonly Dictionary<int, Node3D> _heroes = new();
    Control _plaqueLayer = null!;
    FlyingSigns _signs = null!;
    readonly Queue<BattleEvent> _queue = new();
    bool _playing;
    double _attackRemainderUntil;
    readonly IBattleAI _autoAi = new GreedyBattleAI { MaxCandidates = 16 };
    InputMode _mode = InputMode.Normal;
    SpellOption? _pendingSpell;
    AbilityOption? _pendingAbility;
    int? _deploySelected;
    bool _resultsShown;

    enum InputMode { Normal, Spell, Ability }

    public CombatScreen(CombatSession session)
    {
        Session = session;
        Name = "CombatScreen";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public static void Open(CombatSession session)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.CurrentScene?.QueueFree();
        var screen = new CombatScreen(session);
        tree.Root.AddChild(screen);
        tree.CurrentScene = screen;
    }

    public override void _Ready()
    {
        // Like the front end, the original HUD is laid out in real pixels.
        GetTree().Root.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;

        Arena = ArenaScene.Create(PickArena(Session.Seed));
        AddChild(Arena);
        PlaceObstacles();

        _plaqueLayer = new Control { Name = "Plaques", AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_plaqueLayer);
        _signs = new FlyingSigns();
        AddChild(_signs);
        _signs.Attach(Arena.Camera);

        foreach (var u in Session.Battle.State.Units)
            if (!u.IsObject && (u.Alive || u.IsWarMachine)) AddUnitView(u);
        for (int side = 0; side < 2; side++) AddHeroView(side);

        Hud = new CombatHud(this);
        AddChild(Hud);
        Hud.Log.AddLine(CombatLogText.StartCombat());

        foreach (var e in Session.Opening) _queue.Enqueue(e);
        RefreshAll();
        if (CombatLauncher.SkipTactics) CmdEndTactics();
        if (CombatLauncher.InfoUnit is int info && Session.Battle.State.Unit(info) is { } iu) Hud.ShowUnitInfo(iu);
        if (CombatLauncher.FakeMouse != null)
        {
            _fakeCursor = new TextureRect { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 100 };
            AddChild(_fakeCursor);
        }
    }

    TextureRect? _fakeCursor;
    int _attackCount;

    Vector2 MousePos => CombatLauncher.FakeMouse ?? GetViewport().GetMousePosition();

    void DrawFakeCursor(CombatCursor c)
    {
        if (_fakeCursor == null || CombatLauncher.FakeMouse is not { } p) return;
        if (CombatCursors.Texture(c) is { } cur)
        {
            _fakeCursor.Texture = cur.Tex;
            _fakeCursor.Position = p - cur.Hotspot;
            _fakeCursor.Size = cur.Tex.GetSize();
        }
    }

    /// <summary>Deterministic arena choice from the battle seed (both network peers agree).</summary>
    static string PickArena(ulong seed)
    {
        var all = ArenaDefinition.ListArenas()
            .Where(p => p.Contains("Grass", StringComparison.OrdinalIgnoreCase) && !p.Contains("Beach", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (all.Count == 0) all = ArenaDefinition.ListArenas().ToList();
        return all[(int)(seed % (ulong)all.Count)];
    }

    // ---------------------------------------------------------------- scene setup

    void PlaceObstacles()
    {
        // The engine places its own obstacles (BattleState.Obstacles); show them with the
        // original 1x1 grass-arena obstacle models.
        var models = new[] { "Rock1x1_1", "Rock1x1_2", "Rock1x1_3", "Rock1x1_4" };
        var loader = new ModelLoader(Game.Instance.Xdb);
        var cache = new Dictionary<string, ArrayMesh?>();
        foreach (var c in Session.Battle.State.Obstacles)
        {
            var name = models[(c.X * 7 + c.Y * 13) % models.Length];
            var obstacle = $"ArenaObjects/_(ArenaObstacle)/GrassArena/{name}.xdb";
            if (!cache.TryGetValue(name, out var mesh))
            {
                mesh = null;
                try
                {
                    if (Game.Instance.Fs.Exists(obstacle))
                    {
                        var r = H5.Core.Xdb.XdbRef.Parse((string?)Game.Instance.Xdb.Load(obstacle).Root!.Element("Model")?.Attribute("href"), obstacle);
                        if (!r.IsEmpty) mesh = ModelBuilder.BuildMesh(loader.Load(r.Path), skinned: false);
                    }
                }
                catch (Exception e) { GD.PushWarning($"[H5] obstacle {name}: {e.Message}"); }
                cache[name] = mesh;
            }
            if (mesh == null) continue;
            Arena.AddChild(new MeshInstance3D
            {
                Name = $"Obstacle_{c.X}_{c.Y}", Mesh = mesh,
                Position = Arena.Grid.CellCenter(new Vector2I(c.X, c.Y)),
                Rotation = new Vector3(0, (c.X * 3 + c.Y) * 0.7f, 0),
            });
        }
    }

    UnitView AddUnitView(Unit u)
    {
        var view = new UnitView(u);
        Arena.Units.AddChild(view);
        view.Build(Arena, _plaqueLayer);
        view.Idle();
        _units[u.Id] = view;
        return view;
    }

    void AddHeroView(int side)
    {
        var h = Session.Battle.State.Heroes[side];
        if (!h.Present) return;
        string? characterPath = null;
        if (Game.Instance.Db.Heroes.TryGetValue(h.HeroId, out var def) && Game.Instance.Fs.Exists(def.SourcePath))
        {
            var root = Game.Instance.Xdb.Load(def.SourcePath).Root!;
            var r = H5.Core.Xdb.XdbRef.Parse((string?)root.Element("HeroCharacterArena")?.Attribute("href"), def.SourcePath);
            if (!r.IsEmpty) characterPath = r.Path;
        }
        if (characterPath == null || !Game.Instance.Fs.Exists(characterPath)) return;
        try
        {
            var c = CharacterBuilder.Build(characterPath);
            c.Name = $"Hero{side}";
            // Heroes watch from behind their army's flank, outside the grid.
            float gx = side == 0 ? -2.0f : ArenaGrid.Width + 2.0f;
            float gy = ArenaGrid.Height / 2f;
            c.Position = Arena.Grid.CellCenter(gx, gy);
            c.Rotation = new Vector3(0, ArenaGrid.FacingYaw(side), 0);
            Arena.Units.AddChild(c);
            _heroes[side] = c;
        }
        catch (Exception e) { GD.PushWarning($"[H5] hero model {characterPath}: {e.Message}"); }
    }

    public UnitView? View(int id) => _units.GetValueOrDefault(id);

    // ---------------------------------------------------------------- frame loop

    public override void _Process(double delta)
    {
        foreach (var v in _units.Values) v.PositionPlaque(Arena.Camera);

        if (!_playing)
        {
            if (_queue.Count > 0) { _ = RunPlayback(); return; }
            if (Session.TryDequeue(out var batch))
            {
                foreach (var e in batch) _queue.Enqueue(e);
                _ = RunPlayback();
                return;
            }
            if (Session.Ended) { ShowResults(); return; }
            Session.Tick();
            if (Session.TryDequeue(out batch))
            {
                foreach (var e in batch) _queue.Enqueue(e);
                _ = RunPlayback();
                return;
            }
            if (Autoplay || CombatLauncher.AutoplayTurns > 0) AutoplayStep();
            AimDevMouse();
            UpdateHover();
        }
        Hud.SetDesync(Session.Desynced);
    }

    void AutoplayStep()
    {
        var b = Session.Battle;
        if (Session.Busy || Session.Ended) return;
        if (b.State.Phase == BattlePhase.Tactics)
        {
            foreach (var side in Session.LocalSides)
                if (!b.State.TacticsDone[side]) { Session.Submit(new EndTacticsCmd { Side = side }); return; }
            return;
        }
        if (b.SideToAct is int s && Session.LocalSides.Contains(s))
        {
            Session.Submit(_autoAi.Decide(b, s) with { Side = s });
            if (!Autoplay && CombatLauncher.AutoplayTurns > 0) CombatLauncher.AutoplayTurns--;
        }
    }

    /// <summary>Dev aid (`--hover-target`): aims the simulated mouse at a legal target on our turn.</summary>
    void AimDevMouse()
    {
        if (CombatLauncher.HoverTarget is not { } kind || MySideToAct() is null || CombatLauncher.AutoplayTurns > 0) return;
        var la = Session.Battle.GetLegalActions();
        if (la == null) return;
        int? id = kind == "shoot" ? la.Shoot.Cast<int?>().FirstOrDefault() : la.Melee.Keys.Cast<int?>().FirstOrDefault();
        if (id is not int tid || Session.Battle.State.Unit(tid) is not { } t) return;
        var p = Arena.Camera.UnprojectPosition(Arena.Grid.StackCenter(new Vector2I(t.Pos.X, t.Pos.Y), t.Size) + new Vector3(0, 0.3f, 0));
        CombatLauncher.FakeMouse = p + new Vector2(-6, 4);
        CombatLauncher.HoverTarget = null;
        if (_fakeCursor == null) { _fakeCursor = new TextureRect { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 100 }; AddChild(_fakeCursor); }
        _lastHover = null;
    }

    // ---------------------------------------------------------------- playback

    async Task RunPlayback()
    {
        _playing = true;
        ClearHighlights();
        CombatCursors.Set(CombatCursor.Wait);
        try
        {
            while (_queue.Count > 0)
            {
                var e = _queue.Dequeue();
                try { await Play(e); }
                catch (Exception ex) { GD.PushError($"[H5] playback {e.GetType().Name}: {ex}"); }
            }
            await FinishAttack();
        }
        finally
        {
            _playing = false;
            _lastHover = null;
            SyncWithState();
            RefreshAll();
            CombatCursors.Set(CombatCursor.Default);
        }
    }

    double Now => Time.GetTicksMsec() / 1000.0;

    async Task Delay(double seconds)
    {
        if (seconds > 0) await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    async Task FinishAttack()
    {
        var remaining = _attackRemainderUntil - Now;
        _attackRemainderUntil = 0;
        if (remaining > 0) await Delay(remaining);
        foreach (var v in _units.Values) if (!v.Dead && v.Character?.Player.IsPlaying() == false) v.Idle();
    }

    async Task Play(BattleEvent e)
    {
        var s = Session.Battle.State;
        switch (e)
        {
            case TurnStarted t:
                await FinishAttack();
                Hud.OnTurnStarted(t.Actor);
                break;

            case PhaseChanged p:
                Hud.OnPhase(p.Phase);
                break;

            case Deployed d when View(d.UnitId) is { } dv:
                dv.Cell = d.Cell;
                dv.Position = Arena.Grid.StackCenter(new Vector2I(d.Cell.X, d.Cell.Y), dv.Size);
                break;

            case Moved m when View(m.UnitId) is { } mv:
                await FinishAttack();
                await AnimateMove(mv, m.Path, m.Flying);
                break;

            case Teleported tp when View(tp.UnitId) is { } tv:
                tv.Cell = tp.To;
                tv.Position = Arena.Grid.StackCenter(new Vector2I(tp.To.X, tp.To.Y), tv.Size);
                break;

            case AttackStarted a:
                await FinishAttack();
                await AnimateAttack(a);
                break;

            case Damaged d:
                OnDamaged(d);
                break;

            case Missed mi when View(mi.Target) is { } missed:
                _signs.Event(missed, missed.SignAnchor, "Miss");
                break;

            case Healed h when View(h.Target) is { } hv:
                hv.Count = h.CountNow;
                hv.Dead = false;
                hv.Visible = true;
                hv.UpdatePlaque();
                if (h.Resurrected > 0) _signs.Casualty(hv, hv.SignAnchor, h.Resurrected);
                break;

            case UnitDied ud when View(ud.UnitId) is { } dead:
                dead.Dead = true;
                dead.Count = 0;
                dead.UpdatePlaque();
                if (dead.Has("death")) dead.Play("death");
                else dead.Visible = false;
                break;

            case UnitSummoned us:
                if (s.Unit(us.UnitId) is { } su && View(us.UnitId) == null)
                {
                    var v = AddUnitView(su);
                    v.Cell = us.Cell;
                    v.Count = us.Count;
                    v.Position = Arena.Grid.StackCenter(new Vector2I(us.Cell.X, us.Cell.Y), v.Size);
                    if (v.Has("birth")) v.Play("birth");
                    v.UpdatePlaque();
                }
                Hud.Log.AddLine(CombatLogText.Summoned(this, us));
                break;

            case SpellCast sc:
                await FinishAttack();
                await AnimateSpell(sc);
                break;

            case SpellResisted sr:
                Hud.Log.AddLine(CombatLogText.Resisted(this, sr));
                if (View(sr.Target) is { } rv) _signs.Event(rv, rv.SignAnchor, "SpellResisted");
                break;

            case MoraleProc mp:
                Hud.Log.AddLine(CombatLogText.Morale(this, mp));
                break;

            case LuckProc lp:
                Hud.PendingLuck = lp.Good ? 1 : -1;
                break;

            case Waited w:
                Hud.Log.AddLine(CombatLogText.Simple(this, "Wait", w.Actor));
                break;

            case Defended df:
                Hud.Log.AddLine(CombatLogText.Simple(this, "Defend", df.Actor));
                break;

            case TurnSkipped ts:
                Hud.Log.AddLine($"<body>{CombatLogText.ActorName(this, ts.Actor)}: {CombatLogText.Escape(ts.Reason)}");
                break;

            case AbilityTriggered at:
                Hud.Log.AddLine(CombatLogText.Ability(this, at));
                if (at.Ability == "ABILITY_DEADLY_STRIKE" && View(at.Target ?? at.UnitId) is { } dsv) _signs.Event(dsv, dsv.SignAnchor, "DeadlyStrike");
                break;

            case EffectAdded or EffectRemoved:
                if (e is EffectAdded ea && View(ea.UnitId) is { } eav) UpdateEffectState(eav);
                if (e is EffectRemoved er && View(er.UnitId) is { } erv) UpdateEffectState(erv);
                break;

            case BattleEnded be:
                await FinishAttack();
                Hud.Log.AddLine(CombatLogText.Ended(this, be.Winner));
                foreach (var v in _units.Values)
                    if (!v.Dead && v.Side == be.Winner && v.Has("happy")) v.Play("happy");
                break;
        }
    }

    void UpdateEffectState(UnitView v)
    {
        if (Session.Battle.State.Unit(v.UnitId) is not { } u) return;
        bool pos = u.Effects.Any(x => x.Positive && x.SpellLevel > 0), neg = u.Effects.Any(x => !x.Positive && x.SpellLevel > 0);
        v.EffectState = pos && neg ? 3 : neg ? 1 : pos ? 2 : 0;
        v.UpdatePlaque();
    }

    async Task AnimateMove(UnitView v, IReadOnlyList<Cell> path, bool flying)
    {
        if (path.Count == 0) return;
        const float cellsPerSecond = 3.2f;
        v.Play(v.Has("move") ? "move" : "idle00", loop: true);
        if (flying)
        {
            // Flyers lift off, travel straight, land (the original uses flyby sequences).
            var from = v.Position;
            var to = Arena.Grid.StackCenter(new Vector2I(path[^1].X, path[^1].Y), v.Size);
            v.FaceTowards(to);
            float dist = from.DistanceTo(to);
            float t = Math.Max(0.4f, dist / (cellsPerSecond * 1.6f));
            var tw = CreateTween();
            tw.TweenMethod(Callable.From<float>(k =>
            {
                var p = from.Lerp(to, k);
                p.Y += Mathf.Sin(k * Mathf.Pi) * Math.Min(2.5f, dist * 0.3f);
                v.Position = p;
            }), 0f, 1f, t);
            await ToSignal(tw, Tween.SignalName.Finished);
        }
        else
        {
            foreach (var c in path)
            {
                var to = Arena.Grid.StackCenter(new Vector2I(c.X, c.Y), v.Size);
                if (to.DistanceTo(v.Position) < 0.01f) continue;
                v.FaceTowards(to);
                var tw = CreateTween();
                tw.TweenProperty(v, "position", to, v.Position.DistanceTo(to) / cellsPerSecond);
                await ToSignal(tw, Tween.SignalName.Finished);
            }
        }
        v.Cell = path[^1];
        v.Idle();
    }

    async Task AnimateAttack(AttackStarted a)
    {
        var target = View(a.Target);
        Node3D? attackerNode = Actor.IsHero(a.Attacker) ? _heroes.GetValueOrDefault(Actor.HeroSide(a.Attacker)) : View(a.Attacker);
        if (attackerNode is UnitView av && target != null)
        {
            av.FaceTowards(target.GlobalPosition);
            if (a.Kind != AttackKind.Ranged) target.FaceTowards(av.GlobalPosition);
        }
        string kind = a.Kind switch
        {
            AttackKind.Ranged => "rangeattack",
            AttackKind.Hero => "attack00",
            _ => "attack00",
        };
        if (attackerNode is UnitView u)
        {
            if (!u.Has(kind)) kind = u.Has("attack00") ? "attack00" : "attack";
            if (a.Kind is AttackKind.Melee or AttackKind.Retaliation && u.Has("attack01") && (Session.Battle.State.TurnCounter + a.Target) % 2 == 1)
                kind = "attack01";
            u.Play(kind);
            PlayAnimSound(u, kind);
            float action = u.ActionTime(kind), length = u.Length(kind);
            await Delay(Math.Max(0.15f, action));
            if (CombatLauncher.AttackScreenshot is { } shot && ++_attackCount == shot.N)
            {
                GetViewport().GetTexture().GetImage().SavePng(shot.Path);
                GD.Print($"[H5] attack screenshot {shot.Path} ({kind})");
            }
            _attackRemainderUntil = Now + Math.Max(0, length - action);
        }
        else if (attackerNode is Character hero)
        {
            var heroKind = hero.Has("cast") ? "cast" : "attack00";
            hero.Play(heroKind);
            await Delay(0.6);
            _attackRemainderUntil = Now + 0.6;
        }
        else await Delay(0.25);
    }

    void OnDamaged(Damaged d)
    {
        if (View(d.Target) is not { } v) return;
        v.Count = d.CountLeft;
        if (d.CountLeft > 0)
        {
            v.Play("hit");
            PlayAnimSound(v, "hit");
        }
        v.UpdatePlaque();
        if (d.Killed > 0) _signs.Casualty(v, v.SignAnchor, -d.Killed);
        Hud.Log.AddLine(CombatLogText.Damage(this, d, Hud.PendingLuck));
        Hud.PendingLuck = 0;
    }

    async Task AnimateSpell(SpellCast sc)
    {
        Hud.Log.AddLine(CombatLogText.Spell(this, sc));
        if (Actor.IsHero(sc.Caster) && _heroes.GetValueOrDefault(Actor.HeroSide(sc.Caster)) is Character hero && hero.Has("cast"))
            hero.Play("cast");
        else if (View(sc.Caster) is { } cv && cv.Has("cast"))
            cv.Play("cast");
        await Delay(0.5);
        // Spell visual effects use the undecoded particle format; flash the affected stacks.
        foreach (var id in sc.Affected)
            if (View(id) is { } v) Flash(v);
        await Delay(0.35);
    }

    void Flash(UnitView v)
    {
        var light = new OmniLight3D { LightColor = new Color(0.7f, 0.8f, 1f), LightEnergy = 4f, OmniRange = 2.5f, Position = new Vector3(0, 1.2f, 0) };
        v.AddChild(light);
        var tw = CreateTween();
        tw.TweenProperty(light, "light_energy", 0f, 0.6f);
        tw.TweenCallback(Callable.From(light.QueueFree));
    }

    void PlayAnimSound(UnitView v, string kind)
    {
        var path = CreatureModels.CharacterPathFor(v.CreatureId);
        if (path == null) return;
        try
        {
            var set = new AnimSetLoader(Game.Instance.Xdb).ReadCharacter(path).ArenaAnimSet;
            if (set == null) return;
            var entry = new AnimSetLoader(Game.Instance.Xdb).ReadAnimSet(set).FirstOrDefault(x => x.Kind == kind);
            if (entry?.SoundHref is { } snd) Audio.SoundLibrary.Play(snd, v);
        }
        catch (Exception e) { GD.PushWarning($"[H5] sound {kind}: {e.Message}"); }
    }

    /// <summary>After a batch, snap views to the authoritative state (positions, counts, deaths).</summary>
    void SyncWithState()
    {
        var s = Session.Battle.State;
        foreach (var u in s.Units)
        {
            if (u.IsObject) continue;
            if (!_units.TryGetValue(u.Id, out var v))
            {
                if (u.Alive) AddUnitView(u);
                continue;
            }
            if (u.Alive)
            {
                if (v.Dead) { v.Dead = false; v.Visible = true; v.Idle(); }
                if (v.Cell != u.Pos)
                {
                    v.Cell = u.Pos;
                    v.Position = Arena.Grid.StackCenter(new Vector2I(u.Pos.X, u.Pos.Y), v.Size);
                }
                v.Count = u.Count;
            }
            else if (!v.Dead)
            {
                v.Dead = true;
                v.Count = 0;
                if (u.Removed) v.Visible = false; else v.Play("death");
            }
            v.UpdatePlaque();
        }
    }

    void RefreshAll()
    {
        Hud.Refresh();
    }

    // ---------------------------------------------------------------- input

    int? MySideToAct()
    {
        var b = Session.Battle;
        if (Autoplay || CombatLauncher.AutoplayTurns > 0 || Session.Busy || Session.Ended) return null;
        if (b.State.Phase == BattlePhase.Tactics)
            return Session.LocalSides.FirstOrDefault(sd => !b.State.TacticsDone[sd], -1) is var t && t >= 0 ? t : null;
        return b.SideToAct is int side && Session.LocalSides.Contains(side) ? side : null;
    }

    Vector2I? _lastHover;
    Vector2 _lastMouse;

    void UpdateHover()
    {
        var mouse = MousePos;
        if (Hud.IsOverHud(mouse))
        {
            CombatCursors.Set(CombatCursor.Default);
            ShowStaticHighlights(null);
            return;
        }
        var cell = Arena.CellAtScreen(mouse);
        if (cell == _lastHover && mouse.DistanceTo(_lastMouse) < 2) return;
        _lastHover = cell;
        _lastMouse = mouse;
        var action = Resolve(cell, mouse, out var cursor, out var hl);
        CombatCursors.Set(cursor);
        DrawFakeCursor(cursor);
        ShowStaticHighlights(hl);
        Hud.SetPrediction(action?.Describe(this));
    }

    void ClearHighlights() => Arena.Overlay.ClearMarks();

    /// <summary>Movement area, active stack and any hover marks (original grid materials).</summary>
    void ShowStaticHighlights(List<(Cell, CellMark)>? hover)
    {
        ClearHighlights();
        var b = Session.Battle;
        if (MySideToAct() is not int side) return;
        if (b.State.Phase == BattlePhase.Tactics)
        {
            var (minX, maxX) = b.DeployZone(side);
            for (int x = minX; x <= maxX; x++)
            for (int y = 0; y < b.State.Height; y++)
                Arena.Overlay.SetMark(new Vector2I(x, y), CellMark.Aimable);
            if (_deploySelected is int sel && b.State.Unit(sel) is { } su)
                foreach (var c in su.Cells()) Arena.Overlay.SetMark(new Vector2I(c.X, c.Y), CellMark.Select2);
        }
        else
        {
            var la = b.GetLegalActions();
            if (la != null && _mode == InputMode.Normal)
            {
                foreach (var c in la.Moves) foreach (var fc in Footprint(la.Actor, c)) Arena.Overlay.SetMark(new Vector2I(fc.X, fc.Y), CellMark.Aimable);
                if (!la.IsHero && b.State.Unit(la.Actor) is { } au)
                    foreach (var c in au.Cells()) Arena.Overlay.SetMark(new Vector2I(c.X, c.Y), CellMark.Select2);
            }
        }
        if (hover != null) foreach (var (c, m) in hover) Arena.Overlay.SetMark(new Vector2I(c.X, c.Y), m);
    }

    IEnumerable<Cell> Footprint(int actor, Cell topLeft)
    {
        int size = Session.Battle.State.Unit(actor)?.Size ?? 1;
        for (int dx = 0; dx < size; dx++) for (int dy = 0; dy < size; dy++) yield return new Cell(topLeft.X + dx, topLeft.Y + dy);
    }

    /// <summary>A command the player would issue by clicking, with a description for the prediction box.</summary>
    sealed record PlannedAction(BattleCommand Command, string Kind, int? Target)
    {
        public string Describe(CombatScreen s) => Kind + (Target is int t ? $": {CombatLogText.UnitName(s, t)}" : "");
    }

    PlannedAction? Resolve(Vector2I? hoverCell, Vector2 mouse, out CombatCursor cursor, out List<(Cell, CellMark)> hl)
    {
        cursor = CombatCursor.Default;
        hl = new();
        if (MySideToAct() is not int side || hoverCell is not { } hc) return null;
        var b = Session.Battle;
        var s = b.State;
        var cell = new Cell(hc.X, hc.Y);
        var occupant = s.At(cell);

        if (s.Phase == BattlePhase.Tactics)
        {
            var (minX, maxX) = b.DeployZone(side);
            if (occupant != null && occupant.Side == side && !occupant.IsWarMachine) { cursor = CombatCursor.Interact; return null; }
            if (_deploySelected is int sel && s.Unit(sel) is { } su && cell.X >= minX && cell.X <= maxX)
            {
                var dest = new Cell(Math.Min(cell.X, maxX - su.Size + 1), Math.Min(cell.Y, s.Height - su.Size));
                foreach (var c in su.CellsAt(dest)) hl.Add((c, CellMark.Select));
                cursor = CombatCursor.Move;
                return new PlannedAction(new DeployCmd(sel, dest) { Side = side }, "Deploy", null);
            }
            return null;
        }

        var la = b.GetLegalActions();
        if (la == null) return null;

        if (_mode == InputMode.Spell && _pendingSpell is { } sp)
        {
            switch (sp.Targeting)
            {
                case Battle.SpellTargeting.Unit:
                case Battle.SpellTargeting.UnitAndCell:
                    if (occupant != null && sp.Targets.Contains(occupant.Id))
                    {
                        cursor = CombatCursor.Cast;
                        foreach (var c in occupant.Cells()) hl.Add((c, CellMark.Affected));
                        return new PlannedAction(new CastSpellCmd(la.Actor, sp.Spell, occupant.Id, sp.Targeting == Battle.SpellTargeting.UnitAndCell ? occupant.Pos : null) { Side = side }, SpellName(sp.Spell), occupant.Id);
                    }
                    cursor = CombatCursor.CantCast;
                    return null;
                case Battle.SpellTargeting.Cell:
                    cursor = CombatCursor.Cast;
                    hl.Add((cell, CellMark.Affected));
                    return new PlannedAction(new CastSpellCmd(la.Actor, sp.Spell, null, cell) { Side = side }, SpellName(sp.Spell), null);
            }
        }
        if (_mode == InputMode.Ability && _pendingAbility is { } ab)
        {
            if (ab.Targeting == Battle.AbilityTargeting.Cell)
            {
                cursor = CombatCursor.Ability;
                hl.Add((cell, CellMark.Affected));
                return new PlannedAction(new UseAbilityCmd(la.Actor, ab.Ability, null, cell) { Side = side }, ab.Ability, null);
            }
            var targetId = occupant?.Id ?? (ab.Targeting == Battle.AbilityTargeting.DeadFriend
                ? s.Units.FirstOrDefault(u => !u.Alive && u.Pos == cell && ab.Targets.Contains(u.Id))?.Id : null);
            if (targetId is int tid && ab.Targets.Contains(tid))
            {
                cursor = CombatCursor.Ability;
                hl.Add((cell, CellMark.Affected));
                return new PlannedAction(new UseAbilityCmd(la.Actor, ab.Ability, tid) { Side = side }, ab.Ability, tid);
            }
            cursor = CombatCursor.CantCast;
            return null;
        }

        if (la.IsHero)
        {
            if (occupant != null && la.HeroAttack.Contains(occupant.Id))
            {
                cursor = CombatCursor.AttackRight;
                foreach (var c in occupant.Cells()) hl.Add((c, CellMark.Affected));
                return new PlannedAction(new HeroAttackCmd(occupant.Id) { Side = side }, "Hero attack", occupant.Id);
            }
            return null;
        }

        var me = s.Unit(la.Actor)!;
        if (occupant != null && occupant.Side != side && occupant.Alive)
        {
            if (la.Shoot.Contains(occupant.Id))
            {
                bool far = Distance(me, occupant) > s.Width / 2.0 && !me.Has("ABILITY_NO_RANGE_PENALTY");
                cursor = far ? CombatCursor.ShootPenalty : CombatCursor.Shoot;
                foreach (var c in occupant.Cells()) hl.Add((c, CellMark.Affected));
                return new PlannedAction(new ShootCmd(me.Id, occupant.Id) { Side = side }, "Shoot", occupant.Id);
            }
            if (la.Melee.TryGetValue(occupant.Id, out var froms) && froms.Count > 0)
            {
                var from = PickAttackFrom(me, occupant, froms, mouse);
                cursor = AttackCursor(from, me.Size, occupant);
                foreach (var c in me.CellsAt(from)) hl.Add((c, CellMark.Select));
                foreach (var c in occupant.Cells()) hl.Add((c, CellMark.Affected));
                return new PlannedAction(new AttackCmd(me.Id, occupant.Id, from == me.Pos ? null : from) { Side = side }, "Attack", occupant.Id);
            }
            cursor = CombatCursor.Default;
            return null;
        }
        if (occupant == null)
        {
            var dest = la.Moves.Where(p => me.CellsAt(p).Contains(cell))
                .OrderBy(p => p == cell ? 0 : 1).ThenBy(p => Math.Abs(p.X - cell.X) + Math.Abs(p.Y - cell.Y)).Cast<Cell?>().FirstOrDefault();
            if (dest is Cell d)
            {
                cursor = me.Flying ? CombatCursor.Fly : CombatCursor.Move;
                foreach (var c in me.CellsAt(d)) hl.Add((c, CellMark.Select));
                return new PlannedAction(new MoveCmd(me.Id, d) { Side = side }, "Move", null);
            }
        }
        return null;
    }

    static double Distance(Unit a, Unit b)
    {
        var (ax, ay) = a.Center; var (bx, by) = b.Center;
        return Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
    }

    /// <summary>The original picks the strike position from where on the target the mouse is.</summary>
    Cell PickAttackFrom(Unit me, Unit target, List<Cell> froms, Vector2 mouse)
    {
        var tCenter = Arena.Grid.StackCenter(new Vector2I(target.Pos.X, target.Pos.Y), target.Size);
        var screenCenter = Arena.Camera.UnprojectPosition(tCenter);
        var dir = mouse - screenCenter;
        if (dir.Length() < 3) return froms.Contains(me.Pos) ? me.Pos : froms[0];
        Cell best = froms[0];
        float bestScore = float.MinValue;
        foreach (var f in froms)
        {
            var fp = Arena.Camera.UnprojectPosition(Arena.Grid.StackCenter(new Vector2I(f.X, f.Y), me.Size));
            var d = fp - screenCenter;
            float score = d.Normalized().Dot(dir.Normalized()) - (f == me.Pos ? -0.05f : 0);
            if (score > bestScore) { bestScore = score; best = f; }
        }
        return best;
    }

    /// <summary>Sword cursor pointing from the strike position towards the target (grid directions).</summary>
    CombatCursor AttackCursor(Cell from, int size, Unit target)
    {
        var a = Arena.Camera.UnprojectPosition(Arena.Grid.StackCenter(new Vector2I(from.X, from.Y), size));
        var b = Arena.Camera.UnprojectPosition(Arena.Grid.StackCenter(new Vector2I(target.Pos.X, target.Pos.Y), target.Size));
        var d = b - a;
        float ang = Mathf.RadToDeg(Mathf.Atan2(d.Y, d.X));   // screen space, y down
        int sector = (int)Mathf.PosMod(Mathf.Round(ang / 45f), 8);
        return sector switch
        {
            0 => CombatCursor.AttackRight,
            1 => CombatCursor.AttackRightDown,
            2 => CombatCursor.AttackDown,
            3 => CombatCursor.AttackLeftDown,
            4 => CombatCursor.AttackLeft,
            5 => CombatCursor.AttackLeftUp,
            6 => CombatCursor.AttackUp,
            _ => CombatCursor.AttackRightUp,
        };
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } mb) return;
        if (_playing) return;
        var cell = Arena.CellAtScreen(mb.Position);
        if (mb.ButtonIndex == MouseButton.Right)
        {
            if (_mode != InputMode.Normal) { CancelTargeting(); return; }
            if (cell is { } rc && Session.Battle.State.At(new Cell(rc.X, rc.Y)) is { } info) Hud.ShowUnitInfo(info);
            return;
        }
        if (mb.ButtonIndex != MouseButton.Left) return;
        if (MySideToAct() is not int side) return;
        var s = Session.Battle.State;
        if (s.Phase == BattlePhase.Tactics && cell is { } tc && s.At(new Cell(tc.X, tc.Y)) is { } own && own.Side == side && !own.IsWarMachine)
        {
            _deploySelected = own.Id;
            _lastHover = null;
            ShowStaticHighlights(null);
            return;
        }
        var action = Resolve(cell, mb.Position, out _, out _);
        if (action == null) return;
        Submit(action.Command);
        GetViewport().SetInputAsHandled();
    }

    public void Submit(BattleCommand cmd)
    {
        _mode = InputMode.Normal;
        _pendingSpell = null;
        _pendingAbility = null;
        _lastHover = null;
        if (cmd is DeployCmd) _deploySelected = null;
        Session.Submit(cmd);
        if (Session.LastRejection is { } why) Hud.Log.AddLine($"<body><color=FFFF6040>{CombatLogText.Escape(why)}");
    }

    void CancelTargeting()
    {
        _mode = InputMode.Normal;
        _pendingSpell = null;
        _pendingAbility = null;
        _lastHover = null;
    }

    // ---------------------------------------------------------------- HUD commands

    internal void CmdWait()
    {
        if (MySideToAct() is int side && Session.Battle.GetLegalActions() is { CanWait: true } la)
            Submit(new WaitCmd(la.Actor) { Side = side });
    }

    internal void CmdDefend()
    {
        if (MySideToAct() is int side && Session.Battle.GetLegalActions() is { } la)
        {
            if (la.CanDefend) Submit(new DefendCmd(la.Actor) { Side = side });
            else Submit(new WaitCmd(la.Actor) { Side = side });
        }
    }

    internal void CmdEndTactics()
    {
        var s = Session.Battle.State;
        foreach (var side in Session.LocalSides)
            if (s.Phase == BattlePhase.Tactics && !s.TacticsDone[side]) Submit(new EndTacticsCmd { Side = side });
    }

    internal void CmdSurrender()
    {
        if (Session.Ended) return;
        int side = MySideToAct() ?? Session.ViewSide;
        Submit(new SurrenderCmd { Side = side });
    }

    internal void CmdAutoCombat()
    {
        Autoplay = !Autoplay;
        Hud.Log.AddLine(Autoplay ? "<body>Auto combat on." : "<body>Auto combat off.");
    }

    internal IReadOnlyList<SpellOption> AvailableSpells()
    {
        if (MySideToAct() is null) return Array.Empty<SpellOption>();
        return (IReadOnlyList<SpellOption>?)Session.Battle.GetLegalActions()?.Spells ?? Array.Empty<SpellOption>();
    }

    internal void ChooseSpell(SpellOption sp)
    {
        if (MySideToAct() is not int side || Session.Battle.GetLegalActions() is not { } la) return;
        if (sp.Targeting == Battle.SpellTargeting.None) { Submit(new CastSpellCmd(la.Actor, sp.Spell) { Side = side }); return; }
        _mode = InputMode.Spell;
        _pendingSpell = sp;
        _lastHover = null;
    }

    internal void CmdSpecial()
    {
        if (MySideToAct() is not int side || Session.Battle.GetLegalActions() is not { } la || la.Abilities.Count == 0) return;
        var ab = la.Abilities[0];
        if (ab.Targeting == Battle.AbilityTargeting.None) { Submit(new UseAbilityCmd(la.Actor, ab.Ability) { Side = side }); return; }
        _mode = InputMode.Ability;
        _pendingAbility = ab;
        _lastHover = null;
    }

    public string SpellName(string id) => Game.Instance.Db.Spell(id)?.Name is { Length: > 0 } n ? n : id;

    // ---------------------------------------------------------------- end

    void ShowResults()
    {
        if (_resultsShown) return;
        _resultsShown = true;
        ClearHighlights();
        CombatCursors.Reset();
        var (winner, reason) = Session.EndResult!.Value;
        GD.Print($"[H5] combat finished: winner side {winner?.ToString() ?? "none"} ({reason}) hash {Session.Battle.State.ComputeHash()} desync={Session.Desynced}");
        Hud.ShowResults(winner, reason);
        if (CombatLauncher.ResultsScreenshot is { } shot)
            GetTree().CreateTimer(1.0, ignoreTimeScale: true).Timeout += () =>
            {
                GetViewport().GetTexture().GetImage().SavePng(shot);
                GD.Print($"[H5] results screenshot {shot}");
            };
        if (QuitWhenDone) GetTree().CreateTimer(2.0, ignoreTimeScale: true).Timeout += () => GetTree().Quit();
    }

    internal void ReturnToMenu()
    {
        Session.Leave();
        CombatCursors.Reset();
        Autoplay = false;
        GetTree().ChangeSceneToFile("res://scenes/MainMenu.tscn");
    }

    public override void _ExitTree() => CombatCursors.Reset();
}
