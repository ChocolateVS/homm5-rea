using Godot;
using H5.Adventure.Map;
using H5.Assets.Scenes;
using H5.Core.Xdb;
using H5.Scripting.Runtime;
using H5Reborn.Adventure;
using H5Reborn.Audio;
using H5Reborn.Cinematics;
using H5Reborn.Effects;
using H5Reborn.Ui;

namespace H5Reborn.Campaign;

/// <summary>
/// Client side of the map scripts (src/H5.Scripting AdventureScripting): hosts the map's
/// Lua script for an adventure screen, ticks it at the original rate (one segment per
/// 200 ms — GetSoundTimeInSleeps divides milliseconds by 200), shows its UI requests with
/// the original windows and answers them so the script's callback runs, and plays its
/// presentation events (sounds, effects, camera moves, flying signs). It also watches for
/// the end of the game and shows the mission results.
///
/// Requests: message_box / question_box / talk_box / tutorial_message → the stock boxes
/// (AdvDialogs); dialog_scene → <see cref="DialogScenePlayer"/>; adv_map_dialog → the
/// stock MiniDialogScreen over the map with the dialog's camera and voiced sentences.
/// Full-screen scenes pause the script clock; message boxes don't (the script keeps
/// running, as the original's callback-based boxes allow).
/// </summary>
public partial class ScriptUiBridge : Node
{
    public AdventureScreen Screen { get; }
    public AdventureScripting? Host { get; private set; }
    readonly HashSet<int> _shown = new();
    double _clock;
    int _fullScreen;
    bool _endShown;

    /// <summary>Script segment length in seconds.</summary>
    public const double Segment = 0.2;

    ScriptUiBridge(AdventureScreen screen)
    {
        Screen = screen;
        Name = "ScriptUiBridge";
    }

    /// <summary>The one-line hook for AdventureScreen._Ready: creates the script host and starts the map script.</summary>
    public static ScriptUiBridge Attach(AdventureScreen screen)
    {
        if (screen.GetNodeOrNull<ScriptUiBridge>("ScriptUiBridge") is { } existing) return existing;
        var bridge = new ScriptUiBridge(screen);
        screen.AddChild(bridge);
        bridge.StartScripts();
        return bridge;
    }

    void StartScripts()
    {
        var session = Screen.Session;
        var map = session.Map;
        if (map.ScriptPath == null) return;
        try
        {
            var fs = Game.Instance.Fs;
            var host = AdventureScripting.Create(session.Game, session.Files, new EnumTables(fs), new SharedCatalog(new XdbLoader(fs)));
            // MMH 5.5 player settings changed in the options screen (appended to scripts/H55-Settings.lua).
            host.SettingOverrides = Options.Mmh55Options.Overrides();
            if (CampaignFlow.Active is { } a && a.Session == session)
                host.World.GameVars["__campaign_bonus"] = Math.Max(0, a.BonusIndex).ToString();
            // Loading a save restores the Lua heap and threads instead of starting the script (game/src/Save).
            if (Save.SaveManager.TakePendingScripts(session) is { } saved) host.Load(saved);
            else
            {
                string? src = session.Files.TryReadAllBytes(map.ScriptPath, out var b) ? ScriptText.FromBytes(b) : null;
                host.Start(map.ScriptPath, src);
            }
            Host = host;
            // Dev: `--script-eval=<lua>` runs a chunk once the map script has started.
            if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--script-eval=")) is { } eval)
                host.L.DoString(eval["--script-eval=".Length..], "dev");
            Flush();
        }
        catch (Exception e)
        {
            GD.PushError($"[H5] map script {map.ScriptPath}: {e.Message}");
        }
    }

    public override void _Process(double delta) => Update(delta);

    /// <summary>Advances the scripts by real time and presents whatever they asked for.</summary>
    public void Update(double delta)
    {
        if (Host != null && _fullScreen == 0)
        {
            _clock += delta;
            int segments = 0;
            while (_clock >= Segment && segments < 10) { _clock -= Segment; segments++; }
            if (segments > 0) Host.Tick(segments);
        }
        Flush();
        CheckEnd();
    }

