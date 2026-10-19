using System.Globalization;
using System.Xml.Linq;
using Godot;
using H5.Assets.Model;
using H5.Assets.Scenes;
using H5.Core.Xdb;
using H5Reborn.Arena;
using H5Reborn.Audio;
using H5Reborn.Effects;
using H5Reborn.Render;
using H5Reborn.Ui;
using SN = System.Numerics;

namespace H5Reborn.Cinematics;

/// <summary>
/// Plays a DialogScenes/**/DialogScene.xdb the way the stock dialog screen does
/// (UIGameRoot N_DIALOG_SCENE_SCREEN_ID → UI/DialogueScreen): the scene's small map is
/// built as a 3D set in its own world, the actors (heroes with their HeroCharacterArena,
/// monsters with their creature character) stand at their positions, and each sentence
/// shows the speaker's name and text in the original panel while its voice plays, the
/// camera moves from the sentence's start camera to its finish camera, and the timed
/// sounds, effects and actor animations fire. Left click / Space advances to the next
/// sentence, the Skip button (skip_scene) or Escape ends the scene.
///
/// Timing (from the data, see docs/formats/dialogscenes.md): a voiced sentence lasts the
/// voice length plus `duration`; an unvoiced one lasts `duration` seconds.
/// </summary>
public partial class DialogScenePlayer : Control
{
    public DialogSceneDef Def { get; }
    readonly Action _onFinished;
    SubViewport _viewport = null!;
    Node3D _world = null!;
    Camera3D _camera = null!;
    UiScreen _ui = null!;
    readonly Dictionary<string, ActorView> _actors = new(StringComparer.Ordinal);
    int _sentence = -1;
    double _time, _length;
    readonly List<(double At, Action Fire)> _cues = new();
    Node? _voice;
    // Scene music lives on the shared music stack (the adventure theme underneath pauses and
    // resumes after the scene); every context the scene pushed is popped when it ends.
    readonly List<Audio.MusicManager.Handle> _musicHandles = new();
    Audio.MusicManager.Handle? _music;
    float _musicVolume = 1f;
    int _ambientStops;
    bool _finished;

    /// <summary>World units per map tile (docs/formats/arena.md).</summary>
    const float Tile = ArenaGrid.TileSize;

    public DialogScenePlayer(DialogSceneDef def, Action onFinished)
    {
        Def = def;
        _onFinished = onFinished;
        Name = "DialogScene";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>Loads a scene by its script href ("/DialogScenes/…/DialogScene.xdb#xpointer(/DialogScene)").</summary>
    public static DialogScenePlayer? Open(Control parent, string href, Action onFinished)
    {
        var path = XdbRef.Parse(href).Path;
        if (!Game.Instance.Fs.Exists(path))
        {
            // The original reports `Dialog "%s" is not exists` (exe 005de0d0) and the script goes on.
            GD.PushWarning($"[H5] Dialog \"{href}\" is not exists");
            onFinished();
            return null;
        }
        var def = DialogSceneLoader.Load(Game.Instance.Xdb, path);
        var player = new DialogScenePlayer(def, onFinished);
        parent.AddChild(player);
        return player;
    }

    sealed class ActorView
    {
        public SceneActor Def = null!;
        public Node3D Node = null!;
        public Character? Character;
        public string DisplayName = "";
        public float MoveSpeed = 3.5f;
        public List<Vector3> Path = new();
        public float FinalYaw;
        public bool Walking;
        public string AfterWalk = "idle00";
    }

    public override void _Ready()
    {
        // Original screens lay out in real pixels (as the main menu and adventure screen do).
        H5Reborn.Settings.VideoSettings.ApplyScaling();
        var container = new SubViewportContainer { AnchorRight = 1, AnchorBottom = 1, Stretch = true, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(container);
        _viewport = new SubViewport { OwnWorld3D = true, HandleInputLocally = false, Msaa3D = Viewport.Msaa.Msaa2X };
        container.AddChild(_viewport);
        _world = new Node3D { Name = "SceneWorld" };
        _viewport.AddChild(_world);

        BuildSet();
        BuildActors();

        _camera = new Camera3D { Name = "DialogCamera" };
        _world.AddChild(_camera);
        _camera.MakeCurrent();

        _ui = new UiScreen("UI/DialogueScreen.(WindowScreen).xdb");
        AddChild(_ui);
        _ui.Bus.On("skip_scene", Finish);

        if (Def.Music != null)
        {
            _musicVolume = Def.MusicVolume / 100f;
            PushMusic(Audio.MusicManager.PushTrack("dialog", Audio.AudioSystem.Data.Track(Def.Music), _musicVolume));
        }
        NextSentence();
    }

    // ---- set ---------------------------------------------------------------------------

    ArenaDefinition _set = null!;

    void BuildSet()
    {
        _set = ArenaDefinition.FromScenery(Def.MapPath, 0, 0, ArenaGrid.Width, ArenaGrid.Height);
        if (Def.AmbientLight != null && LightReader.Read(Def.AmbientLight) is { } light) _set.Light = light;
        var arena = ArenaScene.Create(_set);
        _world.AddChild(arena);
        // The combat-only parts of the arena builder are not part of a dialog set.
        arena.Overlay.Visible = false;
        arena.Camera.Current = false;
        // ArenaScene fades scenery between its combat camera and the grid; undo that here.
        Callable.From(() => Callable.From(() => ResetFades(arena)).CallDeferred()).CallDeferred();
    }

    static void ResetFades(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is GeometryInstance3D g) g.Transparency = 0;
            ResetFades(child);
        }
    }

