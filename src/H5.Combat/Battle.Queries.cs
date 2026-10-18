namespace H5.Combat;

// Read-only queries for clients (combat screen, AI previews). None of these mutate the battle.
public sealed partial class Battle
{
    /// <summary>Effective hero initiative for a side (HeroInitiative from DefaultStats, × specialisation/skill modifiers).</summary>
    public double HeroInitiativeOf(int side) => HeroInitiative(side);

    /// <summary>
    /// Predicted order of the next <paramref name="count"/> turns, as shown on the original ATB bar:
    /// every predicted actor is assumed to take a normal action (ATB back to 0), effect expiries
    /// change initiative at their expiry time, and ties break like the real turn order.
    /// The current actor (if any) comes first at the current time.
    /// </summary>
    public List<(int Actor, double Time)> PredictTurnOrder(int count)
    {
        var list = new List<(int Actor, double Time)>();
        if (S.Phase != BattlePhase.Combat || count <= 0) return list;
        var sim = new Battle(Db, S.Clone());
        if (S.Active is int active)
        {
            list.Add((active, S.Time));
            sim.SetAtb(active, 0, emit: false);
        }
        while (list.Count < count)
        {
            int? next = sim.AdvanceTimeToNextActor();
            if (next == null) break;
            list.Add((next.Value, sim.S.Time));
            sim.SetAtb(next.Value, 0, emit: false);
        }
        return list;
    }

    /// <summary>
    /// The path the unit would walk to end at <paramref name="to"/> this turn (excluding its current
    /// cell, ending with <paramref name="to"/>), or null if unreachable. Flyers go straight to the target.
    /// </summary>
    public List<Cell>? PathTo(int unitId, Cell to)
    {
        var u = S.Unit(unitId);
        if (u == null || !u.Alive) return null;
        var reach = ReachableWithPaths(u);
        if (!reach.ContainsKey(to)) return null;
        var path = new List<Cell>();
        var cur = to;
        for (int guard = 0; guard < 256 && cur != u.Pos; guard++)
        {
            path.Add(cur);
            cur = reach[cur].Prev;
        }
        path.Reverse();
        return path;
    }
}
