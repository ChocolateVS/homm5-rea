using H5.Scripting.Lua;

namespace H5.Scripting.Runtime;

public sealed partial class AdventureScripting
{
    /// <summary>A message argument: a text path, or { "file.txt"; name = value, … } for &lt;value&gt; substitution.</summary>
    (string Resource, Dictionary<string, string> Values) MessageArg(HostContext c, int i)
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

    UiRequest Message(HostContext c, string kind, int msgArg, int cbArg, int cbNoArg = -1, List<int>? players = null)
    {
        var (res, values) = MessageArg(c, msgArg);
        if (kind is "message_box" && res.Length == 0 && values.Count == 0) throw c.Error("No message passed!");
        var req = RequestUi(kind, res, c.Has(cbArg) ? c.Str(cbArg) : "", cbNoArg >= 0 && c.Has(cbNoArg) ? c.Str(cbNoArg) : "");
        req.Values = values;
        if (players != null) req.Players = players;
        return req;
    }

    void RegisterPresentationApi()
    {
        Reg("MessageBoxInt", c => Message(c, "message_box", 0, 1));
        Reg("MessageBox", c => Message(c, "message_box", 0, 1));
        Reg("QuestionBoxInt", c => Message(c, "question_box", 0, 1, 2));
        Reg("QuestionBox", c => Message(c, "question_box", 0, 1, 2));
        Reg("MessageBoxForPlayers", c => Message(c, "message_box", 1, 2, -1, PlayersFilter(c.Int(0))));
        Reg("QuestionBoxForPlayers", c => Message(c, "question_box", 1, 2, 3, PlayersFilter(c.Int(0))));
        Reg("TalkBoxForPlayers", c =>
        {
            var req = RequestUi("talk_box", c.Str(2), c.Has(5) ? c.Str(5) : "");
            req.Players = PlayersFilter(c.Int(0));
            req.Values["icon"] = c.Str(1);
            req.Values["title"] = c.Str(3);
            req.Values["text"] = c.Str(4);
        });
        Reg("StartDialogSceneInt", c => { var r = RequestUi("dialog_scene", c.Str(0), c.Has(1) ? c.Str(1) : ""); r.SaveName = c.Str(2); });
        Reg("StartDialogScene", c => { var r = RequestUi("dialog_scene", c.Str(0), c.Has(1) ? c.Str(1) : ""); r.SaveName = c.Str(2); });
        Reg("StartDialogSceneForPlayers", c => { var r = RequestUi("dialog_scene", c.Str(1), c.Has(2) ? c.Str(2) : ""); r.Players = PlayersFilter(c.Int(0)); });
        Reg("StartCutSceneInt", c => { var r = RequestUi("cut_scene", c.Str(0), c.Has(1) ? c.Str(1) : ""); r.SaveName = c.Str(2); });
        Reg("StartCutScene", c => { var r = RequestUi("cut_scene", c.Str(0), c.Has(1) ? c.Str(1) : ""); r.SaveName = c.Str(2); });
        Reg("StartCutSceneForPlayers", c => { var r = RequestUi("cut_scene", c.Str(1), c.Has(2) ? c.Str(2) : ""); r.Players = PlayersFilter(c.Int(0)); });
        Reg("StartAdvMapDialog", c =>
        {
            int index = c.Has(0) ? c.Int(0) : 0;
            if (index < 0 || index >= Data.Dialogs.Count) throw c.Error($"Adventure map dialog #{index} is not exists");
            RequestUi("adv_map_dialog", index.ToString(), c.Has(1) ? c.Str(1) : "");
        });
        Reg("OverrideAdvMapDialogPos", c => Emit("adv_dialog_pos", ("dialog", c.Int(0)), ("x", c.Number(1)), ("y", c.Number(2))));
        Reg("OpenPuzzleMap", c => RequestUi("puzzle_map", c.Int(1).ToString()).Players.Add(c.Int(0)));

        Reg("ShowFlyingSign", c =>
        {
            var (res, values) = MessageArg(c, 0);
            Emit("flying_sign", ("text", res), ("object", c.Str(1)), ("player", c.Has(2) ? c.Int(2) : -1),
                ("time", c.Has(3) ? c.Number(3) : 1.0), ("values", string.Join(";", values.Select(kv => kv.Key + "=" + kv.Value))));
        });
        Reg("Play2DSound", c => Emit("sound_2d", ("sound", c.Str(0))));
        Reg("Play2DSoundForPlayers", c => Emit("sound_2d", ("sound", c.Str(1)), ("players", string.Join(",", PlayersFilter(c.Int(0))))));
        Reg("Play3DSound", c => Emit("sound_3d", ("sound", c.Str(0)), ("x", c.Number(1)), ("y", c.Number(2)), ("floor", c.Int(3))));
        Reg("Play3DSoundForPlayers", c => Emit("sound_3d", ("sound", c.Str(1)), ("x", c.Number(2)), ("y", c.Number(3)), ("floor", c.Int(4)), ("players", string.Join(",", PlayersFilter(c.Int(0))))));
        Reg("MoveCamera", c => MoveCamera(c, 0, null));
        Reg("MoveCameraForPlayers", c => MoveCamera(c, 1, PlayersFilter(c.Int(0))));
        Reg("DisableCameraFollowHeroes", c => World.Settings["camera_follow_disabled"] = $"{c.Bool(0)}|{c.Bool(1)}|{c.Bool(2)}");
        Reg("DisableCameraFollowHeroesForPlayers", c => World.Settings["camera_follow_disabled"] = $"{c.Bool(1)}|{c.Bool(2)}|{c.Bool(3)}");
        Reg("SetAmbientLight", c => Emit("ambient_light", ("floor", c.Int(0)), ("light", c.Str(1)), ("fade", c.Bool(2)), ("time", c.Has(3) ? c.Number(3) : 1.0)));
        Reg("SetAmbientLightForPlayers", c => Emit("ambient_light", ("floor", c.Int(1)), ("light", c.Str(2)), ("fade", c.Bool(3)), ("time", c.Has(4) ? c.Number(4) : 1.0)));
        Reg("SetCombatAmbientLight", c => World.Settings["combat_light"] = c.Str(0));
        Reg("SetObjectFlashlight", c =>
        {
            World.Settings["flashlight:" + c.Str(0)] = c.Str(1);
            Emit("flashlight", ("object", c.Str(0)), ("light", c.Str(1)));
        });
        Reg("PlayVisualEffect", c =>
        {
            Emit("visual_effect", ("effect", c.Str(0)), ("object", c.Str(1)), ("tag", c.Str(2)),
                ("x", c.Number(3)), ("y", c.Number(4)), ("z", c.Number(5)), ("rot", c.Number(6)), ("floor", c.Int(7)));
        });
        Reg("StopVisualEffects", c => Emit("stop_visual_effects", ("tag", c.Str(0))));
        Reg("PlayObjectAnimation", c =>
        {
            var name = c.Str(0);
            if (Game.HeroByName(name) == null && Game.ObjectByName(name) == null) throw c.Error($"Object \"{name}\" does not exist");
            Emit("object_animation", ("object", name), ("animation", c.Str(1)), ("action", c.Int(2)));
        });
    }

    void MoveCamera(HostContext c, int a, List<int>? players)
    {
        Emit("move_camera", ("x", c.Number(a)), ("y", c.Number(a + 1)), ("floor", c.Int(a + 2)),
            ("zoom", c.Has(a + 3) ? c.Number(a + 3) : 50), ("pitch", c.Has(a + 4) ? c.Number(a + 4) : Math.PI / 2),
            ("yaw", c.Has(a + 5) ? c.Number(a + 5) : 0), ("noZoom", c.Bool(a + 6)), ("noRotate", c.Bool(a + 7)),
            ("fast", c.Bool(a + 8)), ("players", players == null ? "" : string.Join(",", players)));
    }
}