    void Flush()
    {
        if (Host == null) return;
        foreach (var err in Host.Errors) GD.PushWarning($"[H5] script: {err}");
        Host.Errors.Clear();
        foreach (var line in Host.Log) GD.Print($"[H5] script: {line}");
        Host.Log.Clear();

        if (Host.GameEvents.Count > 0)
        {
            // Script-made changes (heroes moved/removed, objects created…) play through the adventure
            // screen's event queue so they animate; a resync marker follows them.
            Screen.Session.EnqueueScripted(Host.GameEvents.ToList());
            Host.GameEvents.Clear();
        }
        foreach (var ev in Host.Events.ToList()) Present(ev);
        Host.Events.Clear();
        // One request at a time, oldest first, like the original's modal boxes and scenes.
        if (_presenting == 0)
            foreach (var req in Host.World.PendingUi.OrderBy(r => r.Id))
                if (_shown.Add(req.Id)) { _presenting++; Show(req); break; }
    }

    int _presenting;

    // ---- presentation events -----------------------------------------------------------

    void Present(ScriptEvent ev)
    {
        string V(string k) => ev.Data.TryGetValue(k, out var v) ? v : "";
        float F(string k) => float.TryParse(V(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0;
        switch (ev.Kind)
        {
            case "sound_2d":
            case "sound_3d":
                if (V("sound").Length > 0) SoundLibrary.Play(V("sound"), this);
                break;
            case "move_camera":
                Screen.World.Camera.CenterOn(new Tile((int)F("x"), (int)F("y"), (int)F("floor")), instant: V("fast") == "True");
                if (V("noRotate") != "True" && ev.Data.ContainsKey("yaw")) Screen.World.Camera.SetYaw(-Mathf.RadToDeg(F("yaw")));
                break;
            case "visual_effect":
                var pos = Screen.Session.Game.ObjectByName(V("object")) is { } o
                    ? Screen.World.TileCenter(o.Pos)
                    : new Vector3(F("x") + 0.5f, Screen.World.HeightAt((int)F("floor"), F("x"), F("y")) + F("z"), -(F("y") + 0.5f));
                EffectPlayer.PlayAt(V("effect"), Screen.World, pos, F("rot"));
                break;
            case "flying_sign":
                var text = ResolveText(V("text"), ParseValues(V("values")));
                var at = Screen.Session.Game.ObjectByName(V("object")) is { } so ? so.Pos
                    : Screen.Session.Game.HeroByName(V("object")) is { } sh ? sh.Pos : (Tile?)null;
                if (at is { } t) Screen.Message("", text); // stock signs float over the object; shown as a box until the screen exposes its fly-text layer
                break;
            case "objective_state":
                // The objective notice and its sound are for the objective's own player, and only
                // for objectives the player can see (00c5fec0: ACTIVE/COMPLETED/FAILED_SOUND).
                if ((int)F("player") - 1 == Screen.Session.Human.Index
                    && Host.World.Objectives.TryGetValue(ScriptWorld.ObjectiveKey(V("name"), (int)F("player") - 1), out var ob) && ob.Visible)
                    Audio.AdventureEventAudio.ObjectiveState((int)F("state"));
                break;
        }
    }

    static Dictionary<string, string> ParseValues(string packed) =>
        packed.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1]);

    /// <summary>A script message: a text file path (map-relative or absolute) with &lt;value=name&gt; substitutions.</summary>
    string ResolveText(string resource, IReadOnlyDictionary<string, string> values)
    {
        string text = resource;
        if (resource.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            var files = Screen.Session.Files;
            var path = PakFileSystemPath(resource);
            text = files.TryReadAllBytes(path, out var bytes) ? H5.Core.Xdb.TextDecoding.Decode(bytes) : resource;
        }
        foreach (var (k, v) in values) text = text.Replace($"<value={k}>", v);
        return text.Trim();
    }

    static string PakFileSystemPath(string p) => H5.Core.IO.PakFileSystem.Normalize(p);

    // ---- UI requests -------------------------------------------------------------------

