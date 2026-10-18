namespace H5.Combat;

// Haven / Inferno / Necropolis / Sylvan creature-ability rules confirmed against H5_Game.exe
// (combat audit part A, docs/rules/audit/combat_abilities_a.md).
public sealed partial class Battle
{
    /// <summary>
    /// Mana taken by Mana Stealer / Mana Destroyer / Syphon Mana (exe FUN_0097bd60): with
    /// k = ManaStealPerWeekPopulation (0 if not positive), Syphon Mana uses k' = trunc(k · SyphonManaCoef + 0.5);
    /// the amount is min(enemy hero mana, ceil(stack size · k / weekly growth)).
    /// </summary>
    int ManaDrainAmount(Unit u, int enemyMana, bool syphon)
    {
        int k = Math.Max(0, (int)Stats.Get("combat/Creatures/Inferno/ManaStealPerWeekPopulation", 4));
        if (syphon) k = (int)((float)k * (float)Stats.Get("combat/Creatures/Inferno/SyphonManaCoef", 0.5) + 0.5f);
        int growth = Math.Max(1, u.WeeklyGrowth);
        return Math.Min(enemyMana, (u.Count * k + growth - 1) / growth);
    }
}
