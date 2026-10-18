using System.Globalization;
using System.Xml.Linq;
using Godot;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Assets.Model;
using H5.Core.Xdb;
using H5Reborn.Render;

namespace H5Reborn.Adventure;

/// <summary>
/// The 3D adventure map: both floors' terrain, every placed object with its original
/// model (statics batched per model; buildings, pickups, monsters and towns as nodes,
/// animated where their shared descriptor has an AnimSet), heroes in their player's
/// colour variant, per-floor lighting and sky dome, the fog-of-war pass and the hero
/// path markers. Only the floor being viewed is visible, like the original.
/// </summary>
public partial class AdvWorld : Node3D
{
    readonly AdventureSession _session;
    readonly Node3D[] _floors = new Node3D[2];
    readonly MeshInstance3D?[] _terrain = new MeshInstance3D?[2];
    readonly WorldEnvironment?[] _env = new WorldEnvironment?[2];
    readonly DirectionalLight3D?[] _sun = new DirectionalLight3D?[2];
    readonly Dictionary<int, Node3D> _objects = new();
    readonly Dictionary<int, Character> _heroes = new();
    readonly Dictionary<string, (ArrayMesh Mesh, ModelAsset Model)?> _meshCache = new(StringComparer.OrdinalIgnoreCase);
    readonly ModelLoader _loader = new(Game.Instance.Xdb);
    readonly AnimSetLoader _anims = new(Game.Instance.Xdb);
    ShaderMaterial? _shroudMaterial;
    ImageTexture?[] _shroudTex = new ImageTexture?[2];
    Node3D _pathRoot = null!;
    Node3D? _selection;

    public AdvCamera Camera { get; private set; } = null!;
    /// <summary>Dev (`--adventure-reveal`): no fog of war.</summary>
    public static bool RevealAll;
    public int Floor { get; private set; }
    public AdvMap Map => _session.Map;

    public AdvWorld(AdventureSession session)
    {
        _session = session;
        Name = "AdvWorld";
    }

    public override void _Ready()
    {
        for (int f = 0; f < 2; f++)
        {
            var floor = Map.Floor(f);
            _floors[f] = new Node3D { Name = $"Floor{f}", Visible = false };
            AddChild(_floors[f]);
            if (floor?.Data == null) continue;
            _terrain[f] = AdvTerrain.Build(floor.Data);
            _floors[f].AddChild(_terrain[f]);
            BuildLighting(f);
        }
        BuildStatics();
        foreach (var o in Map.Objects)
            if (o.Kind != ObjectKind.Static && o.Kind != ObjectKind.Hero) SyncObject(o.Index);
        foreach (var h in _session.S.Heroes) SyncHero(h);

        _pathRoot = new Node3D { Name = "Path" };
        AddChild(_pathRoot);
        BuildShroud();

        Camera = new AdvCamera(this) { Name = "Camera" };
        AddChild(Camera);
        Camera.MakeCurrent();
        SetFloor(Map.InitialFloor);
        PlaceHeroesWhenPhysicsReady();
    }

