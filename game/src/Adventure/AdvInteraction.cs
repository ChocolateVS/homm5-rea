using Godot;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using HeroState = H5.Adventure.State.HeroState;

namespace H5Reborn.Adventure;

/// <summary>
/// Adventure-map object interaction as H5_Game.exe does it: the object under the mouse is the one whose
/// model the ray hits (005913b0), falling back to the object on / covering the tile; the action and the
/// route go to its entrance (00600060 → 00cebd50, <see cref="H5.Adventure.Engine.Adventure.Target"/>);
/// the hover handler 0059e1f0 turns the action into the cursor; the click handler 00597640 plots on the
/// first click and moves when the same pick (tile and object) is clicked again.
/// </summary>
public partial class AdventureScreen
{
    (Tile Tile, int Object, int Hero)? _plotPick;

    MapObject? PickAt(Vector2 screen, Tile tile)
    {
        int idx = World.PickObject(World.Camera.ProjectRayOrigin(screen), World.Camera.ProjectRayNormal(screen));
        if (idx >= 0 && idx < Session.Map.Objects.Count && Session.Map.Objects[idx] is { } o && o.Kind != ObjectKind.Static
            && (o.ActiveTiles.Any(World.IsExplored) || World.IsExplored(o.Pos)))
            return o;
        return Session.Game.ObjectAtTile(tile);
    }

    /// <summary>The action for the selected hero pointing at a screen position over a map tile.</summary>
    AdvTarget HoverTarget(Vector2 screen, Tile tile) =>
        Session.Game.Target(Session.Human.Index, Selected, tile, PickAt(screen, tile));

    /// <summary>
    /// Cursor per action (0059e1f0 switch, jump table 0059e694/0059e6bc; day families via 0058c500).
    /// </summary>
    public static AdvCursor CursorFor(AdvAction a) => a switch
    {
        AdvAction.Move => AdvCursor.Move,
        AdvAction.ShipMove => AdvCursor.ShipMove,
        AdvAction.BoardShip => AdvCursor.BoardShip,
        AdvAction.UnboardShip => AdvCursor.UnboardShip,
        AdvAction.Use => AdvCursor.Use,
        AdvAction.Attack => AdvCursor.Attack,
        AdvAction.OwnBuilding => AdvCursor.Garrison,
        AdvAction.OwnTown => AdvCursor.Town,
        AdvAction.MeetHero => AdvCursor.MeetHeroes,
        AdvAction.SelfHero => AdvCursor.Finger,
        AdvAction.Wait => AdvCursor.Wait,
        AdvAction.Cast => AdvCursor.Cast,
        AdvAction.Default => AdvCursor.Default,
        _ => AdvCursor.CantGo, // NoPath (4), NoHero (8)
    };

    /// <summary>Generic map click with a hero selected: first click plots the route, the same pick again moves.</summary>
    void ClickTarget(HeroState hero, Tile tile, Vector2 screen)
    {
        var target = HoverTarget(screen, tile);
        var pick = (tile, target.Object, target.Hero);
        if (_plotted is { } dest && _plotPick == pick) { SendMove(hero, dest); _plotPick = null; return; }
        if (Verbose) GD.Print($"[H5] click {tile}: {target.Action} → {target.Dest} (object {target.Object}, {target.Days + 1} day(s))");
        if (target.Path == null || target.Action is AdvAction.NoPath or AdvAction.NoHero or AdvAction.SelfHero)
        {
            World.ClearPath();
            _plotted = null;
            _plotPick = null;
            return;
        }
        Plot(hero, target.Dest, target.Action == AdvAction.Attack);
        _plotPick = _plotted != null ? pick : null;
    }

    /// <summary>Dev/test (`--adventure-hover=Kind[:body]`): points the fake mouse at the entrance (or the body
    /// cell farthest from it) of the object of that kind nearest to the selected hero.</summary>
    public static string? DevHover;
    int _devHoverFrames;

