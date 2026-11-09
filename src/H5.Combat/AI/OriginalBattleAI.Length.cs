namespace H5.Combat.AI;

// "Combat length evaluation" FUN_00b393a0 (from FUN_00a30460: settings +0xa3 → think +0x1b7). Ledger: AUDIT_AI.md.
public sealed partial class OriginalBattleAI
{
    /// <summary>
    /// FUN_00b393a0(army): moves + fights of a rough battle estimate, and whether we survive it (result +0 / +4).
    /// Moves: over every (own creature, enemy creature) pair (army vf 0x64 lists, stacks with the creature interface vf 0x6c),
    /// H += the enemy's total health (vf 0x1dc(vf 0x20)) and W += (health · d / max(1, own speed vf 0x48)) / 2 (int, C
    /// division), d = <see cref="ExeDistance"/>; moves = (H + 2W) / (2H), 0 when H = 0. Fights: <see cref="FightRounds"/>
    /// on the position value (FUN_00bfc3c0): our damage FUN_00bfc4a0, the enemy's, our health FUN_00bf9f80 and the enemy's
    /// FUN_00bfa010 (<see cref="HealthMeasure"/>).
    /// </summary>
    public static (bool Win, int Length) CombatLength(Battle b, int side, CombatValue v)
    {
        var live = b.State.Living.Where(x => !x.IsObject && !x.IsWarMachine).ToList();
        int h = 0, w = 0;
        unchecked
        {
            foreach (var o in live.Where(x => x.Side == side))
            {
                int sp = Math.Max(1, b.SpeedOf(o));
                foreach (var e in live.Where(x => x.Side != side))
                {
                    int hp = e.TotalHealth;
                    h += hp;
                    w += hp * ExeDistance(o, o.Pos, e, e.Pos) / sp / 2;
                }
            }
        }
        int moves = h != 0 ? unchecked(h + w * 2) / unchecked(h * 2) : 0;
        var (win, fights) = FightRounds(v.My.DamageMeasure(v.Enemy), v.Enemy.DamageMeasure(v.My), HealthMeasure(v.My), HealthMeasure(v.Enemy));
        return (win, moves + fights);
    }

    /// <summary>
    /// FUN_00b3c870(a, b): with dx = a.x − b.x, dy = a.y − b.y, a size-2 <paramref name="a"/> takes x − 1 when dx &gt; 0 and
    /// y − 1 when dy &gt; 0, a size-2 <paramref name="t"/> x − 1 when dx &lt; 0 and y − 1 when dy &lt; 0; then
    /// min(|dx'|, |dy'|) + 2 · max(|dx'|, |dy'|).
    /// </summary>
    public static int ExeDistance(Unit a, Cell ap, Unit t, Cell tp)
    {
        int ax = ap.X, ay = ap.Y, bx = tp.X, by = tp.Y;
        int dx = ax - bx, dy = ay - by;
        if (a.Size > 1) { ax -= dx > 0 ? 1 : 0; ay -= dy > 0 ? 1 : 0; }
        if (t.Size > 1) { bx -= dx < 0 ? 1 : 0; by -= dy < 0 ? 1 : 0; }
        int x = Math.Abs(ax - bx), y = Math.Abs(ay - by);
        return Math.Min(x, y) + 2 * Math.Max(x, y);
    }

    /// <summary>
    /// The fight part of FUN_00b393a0: up to 20 rounds while both healths are above 0: enemy health −= our damage, the
    /// enemy damage becomes trunc(max(0, (1 − (float)(our damage / enemy health)) · enemy damage)) (integer division, x87
    /// chop: FISTP after FLDCW with RC = 11), our health −= that new enemy damage, our damage the same way against our
    /// health; each health floored at 0. Returns (our health &gt; 0, rounds).
    /// </summary>
    public static (bool Win, int Rounds) FightRounds(int ourDamage, int eneDamage, int ourHealth, int eneHealth)
    {
        int n = 0;
        for (; eneHealth > 0 && ourHealth > 0 && n < 20; n++)
        {
            int e1 = eneHealth - ourDamage;
            eneDamage = Chop(Math.Max(0f, (float)((1f - (float)(ourDamage / eneHealth)) * (double)eneDamage)));
            int o1 = ourHealth - eneDamage;
            ourDamage = Chop(Math.Max(0f, (float)((1f - (float)(eneDamage / ourHealth)) * (double)ourDamage)));
            eneHealth = Math.Max(0, e1);
            ourHealth = Math.Max(0, o1);
        }
        return (ourHealth > 0, n);
    }

    static int Chop(float f) => f >= -2147483648f && f < 2147483648f ? (int)f : int.MinValue;

    /// <summary>FUN_00bf9f80 / FUN_00bfa010: trunc([8] · max(0.1, 1 / factor(1, trunc([9])))).</summary>
    public static int HealthMeasure(SideValue s) =>
        (int)(s.TroopsHealth * Math.Max(0.1f, 1 / SideValue.Factor(1.0, (int)s.TroopsDefence)));

    /// <summary>Settings +0xa3 → think +0x1b7 (FUN_00a30460): the evaluated length is below 3.</summary>
    public static bool ShortCombat(Battle b, int side, CombatValue start) => CombatLength(b, side, start).Length < 3;
}