    /// <summary>Colliders only answer queries after a physics step; re-place heroes then.</summary>
    async void PlaceHeroesWhenPhysicsReady()
    {
        for (int i = 0; i < 2; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        foreach (var h in _session.S.Heroes) SyncHero(h);
    }

    // ---------------------------------------------------------------- coordinates

    float _lastCamDistance = -1;

    /// <summary>
    /// Keeps the far view usable when the camera pulls back past the stock 70 (extended zoom):
    /// fog thins in proportion so distant terrain stays readable, and the sun's shadow range grows
    /// with the view. At or below the stock distance the original values apply unchanged.
    /// </summary>
    public void OnCameraDistance(float distance)
    {
        if (Math.Abs(distance - _lastCamDistance) < 0.5f) return;
        _lastCamDistance = distance;
        float k = Math.Max(1f, distance / AdvCamera.MaxDistance);
        for (int f = 0; f < 2; f++)
        {
            if (_envRes[f] is { } env && env.FogEnabled)
            {
                if (!env.HasMeta("baseFog")) env.SetMeta("baseFog", env.FogDensity);
                env.FogDensity = (float)env.GetMeta("baseFog") / k;
            }
            if (_sun[f] is { } sun) sun.DirectionalShadowMaxDistance = 120 * k;
        }
    }

    public float HeightAt(int floor, float mapX, float mapY) =>
        Map.Floor(floor)?.Data is { } t ? t.SampleHeight(Math.Clamp(mapX, 0, t.TilesX), Math.Clamp(mapY, 0, t.TilesY)) : 0f;

    /// <summary>World position of a tile centre on the terrain.</summary>
    /// <summary>
    /// World units per map tile. Models, the original cameras (Rod 50, siege/town models) and the exe's
    /// camera anchors all use 2 units per tile; terrain heights are already world units.
    /// </summary>
    public const float Unit = 2f;

    public Vector3 TileCenter(Tile t) => new((t.X + 0.5f) * Unit, HeightAt(t.Floor, t.X + 0.5f, t.Y + 0.5f), -(t.Y + 0.5f) * Unit);

    /// <summary>World position of a fractional map position (tile units) on the terrain.</summary>
    public Vector3 MapToWorld(float mapX, float mapY) => new(mapX * Unit, HeightAt(Floor, mapX, mapY), -mapY * Unit);

    /// <summary>Highest walkable surface above the terrain a hero stands on (town plazas and gates).</summary>
    const float MaxStepUp = 1.6f;

    /// <summary>
    /// Where a hero or path marker stands on a tile: the terrain, or the top of object
    /// geometry built over it (a town's paved base in front of its gate) when that lies
    /// within <see cref="MaxStepUp"/> above the ground — roofs and arches above are ignored.
    /// Uses the collision shapes added for towns (see <see cref="AddWalkable"/>).
    /// </summary>
    public Vector3 StandPoint(Tile t) => StandPointAt(TileCenter(t));

    /// <summary>
    /// <see cref="StandPoint(Tile)"/> for any point on the map, used while a hero walks between tile
    /// centres so it follows the terrain and town bases continuously instead of cutting straight
    /// lines between centre heights.
    /// </summary>
    public Vector3 StandPointAt(float mapX, float mapY, int floor) =>
        StandPointAt(new Vector3(mapX * Unit, HeightAt(floor, mapX, mapY), -mapY * Unit));

    Vector3 StandPointAt(Vector3 p)
    {
        var space = GetWorld3D()?.DirectSpaceState;
        if (space == null) return p;
        float best = p.Y;
        var from = p + new Vector3(0, MaxStepUp, 0);
        for (int i = 0; i < 4; i++)
        {
            var q = PhysicsRayQueryParameters3D.Create(from, p - new Vector3(0, 0.05f, 0), WalkableMask);
            q.HitBackFaces = false;
            var hit = space.IntersectRay(q);
            if (hit.Count == 0) break;
            var y = ((Vector3)hit["position"]).Y;
            if (y < MaxStepUp + p.Y - 0.01f) { best = Math.Max(best, y); break; }
            from = (Vector3)hit["position"] - new Vector3(0, 0.02f, 0);
        }
        return new Vector3(p.X, best, p.Z);
    }

    const uint WalkableMask = 1 << 9;

    /// <summary>Adds a static trimesh collider for an object's mesh so heroes can stand on it.</summary>
    static void AddWalkable(Node3D node, ArrayMesh mesh)
    {
        var body = new StaticBody3D { Name = "Walkable", CollisionLayer = WalkableMask, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = mesh.CreateTrimeshShape() });
        node.AddChild(body);
    }

    public static Vector2 WorldToMap(Vector3 w) => new(w.X / Unit, -w.Z / Unit);

