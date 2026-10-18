using System.Xml.Linq;
using Godot;
using H5.Assets.Model;
using H5.Combat;
using H5.Core.Data;
using H5.Core.Xdb;
using H5Reborn.Render;

namespace H5Reborn.Arena;

/// <summary>
/// Siege fortifications on a 14×14 battlefield, from the town's siege ArenaDesc
/// (TownCombatDef.ArenaDesc): environment objects, the walls/gate/towers/moat as
/// (ArenaModObject)s whose model follows the fortification level (upgrades) and the
/// building's damage state (ruins), and the archers standing on the towers.
///
/// The ArenaDesc meshes are exported in place in their own frame (2 units per tile, like
/// all models). Its offset to the battle world is derived from the buildings themselves:
/// each building's intact model is centred on its footprint (ArenaBuildingDef Pos/Size in
/// internal siege tiles), so the mean difference between footprint centres and model
/// centres gives the translation; the model base (lowest point) is put on the terrain.
/// </summary>
public partial class SiegeView : Node3D
{
    readonly ArenaScene _arena;
    readonly BattleState _state;
    readonly TownCombatDef _layout;
    readonly ModelLoader _loader = new(Game.Instance.Xdb);
    readonly Dictionary<string, ArrayMesh?> _meshes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, MeshInstance3D> _buildingNodes = new();
    readonly Dictionary<int, int> _shownRuin = new();
    readonly Dictionary<int, Character> _shooters = new();   // tower unit id → archer on the tower
    Vector3 _offset;
    string _descPath = "";

    public SiegeView(ArenaScene arena, BattleState state)
    {
        Name = "Siege";
        _arena = arena;
        _state = state;
        _layout = Game.Instance.Db.TownCombat[state.Siege!.Town];
    }

    public override void _Ready()
    {
        if (_state.Siege is not { } siege) return;
        _descPath = XdbRef.Parse(siege.ArenaDesc ?? _layout.ArenaDesc, null).Path;
        _offset = ComputeOffset();

        // Environment pieces of the siege arena (walls running off-field, props).
        if (Game.Instance.Fs.Exists(_descPath))
        {
            var desc = Game.Instance.Xdb.Load(_descPath).Root!;
            foreach (var item in desc.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                var objPath = XdbRef.Parse((string?)item.Attribute("href"), _descPath).Path;
                if (!objPath.Contains("(ArenaEnvObject)") || !Game.Instance.Fs.Exists(objPath)) continue;
                var obj = Game.Instance.Xdb.Load(objPath).Root!;
                var model = XdbRef.Parse((string?)obj.Element("Model")?.Attribute("href"), objPath).Path;
                if (Mesh(model) is { } mesh) AddChild(new MeshInstance3D { Name = System.IO.Path.GetFileNameWithoutExtension(objPath), Mesh = mesh, Position = _offset });
            }
        }

        foreach (var b in siege.Buildings) UpdateBuilding(b, effects: false);
        PlaceShooters();
    }

    // ---------------------------------------------------------------- models

    ArrayMesh? Mesh(string modelPath)
    {
        if (string.IsNullOrEmpty(modelPath)) return null;
        if (_meshes.TryGetValue(modelPath, out var m)) return m;
        try { m = Game.Instance.Fs.Exists(modelPath) ? ModelBuilder.BuildMesh(_loader.Load(modelPath), skinned: false) : null; }
        catch (Exception e) { GD.PushWarning($"[H5] siege model {modelPath}: {e.Message}"); m = null; }
        return _meshes[modelPath] = m;
    }

