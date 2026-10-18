using H5.Adventure.State;

namespace H5Reborn.AdvNet;

/// <summary>
/// Which battle sides humans command in a local (single-player or hot-seat) adventure battle.
/// The attacker (side 0) is the human whose turn it is; in hot-seat the defender (side 1) is also
/// played at this PC when it belongs to another human: a defending hero, or a town (with or
/// without a garrison hero) owned by a human player.
/// </summary>
public static class HotSeatBattles
{
    public static int[] HumanSides(AdvState s, PendingBattle pb)
    {
        bool Human(int owner) => owner >= 0 && s.Players.Any(p => p.Index == owner && p.Human);
        int defenderOwner = -1;
        if (pb.DefenderHero >= 0)
            defenderOwner = s.Heroes.FirstOrDefault(h => h.Id == pb.DefenderHero)?.Owner ?? -1;
        else if (pb.DefenderObject >= 0)
            defenderOwner = s.Towns.FirstOrDefault(t => t.Object == pb.DefenderObject)?.Owner ?? -1;
        return Human(defenderOwner) ? new[] { 0, 1 } : new[] { 0 };
    }
}