    /// <summary>The tile under a ray on the current floor (heightfield ray march), or null.</summary>
    public Tile? TileAtRay(Vector3 origin, Vector3 dir)
    {
        if (Map.Floor(Floor)?.Data is not { } t) return null;
        dir = dir.Normalized();
        Vector3? prev = null;
        for (float s = 0; s < 800; s += 0.3f)
        {
            var p = origin + dir * s;
            var m = WorldToMap(p);
            float h = m.X >= 0 && m.Y >= 0 && m.X <= t.TilesX && m.Y <= t.TilesY ? t.SampleHeight(m.X, m.Y) : 0f;
            if (p.Y <= h)
            {
                // Refine between the previous sample and this one.
                var a = prev ?? p;
                for (int i = 0; i < 6; i++)
                {
                    var mid = (a + p) * 0.5f;
                    var mm = WorldToMap(mid);
                    if (mid.Y <= t.SampleHeight(Math.Clamp(mm.X, 0, t.TilesX), Math.Clamp(mm.Y, 0, t.TilesY))) p = mid; else a = mid;
                }
                var hit = WorldToMap(p);
                int x = (int)MathF.Floor(hit.X), y = (int)MathF.Floor(hit.Y);
                return x >= 0 && y >= 0 && x < t.TilesX && y < t.TilesY ? new Tile(x, y, Floor) : null;
            }
            prev = p;
        }
        return null;
    }

    public void SetFloor(int floor)
    {
        if (Map.Floor(floor) == null) floor = Map.Floor(0) != null ? 0 : 1;
        Floor = floor;
        for (int f = 0; f < 2; f++)
        {
            _floors[f].Visible = f == floor;
            if (_sun[f] != null) _sun[f]!.Visible = f == floor;
            if (_env[f] != null) _env[f]!.Environment = f == floor ? _envRes[f] : null;
        }
        if (_shroudMaterial != null && _shroudTex[floor] != null) _shroudMaterial.SetShaderParameter("shroud", _shroudTex[floor]);
        RefreshShroud();
    }

    // ---------------------------------------------------------------- lighting

    readonly Godot.Environment?[] _envRes = new Godot.Environment?[2];

    static readonly string[] RmgGroundLights =
    {
        "Lights/_(AmbientLight)/0_Default_AmbientLight.xdb",
        "Lights/_(AmbientLight)/AdvMap/C1M1.xdb",
        "Lights/_(AmbientLight)/Day_light_AdvMap01(EugeneTest).xdb",
        "Lights/_(AmbientLight)/Arena/mausoleum/Day_light_AdvMap01(EugeneTest).xdb",
        "Lights/_(AmbientLight)/Arena/inferno/DesertArena01.xdb",
    };