    /// <summary>(ArenaModObject) upgrades[level].ruins[stage].Model; levels without models fall back to the nearest lower one.</summary>
    static List<string?> RuinModels(string? objHref, int fortLevel)
    {
        var path = XdbRef.Parse(objHref, null).Path;
        if (string.IsNullOrEmpty(path) || !Game.Instance.Fs.Exists(path)) return new();
        var upgrades = Game.Instance.Xdb.Load(path).Root!.Element("upgrades")?.Elements("Item").ToList() ?? new();
        for (int level = Math.Min(fortLevel, upgrades.Count - 1); level >= 0; level--)
        {
            var ruins = upgrades[level].Element("ruins")?.Elements("Item")
                .Select(r => (string?)r.Element("Model")?.Attribute("href"))
                .Select(h => string.IsNullOrEmpty(h) ? null : XdbRef.Parse(h, path).Path).ToList() ?? new();
            if (ruins.Any(r => r != null)) return ruins;
        }
        // Some layouts only author upgrade 1; use the first level that has models.
        foreach (var up in upgrades)
        {
            var ruins = up.Element("ruins")?.Elements("Item").Select(r => (string?)r.Element("Model")?.Attribute("href"))
                .Select(h => string.IsNullOrEmpty(h) ? null : XdbRef.Parse(h, path).Path).ToList() ?? new();
            if (ruins.Any(r => r != null)) return ruins;
        }
        return new();
    }

    /// <summary>Ruin stage for a building's state: intact, damaged (≤ half health, when a middle stage exists), destroyed.</summary>
    static int RuinStage(SiegeBuilding b, int stages)
    {
        if (stages <= 1) return 0;
        if (b.Destroyed) return stages - 1;
        return stages >= 3 && b.Health * 2 <= b.MaxHealth ? 1 : 0;
    }

    Vector3 ComputeOffset()
    {
        var sum = Vector3.Zero;
        int n = 0;
        float baseY = 0;
        foreach (var def in _layout.Buildings)
        {
            if (def.SizeX <= 0 || def.SizeY <= 0 || def.Object == null) continue;
            var models = RuinModels(def.Object, _state.Siege!.FortLevel);
            if (models.FirstOrDefault(m => m != null) is not { } path || !Game.Instance.Fs.Exists(path)) continue;
            var asset = _loader.Load(path);
            var pts = asset.Mesh.Parts.SelectMany(p => p.Positions).Select(ModelBuilder.ToGodot).ToList();
            if (pts.Count == 0) continue;
            var min = pts.Aggregate(new Vector3(1e9f, 1e9f, 1e9f), (a, p) => a.Min(p));
            var max = pts.Aggregate(new Vector3(-1e9f, -1e9f, -1e9f), (a, p) => a.Max(p));
            // Footprint centre in field cells (internal − offset).
            float fx = def.PosX + def.SizeX * 0.5f - _state.Siege!.OffsetX, fy = def.PosY + def.SizeY * 0.5f - _state.Siege.OffsetY;
            var target = _arena.Grid.CellCenter(fx, fy);
            var center = (min + max) * 0.5f;
            sum += new Vector3(target.X - center.X, 0, target.Z - center.Z);
            baseY += target.Y - min.Y;
            n++;
        }
        return n == 0 ? Vector3.Zero : new Vector3(sum.X / n, baseY / n, sum.Z / n);
    }

    // ---------------------------------------------------------------- buildings

    public void UpdateBuilding(SiegeBuilding b, bool effects)
    {
        if (b.DefIndex < 0 || b.DefIndex >= _layout.Buildings.Count) return;
        var def = _layout.Buildings[b.DefIndex];
        var models = RuinModels(def.Object, _state.Siege!.FortLevel);
        int stage = RuinStage(b, models.Count);
        if (_shownRuin.TryGetValue(b.Id, out var shown) && shown == stage) return;
        int previous = _shownRuin.GetValueOrDefault(b.Id, 0);
        _shownRuin[b.Id] = stage;
        if (_buildingNodes.TryGetValue(b.Id, out var old)) { old.QueueFree(); _buildingNodes.Remove(b.Id); }
        if (stage < models.Count && models[stage] is { } path && Mesh(path) is { } mesh)
        {
            var node = new MeshInstance3D { Name = $"{b.Type}_{b.Id}", Mesh = mesh, Position = _offset };
            AddChild(node);
            _buildingNodes[b.Id] = node;
        }
        if (effects && stage > previous)
        {
            // DestroyEffects are listed per ruin level (0 = first collapse stage, 1 = final).
            foreach (var fx in def.DestroyEffects.Where(e => e.RuinLevel == stage - 1 || (b.Destroyed && e.RuinLevel == def.DestroyEffects.Max(d => d.RuinLevel))))
                if (fx.Effect is { } eff) Effects.EffectPlayer.PlayAt(XdbRef.Parse(eff, null).Path, _arena, Center(b));
        }
        if (b.Destroyed && b.TowerUnit >= 0 && _shooters.Remove(b.TowerUnit, out var archer)) archer.QueueFree();
    }

