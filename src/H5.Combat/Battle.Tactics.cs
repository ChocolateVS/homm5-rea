namespace H5.Combat;

// Tactics phase army bar (exe NUI::CCombatArenaScreen::CPlacementInfo, FUN_00577fa0 / FUN_005779d0): stacks can be
// taken off the field, split and merged before combat starts.
public sealed partial class Battle
{
    /// <summary>The side's own creature stacks in tactics, on the field or in the army bar (the exe's placement entries).</summary>
    public IEnumerable<Unit> TacticsStacks(int side) =>
        S.Units.Where(u => u.Side == side && (u.Alive || u.Reserve) && u.Kind == UnitKind.Creature && !u.Summoned);

    string? TacticsCheck(int side)
    {
        if (S.Phase != BattlePhase.Tactics) return "not in tactics phase";
        if (S.TacticsDone[side]) return "tactics already finished";
        return null;
    }

    string? DoReserve(ReserveCmd c)
    {
        if (TacticsCheck(c.Side) is { } err) return err;
        var u = S.Unit(c.UnitId);
        if (u == null || u.Side != c.Side || !u.Alive || u.Kind != UnitKind.Creature) return "invalid unit";
        u.Reserve = true;
        Emit(new Reserved(u.Id));
        return null;
    }

    /// <summary>
    /// exe FUN_004dbd70 (placement list, from, to or -1, count): count in 1..from's size; the target holds the same
    /// creature; a new stack needs a free army slot (7) unless the whole stack moves; moving a whole stack into another
    /// merges them, and the creature artifact passes over when only the source carries one (FUN_0099a020).
    /// </summary>
    string? DoSplitStack(SplitStackCmd c)
    {
        if (TacticsCheck(c.Side) is { } err) return err;
        var stacks = TacticsStacks(c.Side).ToList();
        var src = stacks.Find(u => u.Id == c.UnitId);
        if (src == null) return "invalid unit";
        if (c.Count <= 0 || c.Count > src.Count) return "invalid count";
        if (c.IntoId is int into)
        {
            var dst = stacks.Find(u => u.Id == into);
            if (dst == null || dst.Id == src.Id) return "invalid target";
            if (dst.CreatureId != src.CreatureId) return "different creatures";
            bool whole = c.Count == src.Count;
            if (whole && src.ArtifactEffects >= 0 && dst.ArtifactEffects < 0) { dst.ArtifactEffects = src.ArtifactEffects; src.ArtifactEffects = -1; }
            dst.Count += c.Count; dst.InitialCount = dst.Count;
            src.Count -= c.Count; src.InitialCount = src.Count;
            if (whole) { src.Reserve = false; src.Removed = true; }
            Emit(new StackChanged(dst.Id, dst.CreatureId, dst.Count, false));
            Emit(new StackChanged(src.Id, src.CreatureId, src.Count, false));
            return null;
        }
        if (c.Count == src.Count) return null;   // the whole stack only changes army slot
        if (stacks.Count >= 7) return "no free army slot";
        var n = BattleSetup.AddCreature(Db, S, c.Side, src.CreatureId, c.Count, new Cell(-1, -1), summoned: false);
        n.Reserve = true;
        src.Count -= c.Count; src.InitialCount = src.Count;
        Emit(new StackChanged(src.Id, src.CreatureId, src.Count, false));
        Emit(new StackChanged(n.Id, n.CreatureId, n.Count, true));
        return null;
    }

    /// <summary>Auto placement re-places every stack, the army bar included (exe FUN_00578510: FUN_00578290 then FUN_0085a190).</summary>
    string? DoAutoplace(AutoplaceCmd c)
    {
        if (TacticsCheck(c.Side) is { } err) return err;
        foreach (var u in TacticsStacks(c.Side)) u.Reserve = false;
        foreach (var (id, cell) in BattleSetup.AutoPlacement(Db, S, c.Side))
            if (S.Unit(id) is { } u)
            {
                if (cell is not Cell at) { u.Reserve = true; continue; }   // removed from the arena (FUN_00859b40)
                u.Pos = u.StartPos = at;
                Emit(new Deployed(u.Id, at));
            }
        return null;
    }
}
