using Godot;
using H5Reborn.Adventure;

namespace H5Reborn.Town;

/// <summary>
/// Dev/test switches for the town and hero screens:
/// `--view-town=TOWN_HEAVEN` (3D interior alone, every building at its highest level),
/// `--town-open` (with --adventure=…: open the human's first town once the map is up),
/// `--town-goto=hall|recruit|tavern|guild|market|blacksmith|fort|hero|trade|spellbook|kingdom`
/// (then open that sub-screen), `--town-build-all` (grant every building first),
/// `--town-orbit=yaw,pitch` (rotate the town camera).
/// </summary>
public static class TownLauncher
{
    public static bool OpenTown;
    public static string? Goto;
    public static bool BuildAll;
    public static Vector2? OrbitBy;

    public static bool HandleArg(string arg)
    {
        if (arg.StartsWith("--view-town="))
        {
            var type = arg["--view-town=".Length..];
            Callable.From(() => ViewTown(type)).CallDeferred();
            return true;
        }
        if (arg == "--town-open") { OpenTown = true; Callable.From(WaitForAdventure).CallDeferred(); return true; }
        if (arg.StartsWith("--town-goto=")) { Goto = arg["--town-goto=".Length..]; return true; }
        if (arg == "--town-build-all") { BuildAll = true; return true; }
        if (arg == "--adventure-debug-cam") { Callable.From(DebugCam).CallDeferred(); return true; }
        if (arg.StartsWith("--town-orbit="))
        {
            var p = arg["--town-orbit=".Length..].Split(',');
            OrbitBy = new Vector2(float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }
        return false;
    }

    static void ViewTown(string type)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.CurrentScene?.QueueFree();
        var scene = new TownScene3D(TownData.InteriorOf(type) ?? type);
        tree.Root.AddChild(scene);
        tree.CurrentScene = scene;
        Callable.From(() =>
        {
            foreach (var b in scene.Buildings.Values) scene.ShowLevel(b, b.Levels.Count - 1);
            if (OrbitBy is { } o) scene.Orbit(o.X, o.Y, 1);
        }).CallDeferred();
    }

    /// <summary>Dev: logs the adventure camera and the meshes whose bounds contain it.</summary>
    static async void DebugCam()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        for (int i = 0; i < 3000; i++)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (tree.CurrentScene is not AdventureScreen adv || adv.World?.Camera == null || i < 600) continue;
            var cam = adv.World.Camera;
            GD.Print($"[H5] adv cam pos={cam.GlobalPosition} fwd={-cam.GlobalBasis.Z} near={cam.Near} far={cam.Far} current={cam.Current}");
            int n = 0;
            foreach (var mi in adv.World.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
            {
                n++;
                if (!mi.IsVisibleInTree() || mi.Mesh == null) continue;
                var box = mi.GlobalTransform * mi.GetAabb();
                if (box.Grow(0.5f).HasPoint(cam.GlobalPosition)) GD.Print($"[H5]   camera inside {mi.GetPath()} box={box.Position}..{box.End}");
                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                    if ((mi.GetActiveMaterial(s)) is ShaderMaterial)
                    {
                        GD.Print($"[H5]   shader surface {mi.GetPath()} box={box.Position}..{box.End}");
                        break;
                    }
            }
            GD.Print($"[H5] adv meshes={n}");
            var hp = adv.Session.Human;
            foreach (var h in adv.Session.S.Heroes.Where(h => h.Owner == hp.Index)) GD.Print($"[H5] hero {h.Name} pos={h.Pos} alive={h.Alive} garrisoned={h.Garrisoned} selected={adv.SelectedHeroId == h.Id}");
            foreach (var tw in adv.Session.S.Towns.Where(t => t.Owner == hp.Index)) GD.Print($"[H5] town {tw.Name} pos={tw.Pos}");
            var target = cam.GlobalPosition + (-cam.GlobalBasis.Z) * 30;
            GD.Print($"[H5] cam looks at ~{target}");
            var hits = new List<(float D, string Name, Aabb Box)>();
            foreach (var mi in adv.World.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
            {
                if (!mi.IsVisibleInTree() || mi.Mesh == null || mi.Name == "SkyDome" || mi.Name == "Shroud") continue;
                var box = mi.GlobalTransform * mi.GetAabb();
                if (box.IntersectsSegment(cam.GlobalPosition, target))
                    hits.Add(((box.GetCenter() - cam.GlobalPosition).Length(), mi.GetPath().ToString().Replace("/root/AdventureScreen/AdvWorld/", ""), box));
            }
            foreach (var hh in hits.OrderBy(x => x.D).Take(8)) GD.Print($"[H5]   ray hits {hh.Name} at {hh.D:0.0} box={hh.Box.Position}..{hh.Box.End}");
            if (adv.World.FindChild("Town_905", true, false)?.FindChild("Mesh", true, false) is MeshInstance3D tm)
                for (int s = 0; s < tm.Mesh.GetSurfaceCount(); s++)
                {
                    var m = tm.GetActiveMaterial(s);
                    var desc = m switch
                    {
                        StandardMaterial3D sm => $"std tex={sm.AlbedoTexture?.ResourcePath}{(sm.AlbedoTexture as ImageTexture)?.GetSize()} transp={sm.Transparency} blend={sm.BlendMode} shading={sm.ShadingMode} cull={sm.CullMode} emis={sm.EmissionEnabled}",
                        ShaderMaterial shm => $"shader {shm.Shader?.ResourcePath}",
                        _ => m?.GetType().Name ?? "null",
                    };
                    GD.Print($"[H5]   town surface {s} {(tm.Mesh as ArrayMesh)?.SurfaceGetName(s)} aabb={(tm.Mesh as ArrayMesh)?.SurfaceGetArrays(s)[0].AsVector3Array().Length} {desc}");
                }
            GD.Print($"[H5] human={hp.Index} floor={adv.World.Floor} explored={string.Join(",", hp.Explored.Select(b => b.Count(x => x)))} players={string.Join(",", adv.Session.S.Players.Select(p => $"{p.Index}:{p.Human}"))} current={adv.Session.S.CurrentPlayer}");
            if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--adventure-debug-center=")) is { } cc)
            {
                var xy = cc.Split('=')[1].Split(',');
                adv.World.Camera.CenterOn(new H5.Adventure.Map.Tile(int.Parse(xy[0]), int.Parse(xy[1]), 0));
            }
            if (OS.GetCmdlineUserArgs().Contains("--adventure-hide-sky"))
                foreach (var d in adv.World.FindChildren("SkyDome", "", true, false).OfType<Node3D>()) d.Visible = false;
            if (OS.GetCmdlineUserArgs().Contains("--adventure-hide-shroud") && adv.World.FindChild("Shroud", true, false) is Node3D sh) sh.Visible = false;
            return;
        }
    }

    static async void WaitForAdventure()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        for (int i = 0; i < 2000; i++)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (tree.CurrentScene is AdventureScreen adv && adv.Hud != null && adv.Session.IsHumanTurn)
            {
                for (int k = 0; k < 10; k++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                var town = adv.MyTowns().FirstOrDefault();
                if (town == null) { GD.PushWarning("[H5] --town-open: the human player has no town"); return; }
                if (BuildAll) TownData.GrantAllBuildings(adv.Session, town);
                var screen = TownScreen.Open(adv, town);
                if (Goto != null) screen.OpenSub(Goto);
                return;
            }
        }
    }
}
