using H5.Scripting.Lua;

namespace H5.Scripting.Runtime;

public sealed partial class AdventureScripting
{
    // ---- message text (FUN_006b5010) ----------------------------------------------------------

    /// <summary>
    /// Text file contents as the engine's text cache returns them (FUN_004c7430): an empty path or
    /// a missing file gives ""; a path not starting with '/' is made absolute by prefixing '/'.
    /// </summary>
    string LoadScriptText(string path)
    {
        if (path.Length == 0) return "";
        if (path[0] != '/') path = "/" + path;
        var b = _readFile(path);
        return b == null ? "" : H5.Core.Xdb.TextDecoding.Decode(b);
    }

    /// <summary>
    /// FUN_006b5010: builds a message from a script value. A table { path; name = value, … } loads
    /// t[1] (a string) and replaces every &lt;value=name&gt; with the text built from t[name] (recursively:
    /// a number is "%d" of its truncated value, a string is another text path, nil gives ""); a
    /// "&lt;value=" without a closing '&gt;' fails. A number gives "%d"; a string is a text path.
    /// Anything else fails.
    /// </summary>
    bool BuildMessageText(LuaValue v, out string text, int nesting = 0)
    {
        text = "";
        if (v.IsTable)
        {
            var t = v.TableValue;
            var first = t.Get(1);
            if (!(first.IsString || first.IsNumber) || !first.TryToStr(out var path)) return false;
            text = LoadScriptText(path);
            int pos = 0;
            for (int guard = 0; guard < 100_000; guard++)
            {
                int at = text.IndexOf("<value=", pos, StringComparison.Ordinal);
                if (at < 0) break;
                int close = text.IndexOf('>', at + 7);
                if (close < 0) { text = ""; return false; }
                var name = text[(at + 7)..close];
                string sub = "";
                if (name.Length > 0 && nesting < 64) BuildMessageText(t.Get(name), out sub, nesting + 1);
                text = text[..at] + sub + text[(close + 1)..];
                pos = at;
            }
            return true;
        }
        if (v.TryToNumber(out var n))
        {
            text = TruncInt(n).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        if (v.IsString)
        {
            text = LoadScriptText(v.Str);
            return true;
        }
        return false;
    }

    /// <summary>x87 FISTP with truncation: out-of-range values give the integer indefinite.</summary>
    static int TruncInt(double d) =>
        double.IsNaN(d) || d >= 2147483648.0 || d <= -2147483649.0 ? int.MinValue : (int)d;

    /// <summary>
    /// The message argument checks shared by MessageBox, QuestionBox, their ForPlayers forms and
    /// ShowFlyingSign: an explicit nil is "No message passed!", a value the text builder rejects
    /// (an absent argument included) is "Cannot construct message text!", an empty result is
    /// "Empty message text!".
    /// </summary>
    string MessageText(HostContext c, int i)
    {
        if (i < c.Count && c.Arg(i).IsNil) throw c.Error("No message passed!");
        if (!BuildMessageText(c.Arg(i), out var text)) throw c.Error("Cannot construct message text!");
        if (text.Length == 0) throw c.Error("Empty message text!");
        return text;
    }

    /// <summary>A callback argument: its text when it is a string or a number (lua_isstring), else "".</summary>
    static string CallbackArg(HostContext c, int i) =>
        i < c.Count && (c.Arg(i).IsString || c.Arg(i).IsNumber) ? c.Str(i) : "";

    /// <summary>The player filter of the ForPlayers forms: a number truncated, anything else 0.</summary>
    static int FilterArg(HostContext c, int i) => i < c.Count && c.Arg(i).TryToNumber(out var d) ? TruncInt(d) : 0;

    /// <summary>Message argument as given (resource path / table form), kept for the UI's own use.</summary>
    static (string Resource, Dictionary<string, string> Values) MessageArg(HostContext c, int i)
    {
        var a = c.Arg(i);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (a.IsTable)
        {
            var t = a.TableValue;
            string resource = t.Get(1).TryToStr(out var s) ? s : "";
            foreach (var (k, v) in t.Pairs())
                if (k.IsString && v.TryToStr(out var vs)) values[k.Str] = vs;
            return (resource, values);
        }
        return (c.Str(i), values);
    }

    List<int> PlayersFilter(int filter)
    {
        var res = new List<int>();
        if (filter < 0)
        {
            int team = -filter - 1;
            res.AddRange(S.Players.Where(p => p.Team == team).Select(p => p.Index + 1));
        }
        else
            for (int p = 0; p < 8; p++)
                if ((filter & (1 << p)) != 0) res.Add(p + 1);
        return res;
    }

    /// <summary>
    /// MessageBox / QuestionBox (FUN_005db320 / 005db5f0) and the ForPlayers forms (005db910 /
    /// 005dbc30): the text is built and checked here, then the box is queued with the callback
    /// name(s) (IAdventureMap +0x2c / +0x30).
    /// </summary>
    UiRequest Message(HostContext c, string kind, int msgArg, int cbArg, int cbNoArg = -1, List<int>? players = null)
    {
        var text = MessageText(c, msgArg);
        var (res, values) = MessageArg(c, msgArg);
        var req = RequestUi(kind, res, CallbackArg(c, cbArg), cbNoArg >= 0 ? CallbackArg(c, cbNoArg) : "");
        req.Values = values;
        req.Text = text;
        if (players != null) req.Players = players;
        return req;
    }

    // ---- floors, resources, players ------------------------------------------------------------

    /// <summary>The floor checks of the camera/sound/light natives: [0,1] else <paramref name="invalid"/>, then the map's floor.</summary>
    H5.Adventure.Map.MapFloor FloorArg(HostContext c, int floor, string invalid)
    {
        if (floor < 0 || floor >= 2) throw c.Error($"{invalid} #{floor}");
        return Map.Floor(floor) ?? throw c.Error($"Floor #{floor} is not exists");
    }

    /// <summary>
    /// A database resource lookup cast to a type (FUN_009b0f60 + dynamic cast): the xdb exists and
    /// its root element is <paramref name="type"/>.
    /// </summary>
    bool ResourceIs(string href, string type)
    {
        var r = H5.Core.Xdb.XdbRef.Parse(href);
        if (r.IsEmpty) return false;
        var b = _readFile(r.Path);
        if (b == null) return false;
        var xml = H5.Core.Xdb.TextDecoding.Decode(b);
        for (int p = xml.IndexOf('<'); p >= 0 && p + 1 < xml.Length; p = xml.IndexOf('<', p + 1))
        {
            char ch = xml[p + 1];
            if (ch is '?' or '!') continue;
            int e = p + 1;
            while (e < xml.Length && (char.IsLetterOrDigit(xml[e]) || xml[e] is '_' or '.' or ':' or '-')) e++;
            return string.Equals(xml[(p + 1)..e], type, StringComparison.Ordinal);
        }
        return false;
    }

    H5.Adventure.Map.Tile? NamedObjectPos(string name) => Game.HeroByName(name)?.Pos ?? Game.ObjectByName(name)?.Pos;

    /// <summary>Script player state 1 (in the game and alive), as GetPlayerState reports it.</summary>
    bool PlayerActive(int scriptPlayer)
    {
        var p = Game.Player(scriptPlayer - 1);
        return p != null && p.Alive && !(S.Finished && S.WinnerTeam == p.Team);
    }

    void RegisterPresentationApi()
    {
        Reg("MessageBoxInt", c => Message(c, "message_box", 0, 1));
        Reg("MessageBox", c => Message(c, "message_box", 0, 1));
        Reg("QuestionBoxInt", c => Message(c, "question_box", 0, 1, 2));
        Reg("QuestionBox", c => Message(c, "question_box", 0, 1, 2));
        Reg("MessageBoxForPlayers", c => Message(c, "message_box", 1, 2, -1, PlayersFilter(FilterArg(c, 0))));
        Reg("QuestionBoxForPlayers", c => Message(c, "question_box", 1, 2, 3, PlayersFilter(FilterArg(c, 0))));
        Reg("TalkBoxForPlayers", c =>
        {
            var req = RequestUi("talk_box", c.Str(2), c.Has(5) ? c.Str(5) : "");
            req.Players = PlayersFilter(c.Int(0));
            req.Values["icon"] = c.Str(1);
            req.Values["title"] = c.Str(3);
            req.Values["text"] = c.Str(4);
        });
        // StartDialogScene (005de0d0) / StartCutScene (005de340): the scene must resolve to a
        // dialog scene / an SAnimScene; a non-empty save name first saves the game under it.
        Reg("StartDialogSceneInt", c => DialogScene(c, "dialog_scene", 0));
        Reg("StartDialogScene", c => DialogScene(c, "dialog_scene", 0));
        Reg("StartDialogSceneForPlayers", c => { var r = RequestUi("dialog_scene", c.Str(1), c.Has(2) ? c.Str(2) : ""); r.Players = PlayersFilter(c.Int(0)); });
        Reg("StartCutSceneInt", c => DialogScene(c, "cut_scene", 0));
        Reg("StartCutScene", c => DialogScene(c, "cut_scene", 0));
        Reg("StartCutSceneForPlayers", c => { var r = RequestUi("cut_scene", c.Str(1), c.Has(2) ? c.Str(2) : ""); r.Players = PlayersFilter(c.Int(0)); });
        // StartAdvMapDialog (005e2e80): no index check here; the dialog index and callback are
        // handed to the adventure map (+0x64).
        Reg("StartAdvMapDialog", c => RequestUi("adv_map_dialog", c.Int(0).ToString(System.Globalization.CultureInfo.InvariantCulture), c.Str(1)));
        // OverrideAdvMapDialogPos (005e3090): (dialog, a1, x, y, z) → +0x68(dialog, a1, {2x', 2y', z})
        // with x' = (float)(x - 0.5) + 0.5 in the x87's float stores.
        Reg("OverrideAdvMapDialogPos", c =>
        {
            float x = (float)c.Number(2), y = (float)c.Number(3);
            float tx = (float)((double)x - 0.5), ty = (float)((double)y - 0.5);
            Emit("adv_dialog_pos", ("dialog", c.Int(0)), ("index", c.Int(1)),
                ("x", (float)(((double)tx + 0.5) * 2.0)), ("y", (float)(((double)ty + 0.5) * 2.0)), ("z", (float)c.Number(4)));
        });
        // OpenPuzzleMap (005e29f0): player in [1,8], then the player must exist.
        Reg("OpenPuzzleMap", c =>
        {
            int p = c.Int(0);
            if (p < 1 || p > 8) throw c.Error($"Invalid player id #{p}");
            if (Game.Player(p - 1) == null) throw c.Error($"Player #{p} is not exists");
            RequestUi("puzzle_map", c.Int(1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Players.Add(p);
        });

        // ShowFlyingSign (005dc450): (message, object, player = -1, time = 1.0).
        Reg("ShowFlyingSign", c =>
        {
            var text = MessageText(c, 0);
            var name = CallbackArg(c, 1);
            int player = -1;
            if (2 < c.Count && !c.Arg(2).IsNil && c.Arg(2).TryToNumber(out var pd)) player = TruncInt(pd);
            double time = 1.0;
            if (3 < c.Count && !c.Arg(3).IsNil && c.Arg(3).TryToNumber(out var td) && td > 0.0) time = td;
            if (NamedObjectPos(name) == null) throw c.Error($"Object with name \"{name}\" doesn't exist on adventure map");
            if (player != -1)
            {
                if (player < 1 || player > 8) throw c.Error($"Invalid player id #{player}");
                if (!PlayerActive(player)) throw c.Error($"Player #{player} is not active");
            }
            Emit("flying_sign", ("text", text), ("object", name), ("player", player), ("time", time), ("values", ""));
        });

        // Play2DSound (005dc970): the second argument is never read — the exe passes the first
        // argument's (string) number slot, which is 0. A sound that does not resolve is only logged.
        Reg("Play2DSound", c =>
        {
            var snd = c.Str(0);
            if (!ResourceIs(snd, "Sound")) { Log.Add($"Play2DSound: Sound \"{snd}\" was not found!"); return; }
            Emit("sound_2d", ("sound", snd), ("flag", false));
        });
        Reg("Play2DSoundForPlayers", c =>
        {
            var snd = c.Str(1);
            if (!ResourceIs(snd, "Sound")) { Log.Add($"Play2DSoundForPlayers: Sound \"{snd}\" was not found!"); return; }
            Emit("sound_2d", ("sound", snd), ("flag", c.Int(2) != 0), ("players", string.Join(",", PlayersFilter(c.Int(0)))));
        });
        Reg("Play3DSound", c => Play3DSound(c, 0, "Play3DSound", null));
        Reg("Play3DSoundForPlayers", c => Play3DSound(c, 1, "Play3DSoundForPlayers", PlayersFilter(c.Int(0))));
        Reg("MoveCamera", c => MoveCamera(c, 0, null));
        Reg("MoveCameraForPlayers", c => MoveCamera(c, 1, PlayersFilter(c.Int(0))));
        // DisableCameraFollowHeroes (005df710): number arguments, each "!= 0" (truncated).
        Reg("DisableCameraFollowHeroes", c => World.Settings["camera_follow_disabled"] = $"{c.Int(0) != 0}|{c.Int(1) != 0}|{c.Int(2) != 0}");
        Reg("DisableCameraFollowHeroesForPlayers", c => World.Settings["camera_follow_disabled"] = $"{c.Int(1) != 0}|{c.Int(2) != 0}|{c.Int(3) != 0}");
        // SetAmbientLight (005dab00): floor ID [0,1] and existing, a non-empty light name; the fade
        // time is passed as trunc(time * 1000) ms when fading, else 0.
        Reg("SetAmbientLight", c => AmbientLight(c, 0, null));
        Reg("SetAmbientLightForPlayers", c => AmbientLight(c, 1, PlayersFilter(c.Int(0))));
        Reg("SetCombatAmbientLight", c => World.Settings["combat_light"] = c.Str(0));
        // SetObjectFlashlight (005db020): the object must exist; "" removes the flashlight.
        Reg("SetObjectFlashlight", c =>
        {
            var name = c.Str(0);
            if (name.Length == 0 || NamedObjectPos(name) == null) throw c.Error($"Object \"{name}\" is not exists");
            var light = c.Str(1);
            if (light.Length == 0) World.Settings.Remove("flashlight:" + name);
            else World.Settings["flashlight:" + name] = light;
            Emit("flashlight", ("object", name), ("light", light));
        });
        // PlayVisualEffect (005e1710): (effect, object, tag, x, y, z, rot, floor, a8).
        Reg("PlayVisualEffect", c =>
        {
            var effect = c.Str(0);
            if (!ResourceIs(effect, "Effect")) throw c.Error($"Effect \"{effect}\" does not exist!");
            var name = c.Str(1);
            int floor = c.Int(7);
            if (name.Length > 0)
            {
                var pos = NamedObjectPos(name) ?? throw c.Error($"Object \"{name}\" does not exist!");
                floor = pos.Floor;
            }
            // The message formats the floor (converted to double) with %d, i.e. the low dword of the double.
            if (Map.Floor(floor) == null)
                throw c.Error($"Invalid floor ({unchecked((int)BitConverter.DoubleToInt64Bits((double)(float)floor))})!");
            Emit("visual_effect", ("effect", effect), ("object", name), ("tag", c.Str(2)),
                ("x", c.Number(3)), ("y", c.Number(4)), ("z", c.Number(5)), ("rot", c.Number(6)), ("floor", floor), ("a8", c.Int(8)));
        });
        Reg("StopVisualEffects", c => Emit("stop_visual_effects", ("tag", c.Str(0))));
        Reg("PlayObjectAnimation", c =>
        {
            var name = c.Str(0);
            if (Game.HeroByName(name) == null && Game.ObjectByName(name) == null) throw c.Error($"Object \"{name}\" does not exist");
            Emit("object_animation", ("object", name), ("animation", c.Str(1)), ("action", c.Int(2)));
        });
    }

    void DialogScene(HostContext c, string kind, int a)
    {
        var scene = c.Str(a);
        bool ok = kind == "cut_scene" ? ResourceIs(scene, "AnimScene") : ResourceIs(scene, "DialogScene");
        if (!ok) throw c.Error(kind == "cut_scene" ? $"CutScene {scene} not found" : $"Dialog \"{scene}\" is not exists");
        var r = RequestUi(kind, scene, c.Str(a + 1));
        r.SaveName = c.Str(a + 2);
    }

    /// <summary>Play3DSound (005dcb90): (sound, x, y, floor). The X check compares Y with the width, as the exe does.</summary>
    void Play3DSound(HostContext c, int a, string native, List<int>? players)
    {
        var snd = c.Str(a);
        if (!ResourceIs(snd, "Sound")) { Log.Add($"{native}: Sound \"{snd}\" was not found!"); return; }
        int x = c.Int(a + 1), y = c.Int(a + 2), floor = c.Int(a + 3);
        var f = FloorArg(c, floor, "Invalid floor number");
        if (x < 0 || y >= f.Width) throw c.Error($"X is out of range (must be in [0,{f.Width - 1}], now is {y}");
        if (y >= f.Height) throw c.Error($"Y is out of range (must be in [0,{f.Height - 1}], now is {y}");
        Emit("sound_3d", ("sound", snd), ("x", x), ("y", y), ("floor", floor), ("players", players == null ? "" : string.Join(",", players)));
    }

    void AmbientLight(HostContext c, int a, List<int>? players)
    {
        int floor = c.Int(a);
        FloorArg(c, floor, "Invalid floor ID");
        var light = c.Str(a + 1);
        if (light.Length == 0) throw c.Error("Light name is not specified");
        bool fade = c.Bool(a + 2);
        int ms = fade ? TruncInt((double)((float)c.Number(a + 3) * 1000.0f)) : 0;
        Emit("ambient_light", ("floor", floor), ("light", light), ("fade", fade), ("time", ms / 1000.0), ("ms", ms),
            ("players", players == null ? "" : string.Join(",", players)));
    }

    /// <summary>
    /// MoveCamera (005df150): x and y are the truncated tile coordinates, floor in [0,1] and
    /// existing; zoom, pitch, yaw are floats; the three flags are numbers tested "!= 0".
    /// </summary>
    void MoveCamera(HostContext c, int a, List<int>? players)
    {
        int floor = c.Int(a + 2);
        FloorArg(c, floor, "Invalid floor number");
        Emit("move_camera", ("x", c.Int(a)), ("y", c.Int(a + 1)), ("floor", floor),
            ("zoom", (float)c.Number(a + 3)), ("pitch", (float)c.Number(a + 4)),
            ("yaw", (float)c.Number(a + 5)), ("noZoom", c.Int(a + 6) != 0), ("noRotate", c.Int(a + 7) != 0),
            ("fast", c.Int(a + 8) != 0), ("players", players == null ? "" : string.Join(",", players)));
    }
}
