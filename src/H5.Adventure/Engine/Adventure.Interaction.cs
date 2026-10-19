using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>
/// What pointing at a map position means for the selected hero (the action struct H5_Game.exe fills in
/// 00600e70 → 00600060 → 005ff8f0 and turns into a cursor in 0059e1f0). Values are the exe's action codes.
/// </summary>
public enum AdvAction
{
    Move = 0, Wait = 1, Default = 3, NoPath = 4, Use = 6, Attack = 7, NoHero = 8, Cast = 9,
    OwnBuilding = 10, OwnTown = 11, ShipMove = 12, BoardShip = 13, UnboardShip = 14, MeetHero = 15, SelfHero = 16,
}

/// <summary>Hover/click resolution: the action, the tile the hero walks to and the route there.</summary>
/// <param name="Days">Extra days the route needs (0 = today), as 00b4f930 counts them; the cursor shows Days + 1.</param>
/// <param name="Object">Map object the action is aimed at, or -1.</param>
/// <param name="Hero">Hero the action is aimed at, or -1.</param>
public readonly record struct AdvTarget(AdvAction Action, Tile Dest, List<Tile>? Path, int Days, int Object, int Hero);

public sealed partial class Adventure
{
    /// <summary>
    /// The object under a map tile when the mouse ray hits no object model (fallback of the pick 005913b0:
    /// 00cea350 — the object standing on / entered from the tile — then 00cea010 — the object whose
    /// blocked cells cover it). Static decorations are not interactive and never returned.
    /// </summary>
    public MapObject? ObjectAtTile(Tile t)
    {
        MapObject? blocked = null;
        foreach (var o in Map.Objects)
        {
            if (o.Kind == ObjectKind.Static || o.Pos.Floor != t.Floor) continue;
            if (S.Objects.TryGetValue(o.Index, out var os) && os.Removed) continue;
            if (o.Pos == t || o.ActiveTiles.Contains(t)) return o;
            if (blocked == null && o.BlockedTiles.Contains(t)) blocked = o;
        }
        return blocked;
    }

    /// <summary>
    /// Resolves the action for the selected hero pointing at <paramref name="tile"/> with
    /// <paramref name="picked"/> the object under the mouse (model pick or <see cref="ObjectAtTile"/>); <paramref name="player"/> is the player looking.
    /// Objects are approached at their entrance: the active cells sorted by squared distance to the pointed
    /// tile, the first one with a route wins (00cebd50); heroes and monsters are approached on their tile.
    /// </summary>
    public AdvTarget Target(int player, HeroState? hero, Tile tile, MapObject? picked)
    {
        // Heroes standing on the tile (picked by model in the exe; their tile here).
        var other = S.Heroes.Find(x => x.Alive && !x.Garrisoned && x.Pos == tile);
        if (hero != null && other != null && other.Id == hero.Id) return new(AdvAction.SelfHero, tile, null, 0, -1, hero.Id);
        // Own (allied) town: OwnTown even without a selected hero (00600e70, FUN_00a85030 ally test).
        var townPicked = picked?.Kind == ObjectKind.Town ? S.Towns.Find(tn => tn.Object == picked.Index) : TownAt(tile);
        if (hero == null)
        {
            if (other != null && other.Owner == player) return new(AdvAction.MeetHero, tile, null, 0, -1, other.Id);
            return new(AdvAction.NoHero, tile, null, 0, -1, -1);
        }
        if (other != null)
        {
            var act = Allied(other.Owner, hero.Owner) ? AdvAction.MeetHero : AdvAction.Attack;
            return Routed(hero, act, other.Pos, other.Pos, -1, other.Id);
        }
        if (townPicked != null)
        {
            var o = O(townPicked.Object);
            AdvAction act;
            if (Allied(townPicked.Owner, hero.Owner)) act = AdvAction.OwnTown;
            else
            {
                bool heroInside = S.Heroes.Any(x => x.Alive && x.InTown == townPicked.Object);
                act = heroInside || townPicked.Garrison.StackCount() > 0 ? AdvAction.Attack : AdvAction.Use;
            }
            return Approach(hero, act, o, tile);
        }
        if (picked != null && picked.Kind != ObjectKind.Static)
        {
            var os = S.Objects.GetValueOrDefault(picked.Index);
            if (os?.Removed == true) picked = null;
            else
            {
                AdvAction act = ObjectAction(hero, picked, os);
                if (picked.Kind == ObjectKind.Monster) return Routed(hero, act, picked.Pos, picked.Pos, picked.Index, -1);
                return Approach(hero, act, picked, tile);
            }
        }
        // Plain tile: a monster guarding it turns the step into a fight.
        if (GuardingMonster(tile) is { } guard) return Routed(hero, AdvAction.Attack, tile, tile, guard.Index, -1);
        var plain = !hero.InBoat
            ? (IsWaterTile(tile) && BoatAt(tile) is { HeroId: -1 } ? AdvAction.BoardShip : AdvAction.Move)
            : (IsWaterTile(tile) ? AdvAction.ShipMove : AdvAction.UnboardShip);
        return Routed(hero, plain, tile, tile, -1, -1);
    }

