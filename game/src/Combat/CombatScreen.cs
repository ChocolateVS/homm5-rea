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
    CombatCamera _camera = null!;
    CinematicCameras _cine = null!;
    SiegeView? _siege;

    readonly Dictionary<int, UnitView> _units = new();
    readonly Dictionary<int, Node3D> _heroes = new();
    Control _plaqueLayer = null!;
    FlyingSigns _signs = null!;
    readonly Queue<BattleEvent> _queue = new();
    bool _playing;
    double _attackRemainderUntil;
    readonly IBattleAI _autoAi = H5.Combat.AI.BattleAiRegistry.Create("original");
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
        H5Reborn.Settings.VideoSettings.ApplyScaling();
        Audio.CombatAudio.Attach(this);

        // The authoritative battle state names its arena (engine-picked, seeded); older states
        // without one fall back to the duel rule below.
        var stateArena = Session.Battle.State.Arena;
        var st = Session.Battle.State;
        ArenaDefinition definition;
        if (st.Siege is { } siege && Game.Instance.Db.TownCombat.TryGetValue(siege.Town, out var layout))
            definition = ArenaDefinition.FromScenery(siege.Scenery ?? layout.Scenery!, layout.MountX, layout.MountY, st.Width, st.Height);
        else
            definition = !string.IsNullOrEmpty(stateArena) ? ArenaDefinition.Load(stateArena) : ArenaDefinition.Load(PickArena(Session.Seed));
        GD.Print($"[H5] combat arena: {definition.ScenePath} mount ({definition.MountX},{definition.MountY})");
        Arena = ArenaScene.Create(definition);
        AddChild(Arena);
        PlaceObstacles();
        if (st.Siege != null) { _siege = new SiegeView(Arena, st); Arena.AddChild(_siege); }
        _camera = new CombatCamera(Arena);
        _cine = new CinematicCameras(_camera, definition.CamerasSet);
        ApplyPhaseCamera(st.Phase, instant: true);

        _plaqueLayer = new Control { Name = "Plaques", AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_plaqueLayer);
        _signs = new FlyingSigns();
        AddChild(_signs);
        _signs.Attach(Arena.Camera);

        foreach (var u in Session.Battle.State.Units)
            if (!u.IsObject && !IsTowerUnit(u) && (u.Alive || u.IsWarMachine)) AddUnitView(u);
        for (int side = 0; side < 2; side++) AddHeroView(side);

        Hud = new CombatHud(this);
        AddChild(Hud);
        Hud.Log.AddLine(CombatLogText.StartCombat());

        foreach (var e in Session.Opening) _queue.Enqueue(e);
        RefreshAll();
        if (CombatLauncher.SkipTactics) CmdEndTactics();
        if (CombatLauncher.InfoUnit is int info && Session.Battle.State.Unit(info) is { } iu) Hud.ShowUnitInfo(iu);
        if (CombatLauncher.HudTooltip is { } ht)
            GetTree().CreateTimer(0.5).Timeout += () =>
            {
                if (Hud.Screen.Find(ht) is { } n && n._MakeCustomTooltip(n.TooltipText) is Control tip)
                {
                    Hud.AddChild(tip);
                    tip.Position = n.GetGlobalRect().Position + new Vector2(0, -60);
                }
            };
        if (CombatLauncher.RetreatDialogCost is { } rdc)
            GetTree().CreateTimer(1.0).Timeout += () => { _devRansom = rdc; RetreatDialog.Open(this, Hud); };
        if (CombatLauncher.TooltipUnit is { } tu && Session.Battle.State.Unit(tu.Unit) is { } tUnit)
            Callable.From(() =>
            {
                var at = Arena.Camera.UnprojectPosition(Arena.Grid.StackCenter(new Vector2I(tUnit.Pos.X, tUnit.Pos.Y), tUnit.Size));
                for (int i = 0; i < tu.Clicks; i++) ShowTooltip(tUnit, at);
            }).CallDeferred();
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

    /// <summary>
    /// Duel arenas: duels are fought on Maps/DuelMode/PresetMap, which is all grass, so the
    /// original draws from the grass entry of CombatArenaTypes (average size: Grass_Big_01,
    /// Grass_Big_02, Grass_Small_03). The choice is seeded so both network peers agree.
    /// </summary>
    static CombatArenaTable.Entry PickArena(ulong seed)
    {
        var all = CombatArenaTable.ForBattle(CombatArenaTable.TerrainGrass);
        return all[(int)(seed % (ulong)all.Count)];
    }

    // ---------------------------------------------------------------- scene setup

    void PlaceObstacles()
    {
        var s = Session.Battle.State;
        var loader = new ModelLoader(Game.Instance.Xdb);
        var cache = new Dictionary<string, ArrayMesh?>(StringComparer.OrdinalIgnoreCase);
        ArrayMesh? Mesh(string modelPath)
        {
            if (cache.TryGetValue(modelPath, out var m)) return m;
            try { m = Game.Instance.Fs.Exists(modelPath) ? ModelBuilder.BuildMesh(loader.Load(modelPath), skinned: false) : null; }
            catch (Exception e) { GD.PushWarning($"[H5] obstacle {modelPath}: {e.Message}"); m = null; }
            return cache[modelPath] = m;
        }

        if (s.ObstaclePlacements.Count > 0)
        {
            // Original obstacle models placed by the engine's port of the stock generator: the
            // model origin sits on the anchor tile, turned by the placement's quarter turns.
            foreach (var p in s.ObstaclePlacements)
            {
                if (p.Model == null || Mesh(p.Model) is not { } mesh) continue;
                Arena.AddChild(new MeshInstance3D
                {
                    Name = $"Obstacle_{p.Anchor.X}_{p.Anchor.Y}", Mesh = mesh,
                    Position = Arena.Grid.CellCenter(new Vector2I(p.Anchor.X, p.Anchor.Y)),
                    Rotation = new Vector3(0, p.Rotation * Mathf.Pi / 2, 0),
                });
            }
            return;
        }

        // States without placements (fixed cell lists): show the original grass 1x1 rocks.
        var rocks = new[] { "Rock1x1_1", "Rock1x1_2", "Rock1x1_3", "Rock1x1_4" };
        foreach (var c in s.Obstacles)
        {
            var obstacle = $"ArenaObjects/_(ArenaObstacle)/GrassArena/{rocks[(c.X * 7 + c.Y * 13) % rocks.Length]}.xdb";
            if (!Game.Instance.Fs.Exists(obstacle)) continue;
            var r = H5.Core.Xdb.XdbRef.Parse((string?)Game.Instance.Xdb.Load(obstacle).Root!.Element("Model")?.Attribute("href"), obstacle);
            if (r.IsEmpty || Mesh(r.Path) is not { } mesh) continue;
            Arena.AddChild(new MeshInstance3D
            {
                Name = $"Obstacle_{c.X}_{c.Y}", Mesh = mesh,
                Position = Arena.Grid.CellCenter(new Vector2I(c.X, c.Y)),
                Rotation = new Vector3(0, (c.X * 3 + c.Y) * 0.7f, 0),
            });
        }
    }

    /// <summary>Siege tower actors (CreatureId SIEGE_*) are drawn as the archer on their tower.</summary>
    static bool IsTowerUnit(Unit u) => u.CreatureId.StartsWith("SIEGE_", StringComparison.Ordinal);

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
            // The stock manual's combat screenshot (Game Manual 3.0 p.14) shows a hero standing in
            // a ~2-cell square just outside the grid's left edge beside the far rows; with the
            // armies deployed along the 12-cell edges, each hero stands off the left end of its
            // own deployment rows (attacker: near rows, defender: far rows), facing the field.
            var pos = Arena.Grid.CellCenter(-1.0f, side == 0 ? 1.0f : ArenaGrid.Height - 1.0f);
            c.Position = pos;
            // Models face Godot +Z at yaw 0 (see UnitView.FaceTowards).
            var d = Arena.Grid.GridCenter - pos;
            c.Rotation = new Vector3(0, Mathf.Atan2(d.X, d.Z), 0);
            Arena.Units.AddChild(c);
            _heroes[side] = c;
        }
        catch (Exception e) { GD.PushWarning($"[H5] hero model {characterPath}: {e.Message}"); }
    }

    public UnitView? View(int id) => _units.GetValueOrDefault(id);

    // ---------------------------------------------------------------- frame loop

    /// <summary>Setvar combat_stir_animation_period (default 60000 ms, exe 00af7750).</summary>
    const double StirPeriod = 60.0;
    readonly Dictionary<int, double> _nextStir = new();

    /// <summary>Idle stacks occasionally play their "stir00" fidget, once per stir period each.</summary>
    void UpdateStirs()
    {
        double now = Now;
        foreach (var v in _units.Values)
        {
            if (!_nextStir.TryGetValue(v.UnitId, out var at))
            {
                // Stagger stacks deterministically over the period so they don't fidget in unison.
                _nextStir[v.UnitId] = now + StirPeriod * ((v.UnitId * 0.618034) % 1.0);
                continue;
            }
            if (now < at) continue;
            _nextStir[v.UnitId] = now + StirPeriod;
            if (!v.Dead && v.Has("stir00") && v.Character?.Player.CurrentAnimation.ToString().EndsWith("idle00") == true)
            {
                v.Play("stir00");
                v.Character.Player.AnimationFinished += OnStirDone;
                void OnStirDone(StringName _) { v.Character!.Player.AnimationFinished -= OnStirDone; v.Idle(); }
            }
        }
    }

    public override void _Process(double delta)
    {
        _camera.Process(delta, GetViewport().GuiGetFocusOwner() is not LineEdit);
        _cine.Process(delta);
        foreach (var v in _units.Values) v.PositionPlaque(Arena.Camera);
        if (!_playing) UpdateStirs();

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
            else if (CombatLauncher.OpenSpellbook && !Session.Busy && MySideToAct() is int me)
            {
                if (Session.Battle.State.Phase == BattlePhase.Tactics) CmdEndTactics();
                else if (Session.Battle.GetLegalActions() is { IsHero: true }) { CombatLauncher.OpenSpellbook = false; Hud.ToggleSpellsPublic(); }
                else Session.Submit(_autoAi.Decide(Session.Battle, me) with { Side = me });
            }
            AimDevMouse();
            UpdateHover();
            Hud.UpdateCursorPrediction(MousePos);
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
        int? id;
        if (kind.StartsWith("spell:"))
        {
            // "spell:SPELL_ID": on the hero's turn, arm that spell and hover a stack it is refused on.
            if (!la.IsHero) { if (!Session.Busy && !_playing) Session.Submit(new DefendCmd(la.Actor) { Side = la.Side }); return; }
            if (la.Spells.Find(o => o.Spell == kind[6..]) is not { } opt) return;
            _mode = InputMode.Spell;
            _pendingSpell = opt;
            id = Session.Battle.State.Living.Where(u => !u.IsWarMachine && Session.Battle.SpellTargetRefusalKey(la.Actor, la.Side, opt.Spell, opt.Mastery, u.Id) != null)
                .Select(u => (int?)u.Id).FirstOrDefault();
        }
        else id = kind == "shoot" ? la.Shoot.Cast<int?>().FirstOrDefault() : la.Melee.Keys.Cast<int?>().FirstOrDefault();
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
            _cine.EventEnded();
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

    /// <summary>The last spell cast (damage entries name its caster) and its not yet written cast entry.</summary>
    SpellCast? _lastCast;
    string? _pendingCastLine;
    /// <summary>First Aid Tent whose heal entry waits for the Healed event.</summary>
    int? _tentHealer;

    /// <summary>
    /// A spell that damages logs one CastSpellTargetedDamage (or its own text) per target instead of the plain cast
    /// entry (exe FUN_004e6600 / FUN_0097efd0 create the CCombatLogCastSpell with the damage flag); the plain entry is
    /// held until the next event shows the spell did no damage.
    /// </summary>
    void FlushCastLine(BattleEvent e)
    {
        if (_pendingCastLine == null || e is EffectAdded or EffectRemoved or ManaChanged or AtbChanged or SpellResisted or Missed) return;
        bool damagedBySpell = e is Damaged { Kind: AttackKind.Spell, By: { } by } && _lastCast != null && _lastCast.Spell.EndsWith(by);
        if (!damagedBySpell) Hud.Log.AddLine(_pendingCastLine);
        _pendingCastLine = null;
    }

    async Task Play(BattleEvent e)
    {
        var s = Session.Battle.State;
        CombatLogText.SetViewSide(Session.ViewSide);
        FlushCastLine(e);
        switch (e)
        {
            case TurnStarted t:
                await FinishAttack();
                _cine.EventEnded();
                Hud.OnTurnStarted(t.Actor);
                break;

            case PhaseChanged p:
                Hud.OnPhase(p.Phase);
                ApplyPhaseCamera(p.Phase, instant: false);
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
                if (_tentHealer is int tent && CombatLogText.TentHeal(this, tent, h) is { } healLine) Hud.Log.AddLine(healLine);
                _tentHealer = null;
                break;

            case UnitDied ud when View(ud.UnitId) is { } dead:
                // Death track cameras (stock sieges) only when no fight camera is already running.
                if (!_cine.Playing && Participant(dead) is { } dying) _cine.Begin(CinematicCameras.Action.Death, dying, null, null);
                dead.Dead = true;
                dead.Count = 0;
                dead.UpdatePlaque();
                // The body stays on the field after the death animation (corpses can be raised).
                if (dead.Has("death")) dead.PlayAction("death");
                else dead.Visible = false;
                break;

            case UnitSummoned us:
                if (s.Unit(us.UnitId) is { } su && View(us.UnitId) == null)
                {
                    var v = AddUnitView(su);
                    v.Cell = us.Cell;
                    v.Count = us.Count;
                    v.Position = Arena.Grid.StackCenter(new Vector2I(us.Cell.X, us.Cell.Y), v.Size);
                    if (v.Has("birth")) v.PlayAction("birth");
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
                if (View(mp.UnitId) is { } moraleView)
                    ProcVisual(moraleView, mp.Good ? "Effects/_(Effect)/Spells/MoraleGood.xdb" : "Effects/_(Effect)/Spells/MoraleBad.xdb",
                        mp.Good ? "Sounds/_(Sound)/Spells/GoodMoral.xdb" : "Sounds/_(Sound)/Spells/BadMoral.xdb");
                break;

            case LuckProc lp:
                if (View(lp.UnitId) is { } lv)
                    ProcVisual(lv, lp.Good ? "Effects/_(Effect)/Spells/LuckGood.xdb" : "Effects/_(Effect)/Spells/LuckBad.xdb",
                        lp.Good ? "Sounds/_(Sound)/Spells/LuckGood.xdb" : "Sounds/_(Sound)/Spells/LuckBad.xdb");
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
                if (at.Ability == "FIRST_AID") _tentHealer = at.UnitId;
                else if (CombatLogText.Ability(this, at) is { } abilityLine) Hud.Log.AddLine(abilityLine);
                if (at.Ability == "ABILITY_DEADLY_STRIKE" && View(at.Target ?? at.UnitId) is { } dsv) _signs.Event(dsv, dsv.SignAnchor, "DeadlyStrike");
                if (at.Ability == "CATAPULT_SHOT" && View(at.UnitId) is { } cat) await AnimateCatapult(cat);
                break;

            case EffectAdded or EffectRemoved:
                if (e is EffectAdded ea && View(ea.UnitId) is { } eav) UpdateEffectState(eav);
                if (e is EffectRemoved er && View(er.UnitId) is { } erv) UpdateEffectState(erv);
                break;

            case BuildingDamaged bd:
                await FinishAttack();
                _siege?.OnDamaged(bd);
                await Delay(0.3);
                break;

            case BattleEnded be:
                await FinishAttack();
                Hud.Log.AddLine(CombatLogText.Ended(this, be.Winner));
                foreach (var v in _units.Values)
                    if (!v.Dead && v.Side == be.Winner && v.Has("happy")) v.PlayAction("happy");
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
        // Ground speed per exe 008b85e0 (move clip MovementSpeed × SpeedFactor, default 3 units/s);
        // the clip's own forward travel is extracted (CharacterBuilder.ExtractRootMotion).
        float unitsPerSecond = v.LocomotionSpeed;
        if (v.Has("moveStart") && v.Has("moveEnd"))
        {
            // Teleporting movers (Devils, Vampire Lords): vanish with moveStart, appear at the
            // destination with moveEnd, as their arena anim sets are authored.
            var dest = Arena.Grid.StackCenter(new Vector2I(path[^1].X, path[^1].Y), v.Size);
            v.FaceTowards(dest);
            v.PlayAction("moveStart");
            await Delay(Math.Max(0.2f, v.Length("moveStart")));
            v.Position = dest;
            v.PlayAction("moveEnd");
            await Delay(Math.Max(0.2f, v.Length("moveEnd")));
            v.Cell = path[^1];
            v.Idle();
            return;
        }
        if (CombatLauncher.MoveFrames is { } mf && ++_moveCount == mf.N)
            for (int i = 0; i < mf.Count; i++)
            {
                int frame = i;
                GetTree().CreateTimer(mf.Interval * i).Timeout += () =>
                {
                    var file = $"{mf.Prefix}{frame:D2}.png";
                    GetViewport().GetTexture().GetImage().SavePng(file);
                    GD.Print($"[H5] move frame {file}");
                };
            }

        // The original moves every creature, walker or flyer, with one routine (exe 00a733a0 →
        // 008b8a70): turn in place to the first segment playing the move clip (008b88b0), follow
        // the path at constant speed, and keep facing along the last segment at the end. Flyers
        // fly the straight line to the destination at their normal height (their idle and move
        // clips are already airborne); there is no take-off arc.
        var points = new List<Vector3> { v.Position };
        var cells = flying ? new[] { path[^1] } : path;
        foreach (var c in cells)
        {
            var p = Arena.Grid.StackCenter(new Vector2I(c.X, c.Y), v.Size);
            if (p.DistanceTo(points[^1]) > 0.01f) points.Add(p);
        }
        var lengths = new float[points.Count];
        for (int i = 1; i < points.Count; i++) lengths[i] = lengths[i - 1] + points[i - 1].DistanceTo(points[i]);
        float total = lengths[^1];
        if (total > 0.01f)
        {
            static float SegYaw(Vector3 a, Vector3 b) => Mathf.Atan2(b.X - a.X, b.Z - a.Z);
            await v.TurnTo(SegYaw(points[0], points[1]));
            if (v.Has("move")) v.Play("move", loop: true);

            // Along the path the facing follows each segment; at a corner the unit turns toward the
            // next segment at the in-place turn rate while it keeps walking (inferred from the turn
            // routine; the path track's own corner handling was not traced).
            int seg = 0;
            float yaw = v.Rotation.Y, targetYaw = yaw;
            double last = 0;
            var tw = CreateTween();
            tw.TweenMethod(Callable.From<float>(dist =>
            {
                while (seg < points.Count - 2 && dist > lengths[seg + 1])
                {
                    seg++;
                    targetYaw = SegYaw(points[seg], points[seg + 1]);
                }
                float segLen = lengths[seg + 1] - lengths[seg];
                float k = segLen > 0 ? (dist - lengths[seg]) / segLen : 1f;
                v.Position = points[seg].Lerp(points[seg + 1], Mathf.Clamp(k, 0f, 1f));

                double now = Time.GetTicksMsec() / 1000.0;
                float dt = last == 0 ? 0f : (float)(now - last);
                last = now;
                float delta = UnitView.YawDelta(yaw, targetYaw);
                float fullTurn = v.TurnSeconds(Mathf.Pi);
                float maxStep = fullTurn > 0 ? Mathf.Pi / fullTurn * dt : Mathf.Pi;
                yaw += Mathf.Clamp(delta, -maxStep, maxStep);
                v.Rotation = new Vector3(0, yaw, 0);
            }), 0f, total, total / unitsPerSecond);
            await ToSignal(tw, Tween.SignalName.Finished);
            v.Rotation = new Vector3(0, SegYaw(points[^2], points[^1]), 0);
        }
        v.Cell = path[^1];
        v.Idle();
    }

    /// <summary>A battlefield participant for the track-camera filters (CinematicCameras).</summary>
    CinematicCameras.Participant? Participant(Node3D? node)
    {
        if (node == null) return null;
        var pos = CombatCamera.ToEngine(node.GlobalPosition);
        if (node is UnitView u)
        {
            var visual = Game.Instance.Db.Creatures.GetValueOrDefault(u.CreatureId)?.VisualPath;
            return new CinematicCameras.Participant(u.IsWarMachine ? CinematicCameras.CharTypeKind.Machine : CinematicCameras.CharTypeKind.Creature,
                visual, u.Size, new Vector2I(u.Cell.X + ArenaGrid.MarginX, u.Cell.Y + ArenaGrid.MarginY), pos, u.Rotation.Y);
        }
        return new CinematicCameras.Participant(CinematicCameras.CharTypeKind.Hero, null, 1, new Vector2I(-1, -1), pos, node.Rotation.Y);
    }

    async Task AnimateAttack(AttackStarted a)
    {
        var target = View(a.Target);
        Node3D? attackerNode = Actor.IsHero(a.Attacker) ? _heroes.GetValueOrDefault(Actor.HeroSide(a.Attacker)) : View(a.Attacker);
        if (attackerNode is UnitView av && target != null)
        {
            // Both sides turn with the in-place turn routine (exe 008b88b0) rather than snapping;
            // the defender's turn runs while the attacker turns.
            if (a.Kind != AttackKind.Ranged) _ = target.TurnTo(target.YawTowards(av.GlobalPosition), playMoveClip: false);
            await av.TurnTo(av.YawTowards(target.GlobalPosition));
        }
        // A fight event may switch to a track camera (retaliations belong to the same fight).
        if (a.Kind is AttackKind.Melee or AttackKind.Ranged && Participant(attackerNode) is { } actor)
            _cine.Begin(a.Kind == AttackKind.Ranged ? CinematicCameras.Action.Ranged : CinematicCameras.Action.Melee, actor, Participant(target), null);
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
            u.PlayAction(kind);
            float action = u.ActionTime(kind), length = u.Length(kind);
            await Delay(Math.Max(0.15f, action));
            if (CombatLauncher.AttackScreenshot is { } shot && ++_attackCount == shot.N)
            {
                GetViewport().GetTexture().GetImage().SavePng(shot.Path);
                GD.Print($"[H5] attack screenshot {shot.Path} ({kind})");
            }
            _attackRemainderUntil = Now + Math.Max(0, length - action);
            // Shooters release their original (Shot) at the action frame; the hit (damage event,
            // hit animation, casualty sign) waits for the missile to arrive.
            if (a.Kind == AttackKind.Ranged && target != null && CreatureModels.ShotFor(u.CreatureId) is { } shotXdb)
            {
                var arrived = false;
                var p = Effects.ProjectilePlayer.Fire(shotXdb, Arena, u, (target.GlobalPosition + target.SignAnchor()) * 0.5f, () => arrived = true);
                MaybeCapture(ref _shotCount, CombatLauncher.ShotScreenshot, Math.Max(0.05, (p?.FlightTime ?? 0) * 0.5));
                double deadline = Now + Math.Min(3.0, (p?.FlightTime ?? 0) + 0.2);
                while (!arrived && Now < deadline) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        else if (_siege?.ShooterFor(a.Attacker) is { } archer && target != null)
        {
            // A tower shoots: its archer plays the ranged attack and fires the tower's (Shot).
            var shootKind = archer.Has("rangeattack") ? "rangeattack" : "attack00";
            archer.Play(shootKind);
            await Delay(0.5);
            if (_siege.ShotFor(a.Attacker) is { } towerShot)
            {
                var arrived = false;
                var p = Effects.ProjectilePlayer.Fire(towerShot, Arena, archer, (target.GlobalPosition + target.SignAnchor()) * 0.5f, () => arrived = true);
                double deadline = Now + Math.Min(3.0, (p?.FlightTime ?? 0) + 0.2);
                while (!arrived && Now < deadline) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (archer.Has("idle00")) archer.Play("idle00", loop: true);
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
        if (d.CountLeft > 0) v.PlayAction("hit");
        v.UpdatePlaque();
        if (d.Killed > 0) _signs.Casualty(v, v.SignAnchor, -d.Killed);
        Hud.Log.AddLine(CombatLogText.Damage(this, d, Hud.PendingLuck, _lastCast));
        Hud.PendingLuck = 0;
    }

    int _shotCount, _spellCount, _moveCount;

    /// <summary>The catapult hurls its boulder at the building hit next (the following BuildingDamaged event).</summary>
    async Task AnimateCatapult(UnitView cat)
    {
        await FinishAttack();
        var kind = cat.Has("rangeattack") ? "rangeattack" : "attack00";
        cat.PlayAction(kind);
        await Delay(Math.Max(0.2f, cat.ActionTime(kind)));
        var next = _queue.OfType<BuildingDamaged>().FirstOrDefault();
        if (next != null && _siege != null && Session.Battle.State.Siege?.Building(next.Building) is { } b)
        {
            const string catapultVisual = "GameMechanics/CreatureVisual/WarMachines/Catapult.xdb";
            string? shot = null;
            if (Game.Instance.Fs.Exists(catapultVisual))
            {
                var r = H5.Core.Xdb.XdbRef.Parse((string?)Game.Instance.Xdb.Load(catapultVisual).Root?.Element("AnimShot")?.Attribute("href"), catapultVisual);
                if (!r.IsEmpty && Game.Instance.Fs.Exists(r.Path)) shot = r.Path;
            }
            if (shot != null)
            {
                var arrived = false;
                var p = Effects.ProjectilePlayer.Fire(shot, Arena, cat, _siege.Center(b) + new Vector3(0, 2f, 0), () => arrived = true);
                double deadline = Now + Math.Min(4.0, (p?.FlightTime ?? 0) + 0.2);
                while (!arrived && Now < deadline) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        _attackRemainderUntil = Now + Math.Max(0, cat.Length(kind) - cat.ActionTime(kind));
    }

    /// <summary>Morale/luck proc: the stock Effects/_(Effect)/Spells/{Morale,Luck}{Good,Bad} effect and sound on the stack.</summary>
    void ProcVisual(UnitView v, string effect, string sound)
    {
        if (Game.Instance.Fs.Exists(effect) && v.Model is { } m) Effects.EffectPlayer.Play(effect, m);
        if (Game.Instance.Fs.Exists(sound)) Audio.SoundLibrary.Play(sound, v);
    }

    void MaybeCapture(ref int counter, (int N, string Path)? shot, double after)
    {
        if (shot is not { } s || ++counter != s.N) return;
        GetTree().CreateTimer(after).Timeout += () =>
        {
            GetViewport().GetTexture().GetImage().SavePng(s.Path);
            GD.Print($"[H5] capture {s.Path}");
        };
    }

    async Task AnimateSpell(SpellCast sc)
    {
        _lastCast = sc;
        _pendingCastLine = CombatLogText.Spell(this, sc);
        Node3D? casterNode = Actor.IsHero(sc.Caster) ? _heroes.GetValueOrDefault(Actor.HeroSide(sc.Caster)) : View(sc.Caster);
        if (Participant(casterNode) is { } caster)
        {
            Vector3? point = sc.Cell is { } sp ? CombatCamera.ToEngine(Arena.Grid.CellCenter(new Vector2I(sp.X, sp.Y))) : null;
            _cine.Begin(CinematicCameras.Action.SpellCast, caster, sc.Target is { } spellTarget ? Participant(View(spellTarget)) : null, sc.Spell, point);
        }
        if (Actor.IsHero(sc.Caster) && _heroes.GetValueOrDefault(Actor.HeroSide(sc.Caster)) is Character hero && hero.Has("cast"))
            hero.Play("cast");
        else if (View(sc.Caster) is { } cv && cv.Has("cast"))
            cv.PlayAction("cast");
        await Delay(0.4);
        // The spell's original (SpellVisual); playback continues after its HitDelay so damage
        // lands with the visual.
        var spellXdb = Game.Instance.Db.Spell(sc.Spell)?.SourcePath;
        // One visual per cast: at the target stack, else the target cell (area spells), else the
        // field centre (global spells such as Armageddon). Playing it on every affected stack
        // multiplies area effects far beyond the original.
        var points = new List<Vector3>();
        if (sc.Target is int tid && View(tid) is { } tv) points.Add(tv.GlobalPosition);
        else if (sc.Cell is { } c) points.Add(Arena.Grid.CellCenter(new Vector2I(c.X, c.Y)));
        else if (sc.Affected.Count == 1 && View(sc.Affected[0]) is { } one) points.Add(one.GlobalPosition);
        else if (sc.Affected.Count > 0) points.Add(Arena.Grid.GridCenter);
        if (!string.IsNullOrEmpty(spellXdb) && points.Count > 0)
        {
            int pending = points.Count;
            foreach (var at in points) Effects.SpellVisualPlayer.PlaySpell(spellXdb, Arena, at, () => pending--);
            MaybeCapture(ref _spellCount, CombatLauncher.SpellScreenshot, 0.5);
            double deadline = Now + 2.5;
            while (pending > 0 && Now < deadline) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        else await Delay(0.35);
    }


    /// <summary>After a batch, snap views to the authoritative state (positions, counts, deaths).</summary>
    void SyncWithState()
    {
        var s = Session.Battle.State;
        foreach (var u in s.Units)
        {
            if (u.IsObject) continue;
            if (IsTowerUnit(u)) continue;
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
                if (u.Removed) v.Visible = false; else v.PlayAction("death");
            }
            v.UpdatePlaque();
        }
    }

    void RefreshAll()
    {
        Hud.Refresh();
    }

    /// <summary>
    /// Phase changes only change army visibility: the exe sets the combat camera once at combat init
    /// and keeps it through tactics and combat (docs/rules/CAMERA.md).
    /// </summary>
    void ApplyPhaseCamera(BattlePhase phase, bool instant)
    {
        if (phase == BattlePhase.Tactics)
        {
            var s = Session.Battle.State;
            int side = Session.LocalSides.FirstOrDefault(sd => !s.TacticsDone[sd], Session.LocalSides.FirstOrDefault(0));
            _camera.ShowTacticsView(side, instant);
            // "At this stage you cannot actually see the enemy army ... revealed when combat starts" (manual p.14).
            SetArmyVisibility(v => v.Side == side);
        }
        else if (phase == BattlePhase.Combat)
        {
            _camera.ShowBattleView(instant);
            SetArmyVisibility(_ => true);
        }
    }

    void SetArmyVisibility(Func<UnitView, bool> visible)
    {
        foreach (var v in _units.Values)
        {
            v.Visible = visible(v);
            v.UpdatePlaque();
        }
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
        _refusal = null;
        var action = Resolve(cell, mouse, out var cursor, out var hl);
        CombatCursors.Set(cursor);
        DrawFakeCursor(cursor);
        ShowStaticHighlights(hl);
        // Moves show no prediction in the original; attacks, shots, spells and abilities do; a refused
        // spell target shows the refusal reason instead.
        Hud.SetPrediction(action?.Pred ?? _refusal);
    }

    /// <summary>Refusal text for the hovered target (set by Resolve when it returns no action).</summary>
    CombatPredictions.Prediction? _refusal;

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
    sealed record PlannedAction(BattleCommand Command, string Kind, int? Target, CombatPredictions.Prediction? Pred = null);

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
                    // A refused target shows the reason in the box with the can't-cast cursor (exe FUN_00982010),
                    // including immune stacks, which the original never lets you pick.
                    string? refusal = occupant == null ? null : b.SpellTargetRefusalKey(la.Actor, side, sp.Spell, sp.Mastery, occupant.Id);
                    if (refusal != null) { _refusal = new CombatPredictions.Prediction(CombatPredictions.Str(refusal), null); cursor = CombatCursor.CantCast; return null; }
                    if (occupant != null && sp.Targets.Contains(occupant.Id))
                    {
                        cursor = CombatCursor.Cast;
                        foreach (var c in occupant.Cells()) hl.Add((c, CellMark.Affected));
                        var castU = new CastSpellCmd(la.Actor, sp.Spell, occupant.Id, sp.Targeting == Battle.SpellTargeting.UnitAndCell ? occupant.Pos : null) { Side = side };
                        return new PlannedAction(castU, SpellName(sp.Spell), occupant.Id,
                            CombatPredictions.Spell(b, castU, CombatLogText.Escape(SpellName(sp.Spell)), CombatLogText.Escape(CombatLogText.UnitName(this, occupant.Id))));
                    }
                    cursor = CombatCursor.CantCast;
                    return null;
                case Battle.SpellTargeting.Cell:
                    cursor = CombatCursor.Cast;
                    hl.Add((cell, CellMark.Affected));
                    var castC = new CastSpellCmd(la.Actor, sp.Spell, null, cell) { Side = side };
                    return new PlannedAction(castC, SpellName(sp.Spell), null, CombatPredictions.Spell(b, castC, CombatLogText.Escape(SpellName(sp.Spell)), null));
            }
        }
        if (_mode == InputMode.Ability && _pendingAbility is { } ab)
        {
            if (ab.Ability == "CATAPULT_SHOT")
            {
                // Manual catapult aiming (Catapult perk): only fortification cells are targets.
                if (s.Siege != null && b.CatapultTargets().Find(t => t.Blocked.Contains(cell)) is { } bt)
                {
                    cursor = CombatCursor.Ability;
                    foreach (var c in bt.Blocked) hl.Add((c, CellMark.Affected));
                    return new PlannedAction(new UseAbilityCmd(la.Actor, ab.Ability, null, cell) { Side = side }, "Catapult", null);
                }
                cursor = CombatCursor.CantCast;
                return null;
            }
            if (ab.Targeting == Battle.AbilityTargeting.Cell)
            {
                cursor = CombatCursor.Ability;
                hl.Add((cell, CellMark.Affected));
                var useC = new UseAbilityCmd(la.Actor, ab.Ability, null, cell) { Side = side };
                return new PlannedAction(useC, ab.Ability, null, CombatPredictions.Ability(b, useC, CombatLogText.Escape(AbilityName(ab.Ability)), null));
            }
            var targetId = occupant?.Id ?? (ab.Targeting == Battle.AbilityTargeting.DeadFriend
                ? s.Units.FirstOrDefault(u => !u.Alive && u.Pos == cell && ab.Targets.Contains(u.Id))?.Id : null);
            if (targetId is int tid && ab.Targets.Contains(tid))
            {
                cursor = CombatCursor.Ability;
                hl.Add((cell, CellMark.Affected));
                var useT = new UseAbilityCmd(la.Actor, ab.Ability, tid) { Side = side };
                return new PlannedAction(useT, ab.Ability, tid, CombatPredictions.Ability(b, useT, CombatLogText.Escape(AbilityName(ab.Ability)),
                    CombatLogText.Escape(CombatLogText.UnitName(this, tid))));
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
                return new PlannedAction(new HeroAttackCmd(occupant.Id) { Side = side }, "Hero attack", occupant.Id,
                    CombatPredictions.HeroAttack(b, side, occupant.Id));
            }
            return null;
        }

        var me = s.Unit(la.Actor)!;
        if (occupant != null && occupant.Side != side && occupant.Alive)
        {
            if (la.Shoot.Contains(occupant.Id))
            {
                // Broken arrow: range penalty, or (sieges) the shot crosses the walls (Battle.WallsHalveShot).
                // Same test as the damage code (exe 00a5c5a0): nearest-cell squared distance > (width/2)^2.
                bool far = (Battle.NearestCellDistanceSq(me, occupant) > (s.Width / 2) * (s.Width / 2) && !me.IsWarMachine
                            && !me.Has("ABILITY_NO_RANGE_PENALTY") && !me.Has("ABILITY_RANGE_PENALTY"))
                           || (s.Siege != null && b.WallsHalveShot(me, occupant));
                cursor = far ? CombatCursor.ShootPenalty : CombatCursor.Shoot;
                foreach (var c in occupant.Cells()) hl.Add((c, CellMark.Affected));
                return new PlannedAction(new ShootCmd(me.Id, occupant.Id) { Side = side }, "Shoot", occupant.Id,
                    CombatPredictions.Attack(b, me.Id, occupant.Id, null, ranged: true));
            }
            if (la.Melee.TryGetValue(occupant.Id, out var froms) && froms.Count > 0)
            {
                var from = PickAttackFrom(me, occupant, froms, mouse);
                cursor = AttackCursor(from, me.Size, occupant);
                foreach (var c in me.CellsAt(from)) hl.Add((c, CellMark.Select));
                foreach (var c in occupant.Cells()) hl.Add((c, CellMark.Affected));
                return new PlannedAction(new AttackCmd(me.Id, occupant.Id, from == me.Pos ? null : from) { Side = side }, "Attack", occupant.Id,
                    CombatPredictions.Attack(b, me.Id, occupant.Id, from, ranged: false));
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

    /// <summary>Stock combat key bindings (input_a2.cfg sections combat_screen / combat_screen_tactic).</summary>
    bool HandleKey(InputEventKey k)
    {
        if (!k.Pressed || k.Echo || k.CtrlPressed) return false;
        bool tactics = Session.Battle.State.Phase == BattlePhase.Tactics;
        // esc_menu section: close_menu 'ESC' / 'E' while the in-game menu is open.
        if (Hud.EscMenuOpen && k.Keycode is Key.Escape or Key.E) { Hud.CloseEscMenu(); return true; }
        switch (k.Keycode)
        {
            case Key.Escape or Key.F4 or Key.F10: Hud.ShowOptions(); return true;
            case Key.Tab when !tactics: Hud.ToggleLogPublic(); return true;
            case Key.Enter or Key.KpEnter or Key.Space when tactics: CmdEndTactics(); return true;
            case Key.A or Key.V when tactics: CmdAutoplace(); return true;   // input_autoplace_army
            case Key.Enter or Key.KpEnter or Key.Space or Key.D when !tactics: CmdDefend(); return true;
            case Key.W when !tactics: CmdWait(); return true;
            case Key.A or Key.V when !tactics: CmdAutoCombat(); return true;
            case Key.R when !tactics: CmdSurrender(); return true;
            case Key.C or Key.S when !tactics: Hud.ToggleSpellsPublic(); return true;
        }
        return false;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey key && HandleKey(key)) { GetViewport().SetInputAsHandled(); return; }
        // Camera mouse controls (wheel, right-drag rotate/pitch, middle or CTRL+right-drag pan): CombatCamera.
        if (_camera.HandleInput(e)) { GetViewport().SetInputAsHandled(); return; }
        if (e is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Right } rel)
        {
            // Right click (not a camera drag) shows the small creature tooltip next to the cursor
            // (exe 00575430); repeated right-clicks on the same stack cycle info → info → active
            // effects (click count mod 3; fan manual p.304).
            if (!_camera.RightDragged && !_playing && _mode == InputMode.Normal && Arena.CellAtScreen(rel.Position) is { } rc
                && Session.Battle.State.At(new Cell(rc.X, rc.Y)) is { } info && !info.IsObject)
                ShowTooltip(info, rel.Position);
            return;
        }
        if (e is InputEventMouseMotion mm && _tooltip != null && CombatLauncher.TooltipUnit == null && mm.Position.DistanceTo(_tooltipAt) > 24) HideTooltip();
        if (e is not InputEventMouseButton { Pressed: true } mb) return;
        if (mb.ButtonIndex == MouseButton.Left) HideTooltip();
        if (_playing) return;
        var cell = Arena.CellAtScreen(mb.Position);
        if (mb.ButtonIndex == MouseButton.Right)
        {
            if (_mode != InputMode.Normal) CancelTargeting();
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

    /// <summary>Display name of a creature/hero ability id (CombatAbilities texts), the id if unnamed.</summary>
    static string AbilityName(string id) =>
        Game.Instance.Db.Abilities.GetValueOrDefault(id)?.Name is { Length: > 0 } n ? n
        : Game.Instance.Db.Spells.GetValueOrDefault(id)?.Name is { Length: > 0 } sn ? sn : id;

    CreatureTooltip? _tooltip;
    int _tooltipUnit = -1, _tooltipClicks;
    Vector2 _tooltipAt;

    void ShowTooltip(Unit u, Vector2 at)
    {
        _tooltipClicks = u.Id == _tooltipUnit && _tooltip != null ? _tooltipClicks + 1 : 0;
        HideTooltip(keepCount: true);
        _tooltipUnit = u.Id;
        _tooltipAt = at;
        _tooltip = CreatureTooltip.Build(Session.Battle, u, _tooltipClicks % 3, null);
        Hud.AddChild(_tooltip);
        // Placed beside the cursor and kept on screen (exact anchor of the stock tooltip not traced).
        var view = GetViewportRect().Size;
        Callable.From(() =>
        {
            if (_tooltip == null || !IsInstanceValid(_tooltip)) return;
            var size = _tooltip.Size;
            var pos = at + new Vector2(24, 8);
            if (pos.X + size.X > view.X) pos.X = at.X - size.X - 8;
            if (pos.Y + size.Y > view.Y) pos.Y = view.Y - size.Y;
            _tooltip.Position = pos;
        }).CallDeferred();
    }

    void HideTooltip(bool keepCount = false)
    {
        if (_tooltip != null && IsInstanceValid(_tooltip)) _tooltip.QueueFree();
        _tooltip = null;
        if (!keepCount) _tooltipUnit = -1;
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

    /// <summary>
    /// Tactics "Auto place creatures" (OnJog-StartCombat → autoplace_army, keys A/V; exe FUN_0053e510 →
    /// FUN_00578510 re-runs the start placement for the deploying side).
    /// </summary>
    internal void CmdAutoplace()
    {
        var s = Session.Battle.State;
        if (s.Phase != BattlePhase.Tactics) return;
        foreach (var side in Session.LocalSides)
        {
            if (s.TacticsDone[side]) continue;
            foreach (var (id, cell) in BattleSetup.AutoPlacement(Game.Instance.Db, s, side))
                if (s.Unit(id) is { } u && u.Pos != cell) Session.Submit(new DeployCmd(id, cell) { Side = side });
            break;
        }
        _deploySelected = null;
        _lastHover = null;
    }

    /// <summary>
    /// Confirm placement and start combat (OnJog-NextCreature → confirm_placement, keys Enter/Space; exe
    /// FUN_00545260). The exe first asks T_NOT_ALL_CREATURES_PLACED when a stack was left off the field;
    /// every stack is placed in this engine, so the confirmation never arises.
    /// </summary>
    internal void CmdEndTactics()
    {
        var s = Session.Battle.State;
        foreach (var side in Session.LocalSides)
            if (s.Phase == BattlePhase.Tactics && !s.TacticsDone[side]) Submit(new EndTacticsCmd { Side = side });
    }

    (int Cost, int Gold)? _devRansom;
    /// <summary>Dev override of ransom/gold for the retreat box (--retreat-dialog).</summary>
    internal (int Cost, int Gold)? DevRansom => _devRansom;

    /// <summary>True while battle events are being animated (input locked).</summary>
    internal bool IsPlaying => _playing;

    /// <summary>The retreat button / R key: opens the stock Flee/Surrender box when allowed (exe 0053feb0).</summary>
    internal void CmdSurrender()
    {
        if (Session.Ended || RetreatDialog.RefusalCode(this, _playing) != 0) return;
        RetreatDialog.Open(this, Hud);
    }

    internal void CmdAutoCombat()
    {
        // The stock game writes no log line for this (the jog button's own state shows it).
        Autoplay = !Autoplay;
    }

    /// <summary>Legal actions of the stack/hero the local player is acting with, or null.</summary>
    internal LegalActions? MyLegalActions() => MySideToAct() is null ? null : Session.Battle.GetLegalActions();

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

    /// <summary>
    /// Set by whoever opened this combat to take over when the player leaves the results
    /// screen (the adventure map resumes instead of returning to the main menu). One-shot.
    /// </summary>
    public static Action<CombatSession>? ReturnHandler;

    internal void ReturnToMenu()
    {
        if (ReturnHandler is { } handler)
        {
            ReturnHandler = null;
            Session.Leave();
            CombatCursors.Reset();
            Autoplay = false;
            handler(Session);
            return;
        }
        // A networked duel returns to its lobby room (the server keeps the room after the
        // game ends), like the original's MP flow; local games go back to the main menu.
        if (Session.IsNetworked && Ui.Net.LobbyService.Instance.Room != null) MainMenu.ReturnScreen = "wait";
        Session.Leave();
        CombatCursors.Reset();
        Autoplay = false;
        GetTree().ChangeSceneToFile("res://scenes/MainMenu.tscn");
    }

    public override void _ExitTree() => CombatCursors.Reset();
}