    public Vector3 Center(SiegeBuilding b)
    {
        if (b.Blocked.Count == 0) return _arena.Grid.CellCenter(new Vector2I(b.Anchor.X, b.Anchor.Y));
        float x = (float)b.Blocked.Average(c => c.X) + 0.5f, y = (float)b.Blocked.Average(c => c.Y) + 0.5f;
        return _arena.Grid.CellCenter(x, y);
    }

    /// <summary>A hit on a building: its HitEffect/HitSound, then the model for its new state.</summary>
    public void OnDamaged(BuildingDamaged e)
    {
        if (_state.Siege?.Building(e.Building) is not { } b) return;
        var def = b.DefIndex >= 0 && b.DefIndex < _layout.Buildings.Count ? _layout.Buildings[b.DefIndex] : null;
        if (def?.HitEffect is { } hit) Effects.EffectPlayer.PlayAt(XdbRef.Parse(hit, null).Path, _arena, Center(b));
        if (def?.HitSound is { } snd) Audio.SoundLibrary.Play(XdbRef.Parse(snd, null).Path, this);
        UpdateBuilding(b, effects: true);
    }

    // ---------------------------------------------------------------- tower archers

    void PlaceShooters()
    {
        var locators = new List<Vector3>();
        if (Game.Instance.Fs.Exists(_descPath))
            foreach (var item in Game.Instance.Xdb.Load(_descPath).Root!.Element("objects")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                var objPath = XdbRef.Parse((string?)item.Attribute("href"), _descPath).Path;
                if (!objPath.Contains("shooter", StringComparison.OrdinalIgnoreCase) || !Game.Instance.Fs.Exists(objPath)) continue;
                if (LocatorPosition((string?)Game.Instance.Xdb.Load(objPath).Root!.Element("placement")) is { } p) locators.Add(p + _offset);
            }

        foreach (var b in _state.Siege!.Buildings.Where(b => b.IsTower && !b.Destroyed && b.TowerUnit >= 0))
        {
            var def = _layout.Buildings[b.DefIndex];
            var shooterPath = XdbRef.Parse(def.Shooter, null).Path;
            if (string.IsNullOrEmpty(shooterPath) || !Game.Instance.Fs.Exists(shooterPath)) continue;
            var center = Center(b);
            // The locator nearest to the tower top (fallback: above the tower centre).
            var at = locators.Count > 0 ? locators.OrderBy(l => new Vector2(l.X - center.X, l.Z - center.Z).LengthSquared()).First()
                                        : center + new Vector3(0, 8f, 0);
            try
            {
                var archer = CharacterBuilder.Build(shooterPath);
                archer.Position = at;
                var d = _arena.Grid.GridCenter - at;
                archer.Rotation = new Vector3(0, Mathf.Atan2(d.X, d.Z), 0);   // models face +Z at yaw 0
                AddChild(archer);
                _shooters[b.TowerUnit] = archer;
            }
            catch (Exception e) { GD.PushWarning($"[H5] tower archer {shooterPath}: {e.Message}"); }
        }
    }

    /// <summary>ArenaLocObject placement: 4×4 matrix whose translation is stored as (x, height, −meshY), i.e. Godot axes.</summary>
    static Vector3? LocatorPosition(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 128) return null;
        var bytes = Convert.FromHexString(hex[..128]);
        float F(int i) => BitConverter.ToSingle(bytes, i * 4);
        return new Vector3(F(3), F(7), F(11));
    }

    public Character? ShooterFor(int towerUnit) => _shooters.GetValueOrDefault(towerUnit);

    /// <summary>The tower's projectile (ArenaBuildingDef.Shot) for a tower unit.</summary>
    public string? ShotFor(int towerUnit)
    {
        var b = _state.Siege?.Buildings.Find(x => x.TowerUnit == towerUnit);
        if (b == null) return null;
        var shot = XdbRef.Parse(_layout.Buildings[b.DefIndex].Shot, null).Path;
        return string.IsNullOrEmpty(shot) || !Game.Instance.Fs.Exists(shot) ? null : shot;
    }
}
