using Godot;
using H5Reborn.Adventure;

namespace H5Reborn.Town;

/// <summary>
/// Dev/test switches for the town and hero screens:
/// `--view-town=TOWN_HEAVEN` (3D interior alone, every building at its highest level),
/// `--town-open` (with --adventure=…: open the human's first town once the map is up),
/// `--town-goto=hall|recruit|tavern|guild|market|blacksmith|fort|hero|trade|spellbook|kingdom`
/// (then open that sub-screen), `--town-build-all` (grant every building first),
/// `--town-orbit=yaw,pitch` (rotate the town camera), `--town-goto=artifacts` (merchant / Walker's Hut).
/// </summary>
public static class TownLauncher
{
    public static bool OpenTown;
    public static string? Goto;
    public static bool BuildAll;
    /// <summary>Dev only (`--town-caravans=castles|dwellings|info|map|meet`): own every dwelling and one more town on the floor,
    /// send a caravan here, then open that caravan tab of the hire screen (with `--town-goto=fort`).</summary>
    public static string? CaravanTab;
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
        if (arg.StartsWith("--town-caravans=")) { CaravanTab = arg["--town-caravans=".Length..]; return true; }
        if (arg.StartsWith("--town-type=")) { DevType = arg["--town-type=".Length..]; return true; }
        if (arg.StartsWith("--town-dev-hero=")) { DevHero = arg["--town-dev-hero=".Length..]; return true; }
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
            foreach (var mmi in adv.World.FindChildren("*", "MultiMeshInstance3D", true, false).OfType<MultiMeshInstance3D>())
            {
                if (!mmi.IsVisibleInTree() || mmi.Multimesh?.Mesh == null) continue;
                var local = mmi.Multimesh.Mesh.GetAabb();
                for (int k = 0; k < mmi.Multimesh.InstanceCount; k++)
                {
                    var box = mmi.GlobalTransform * mmi.Multimesh.GetInstanceTransform(k) * local;
                    if (box.IntersectsSegment(cam.GlobalPosition, target))
                        hits.Add(((box.GetCenter() - cam.GlobalPosition).Length(), $"{mmi.Name}[{k}]", box));
                }
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

    static string? DevType, DevHero;

    /// <summary>
    /// Dev (screenshots): `--town-dev-hero=avenger` moves the human's first hero into the town as the visiting hero
    /// with Expert Avenger and kills of twice the weekly growth of every tier 1–3 base creature.
    /// </summary>
    static void DevSetupHero(AdventureScreen adv, H5.Adventure.State.TownState town, string what)
    {
        var h = adv.Session.S.Heroes.FirstOrDefault(x => x.Alive && x.Owner == town.Owner);
        if (h == null) return;
        h.Pos = adv.Session.Game.EntranceOf(town);
        h.InTown = town.Object;
        // `--town-dev-hero=def:<HeroId>` swaps the hero's definition (portrait checks, e.g. def:GodricMP).
        if (what.StartsWith("def:", StringComparison.Ordinal)) h.DefId = what[4..];
        // `--town-dev-hero=garrison`: the hero sits in the garrison (empty visiting row checks).
        if (what == "garrison") { Array.Fill(town.Garrison, null); h.Garrisoned = true; }
        if (what == "avenger")
        {
            h.Skills.RemoveAll(s => s.SkillId == "HERO_SKILL_AVENGER");
            h.Skills.Add(new H5.Core.Data.SkillEntry("HERO_SKILL_AVENGER", H5.Core.Data.Mastery.Expert));
            foreach (var c in adv.Session.Game.Core.Creatures.Values.Where(c => c.Stock && c.Tier <= 3 && !c.Upgrade))
                h.Kills[c.Id] = 2 * c.WeeklyGrowth;
            h.FavoriteEnemies = new() { "CREATURE_PEASANT", "" };
            town.Built[H5.Adventure.Engine.Adventure.AvengersGuild] = 1;
        }
        // `--town-dev-hero=barbarian`: a level 12 Barbarian with the Walker's Hut built (talisman screen checks).
        if (what == "barbarian")
        {
            h.Class = "HERO_CLASS_BARBARIAN";
            h.Level = 12;
            town.Built[H5.Adventure.Engine.Adventure.WalkersHut] = 1;
        }
        // `--town-dev-hero=artificer`: Expert Artificer, Knowledge 12, a gremlin stack and two crafted creature artifacts.
        if (what == "artificer")
        {
            h.Skills.RemoveAll(s => s.SkillId == H5.Adventure.Engine.Adventure.ArtificerSkill);
            h.Skills.Add(new H5.Core.Data.SkillEntry(H5.Adventure.Engine.Adventure.ArtificerSkill, H5.Core.Data.Mastery.Expert));
            h.Knowledge = 12;
            h.Army[0] = new H5.Adventure.State.Stack("CREATURE_GREMLIN", 30);
            foreach (var fx in new[] { new[] { H5.Core.Data.MicroEffect.Piercing }, new[] { H5.Core.Data.MicroEffect.Haste, H5.Core.Data.MicroEffect.Luck, H5.Core.Data.MicroEffect.Defence } })
                if (H5.Adventure.Engine.Adventure.BuildMicroArtifact(h, fx) is { } a) H5.Adventure.Engine.Adventure.MicroBagAdd(h, a);
            town.Built[H5.Adventure.Engine.Adventure.ArcaneForgeBuilding] = 1;
        }
    }

    static void DevSetupCaravans(AdventureScreen adv, H5.Adventure.State.TownState town)
    {
        var g = adv.Session.Game;
        foreach (var o in g.Map.Objects.Where(o => o.Kind == H5.Adventure.Map.ObjectKind.Dwelling && o.Pos.Floor == town.Pos.Floor))
        {
            var os = g.S.Obj(o.Index);
            os.Owner = town.Owner;
            for (int i = 0; i < os.Available.Length; i++) os.Available[i] = Math.Max(os.Available[i], 8);
        }
        H5.Adventure.State.TownState? other = null;
        foreach (var t in g.S.Towns.Where(t => t != town && t.Pos.Floor == town.Pos.Floor)
            .OrderBy(t => Math.Abs(t.Pos.X - town.Pos.X) + Math.Abs(t.Pos.Y - town.Pos.Y)))
        {
            int was = t.Owner;
            t.Owner = town.Owner;
            if (g.CaravanSources(town).Any(s => s.Object == t.Object)) { other = t; break; }
            t.Owner = was;
        }
        if (other != null)
        {
            other.Owner = town.Owner;
            other.Built["TB_DWELLING_1"] = Math.Max(1, other.Level("TB_DWELLING_1"));
            other.Built["TB_DWELLING_2"] = Math.Max(2, other.Level("TB_DWELLING_2"));
            other.Available[0] = 20; other.Available[1] = 12;
        }
        var p = g.S.Players.First(x => x.Index == town.Owner);
        p.Purse = new H5.Adventure.State.Purse(new H5.Core.Data.Resources(99, 99, 99, 99, 99, 99, 99999));
        if (g.CaravanSources(town).FirstOrDefault(s => !s.IsTown) is { Object: var src } && g.DwellingRows(src) is { Count: > 0 } rows)
        {
            var r = adv.Session.Apply(new H5.Adventure.Engine.BuyToCaravanCmd(src, town.Object, g.Map.Objects[src].Shared.DwellingCreatures[rows[0]], 3) { Player = town.Owner });
            GD.Print($"[H5] --town-caravans: sources {g.CaravanSources(town).Count}, caravan {(r.Ok ? "sent" : r.Error)}");
        }
        TownHireScreen.DevTab = CaravanTab;
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
                GD.Print($"[H5] --town-open: {town.Name} {town.Type}");
                if (DevType != null) town.Type = DevType;
                if (BuildAll) TownData.GrantAllBuildings(adv.Session, town);
                if (DevHero != null) DevSetupHero(adv, town, DevHero);
                if (CaravanTab != null) DevSetupCaravans(adv, town);
                if (CaravanTab == "meet" && adv.Session.S.Caravans.FirstOrDefault() is { } mc
                    && adv.Session.S.Heroes.FirstOrDefault(h => h.Alive && h.Owner == mc.Owner) is { } mh)
                {
                    // `--town-caravans=meet`: the owner's hero next to the caravan, one swap of different stacks,
                    // then the caravan meet screen (0043cdb0).
                    mh.InTown = -1; mh.Garrisoned = false;
                    mh.Pos = mc.Pos.Offset(1, 0);
                    int cs = Array.FindIndex(mc.Army, x => x is { Count: > 0 });
                    int hs = Array.FindIndex(mh.Army, x => x is { Count: > 0 } && x.Creature != mc.Army[cs]!.Creature);
                    if (cs >= 0 && hs >= 0 && mh.Army.Count(x => x is { Count: > 0 }) > 1)
                    {
                        var sr = adv.Session.Apply(new H5.Adventure.Engine.SwapStacksCmd(mh.Id, -1, hs, -1, -1, cs) { Player = mh.Owner, ToCaravan = mc.Id });
                        GD.Print($"[H5] --town-caravans: swap {(sr.Ok ? "done" : sr.Error)}");
                    }
                    adv.World.SyncHero(mh);
                    adv.World.Camera.CenterOn(mc.Pos.Offset(0, 3));
                    adv.AddChild(new CaravanMeetScreen(adv, mh.Id, mc.Id));
                    return;
                }
                if (CaravanTab == "map" && adv.Session.S.Caravans.FirstOrDefault() is { } car)
                {
                    // `--town-caravans=map`: stay on the adventure map, looking at the new caravan.
                    adv.World.Caravans?.Sync();
                    adv.World.Camera.CenterOn(car.Pos.Offset(0, 3));
                    if (adv.World.GetViewport().GetCamera3D() is { } cam3 && adv.World.Caravans?.FindChild($"Caravan_{car.Id}", true, false) is Node3D cn)
                        GD.Print($"[H5] --town-caravans: caravan node on screen at {cam3.UnprojectPosition(cn.GlobalPosition)}");
                    GD.Print($"[H5] --town-caravans: caravan {car.Id} at {car.Pos}, days {car.DaysLeft}");
                    return;
                }
                var screen = TownScreen.Open(adv, town);
                if (Goto != null) screen.OpenSub(Goto);
                return;
            }
        }
    }
}