    float GroundHeight(float mapX, float mapY) =>
        _set.Terrain.SampleHeight(Math.Clamp(mapX, 0, _set.Terrain.TilesX), Math.Clamp(mapY, 0, _set.Terrain.TilesY));

    /// <summary>Map tile coordinates (objects' origin is the centre of tile floor(Pos)) to Godot.</summary>
    Vector3 MapPoint(float x, float y, float z = 0) =>
        ArenaGrid.MapToWorld(x + 0.5f, y + 0.5f, GroundHeight(x + 0.5f, y + 0.5f) + z);

    /// <summary>Original world units (Z-up, 2 per tile) to Godot.</summary>
    static Vector3 WorldPoint(SN.Vector3 v) => new(v.X, v.Z, -v.Y);

    /// <summary>Models face map −Y (Godot +Z) at rotation 0; Rot is radians about +Z.</summary>
    static float YawToward(Vector3 from, Vector3 to) => Mathf.Atan2(to.X - from.X, to.Z - from.Z);

    // ---- actors ------------------------------------------------------------------------

    void BuildActors()
    {
        var root = new Node3D { Name = "Actors" };
        _world.AddChild(root);
        foreach (var a in Def.Actors.Values)
        {
            var view = BuildActor(a);
            if (view == null) continue;
            root.AddChild(view.Node);
            view.Node.Position = MapPoint(a.X, a.Y, a.Z);
            view.Node.Rotation = new Vector3(0, a.Rot, 0);
            // Actors not listed in the scene's objects only appear once a sentence uses them.
            view.Node.Visible = Def.PresentActors.Count == 0 || Def.PresentActors.Contains(a.Id);
            _actors[a.Id] = view;
        }
    }

