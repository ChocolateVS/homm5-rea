using Godot;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Xdb;
using H5Reborn.Render;
using HeroState = H5.Adventure.State.HeroState;

namespace H5Reborn.Adventure.Water;

/// <summary>
/// Ships on the adventure map: one animated node per <see cref="BoatState"/>, built from the stock ship
/// descriptors (MapObjects/Ship_Galley|Galeon|Junk with their -adv anim sets). An empty ship floats on its
/// tile; a ship with a hero aboard is parented to the hero's node so it follows every walk, turn and
/// teleport the hero makes, and the hero's own model is hidden while he sails.
/// TODO(exe): whether the original also shows the hero standing on deck.
/// </summary>
public partial class BoatLayer : Node3D
{
    readonly AdvWorld _world;
    readonly AdventureSession _session;
    readonly Dictionary<int, Node3D> _boats = new();

    public BoatLayer(AdvWorld world, AdventureSession session)
    {
        _world = world;
        _session = session;
        Name = "Boats";
    }

    /// <summary>Brings every ship node (and the visibility of sailing heroes) in line with the state.</summary>
    public void Sync()
    {
        var s = _session.S;
        foreach (var id in _boats.Keys.Where(id => s.Boats.All(b => b.Id != id)).ToList())
        {
            _boats[id].QueueFree();
            _boats.Remove(id);
        }
        var sailing = new HashSet<int>();
        foreach (var b in s.Boats)
        {
            if (!_boats.TryGetValue(b.Id, out var node))
            {
                node = Build(b);
                if (node == null) continue;
                _boats[b.Id] = node;
            }
            var hero = b.HeroId >= 0 ? s.Hero(b.HeroId) : null;
            var heroNode = hero != null ? _world.HeroNode(hero.Id) : null;
            if (heroNode != null)
            {
                sailing.Add(hero!.Id);
                Reparent(node, heroNode);
                node.Transform = Transform3D.Identity;
            }
            else
            {
                Reparent(node, this);
                node.Position = _world.AboveSea(_world.TileCenter(b.Pos), b.Pos.Floor);
                node.Rotation = new Vector3(0, b.Rot, 0);
                node.Visible = b.Pos.Floor == _world.Floor;
            }
        }
        foreach (var h in s.Heroes)
            if (_world.HeroNode(h.Id) is { } hn) SetHeroModelVisible(hn, !sailing.Contains(h.Id));
    }

    static void Reparent(Node3D node, Node3D parent)
    {
        if (node.GetParent() == parent) return;
        node.GetParent()?.RemoveChild(node);
        parent.AddChild(node);
    }

    /// <summary>Hides the hero's skinned meshes (not the selection ring or the ship parented to him).</summary>
    static void SetHeroModelVisible(Node3D heroNode, bool visible)
    {
        foreach (var child in heroNode.GetChildren())
            if (child is Skeleton3D or MeshInstance3D) ((Node3D)child).Visible = visible;
    }

    Node3D? Build(BoatState b)
    {
        var shared = BoatState.ModelShared[Math.Clamp(b.Model, 0, BoatState.ModelShared.Length - 1)];
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
            var c = CharacterBuilder.BuildFromModel(model, Href("AnimSet"), $"Boat_{b.Id}");
            if (!c.Has("idle00") && c.Player.GetAnimationList() is { Length: > 0 } clips)
                c.Play(clips[0].Replace(Character.Lib, ""), loop: true);
            c.SetMeta("boat", b.Id);
            return c;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[H5] ship model {model}: {e.Message}");
            return null;
        }
    }

    /// <summary>Called when the visible floor changes: empty ships on other floors are hidden.</summary>
    public void OnFloorChanged()
    {
        foreach (var b in _session.S.Boats)
            if (_boats.TryGetValue(b.Id, out var n) && n.GetParent() == this) n.Visible = b.Pos.Floor == _world.Floor;
    }

    // ---------------------------------------------------------------- cursors

    /// <summary>
    /// The movement cursor for a plain (object-free) target: board a ship, land from a ship, sail, or
    /// walk (stock hwcursors BoardShip*, UnboardShip*, ShipMove*, MoveN*).
    /// </summary>
    public static AdvCursor MoveCursor(H5.Adventure.Engine.Adventure g, HeroState hero, Tile t)
    {
        bool water = g.IsWaterTile(t);
        if (!hero.InBoat) return water && g.BoatAt(t) is { HeroId: -1 } ? AdvCursor.BoardShip : AdvCursor.Move;
        return water ? AdvCursor.ShipMove : AdvCursor.UnboardShip;
    }

}