    void Show(UiRequest req)
    {
        bool answered = false;
        Action<bool> answer = yes =>
        {
            if (answered) return;
            answered = true;
            _presenting = Math.Max(0, _presenting - 1);
            Host?.AnswerUi(req.Id, yes);
        };
        if (OS.IsStdOutVerbose() || OS.GetCmdlineUserArgs().Contains("--script-verbose"))
            GD.Print($"[H5] script UI #{req.Id} {req.Kind} '{req.Resource}'");
        switch (req.Kind)
        {
            case "message_box" or "tutorial_message" or "question_box" or "talk_box" when OS.GetCmdlineUserArgs().Contains("--auto-answer-boxes"):
                answer(true);
                break;
            case "message_box":
            case "tutorial_message":
                // Tutorial boxes play UIGameRoot TUTORIAL_SOUND as they open (00770df0).
                if (req.Kind == "tutorial_message") Audio.AudioSystem.PlayNamed("TUTORIAL_SOUND");
                AdvDialogs.Message(Screen.DialogLayer, "", req.Text.Length > 0 ? req.Text.Trim() : ResolveText(req.Resource, req.Values), null, () => answer(true));
                break;
            case "talk_box":
                AdvDialogs.Message(Screen.DialogLayer, ResolveText(req.Values.GetValueOrDefault("title", ""), req.Values),
                    ResolveText(req.Values.GetValueOrDefault("text", req.Resource), req.Values),
                    req.Values.TryGetValue("icon", out var icon) ? Game.Instance.LoadTexture(icon) : null, () => answer(true));
                break;
            case "question_box":
                AdvDialogs.YesNo(Screen.DialogLayer, "", req.Text.Length > 0 ? req.Text.Trim() : ResolveText(req.Resource, req.Values), null, answer);
                break;
            case "dialog_scene" when OS.GetCmdlineUserArgs().Contains("--skip-dialog-scenes"):
                answer(true);
                break;
            case "dialog_scene":
                _fullScreen++;
                var player = DialogScenePlayer.Open(Screen.DialogLayer, req.Resource, () => { _fullScreen--; answer(true); });
                if (player == null) break; // missing scene: callback already ran
                break;
            case "adv_map_dialog":
                if (Host != null && int.TryParse(req.Resource, out var index) && index >= 0 && index < Host.Data.Dialogs.Count)
                {
                    _fullScreen++;
                    Screen.DialogLayer.AddChild(new AdvMapDialogPlayer(Screen, Host.Data.Dialogs[index], DialogCamera(index),
                        Screen.Session.Files.MapXdbPath, () => { _fullScreen--; answer(true); }));
                }
                else answer(true);
                break;
            default:
                // cut_scene (unused by the stock scripts), puzzle_map: not presented yet.
                GD.Print($"[H5] script UI request {req.Kind} '{req.Resource}' auto-answered");
                answer(true);
                break;
        }
    }

    /// <summary>map.xdb dialogs/Item[index]/Camera (MapScriptData keeps only the sentences).</summary>
    CameraPos? DialogCamera(int index)
    {
        var files = Screen.Session.Files;
        var item = files.LoadXml(files.MapXdbPath).Root?.Element("dialogs")?.Elements("Item").ElementAtOrDefault(index);
        return item?.Element("Camera") is { } cam ? CameraPos.Read(cam) : null;
    }

    // ---- end of game -------------------------------------------------------------------

    void CheckEnd()
    {
        var s = Screen.Session.S;
        if (_endShown || !s.Finished) return;
        _endShown = true;
        var human = Screen.Session.Human;
        bool won = s.WinnerTeam is { } team && team == human.Team && human.Alive;
        var active = CampaignFlow.Active is { } a && a.Session == Screen.Session ? a : null;
        CampaignFlow.OnMissionEnded(won);
        var results = new MissionResultsScreen(won, active?.Campaign.InternalName ?? Screen.Session.Map.Name, active?.Mission.Index ?? -1,
            () => CampaignFlow.Continue(Screen, won));
        Screen.DialogLayer.AddChild(results);
    }
}

/// <summary>
/// StartAdvMapDialog: one of the map's dialogs (map.xdb dialogs) shown on the stock
/// MiniDialogScreen over the adventure map. The map camera moves to the dialog's
/// camera (the same Rod/Pitch/Yaw/Anchor record as dialog scenes, in world units = 2 per
/// tile) and each sentence's voice plays, followed by its Pause; Skip ends the dialog,
/// a click advances a sentence.
/// </summary>
public partial class AdvMapDialogPlayer : Control
{
    readonly AdventureScreen _screen;
    readonly AdvMapDialog _dialog;
    readonly CameraPos? _camera;
    readonly string _mapXdb;
    readonly Action _onFinished;
    UiScreen _ui = null!;
    int _index = -1;
    double _time, _length;
    Node? _voice;
    bool _done;
    Transform3D _savedCamera;
    float _savedFov;
    bool _hudWasVisible = true;

    public AdvMapDialogPlayer(AdventureScreen screen, AdvMapDialog dialog, CameraPos? camera, string mapXdb, Action onFinished)
    {
        _screen = screen;
        _dialog = dialog;
        _camera = camera;
        _mapXdb = mapXdb;
        _onFinished = onFinished;
        Name = "AdvMapDialog";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        _ui = new UiScreen("UI/MiniDialogScreen/MiniDialogScreen.(WindowScreen).xdb");
        AddChild(_ui);
        _ui.Bus.On("skip", Finish);
        var cam = _screen.World.Camera;
        _savedCamera = cam.GlobalTransform;
        _savedFov = cam.Fov;
        cam.InputEnabled = false;
        cam.SetProcess(false);
        // MiniDialogScreen replaces the adventure HUD while the dialog runs.
        _hudWasVisible = _screen.Hud.Visible;
        _screen.Hud.Visible = false;
        if (_camera is { } c) ApplyCamera(c);
        ApplyDialogVolumes();
        Next();
    }

