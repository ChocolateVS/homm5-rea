using H5.Adventure.State;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.AI;

// The AI's fight estimate (exe FUN_00bc3af0 and its army sums), used for neutral monsters (provider 004eee00).
public sealed partial class OriginalAdventureAI
{
    /// <summary>
    /// Hero level factor of the AI army sums (exe 004ea520 / 004ea220): ftol(level · ArmyPowerCoefBonusPerHeroLevel
    /// (row +0x1c, float) + 1.0) — truncated to an integer, so with the stock 0.1 heroes below level 10 count once.
    /// 1 without a hero.
    /// </summary>
    public static long HeroLevelFactor(HeroState? hero, AdventureDifficulty diff) =>
        hero == null ? 1 : (long)(hero.Level * (double)(float)diff.ArmyPowerCoefBonusPerHeroLevel + 1.0);

    /// <summary>
    /// Army sum of the AI fight estimate (exe FUN_004ea5c0 over FUN_004ea520): per stack max(Power, 1) (004bab00) ·
    /// <see cref="HeroLevelFactor"/> · count, times SiegeShootersCoef (+0x2e0) for a shooter when
    /// <paramref name="mode"/> is 2 or SiegeFlyerCoef (+0x2f4) for a flyer when it is 1, accumulated with ftol; an army
    /// whose hero belongs to a human player is then × (1 + CowardiceVersusHuman) (FUN_00c02ac0, row +0xc).
    /// GAP-detail: the war machine part (004ea220) is fed by the town's garrison machines (FUN_00cd0a30) only and is
    /// not ported.
    /// </summary>
    public static long AiArmyPower(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, IEnumerable<Stack> stacks, HeroState? hero, int mode = 0)
    {
        long total = 0, level = HeroLevelFactor(hero, diff);
        foreach (var s in stacks)
        {
            if (s.Count <= 0 || !g.Core.Creatures.TryGetValue(s.Creature, out var c)) continue;
            double k = 1.0;
            if (mode == 2 && c.Shots > 0) k = (float)st.SiegeShootersCoef;
            else if (mode == 1 && c.Flying) k = (float)st.SiegeFlyerCoef;
            long v = Math.Max(c.Power, 1) * level * s.Count;
            total = (long)(v * k + total);
        }
        if (hero != null && hero.Owner >= 0 && g.S.Players[hero.Owner].Human)
            total = (long)(total * ((float)diff.CowardiceVersusHuman + 1.0));
        return total;
    }

    /// <summary>
    /// Share of the army that survives (exe FUN_00bc3f90): 1 when A ≥ 10·E, else sqrt(A² − E²) / A; when no human
    /// player sees the battle tile (FUN_0098dbf0) the loss is quartered: 1 − (1 − s) · 0.25.
    /// </summary>
    public static double Survival(double a, double e, bool unseen)
    {
        if (a >= e * 10.0) return 1.0;
        double s = Math.Sqrt(a * a - e * e) / a;
        return unseen ? 1.0 - (1.0 - s) * 0.25 : s;
    }

    /// <summary>
    /// Value of a hero's experience (exe FUN_004e9fe0): L = level of the experience (00b4c840), progress
    /// p = (exp − t[L]) / (t[L+1] − t[L]) + L, value = ftol(ValueOfHeroLevelPow2 · p · p).
    /// </summary>
    public static long LevelValue(Engine.Adventure g, AdventureAiStats st, int exp)
    {
        var t = g.Core.Experience;
        int l = t.LevelFor(exp);
        long baseExp = t.For(l), next = l >= 40 ? int.MaxValue : t.For(l + 1);
        double p = (double)(exp - (int)baseExp) / (int)(next - baseExp) + l;
        return (long)(st.ValueOfHeroLevelPow2 * p * p);
    }

    /// <summary>
    /// Neutral monster value (exe provider 004eee00 → FUN_00bc3af0 with no defender object, minimum survival on,
    /// no siege): A = <see cref="AiArmyPower"/> of the hero's army, E = that of the monster. A &lt; 1 or A ≤ E, or a
    /// survival (<see cref="Survival"/>) below 0.75, refuses the fight (null: state −1). The gain G is the value of the
    /// experience the fight gives (<see cref="LevelValue"/> of exp + Σ count · creature Exp (its hit points when 0)
    /// minus that of exp, FUN_00bc3700 / 004baad0). With m = the strongest other non-allied player's hero armies
    /// (FUN_00bc3a40 over FUN_004ea9d0) &gt; 0 and T = the own player's: T ≤ m → ftol(((f·T − m) · 0.1 + G) · 1.0);
    /// T &gt; m → ftol(G) when f·T ≥ m, else 0; m ≤ 0 → ftol(G). The aggressivity coefficient is 1.0 here (it needs a
    /// defender object).
    /// </summary>
    public static long? MonsterFightValue(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, HeroState h, IReadOnlyList<Stack> monster, bool unseen)
    {
        double a = AiArmyPower(g, st, diff, h.Army.Live(), h);
        double e = AiArmyPower(g, st, diff, monster, null);
        if (a < 1.0 || a <= e) return null;
        double f = Survival(a, e, unseen);
        if (f < 0.75) return null;
        int gained = 0;
        foreach (var s in monster)
            if (s.Count > 0 && g.Core.Creatures.TryGetValue(s.Creature, out var c))
                gained = unchecked(gained + s.Count * (c.Exp != 0 ? c.Exp : c.Health));
        int exp = (int)Math.Min(int.MaxValue, h.Experience);
        double gain = LevelValue(g, st, unchecked(exp + gained)) - LevelValue(g, st, exp);
        double m = StrongestEnemyArmies(g, st, diff, h.Owner);
        if (m <= 0) return (long)(gain * 1.0);
        double total = PlayerHeroArmies(g, st, diff, h.Owner);
        if (total <= m) return (long)(((f * total - m) * 0.1 + gain) * 1.0);
        return f * total >= m ? (long)(gain * 1.0) : 0;
    }

    /// <summary>Σ over the player's heroes of <see cref="AiArmyPower"/> (exe FUN_004ea9d0, no war machines).</summary>
    public static double PlayerHeroArmies(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, int player)
    {
        long sum = 0;
        foreach (var x in g.S.Heroes)
            if (x.Alive && x.Owner == player) sum += AiArmyPower(g, st, diff, x.Army.Live(), x);
        return sum;
    }

    /// <summary>
    /// The largest <see cref="PlayerHeroArmies"/> among players 1..8 still in the game that are not allied with
    /// <paramref name="player"/> (exe FUN_00bc3a40: world vf 0x68(i), vf 0x6c == 1, FUN_00a85030 same team).
    /// </summary>
    public static double StrongestEnemyArmies(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, int player)
    {
        double best = 0;
        int team = g.S.Players[player].Team;
        foreach (var p in g.S.Players)
        {
            if (!p.Alive || p.Index == player || p.Team == team) continue;
            best = Math.Max(best, PlayerHeroArmies(g, st, diff, p.Index));
        }
        return best;
    }
}
