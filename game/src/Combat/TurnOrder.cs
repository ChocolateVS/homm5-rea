using H5.Combat;

namespace H5Reborn.Combat;

/// <summary>
/// Upcoming turn order for the ATB bar. In combat this is the engine's prediction
/// (Battle.PredictTurnOrder: simulated ATB advance including effect expiries and the
/// original tie-breaking); during tactics, before any ATB has advanced, it orders the
/// actors by their starting ATB and initiative the same way.
/// </summary>
public static class TurnOrder
{
    public static List<int> Predict(Battle b, int count)
    {
        if (b.State.Phase == BattlePhase.Combat)
            return b.PredictTurnOrder(count).Select(p => p.Actor).ToList();

        var s = b.State;
        var actors = new List<(int Id, double Atb, double Rate)>();
        foreach (var u in s.Units)
            if (u.Alive && !u.IsObject)
            {
                double rate = b.InitiativeOf(u) / 10.0;
                if (rate > 0) actors.Add((u.Id, u.Atb, rate));
            }
        for (int side = 0; side < 2; side++)
            if (s.Heroes[side].Present && !s.Heroes[side].Fled)
                actors.Add((Actor.Hero(side), s.Heroes[side].Atb, b.HeroInitiativeOf(side) / 10.0));
        return actors.OrderBy(a => Math.Max(0, (1 - a.Atb) / a.Rate)).ThenByDescending(a => a.Rate)
            .Take(count).Select(a => a.Id).ToList();
    }
}