    void BuildLighting(int f)
    {
        var files = _session.Files;
        XElement? light = null;
        string lightFile = files.MapXdbPath;
        if (files.Exists(files.MapXdbPath))
        {
            var mapXdb = files.LoadXml(files.MapXdbPath);
            var listName = f == 1 ? "UndergroundAmbientLights" : "GroundAmbientLights";
            var single = f == 1 ? "UndergroundAmbientLight" : "AmbientLight";
            var lightRef = mapXdb.Root?.Element(listName)?.Elements("Item").FirstOrDefault(i => !string.IsNullOrEmpty((string?)i.Attribute("href")))
                           ?? mapXdb.Root?.Element(single);
            if (lightRef != null && (string?)lightRef.Attribute("href") is { Length: > 0 } href)
            {
                lightFile = files.Resolve(href, files.MapXdbPath);
                if (files.Exists(lightFile)) light = files.LoadXml(lightFile).Root;
            }
        }
        else if (f == 0)
        {
            // Generated map (no descriptor): the stock generator gives the surface one of these lights
            // (as seen in the RMG maps it saved); chosen by the game seed so every peer agrees.
            lightFile = RmgGroundLights[(int)(_session.Seed % (ulong)RmgGroundLights.Length)];
            if (files.Exists(lightFile)) light = files.LoadXml(lightFile).Root;
        }
        Vector3 V(string n, float d)
        {
            var e = light?.Element(n);
            return e == null ? new Vector3(d, d, d) : new Vector3(F(e.Element("x"), d), F(e.Element("y"), d), F(e.Element("z"), d));
        }
        Color C(Vector3 v, float scale = 1) => new(Math.Min(1, v.X * scale), Math.Min(1, v.Y * scale), Math.Min(1, v.Z * scale));

        // The original lights in an overbright (×2) range, like the combat arenas (ArenaScene).
        var fog = V("FogColor", 0.5f);
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = Colors.Black,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = C(V("AmbientColor", 0.3f), 2f),
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        float fogStart = F(light?.Element("FogStartDistance"), 1000), fogDist = F(light?.Element("FogDistance"), 1000);
        if (fogStart < 900)
        {
            env.FogEnabled = true;
            env.FogLightColor = C(fog);
            env.FogDensity = 1f / Math.Max(10f, fogDist) * 0.5f;
        }
        _envRes[f] = env;
        _env[f] = new WorldEnvironment { Name = $"Env{f}" };
        AddChild(_env[f]);

        var sun = new DirectionalLight3D
        {
            Name = $"Sun{f}",
            LightColor = C(V("LightColor", 0.5f), 2f),
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 120,
        };
        float p = Mathf.DegToRad(F(light?.Element("Pitch"), 45)), y = Mathf.DegToRad(F(light?.Element("Yaw"), 45));
        var toSunMap = new Vector3(Mathf.Cos(p) * Mathf.Cos(y), Mathf.Cos(p) * Mathf.Sin(y), Mathf.Sin(p));
        var toSun = new Vector3(toSunMap.X, toSunMap.Z, -toSunMap.Y);
        AddChild(sun);
        sun.LookAtFromPosition(Vector3.Zero, -toSun, Mathf.Abs(toSun.Y) > 0.99f ? Vector3.Forward : Vector3.Up);
        _sun[f] = sun;

        var skyHref = (string?)light?.Element("SkyDome")?.Attribute("href");
        if (!string.IsNullOrEmpty(skyHref) && Mesh(XdbRef.Parse(skyHref, lightFile).Path) is { } sky)
        {
            var mesh = (ArrayMesh)sky.Mesh.Duplicate();
            for (int i = 0; i < mesh.GetSurfaceCount(); i++)
            {
                // Cloud layers use M_CLOUDS_H5, which ModelBuilder builds as a drifting ShaderMaterial
                // (opaque, culled, depth-writing); as a dome it must be a plain unshaded sky backdrop.
                StandardMaterial3D? m = mesh.SurfaceGetMaterial(i) switch
                {
                    StandardMaterial3D std => (StandardMaterial3D)std.Duplicate(),
                    ShaderMaterial sm => new StandardMaterial3D { AlbedoTexture = sm.GetShaderParameter("tex").As<Texture2D>() },
                    _ => null,
                };
                if (m == null) continue;
                m.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                m.DisableFog = true;
                m.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                mesh.SurfaceSetMaterial(i, m);
            }
            var dome = new MeshInstance3D { Name = "SkyDome", Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            dome.Position = new Vector3(Map.TilesX * 0.5f * Unit, 0, -Map.TilesY * 0.5f * Unit);
            _floors[f].AddChild(dome);
        }
    }

    static XDocument? TryLoad(string path) =>
        Game.Instance.Fs.Exists(path) ? Game.Instance.Xdb.Load(path) : null;

    static float F(XElement? e, float def) =>
        e != null && float.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;

    // ---------------------------------------------------------------- objects

    (ArrayMesh Mesh, ModelAsset Model)? Mesh(string? modelPath)
    {
        if (string.IsNullOrEmpty(modelPath)) return null;
        if (_meshCache.TryGetValue(modelPath, out var cached)) return cached;
        (ArrayMesh, ModelAsset)? result = null;
        try
        {
            if (Game.Instance.Fs.Exists(modelPath))
            {
                var model = _loader.Load(modelPath);
                result = (ModelBuilder.BuildMesh(model, skinned: false), model);
            }
        }
        catch (Exception e) { GD.PushWarning($"[H5] adventure model failed {modelPath}: {e.Message}"); }
        _meshCache[modelPath] = result;
        return result;
    }

    Transform3D ObjectTransform(MapObject o)
    {
        var pos = TileCenter(o.Pos);
        return new Transform3D(new Basis(Vector3.Up, o.Rot), pos);
    }

    /// <summary>Scenery (AdvMapStatic) batched into one MultiMesh per model and floor.</summary>
    void BuildStatics()
    {
        foreach (var group in Map.OfKind(ObjectKind.Static).GroupBy(o => (o.Shared.ModelPath, o.Pos.Floor)))
        {
            if (group.Key.Floor is < 0 or > 1 || Mesh(group.Key.ModelPath) is not { } entry) continue;
            var list = group.ToList();
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = entry.Mesh, InstanceCount = list.Count };
            for (int i = 0; i < list.Count; i++) mm.SetInstanceTransform(i, ObjectTransform(list[i]));
            _floors[group.Key.Floor].AddChild(new MultiMeshInstance3D { Name = System.IO.Path.GetFileNameWithoutExtension(group.Key.ModelPath), Multimesh = mm });
        }
    }