    ActorView? BuildActor(SceneActor a)
    {
        var xdb = Game.Instance.Xdb;
        if (!Game.Instance.Fs.Exists(a.SharedPath)) return null;
        var shared = xdb.Load(a.SharedPath).Root!;
        string? Href(XElement? e, string file)
        {
            var h = (string?)e?.Attribute("href");
            return string.IsNullOrEmpty(h) ? null : XdbRef.Parse(h, file).Path;
        }
        string name = "";
        string? characterPath = null;
        if (a.IsHero)
        {
            characterPath = Href(shared.Element("HeroCharacterArena"), a.SharedPath) ?? Href(shared.Element("HeroCharacterAdventure"), a.SharedPath);
            // The hero's NameFileRef sits inside the shared descriptor's Editable block.
            var nameRef = shared.Descendants("NameFileRef").FirstOrDefault(e => !string.IsNullOrEmpty((string?)e.Attribute("href")));
            name = xdb.ReadText((string?)nameRef?.Attribute("href"), a.SharedPath)?.Trim() ?? "";
        }
        else
        {
            var creature = (string?)shared.Element("Creature") ?? "";
            if (creature.Length > 0 && creature != "CREATURE_UNKNOWN")
            {
                characterPath = CreatureModels.CharacterPathFor(creature);
                if (Game.Instance.Db.Creatures.TryGetValue(creature, out var cdef)) name = cdef.Name;
            }
        }
        try
        {
            Character c;
            if (characterPath != null && Game.Instance.Fs.Exists(characterPath))
                c = CharacterBuilder.Build(characterPath);
            else if (Href(shared.Element("Model"), a.SharedPath) is { } model)
                c = CharacterBuilder.BuildFromModel(model, Href(shared.Element("AnimSet"), a.SharedPath), a.Id);
            else return null;
            var view = new ActorView { Def = a, Node = c, Character = c, DisplayName = name };
            var anims = new AnimSetLoader(xdb);
            var set = characterPath != null ? anims.ReadCharacter(characterPath).ArenaAnimSet : null;
            if (set != null && anims.ReadAnimSet(set).FirstOrDefault(e => e.Kind == "move") is { MovementSpeed: > 0.1f } mv)
                view.MoveSpeed = mv.MovementSpeed;
            if (c.Has("idle00")) c.Play("idle00", loop: true);
            return view;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[H5] dialog actor {a.SharedPath}: {e.Message}");
            return null;
        }
    }

    // ---- sentences ---------------------------------------------------------------------

    void NextSentence()
    {
        if (_finished) return;
        StopVoice();
        _sentence++;
        if (_sentence >= Def.Sentences.Count) { Finish(); return; }
        var s = Def.Sentences[_sentence];
        _time = 0;
        _cues.Clear();

        var speaker = _actors.GetValueOrDefault(s.SpeakerId);
        if (speaker != null) speaker.Node.Visible = true;
        var name = s.CustomName.Length > 0 ? s.CustomName : speaker?.DisplayName ?? "";
        _ui.Find("HeroName")?.SetValue("Name", name);
        _ui.Find("HeroText")?.SetValue("Text", s.Text);

        double voiceLength = 0;
        if (s.VoiceHref != null)
        {
            voiceLength = VoiceLength.Of(s.VoiceHref);
            _voice = SoundLibrary.Play(s.VoiceHref, this);
        }
        _length = voiceLength + Math.Max(0, s.Duration);
        if (_length <= 0) _length = 3; // nothing to time the line by: keep it readable

        ApplySentenceAudio(s);

        if (speaker?.Character is { } sc && s.AnimName.Length > 0)
            _cues.Add((s.AnimationDelay, () => PlayOnce(speaker, s.AnimName)));
        foreach (var snd in s.Sounds)
            _cues.Add((snd.Delay, () => SoundLibrary.Play(snd.SoundHref, this)));
        foreach (var fx in s.Effects)
            _cues.Add((fx.Delay, () => EffectPlayer.PlayAt(fx.EffectHref, _world, WorldPoint(fx.Pos), fx.Rot)));
        foreach (var an in s.Animations)
            if (_actors.TryGetValue(an.ActorId, out var actor))
                _cues.Add((an.Delay, () => StartAnimation(actor, an)));
        _cues.Sort((a, b) => a.At.CompareTo(b.At));

        UpdateCamera(0);
    }