    void DevHoverStep()
    {
        if (DevHover is not { } spec || Selected is not { } hero) return;
        var parts = spec.Split(':');
        if (!Enum.TryParse<ObjectKind>(parts[0], true, out var kind)) return;
        var o = Session.Map.Objects
            .Where(x => x.Kind == kind && x.Pos.Floor == hero.Pos.Floor && x.ActiveTiles.Any()
                && !(Session.S.Objects.TryGetValue(x.Index, out var os) && os.Removed))
            .OrderBy(x => Math.Abs(x.Pos.X - hero.Pos.X) + Math.Abs(x.Pos.Y - hero.Pos.Y)).FirstOrDefault();
        if (o == null) return;
        var entrance = o.ActiveTiles.First();
        var tile = parts.Length > 1 && parts[1] == "body"
            ? o.BlockedTiles.Where(b => !o.ActiveTiles.Contains(b))
                .OrderByDescending(b => (b.X - entrance.X) * (b.X - entrance.X) + (b.Y - entrance.Y) * (b.Y - entrance.Y)).FirstOrDefault(entrance)
            : entrance;
        // Keep the camera on the object while the map settles (the start-up camera placement comes later).
        if (_devHoverFrames++ < 240) World.Camera.CenterOn(o.Pos);
        if (_devHoverFrames == 1) GD.Print($"[H5] dev hover {kind} #{o.Index} at {o.Pos}: pointing at {tile} (entrance {entrance})");
        FakeMouse = World.Camera.UnprojectPosition(World.TileCenter(tile));
        if (_devHoverFrames == 300 && parts.Contains("click") && TileAt(FakeMouse.Value) is { } ct)
            AdvClick(ct, false, FakeMouse.Value);
        if (Verbose && Engine.GetProcessFrames() % 60 == 0 && TileAt(FakeMouse.Value) is { } t)
        {
            var target = HoverTarget(FakeMouse.Value, t);
            GD.Print($"[H5] dev hover tile {t}: {target.Action} → {target.Dest}, object {target.Object}, cursor {AdvCursors.Current}");
        }
    }

    /// <summary>An object's own message (shared descriptor messagesFileRef[<paramref name="msg"/>]), or "".</summary>
    string ObjectMessage(int index, int msg, params (string Name, object Value)[] values)
    {
        if (index < 0 || index >= Session.Map.Objects.Count) return "";
        var m = Session.Map.Objects[index].Shared.Messages;
        return msg < m.Count && m[msg].Length > 0 ? AdvTexts.Get(m[msg].TrimStart('/'), values) : "";
    }

    /// <summary>Visit outcomes whose text the object's data defines (shrines, keymaster tents, border guards).</summary>
    string VisitMessage(ObjectVisited ov)
    {
        string Spell() => Session.S.Objects.TryGetValue(ov.Object, out var os) && os.SpellId is { } sp ? Game.Instance.Db.Spell(sp)?.Name ?? sp : "";
        return ov.What switch
        {
            "shrine:learned" => ObjectMessage(ov.Object, 2, ("spell", Spell())),
            "shrine:known" => ObjectMessage(ov.Object, 3, ("spell", Spell())),
            "shrine:cant" => ObjectMessage(ov.Object, 4, ("spell", Spell()), ("skill", ShrineSkill(ov.Object))),
            "bank:empty" => ObjectMessage(ov.Object, 3),
            "prison:empty" => ObjectMessage(ov.Object, 2),
            "prison:full" => ObjectMessage(ov.Object, 3),
            "prison:freed" => ObjectMessage(ov.Object, 4),
            "BUILDING_SIGN" => Session.Map.Objects[ov.Object].MessageText ?? "",
            _ when ov.What.StartsWith("key:") => ObjectMessage(ov.Object, 2),
            _ when ov.What.StartsWith("key_again:") => ObjectMessage(ov.Object, 3),
            _ => "",
        };
    }

    /// <summary>The magic school skill a shrine's spell needs (the rule in Adventure.LearnSpell).</summary>
    string ShrineSkill(int index)
    {
        if (!Session.S.Objects.TryGetValue(index, out var os) || os.SpellId is not { } sp || Game.Instance.Db.Spell(sp) is not { } def) return "";
        string? skill = def.School switch
        {
            "MAGIC_SCHOOL_DESTRUCTIVE" => "HERO_SKILL_DESTRUCTIVE_MAGIC",
            "MAGIC_SCHOOL_DARK" => "HERO_SKILL_DARK_MAGIC",
            "MAGIC_SCHOOL_LIGHT" => "HERO_SKILL_LIGHT_MAGIC",
            "MAGIC_SCHOOL_SUMMONING" => "HERO_SKILL_SUMMONING_MAGIC",
            _ => null,
        };
        return skill != null && Game.Instance.Db.Skills.TryGetValue(skill, out var sd) ? sd.Name ?? skill : "";
    }
}