    /// <summary>Creates, updates or removes the node of a non-static object to match the game state.</summary>
    public void SyncObject(int index)
    {
        var o = Map.Objects[index];
        _session.S.Objects.TryGetValue(index, out var os);
        bool removed = os?.Removed == true;
        if (removed)
        {
            if (_objects.Remove(index, out var gone)) gone.QueueFree();
            return;
        }
        var (model, animSet) = VisualFor(o, os);
        var key = model + "|" + animSet;
        if (_objects.TryGetValue(index, out var existing))
        {
            if ((string?)existing.GetMeta("visual", "") == key) return;
            existing.QueueFree();
            _objects.Remove(index);
        }
        if (model == null || o.Pos.Floor is < 0 or > 1) return;
        Node3D node;
        if (animSet != null)
        {
            try
            {
                var c = CharacterBuilder.BuildFromModel(model, animSet);
                // Buildings without idle00 (a few use idle01/page01…) loop their first clip.
                if (!c.Has("idle00") && c.Player.GetAnimationList() is { Length: > 0 } clips)
                    c.Play(clips[0].Replace(Character.Lib, ""), loop: true);
                node = c;
            }
            catch (Exception e)
            {
                GD.PushWarning($"[H5] animated object {model}: {e.Message}");
                node = StaticNode(model);
            }
        }
        else node = StaticNode(model);
        node.Name = $"{o.Kind}_{index}";
        node.Transform = ObjectTransform(o);
        if (o.Kind == ObjectKind.Town && Mesh(model) is { } walk) AddWalkable(node, walk.Mesh);
        node.SetMeta("visual", key);
        node.SetMeta("object", index);
        _floors[o.Pos.Floor].AddChild(node);
        _objects[index] = node;
    }