    float _speechScale = 1f;

    /// <summary>
    /// map.xdb dialogs/Item BackgroundMusicVolume / BackgroundAmbientVolume / SpeechVolume
    /// (percent, read by the dialog serializer at 00a0da40): the music and ambience levels
    /// under the dialog and its voice level, restored when it closes.
    /// </summary>
    void ApplyDialogVolumes()
    {
        var files = _screen.Session.Files;
        var item = files.LoadXml(files.MapXdbPath).Root?.Element("dialogs")?.Elements("Item").ElementAtOrDefault(_dialog.Index);
        float Pct(string name) => float.TryParse((string?)item?.Element(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Max(0, v) / 100f : 1f;
        AudioSystem.MusicScale = Pct("BackgroundMusicVolume");
        AudioSystem.AmbientScale = Pct("BackgroundAmbientVolume");
        _speechScale = Pct("SpeechVolume");
    }

    void ApplyCamera(CameraPos c)
    {
        // The adventure view is built at 1 unit per tile (AdvWorld.TileCenter) while camera
        // records use world units (2 per tile): keep the record's angles, halve its distances.
        var cam = _screen.World.Camera;
        var target = new Vector3(c.Anchor.X / 2, c.Anchor.Z, -c.Anchor.Y / 2);
        var back = -c.Forward;
        var eye = target + new Vector3(back.X, back.Z, -back.Y) * (c.Rod / 2);
        cam.Fov = c.Fov;
        cam.LookAtFromPosition(eye, target, Vector3.Up);
    }

    void Next()
    {
        if (_done) return;
        if (_voice != null && IsInstanceValid(_voice)) _voice.QueueFree();
        if (++_index >= _dialog.Sentences.Count) { Finish(); return; }
        var (text, sound, pause) = _dialog.Sentences[_index];
        var files = _screen.Session.Files;
        // MapScriptData already resolved the sentence's hrefs to full archive paths.
        var path = Rooted(text);
        var line = files.TryReadAllBytes(path, out var b) ? TextDecoding.Decode(b).Trim() : "";
        _ui.Find("HeroText")?.SetValue("Text", line);
        double voice = 0;
        if (!string.IsNullOrEmpty(sound))
        {
            var href = Rooted(sound);
            voice = VoiceLength.Of(href);
            _voice = SoundLibrary.Play(href, this);
            if (_voice is AudioStreamPlayer vp && _speechScale < 1f) vp.VolumeDb += Mathf.LinearToDb(Math.Max(_speechScale, 0.0001f));
        }
        _time = 0;
        _length = voice + pause;
        if (OS.GetCmdlineUserArgs().Contains("--script-verbose"))
            GD.Print($"[H5] map dialog line {_index} at {Time.GetTicksMsec() / 1000.0:F1}s lasts {_length:F1}s");
        if (_length <= 0.01) _length = 3;
    }

    /// <summary>A full archive path stays as is; anything else is relative to the map file.</summary>
    string Rooted(string p) =>
        _screen.Session.Files.TryReadAllBytes(H5.Core.IO.PakFileSystem.Normalize(p), out _) ? H5.Core.IO.PakFileSystem.Normalize(p) : XdbRef.Parse(p, _mapXdb).Path;

    public override void _Process(double delta)
    {
        if (_done) return;
        // The first frame after a heavy load can be seconds long; don't let it skip lines.
        _time += Math.Min(delta, 0.1);
        if (_time >= _length) Next();
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { AcceptEvent(); Next(); }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { GetViewport().SetInputAsHandled(); Finish(); }
    }

    void Finish()
    {
        if (_done) return;
        _done = true;
        if (_voice != null && IsInstanceValid(_voice)) _voice.QueueFree();
        var cam = _screen.World.Camera;
        cam.GlobalTransform = _savedCamera;
        cam.Fov = _savedFov;
        cam.SetProcess(true);
        cam.InputEnabled = true;
        _screen.Hud.Visible = _hudWasVisible;
        AudioSystem.MusicScale = AudioSystem.AmbientScale = 1f;
        QueueFree();
        _onFinished();
    }
}