    void PlayOnce(ActorView actor, string kind)
    {
        if (actor.Character is not { } c || !c.Has(kind)) return;
        actor.Node.Visible = true;
        c.Play(kind);
        bool stays = kind.StartsWith("death", StringComparison.OrdinalIgnoreCase);
        if (!stays)
            c.Player.AnimationFinished += Back;
        void Back(StringName _)
        {
            c.Player.AnimationFinished -= Back;
            if (!actor.Walking && c.Has("idle00")) c.Play("idle00", loop: true);
        }
    }

    void StartAnimation(ActorView actor, SceneAnimation an)
    {
        actor.Node.Visible = true;
        if (an.MovePoints.Count > 0)
        {
            actor.Path = an.MovePoints.Select(p => MapPoint(p.X, p.Y)).ToList();
            actor.FinalYaw = Mathf.DegToRad(an.FinalAngleDeg);
            if (an.MovementSpeed > 0.1f) actor.MoveSpeed = an.MovementSpeed;
            actor.Walking = true;
            var kind = an.AnimName.Length > 0 ? an.AnimName : "move";
            if (actor.Character is { } c && c.Has(kind)) c.Play(kind, loop: true);
            return;
        }
        if (an.AnimName.Length > 0) PlayOnce(actor, an.AnimName);
    }

    public override void _Process(double delta)
    {
        if (_finished || _sentence < 0 || _sentence >= Def.Sentences.Count) return;
        // The first frame after building the set can be seconds long; don't let it skip lines.
        delta = Math.Min(delta, 0.1);
        _time += delta;
        while (_cues.Count > 0 && _cues[0].At <= _time)
        {
            var cue = _cues[0];
            _cues.RemoveAt(0);
            cue.Fire();
        }
        foreach (var a in _actors.Values) StepWalk(a, (float)delta);
        UpdateCamera((float)Math.Clamp(_time / _length, 0, 1));
        if (_time >= _length) NextSentence();
    }

    void StepWalk(ActorView a, float dt)
    {
        if (!a.Walking) return;
        if (a.Path.Count == 0)
        {
            a.Walking = false;
            a.Node.Rotation = new Vector3(0, a.FinalYaw, 0);
            if (a.Character is { } c && c.Has("idle00")) c.Play("idle00", loop: true);
            return;
        }
        var target = a.Path[0];
        var pos = a.Node.Position;
        var flat = new Vector3(target.X - pos.X, 0, target.Z - pos.Z);
        float step = a.MoveSpeed * dt;
        if (flat.Length() <= step) { a.Node.Position = target; a.Path.RemoveAt(0); return; }
        a.Node.Rotation = new Vector3(0, YawToward(pos, target), 0);
        var next = pos + flat.Normalized() * step;
        var map = ArenaGrid.WorldToMap(next);
        next.Y = GroundHeight(map.X, map.Y);
        a.Node.Position = next;
    }

    // ---- camera ------------------------------------------------------------------------

    void UpdateCamera(float t)
    {
        var s = Def.Sentences[_sentence];
        if (s.Camera is not { } set) return;
        var a = Resolve(set.Start, set.StartDiff, set.StartCorrectionRot, s);
        var b = s.DynamicCamera ? Resolve(set.Finish, set.FinishDiff, set.FinishCorrectionRot, s) : a;
        // Non-uniform sets ease in and out; uniform ones move at constant speed.
        float k = set.UniformMovement ? t : t * t * (3 - 2 * t);
        var c = set.IgnoreYawDiff ? CameraPos.Lerp(a, b with { Yaw = a.Yaw }, k) : CameraPos.Lerp(a, b, k, set.Direction, set.Circles);
        Apply(c);
    }

    /// <summary>Absolute cameras are in world units; relative ones follow the speaker's position and facing.</summary>
    CameraPos Resolve(SceneCamera cam, SN.Vector3 diff, float correctionRot, SceneSentence s)
    {
        var p = cam.Pos;
        var anchor = p.Anchor + diff + new SN.Vector3(0, 0, Def.CameraShiftZ);
        float yaw = p.Yaw + correctionRot;
        if (!cam.Absolute && _actors.TryGetValue(s.SpeakerId, out var spk))
        {
            var origin = spk.Node.Position;
            float rot = spk.Node.Rotation.Y;
            var rotated = SN.Vector2.Transform(new SN.Vector2(p.Anchor.X, p.Anchor.Y), SN.Matrix3x2.CreateRotation(rot));
            anchor = new SN.Vector3(origin.X + rotated.X, -origin.Z + rotated.Y, origin.Y + p.Anchor.Z) + diff;
            yaw += rot + cam.Rot;
        }
        return p with { Anchor = anchor, Yaw = yaw };
    }