    /// <summary>
    /// Object action (005ff8f0): flagged buildings that hold an army (mines, dwellings, garrisons) give the
    /// own-building cursor to their allied owner, a fight when guarded, a visit otherwise; monsters and
    /// guarded objects are fights; empty ships are boarded; everything else is a visit.
    /// </summary>
    AdvAction ObjectAction(HeroState hero, MapObject o, ObjState? os)
    {
        int owner = os?.Owner >= 0 ? os.Owner : o.Owner;
        bool guarded = os?.Guards.Count > 0;
        switch (o.Kind)
        {
            case ObjectKind.Monster:
                return AdvAction.Attack;
            case ObjectKind.Mine or ObjectKind.AbandonedMine or ObjectKind.Dwelling or ObjectKind.DwarvenWarren or ObjectKind.Garrison:
                if (Allied(owner, hero.Owner)) return AdvAction.OwnBuilding;
                return guarded ? AdvAction.Attack : AdvAction.Use;
            default:
                return guarded ? AdvAction.Attack : AdvAction.Use;
        }
    }

    AdvTarget Approach(HeroState hero, AdvAction act, MapObject o, Tile pointed)
    {
        var entrances = o.ActiveTiles.ToList();
        if (entrances.Count == 0) return Routed(hero, act, pointed, pointed, o.Index, -1);
        // Stable sort by squared distance to the pointed tile (00cebd50 → 00cecb10).
        var order = entrances.Select((t, i) => (t, i))
            .OrderBy(e => (e.t.X - pointed.X) * (e.t.X - pointed.X) + (e.t.Y - pointed.Y) * (e.t.Y - pointed.Y)).ThenBy(e => e.i);
        foreach (var (t, _) in order)
        {
            if (hero.Pos == t) return new(act, t, new List<Tile> { t }, 0, o.Index, -1);
            if (FindPath(hero, t) is { Count: > 0 } path) return new(act, t, path, DaysFor(hero, path), o.Index, -1);
        }
        return new(AdvAction.NoPath, entrances[0], null, 0, o.Index, -1);
    }

    AdvTarget Routed(HeroState hero, AdvAction act, Tile dest, Tile pointed, int obj, int heroId)
    {
        if (hero.Pos == dest) return new(act, dest, new List<Tile> { dest }, 0, obj, heroId);
        var path = FindPath(hero, dest);
        if (path == null || path.Count == 0) return new(AdvAction.NoPath, dest, null, 0, obj, heroId);
        return new(act, dest, path, DaysFor(hero, path), obj, heroId);
    }

    /// <summary>
    /// Days beyond today a route needs (00b4f930): walking with the current movement points, a step that
    /// costs more than what is left starts a new day with a full day's points.
    /// </summary>
    public int DaysFor(HeroState hero, IReadOnlyList<Tile> path)
    {
        int days = 0, mp = hero.MovePoints, full = Math.Max(hero.MaxMovePoints, 1);
        var from = hero.Pos;
        foreach (var t in path)
        {
            if (t == hero.Pos) continue;
            int cost = StepCost(hero, from, t);
            if (cost > mp)
            {
                days++;
                mp = full;
                if (cost > full) return 0; // 00b4f930 returns −1; 0058c500 clamps to the first entry
            }
            mp -= cost;
            from = t;
        }
        return days;
    }
}
