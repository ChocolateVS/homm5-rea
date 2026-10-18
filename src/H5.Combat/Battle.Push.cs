namespace H5.Combat;

public sealed partial class Battle
{
    // Order in which the exe lists the eight cells around a position (FUN_00b3d010 with size 1): the right
    // column first, then the far row right-to-left, the left column, and the near row left-to-right. Only matters
    // for exact angle ties.
    static readonly (int X, int Y)[] PushRing =
        { (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1) };

    /// <summary>Force Arrow's push search tolerance (exe FUN_00a5d420 passes π/3 as a float).</summary>
    internal const float ForceArrowPushTolerance = 1.0471976f;
    /// <summary>Paw Strike accepts any direction (exe FUN_00a5d520 passes 2π).</summary>
    internal const float PawStrikePushTolerance = 6.2831855f;
    /// <summary>Harpoon Strike's pull tolerance (exe FUN_00a5d5f0 passes π/4 as a float, 0x3f490fdb).</summary>
    internal const float HarpoonPullTolerance = 0.7853982f;

    /// <summary>
    /// Where a push moves <paramref name="target"/> (exe FUN_00a5ceb0). The push direction is the angle from the
    /// attacker's centre to the target's; every free neighbouring position of the target is a candidate and the one
    /// whose direction from the target's centre is closest to the push direction wins, provided it is within
    /// <paramref name="tolerance"/> radians. An entangled target "succeeds" without moving (the exe returns its
    /// current position). Returns null when no candidate qualifies, in which case the ability does not trigger.
    /// </summary>
    internal Cell? PushDestination(Unit attacker, Unit target, float tolerance, bool towardAttacker = false)
    {
        if (IsEntangled(target)) return target.Pos;
        var (ax, ay) = attacker.Center;
        var (tx, ty) = target.Center;
        // The exe's first argument swaps the two centres: 0 = away from the attacker (push), 1 = towards it (pull).
        float dir = towardAttacker
            ? MathF.Atan2((float)(ay - ty), (float)(ax - tx))
            : MathF.Atan2((float)(ty - ay), (float)(tx - ax));
        float best = 6.2831855f;
        Cell? result = null;
        foreach (var (dx, dy) in PushRing)
        {
            var c = new Cell(target.Pos.X + dx, target.Pos.Y + dy);
            if (!Fits(target, c)) continue;
            float diff = MathF.Abs(WrapAngle(MathF.Atan2(dy, dx) - dir));
            if (diff <= tolerance && diff < best) { best = diff; result = c; }
        }
        return result;
    }

    /// <summary>
    /// Where Harpoon Strike drags its target (exe FUN_00a5d5f0, called from the strike in FUN_00a673c0): nowhere when
    /// the target holds its ground (ABILITY_HOLD_GROUND) or is a large creature while HarpoonStrike_WorksOnLargeCreatures
    /// is false; otherwise the free neighbouring cell whose direction is closest to "towards the harpooner", within π/4
    /// (FUN_00a5ceb0 with its first argument set). Null = no pull.
    /// </summary>
    internal Cell? HarpoonPullDestination(Unit attacker, Unit target)
    {
        if (HasAbilityNow(target, Ab.HoldGround)) return null;
        if (target.Size != 1 && !Stats.Bool("combat/Creatures/Fortress/HarpoonStrike_WorksOnLargeCreatures", true)) return null;
        return PushDestination(attacker, target, HarpoonPullTolerance, towardAttacker: true);
    }

    /// <summary>Wraps an angle into [-π, π] like the exe's FUN_008b7750.</summary>
    static float WrapAngle(float a)
    {
        const float TwoPi = 6.2831855f;
        a %= TwoPi;
        if (a > MathF.PI) a -= TwoPi;
        else if (a < -MathF.PI) a += TwoPi;
        return a;
    }
}