    void Apply(in CameraPos c)
    {
        var eye = WorldPoint(c.Eye);
        var target = WorldPoint(c.Anchor);
        var up = WorldPoint(c.Up);
        if ((target - eye).LengthSquared() < 1e-6f) target = eye + WorldPoint(c.Forward);
        _camera.Fov = c.Fov;
        _camera.Near = Math.Max(0.05f, c.ClipMin);
        _camera.Far = Math.Max(_camera.Near + 1, c.ClipMax);
        _camera.LookAtFromPosition(eye, target, up.LengthSquared() > 1e-6f ? up : Vector3.Up);
    }

    // ---- input / end -------------------------------------------------------------------

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            AcceptEvent();
            NextSentence();
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k) return;
        if (k.Keycode == Key.Escape) { GetViewport().SetInputAsHandled(); Finish(); }
        else if (k.Keycode is Key.Space or Key.Enter or Key.KpEnter) { GetViewport().SetInputAsHandled(); NextSentence(); }
    }

    void StopVoice()
    {
        if (_voice != null && IsInstanceValid(_voice)) _voice.QueueFree();
        _voice = null;
    }

    void PushMusic(Audio.MusicManager.Handle h)
    {
        _musicHandles.Add(h);
        _music = h;
    }

    /// <summary>
    /// Per-line music and ambience controls of a sentence (DialogScene sentences; field names
    /// from the serializer at 00b31280): StopMusic / StopAmbient, MusicFadeOutDuration (ms),
    /// MusicVolumePercent (0 = unchanged) and MusicOverride (Music, StartTime s, TimeShift s).
    /// </summary>
    void ApplySentenceAudio(SceneSentence s)
    {
        if (s.StopAmbient) { _ambientStops++; Audio.AudioSystem.StopAmbient(); }
        if (s.StopMusic || s.MusicFadeOutDuration > 0)
            PushMusic(Audio.MusicManager.PushSilence("dialog-stop", s.StopMusic && s.MusicFadeOutDuration <= 0 ? null : (int)s.MusicFadeOutDuration));
        if (s.MusicVolumePercent > 0)
        {
            _musicVolume = Def.MusicVolume / 100f * (s.MusicVolumePercent / 100f);
            Audio.MusicManager.SetVolume(_music, _musicVolume);
        }
        if (s.OverrideMusic is { } href)
        {
            void Switch()
            {
                if (_finished) return;
                PushMusic(Audio.MusicManager.PushTrack("dialog-override", Audio.AudioSystem.Data.Track(href), _musicVolume, s.OverrideStartTime));
            }
            if (s.OverrideTimeShift > 0) _cues.Add((s.OverrideTimeShift, Switch));
            else Switch();
        }
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        StopVoice();
        for (int i = _musicHandles.Count - 1; i >= 0; i--) Audio.MusicManager.Pop(_musicHandles[i]);
        _musicHandles.Clear();
        _music = null;
        for (; _ambientStops > 0; _ambientStops--) Audio.AudioSystem.ReleaseAmbient();
        QueueFree();
        _onFinished();
    }

    /// <summary>For tests/screenshots: jumps to a sentence and a fraction of its length.</summary>
    public void Seek(int sentence, float fraction)
    {
        sentence = Math.Clamp(sentence, 0, Def.Sentences.Count - 1);
        // Earlier sentences' walks end where their last move point and final angle put the actor.
        for (int i = 0; i < sentence; i++)
            foreach (var an in Def.Sentences[i].Animations)
                if (an.MovePoints.Count > 0 && _actors.TryGetValue(an.ActorId, out var actor))
                {
                    var last = an.MovePoints[^1];
                    actor.Node.Position = MapPoint(last.X, last.Y);
                    actor.Node.Rotation = new Vector3(0, Mathf.DegToRad(an.FinalAngleDeg), 0);
                    actor.Node.Visible = true;
                    actor.Walking = false;
                }
        _sentence = sentence - 1;
        NextSentence();
        float target = (float)(fraction * _length);
        for (float t = 0; t < target; t += 0.05f) _Process(0.05f);
    }
}

