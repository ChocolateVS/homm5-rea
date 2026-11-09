using Godot;
using H5.Adventure.State;
using H5.Core.Xdb;
using H5Reborn.Render;

namespace H5Reborn.Adventure;

/// <summary>
/// Caravans on the adventure map (CAdvMapCaravan): one animated node per <see cref="CaravanState"/>, built from the
/// stock shared MapObjects/Caravans/Caravan.xdb (Model /Characters/Heroes/CARAVAN.xdb, AnimSet CARAVAN-adv). A caravan
/// stands on its tile on the shown floor; the view state already leaves out other players' caravans in the shroud
/// (ViewFilter). GAP: the walk animation along <see cref="H5.Adventure.Engine.CaravanMoved"/> (the caravan jumps to its
/// day's end tile) and the owner's possession marker / flag colour.
/// </summary>
public partial class CaravanLayer : Node3D
{
    readonly AdvWorld _world;
    readonly AdventureSession _session;
    readonly Dictionary<int, Node3D> _nodes = new();

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
            var to = _world.TileCenter(c.Pos);
            // Face the step it made last (keeps the previous heading while it stands).
            var from = node.Position;
            node.Position = to;
            var d = new Vector2(to.X - from.X, to.Z - from.Z);
            if (d.LengthSquared() > 0.01f) node.Rotation = new Vector3(0, Mathf.Atan2(d.X, d.Y), 0);
            node.Visible = c.Pos.Floor == _world.Floor;
        }
    }

    public void OnFloorChanged()
    {
        foreach (var c in _session.S.Caravans)
            if (_nodes.TryGetValue(c.Id, out var n)) n.Visible = c.Pos.Floor == _world.Floor;
    }

    static Node3D? Build(CaravanState c)
    {
        const string shared = H5.Adventure.Engine.Adventure.CaravanShared;
        if (!Game.Instance.Fs.Exists(shared)) return null;
        var root = Game.Instance.Xdb.Load(shared).Root;
        string? Href(string name)
        {
            var h = (string?)root?.Element(name)?.Attribute("href");
            return string.IsNullOrEmpty(h) ? null : XdbRef.Parse(h, shared).Path;
        }
        var model = Href("Model");
        if (model == null) return null;
        try
        {
            var ch = CharacterBuilder.BuildFromModel(model, Href("AnimSet"), $"Caravan_{c.Id}");
            if (!ch.Has("idle00") && ch.Player.GetAnimationList() is { Length: > 0 } clips)
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
