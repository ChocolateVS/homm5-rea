using H5.Combat;

namespace H5Reborn.Combat;

/// <summary>
/// Predicts the upcoming turn order for the ATB bar by advancing copies of the ATB
/// values with each actor's current initiative (fan manual p.309: an actor's gauge
/// fills at Initiative/10 per time unit and resets to 0 after acting). Effects that
/// expire or morale rolls can change the real order; the original bar has the same
/// limitation and re-sorts after every action.
/// </summary>
public static class TurnOrder
{
    public static List<int> Predict(Battle b, int count)
    {
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
                actors.Add((Actor.Hero(side), s.Heroes[side].Atb, 1.0));

        var result = new List<int>();
        if (s.Active is int active)
        {
            result.Add(active);
            int i = actors.FindIndex(a => a.Id == active);
            if (i >= 0) actors[i] = (active, 0, actors[i].Rate);
        }
        for (int guard = 0; result.Count < count && actors.Count > 0 && guard < 200; guard++)
        {
            int best = -1;
            double bestT = double.PositiveInfinity;
            for (int i = 0; i < actors.Count; i++)
            {
                var (id, atb, rate) = actors[i];
                double t = Math.Max(0, (1 - atb) / rate);
                if (t < bestT - 1e-9 || (Math.Abs(t - bestT) <= 1e-9 && best >= 0 && rate > actors[best].Rate))
                { best = i; bestT = t; }
            }
            for (int i = 0; i < actors.Count; i++)
                actors[i] = (actors[i].Id, actors[i].Atb + actors[i].Rate * bestT, actors[i].Rate);
            result.Add(actors[best].Id);
            actors[best] = (actors[best].Id, 0, actors[best].Rate);
        }
        return result;
    }
}