/// <summary>Reads Lights/_(AmbientLight) descriptors into the arena light model.</summary>
public static class LightReader
{
    public static ArenaLight? Read(string path)
    {
        var fs = Game.Instance.Fs;
        if (!fs.Exists(path)) return null;
        var e = Game.Instance.Xdb.Load(path).Root!;
        static float F(XElement? x, float d) => x == null || string.IsNullOrWhiteSpace(x.Value) ? d : float.Parse(x.Value, CultureInfo.InvariantCulture);
        static SN.Vector3 V(XElement? x, SN.Vector3 d) => x == null ? d : new(F(x.Element("x"), 0), F(x.Element("y"), 0), F(x.Element("z"), 0));
        string? H(XElement? x)
        {
            var h = (string?)x?.Attribute("href");
            return string.IsNullOrEmpty(h) ? null : XdbRef.Parse(h, path).Path;
        }
        return new ArenaLight
        {
            LightColor = V(e.Element("LightColor"), new(0.5f)),
            AmbientColor = V(e.Element("AmbientColor"), new(0.3f)),
            ShadeColor = V(e.Element("ShadeColor"), new(0.3f)),
            FogColor = V(e.Element("FogColor"), new(0.5f)),
            Pitch = F(e.Element("Pitch"), 45),
            Yaw = F(e.Element("Yaw"), 45),
            FogStart = F(e.Element("FogStartDistance"), 1000),
            FogDistance = F(e.Element("FogDistance"), 1000),
            SkyDomeModel = H(e.Element("SkyDome")),
            SkyCubeTexture = H(e.Element("Sky")),
            DistanceFog = H(e.Element("DistanceFog")),
            // The record itself, for the original-lighting shaders (published by ArenaScene).
            Source = e,
            SourceFile = path,
        };
    }
}

/// <summary>
/// Sounds/_(Music) descriptors: FileName is an Ogg file relative to the install's bin folder
/// ("..\Music\a2\Heroes_Ornella.ogg"); FadeIn/FadeOut in ms.
/// </summary>
public static class MusicPlayer
{
    public static AudioStreamPlayer? Play(Node parent, string musicHref, float volume = 1f, bool loop = true)
    {
        var path = XdbRef.Parse(musicHref).Path;
        if (!Game.Instance.Fs.Exists(path)) return null;
        var root = Game.Instance.Xdb.Load(path).Root!;
        var file = (string?)root.Element("FileName");
        if (string.IsNullOrWhiteSpace(file)) return null;
        var full = H5.Core.IO.InstallPath.ResolveFile(Game.Instance.Install.Root, "bin", file);
        if (full == null) return null;
        var stream = AudioStreamOggVorbis.LoadFromFile(full);
        if (stream == null) return null;
        stream.Loop = loop;
        var p = new AudioStreamPlayer { Stream = stream, VolumeDb = Mathf.LinearToDb(Math.Max(0.001f, volume)), Bus = Options.AudioBuses.For(Options.AudioBuses.Music) };
        parent.AddChild(p);
        float fadeIn = float.TryParse((string?)root.Element("FadeIn"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fi) ? fi / 1000f : 0;
        if (fadeIn > 0)
        {
            var target = p.VolumeDb;
            p.VolumeDb = -60;
            p.CreateTween().TweenProperty(p, "volume_db", target, fadeIn);
        }
        p.Play();
        return p;
    }
}
