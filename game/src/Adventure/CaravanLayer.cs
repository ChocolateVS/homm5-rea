using Godot;
using H5.Adventure.State;
using H5.Core.Xdb;
using H5Reborn.Render;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// Caravans on the adventure map (CAdvMapCaravan): one animated node per <see cref="CaravanState"/>, built from the
/// stock shared MapObjects/Caravans/Caravan.xdb (Model /Characters/Heroes/CARAVAN.xdb, AnimSet CARAVAN-adv). A caravan
/// stands on its tile on the shown floor; the view state already leaves out other players' caravans in the shroud
/// (ViewFilter). The walk of 00ce4de0 (<see cref="H5.Adventure.Engine.CaravanMoved"/>) is played by <see cref="Walk"/>.
/// Owner colour: the shared's CharacterAdventure (/Characters/Heroes/CARAVAN.(Character).xdb) is not in the stock paks,
/// and ColourModels has no other reader (009b1f20 / 009b2870), so the stock caravan shows its plain Model for every
/// owner; the Caravan-Model_&lt;colour&gt; files are referenced by no xdb.
/// </summary>
public partial class CaravanLayer : Node3D
{
    readonly AdvWorld _world;
    readonly AdventureSession _session;
    readonly Dictionary<int, Character> _nodes = new();
    readonly HashSet<int> _walking = new();

    public CaravanLayer(AdvWorld world, AdventureSession session)
    {
        _world = world;
        _session = session;
        Name = "Caravans";
    }

    public void Sync()
    {
        var s = _session.S;
        foreach (var id in _nodes.Keys.Where(id => s.Caravans.All(c => c.Id != id)).ToList())
        {
            _nodes[id].QueueFree();
            _nodes.Remove(id);
        }
        foreach (var c in s.Caravans)
        {
            if (!_nodes.TryGetValue(c.Id, out var node))
            {
                node = Build(c);
                if (node == null) continue;
                _nodes[c.Id] = node;
                AddChild(node);
            }
            node.Visible = c.Pos.Floor == _world.Floor;
            if (_walking.Contains(c.Id)) continue;
            var to = _world.StandPoint(c.Pos);
            // Face the step it made last (keeps the previous heading while it stands).
            var from = node.Position;
            node.Position = to;
            var d = new Vector2(to.X - from.X, to.Z - from.Z);
            if (d.LengthSquared() > 0.01f) node.Rotation = new Vector3(0, Mathf.Atan2(d.X, d.Y), 0);
        }
    }

    public void OnFloorChanged()
    {
        foreach (var c in _session.S.Caravans)
            if (_nodes.TryGetValue(c.Id, out var n)) n.Visible = c.Pos.Floor == _world.Floor;
    }

    /// <summary>
    /// The day's walk (00ce4de0 after the route cut: the path up to the reached node, played node by node on the
    /// caravan's view, vf 0x98 / 0x9c / 0xa0 positions and the object's path move vf 0x50): the move clip at the
    /// AnimSet's ground speed (CARAVAN-adv-move MovementSpeed) along the tile centres, turning to each step, then idle.
    /// UNVERIFIED: the exe's per-segment timing (vf 0x50, the 10000 / +500 / +750 / +1000 steps of 004b60a0) and the
    /// teleport nodes (flag 0x40: the caravan_image effect, 00ce3cc0) are not reproduced; the speed scale follows the
    /// heroes' adventure_speed_* options.
    /// </summary>
    public async Task Walk(H5.Adventure.Engine.CaravanMoved m, float speedMult)
    {
        if (!_nodes.TryGetValue(m.CaravanId, out var node) || m.Path.Count < 2) { Sync(); return; }
        _walking.Add(m.CaravanId);
        try
        {
            float ground = node.GroundSpeeds.TryGetValue("move", out var gs) && gs > 0.1f ? gs : 3.5f;
            float speed = ground * Math.Max(speedMult, 0.25f);
            if (node.Has("move")) { node.Play("move", loop: true); node.Player.SpeedScale = node.SpeedFactor("move") * speedMult; }
            for (int i = 1; i < m.Path.Count && IsInstanceValid(node); i++)
            {
                var a = _world.StandPoint(m.Path[i - 1]);
                var b = _world.StandPoint(m.Path[i]);
                var d = new Vector2(b.X - a.X, b.Z - a.Z);
                if (d.LengthSquared() > 1e-4f) node.Rotation = new Vector3(0, Mathf.Atan2(d.X, d.Y), 0);
                float len = a.DistanceTo(b), t = 0;
                while (t < len && IsInstanceValid(node))
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    t += speed * (float)GetProcessDeltaTime();
                    node.Position = a.Lerp(b, Math.Min(t / Math.Max(len, 1e-3f), 1f));
                }
            }
            if (IsInstanceValid(node) && node.Has("idle00")) node.Play("idle00", loop: true);
        }
        finally { _walking.Remove(m.CaravanId); }
        Sync();
    }

    static System.Xml.Linq.XElement? _shared;
    static bool _sharedLoaded;

    static System.Xml.Linq.XElement? Shared()
    {
        const string path = H5.Adventure.Engine.Adventure.CaravanShared;
        if (!_sharedLoaded) { _sharedLoaded = true; if (Game.Instance.Fs.Exists(path)) _shared = Game.Instance.Xdb.Load(path).Root; }
        return _shared;
    }

    static string? Href(System.Xml.Linq.XElement? e)
    {
        var h = (string?)e?.Attribute("href");
        return string.IsNullOrEmpty(h) ? null : XdbRef.Parse(h, H5.Adventure.Engine.Adventure.CaravanShared).Path;
    }

    /// <summary>The shared's messagesFileRef[i] (0 Caravan_Name, 1 Caravan_Description).</summary>
    public static string SharedText(int i) =>
        UiResources.Instance.Text(Href(Shared()?.Element("messagesFileRef")?.Elements("Item").ElementAtOrDefault(i)))?.Trim() ?? "";

    /// <summary>The shared's IconTexture (UI/H5A1/Icons/Caravans/Caravan_ico_64).</summary>
    public static Texture2D? Icon() => UiResources.Instance.Texture(Href(Shared()?.Element("IconTexture")));

    static Character? Build(CaravanState c)
    {
        var root = Shared();
        var model = Href(root?.Element("Model"));
        if (model == null) return null;
        try
        {
            var ch = CharacterBuilder.BuildFromModel(model, Href(root?.Element("AnimSet")), $"Caravan_{c.Id}");
            if (ch.Has("idle00")) ch.Play("idle00", loop: true);
            else if (ch.Player.GetAnimationList() is { Length: > 0 } clips)
                ch.Play(clips[0].Replace(Character.Lib, ""), loop: true);
            ch.SetMeta("caravan", c.Id);
            return ch;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[H5] caravan model {model}: {e.Message}");
            return null;
        }
    }
}