    Node3D StaticNode(string model)
    {
        var node = new Node3D();
        if (Mesh(model) is { } entry) node.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = entry.Mesh });
        return node;
    }

    /// <summary>Model and animation set for an object in its current state.</summary>
    (string? Model, string? AnimSet) VisualFor(MapObject o, ObjState? os)
    {
        string? Href(XElement? root, string name, string file)
        {
            var h = (string?)root?.Element(name)?.Attribute("href");
            return string.IsNullOrEmpty(h) ? null : XdbRef.Parse(h, file).Path;
        }
        var shared = o.Shared;
        switch (o.Kind)
        {
            case ObjectKind.Monster:
            {
                // Random monsters are rolled by the engine; show the creature actually standing there.
                var creature = os?.Guards.FirstOrDefault()?.Creature ?? os?.Creature ?? o.Creature;
                if (creature != null && MonsterShared(creature) is { } ms && TryLoad(ms)?.Root is { } msr)
                    return (Href(msr, "Model", ms), Href(msr, "AnimSet", ms));
                break;
            }
            case ObjectKind.Treasure when os is { ResourceKind: >= 0 } && shared.Type.Contains("RANDOM", StringComparison.OrdinalIgnoreCase):
            {
                // Random resource piles show the resource the engine rolled.
                string[] files = { "Wood", "Ore", "Mercury", "Crystal", "Sulfur", "Gems", "Gold" };
                var res = $"MapObjects/{files[Math.Clamp(os.ResourceKind, 0, 6)]}.(AdvMapTreasureShared).xdb";
                if (TryLoad(res)?.Root is { } rr) return (Href(rr, "Model", res), Href(rr, "AnimSet", res));
                break;
            }
            case ObjectKind.Artifact when os?.ArtifactId is { } art:
            {
                // Every artifact names its map object (Artifacts.xdb ArtifactShared).
                const string table = "GameMechanics/RefTables/Artifacts.xdb";
                var item = Game.Instance.Xdb.Load(table).Root?.Element("objects")?.Elements("Item").FirstOrDefault(i => (string?)i.Element("ID") == art);
                if (Href(item?.Element("obj"), "ArtifactShared", table) is { } ash && TryLoad(ash)?.Root is { } ar)
                    return (Href(ar, "Model", ash), Href(ar, "AnimSet", ash));
                break;
            }
            case ObjectKind.Town when shared.Raw?.Element("Exterior") is { } ext:
            {
                // AdvMapTownExterior upgrades: town, +fort, +citadel, then the same with a mage guild,
                // then city and capital variants (order as authored in the stock towns).
                var upgrades = ext.Elements().FirstOrDefault()?.Element("upgrades")?.Elements("Item").ToList();
                if (upgrades is { Count: > 0 })
                {
                    var t = _session.S.Town(o.Index);
                    int hall = t?.Level("TB_TOWN_HALL") ?? 1, fort = Math.Min(2, t?.Level("TB_FORT") ?? 0);
                    bool guild = (t?.Level("TB_MAGIC_GUILD") ?? 0) > 0;
                    int i = hall >= 4 ? 9 : hall >= 3 ? 6 + fort : (guild ? 3 : 0) + fort;
                    var item = upgrades[Math.Clamp(i, 0, upgrades.Count - 1)];
                    var file = shared.Path;
                    return (Href(item, "Model", file), null);
                }
                break;
            }
        }
        return (shared.ModelPath, Href(shared.Raw, "AnimSet", shared.Path));
    }

    static readonly Dictionary<string, string?> _monsterShared = new(StringComparer.OrdinalIgnoreCase);

    static string? MonsterShared(string creature)
    {
        if (_monsterShared.TryGetValue(creature, out var p)) return p;
        p = null;
        if (Arena.CreatureModels.CreatureFile(creature) is { } file && TryLoad(file)?.Root is { } root)
        {
            var h = (string?)root.Element("MonsterShared")?.Attribute("href");
            if (!string.IsNullOrEmpty(h)) p = XdbRef.Parse(h, file).Path;
        }
        return _monsterShared[creature] = p;
    }

    // ---------------------------------------------------------------- heroes

    public Character? HeroNode(int heroId) => _heroes.GetValueOrDefault(heroId);

    /// <summary>Creates/moves/removes a hero's node to match the state.</summary>
    public void SyncHero(H5.Adventure.State.HeroState h)
    {
        if (!h.Alive || h.Garrisoned)
        {
            if (_heroes.Remove(h.Id, out var gone)) gone.QueueFree();
            return;
        }
        if (!_heroes.TryGetValue(h.Id, out var node))
        {
            node = BuildHero(h);
            if (node == null) return;
            _heroes[h.Id] = node;
        }
        if (node.GetParent() != _floors[h.Pos.Floor])
        {
            node.GetParent()?.RemoveChild(node);
            _floors[h.Pos.Floor].AddChild(node);
        }
        node.Position = StandPoint(h.Pos);
    }

    Character? BuildHero(H5.Adventure.State.HeroState h)
    {
        if (!Game.Instance.Db.Heroes.TryGetValue(h.DefId, out var def) || TryLoad(def.SourcePath)?.Root is not { } shared)
        {
            GD.PushWarning($"[H5] hero {h.Id} '{h.DefId}': no shared hero descriptor");
            return null;
        }
        var charHref = (string?)shared.Element("HeroCharacterAdventure")?.Attribute("href");
        if (string.IsNullOrEmpty(charHref)) { GD.PushWarning($"[H5] hero {h.DefId}: no HeroCharacterAdventure"); return null; }
        var charPath = XdbRef.Parse(charHref, def.SourcePath).Path;
        if (TryLoad(charPath)?.Root is not { } character) { GD.PushWarning($"[H5] hero {h.DefId}: missing {charPath}"); return null; }
        string? Href(XElement? e, string file)
        {
            var v = (string?)e?.Attribute("href");
            return string.IsNullOrEmpty(v) ? null : XdbRef.Parse(v, file).Path;
        }
        var model = Href(character.Element("Model"), charPath);
        var colour = _session.S.Players.Find(p => p.Index == h.Owner)?.Colour ?? "";
        var variants = character.Element("ColourModels")?.Elements("Item").ToList();
        int ci = AdvColors.ColourModelIndex(colour);
        if (variants is { Count: > 0 } && ci < variants.Count && Href(variants[ci], charPath) is { } coloured && Game.Instance.Fs.Exists(coloured))
            model = coloured;
        var set = Href(character.Element("AdvMapAnimSet"), charPath) ?? Href(character.Element("ArenaAnimSet"), charPath);
        if (model == null) return null;
        try
        {
            var c = CharacterBuilder.BuildFromModel(model, set, $"Hero_{h.Id}");
            if (AdventureScreen.Verbose) GD.Print($"[H5] hero {h.Id} {h.DefId} at {h.Pos}: model {model}, anims {string.Join(",", c.Player.GetAnimationList())}");
            c.SetMeta("hero", h.Id);
            var mapHero = Map.Objects.FirstOrDefault(o => o.Kind == ObjectKind.Hero && o.Pos == h.Pos);
            c.Rotation = new Vector3(0, mapHero?.Rot ?? 0, 0);
            _heroMoveSpeed[h.Id] = set != null ? _anims.ReadAnimSet(set).FirstOrDefault(e => e.Kind == "move")?.MovementSpeed ?? 0 : 0;
            return c;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[H5] hero model {model}: {e.Message}");
            return null;
        }
    }

    readonly Dictionary<int, float> _heroMoveSpeed = new();

    /// <summary>The move clip's authored MovementSpeed (world units per second), or a default.</summary>
    public float HeroSpeed(int heroId) => _heroMoveSpeed.TryGetValue(heroId, out var s) && s > 0.1f ? s : 3.5f;

    /// <summary>The hero selection symbol (AdvMapHeroShared Selection, projected on the terrain).</summary>
    public void ShowSelection(H5.Adventure.State.HeroState? h)
    {
        // The ring is a child of the hero node and may already be gone with it.
        if (_selection != null && IsInstanceValid(_selection)) _selection.QueueFree();
        _selection = null;
        if (h == null || !Game.Instance.Db.Heroes.TryGetValue(h.DefId, out var def) || TryLoad(def.SourcePath)?.Root is not { } shared) return;
        var href = (string?)shared.Element("Selection")?.Attribute("href");
        if (string.IsNullOrEmpty(href) || Mesh(XdbRef.Parse(href, def.SourcePath).Path) is not { } entry) return;
        _selection = new MeshInstance3D { Name = "Selection", Mesh = SelfLitDecal(entry), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        if (HeroNode(h.Id) is { } hn) hn.AddChild(_selection);
        _selection.Position = new Vector3(0, 0.05f, 0);
    }

    // ---------------------------------------------------------------- path markers

    /// <summary>One step of a plotted path: tile and marker kind.</summary>
    public enum Marker { Available, Gray, Contested }

    readonly Dictionary<string, (ArrayMesh Mesh, ModelAsset Model)?> _plotCache = new();

    /// <summary>
    /// Shows a plotted path with the original HeroPlotitng markers for the hero's town type
    /// (TownTypesInfo plotting): Point* along the way, Target* at the end; Available (green)
    /// for steps reachable this turn, Gray for later days, Contested (red) when the target is a fight.
    /// </summary>
    public void ShowPath(string townType, IReadOnlyList<(Tile Tile, Marker Kind)> steps)
    {
        foreach (var c in _pathRoot.GetChildren()) c.QueueFree();
        for (int i = 0; i < steps.Count; i++)
        {
            bool last = i == steps.Count - 1;
            var (tile, kind) = steps[i];
            if (PlotMesh(townType, last, kind) is not { } entry) continue;
            var node = new MeshInstance3D { Mesh = entry.Mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            // Markers are projected on the terrain in the original; lifted a little so the
            // tile-centre height (bilinear) never puts them under the actual triangles.
            node.Position = StandPoint(tile) + new Vector3(0, 0.3f, 0);
            if (!last)
            {
                // Points are oriented along the path towards the next step.
                var next = steps[i + 1].Tile;
                node.Rotation = new Vector3(0, Mathf.Atan2(next.Y - tile.Y, next.X - tile.X), 0);
            }
            _pathRoot.AddChild(node);
        }
    }

    public void ClearPath()
    {
        foreach (var c in _pathRoot.GetChildren()) c.QueueFree();
    }

    (ArrayMesh Mesh, ModelAsset Model)? PlotMesh(string townType, bool target, Marker kind)
    {
        var key = $"{townType}|{target}|{kind}";
        if (_plotCache.TryGetValue(key, out var cached)) return cached;
        const string table = "GameMechanics/RefTables/TownTypesInfo.xdb";
        string field = (target ? "Target" : "Point") + kind switch { Marker.Available => "Available", Marker.Contested => "Contested", _ => "Gray" };
        string? PathFor(string type)
        {
            var item = Game.Instance.Xdb.Load(table).Root!.Element("objects")!.Elements("Item").FirstOrDefault(i => (string?)i.Element("ID") == type);
            var h = (string?)item?.Element("obj")?.Element("plotting")?.Element(field)?.Attribute("href");
            return string.IsNullOrEmpty(h) ? null : XdbRef.Parse(h, table).Path;
        }
        // Heroes without a town of their own (neutral races) fall back to the Haven markers.
        var result = Mesh(PathFor(townType) ?? PathFor("TOWN_HEAVEN"));
        if (result is { } r) result = (SelfLitDecal(r), r.Model);
        return _plotCache[key] = result;
    }

    /// <summary>
    /// Path markers and hero selection symbols are L_SELFILLUM / AM_OVERLAY decals whose DXT3
    /// textures are fully opaque bright shapes on black (alpha 255 everywhere): drawn additively,
    /// unlit, without depth writes.
    /// </summary>
    static ArrayMesh SelfLitDecal((ArrayMesh Mesh, ModelAsset Model) r)
    {
        var mesh = (ArrayMesh)r.Mesh.Duplicate();
        for (int i = 0; i < mesh.GetSurfaceCount(); i++)
        {
            var tex = r.Model.Materials.ElementAtOrDefault(i)?.DiffuseTexture is { } d ? Game.Instance.LoadTexture(d) : null;
            mesh.SurfaceSetMaterial(i, new StandardMaterial3D
            {
                AlbedoTexture = tex, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, TextureRepeat = false, RenderPriority = 10,
            });
        }
        return mesh;
    }

    // ---------------------------------------------------------------- fog of war

    void BuildShroud()
    {
        _shroudMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://src/Adventure/AdvShroud.gdshader") };
        _shroudMaterial.SetShaderParameter("map_size", new Vector2(Map.TilesX, Map.TilesY) * Unit);
        var quad = new MeshInstance3D
        {
            Name = "Shroud",
            Mesh = new QuadMesh { Size = new Vector2(2, 2), FlipFaces = true },
            MaterialOverride = _shroudMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384,
        };
        AddChild(quad);
    }

    /// <summary>Updates the shroud from the human player's explored tiles.</summary>
    public void RefreshShroud()
    {
        if (_shroudMaterial == null) return;
        var player = _session.Human;
        int f = Floor;
        int w = Map.TilesX, h = Map.TilesY;
        var data = new byte[w * h];
        if (RevealAll) Array.Fill(data, (byte)255);
        else if (f < player.Explored.Count && player.Explored[f] is { } bits && bits.Length >= w * h)
            for (int i = 0; i < w * h; i++) data[i] = bits[i] ? (byte)255 : (byte)0;
        var img = Image.CreateFromData(w, h, false, Image.Format.R8, data);
        if (_shroudTex[f] == null) _shroudTex[f] = ImageTexture.CreateFromImage(img);
        else _shroudTex[f]!.Update(img);
        _shroudMaterial.SetShaderParameter("shroud", _shroudTex[f]);
    }

    public bool IsExplored(Tile t)
    {
        if (RevealAll) return true;
        var p = _session.Human;
        return t.Floor < p.Explored.Count && p.Explored[t.Floor] is { } bits && t.Y * Map.TilesX + t.X < bits.Length && bits[t.Y * Map.TilesX + t.X];
    }
}
