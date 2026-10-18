namespace H5.Combat;

// Retreat (flee) and surrender rules, H5_Game.exe CCombatArmy (RTTI .?AVCCombatArmy@NWorld@@, interface
// vtable 00e17bcc):
//   +0xf0 FUN_004d9e20  CanRetreat(int* reason, bool surrender)
//   +0xec FUN_004d9780  army has a "real" stack (alive creature, not summoned, not a phantom image)
//   +0xf4 FUN_004da9a0  surrender cost = trunc(army gold value / 2), then a hero-side adjustment (vf 0xc)
//   +0xf8 FUN_004da9e0  AI: surrender if gold >= cost + 2500, or army value > cost + 2500
//   FUN_004da8d0        army gold value = sum over alive, non-summoned stacks of creature gold cost x count
public sealed partial class Battle
{
    /// <summary>
    /// The exe's refusal reason for retreating (FUN_004d9e20); 0 = allowed. Codes: 1 no hero, 2 the army
    /// defends a town, 4 Shackles of War on either hero, 5 surrender needs a hero on both sides, 6 surrender
    /// needs a real (non-summoned, non-phantom) stack, 7 retreat denied for this battle, 8 no creature stack left.
    /// UI-only reasons (tactics, auto-combat, duel mode, not your turn, no tavern town) are the client's.
    /// </summary>
    public int RetreatRefusal(int side, bool surrender)
    {
        var h = S.Heroes[side];
        if (!h.Present || h.Fled) return 1;
        if (S.Siege != null && side == 1) return 2;
        if (surrender)
        {
            var enemy = S.Heroes[1 - side];
            if (!enemy.Present || enemy.Fled) return 5;
            if (!S.Units.Any(u => u.Side == side && u.Alive && u.Kind == UnitKind.Creature && !u.Summoned && !u.IsClone)) return 6;
        }
        // Artifact id 7 (Artifacts.xdb order) checked on both armies' heroes via FUN_00ca6680(7).
        if (S.Heroes.Any(x => x.Present && x.Artifacts.Contains("SHACKLES_OF_WAR"))) return 4;
        if (S.RetreatDenied[side]) return 7;
        if (!S.Units.Any(u => u.Side == side && u.Alive && u.Kind == UnitKind.Creature)) return 8;
        return 0;
    }

    /// <summary>
    /// Gold value of a side's surviving army (FUN_004da8d0): creature gold cost x count over alive,
    /// non-summoned creature stacks (resource costs are not counted).
    /// </summary>
    public long ArmyGoldValue(int side)
    {
        long sum = 0;
        foreach (var u in S.Units)
        {
            if (u.Side != side || !u.Alive || u.Kind != UnitKind.Creature || u.Summoned || u.IsClone || u.Count <= 0) continue;
            if (Db.Creatures.TryGetValue(u.CreatureId, out var cd)) sum += (long)cd.Cost.Gold * u.Count;
        }
        return sum;
    }

    /// <summary>Ransom for surrendering (FUN_004da9a0): half the army's gold value, truncated.</summary>
    public long SurrenderCost(int side) => ArmyGoldValue(side) / 2;
}
