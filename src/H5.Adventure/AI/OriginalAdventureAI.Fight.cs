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
    public static long? MonsterFightValue(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, HeroState h, IReadOnlyList<Stack> monster, bool unseen) =>
        FightValue(g, st, diff, h, monster, null, 1.0, unseen);

    /// <summary>
    /// The monster form of the battle estimate (no defender object, minimum survival on, no siege): see
    /// <see cref="BattleEstimate"/>; null = refused.
    /// </summary>
    public static long? FightValue(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, HeroState h, IReadOnlyList<Stack> monster,
                                   HeroState? defHero, double c, bool unseen)
    {
        var (state, v) = BattleEstimate(g, st, diff, h.Army.Live(), h, monster, defHero, -1, noMinimum: false, siege: false, unseen, c);
        return state < 0 ? null : v;
    }

    /// <summary>
    /// The battle estimate FUN_00bc3af0(attacker, defender stacks, defender object, noMinimum (arg 5), siege (arg 6),
    /// unseen (arg 7)) as (state, value):
    ///   A = <see cref="AiArmyPower"/> of the attacker (siege: flyers × SiegeFlyerCoef), E = that of the defender
    ///   (siege: shooters × SiegeShootersCoef); c = 1, or with a defender object the aggressivity of its owner
    ///   (FUN_00c02ae0: AttackHumanAggressivityCoef for a human, AttackComputerAggressivityCoef otherwise);
    ///   A &lt; 1 or A ≤ E → (−1, 0); f = <see cref="Survival"/>; without noMinimum f &lt; 0.75 → (−1, 0);
    ///   G = the attacker's experience value gain from the defender stacks (00bc3700);
    ///   a defender hero (004e9280): (0, ftol(estimate(defender) / n + G · c)), n = players 1..8 not allied with the
    ///   attacker (FUN_00a85090, at least 1);
    ///   otherwise without noMinimum the rival-army adjustment of <see cref="MonsterFightValue"/>, else (0, ftol(G · c)).
    /// The attacker wins (FUN_008664c0) when the state is &gt; 0, or 0 with a value &gt; 0.
    /// Callers: monsters 004eee00 (unseen, minimum), hero targets 00bc3ee0 (noMinimum), the danger gate 008672d0
    /// (noMinimum), the threat defence test 0086cd10 (noMinimum, siege).
    /// GAP-details: the siege war machine part (the town's machines FUN_00cd0a30 → 004ea220) is not added; the
    /// state change of 009d1250 (attacker hero vf 0x21c == 4 against an owner whose vf 0x21c == 5, FUN_00c214d0) is not
    /// identified; the defender's estimate uses this AI's role table (an enemy hero counts as a freelancer).
    /// </summary>
    public static (int State, long Value) BattleEstimate(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff,
        IEnumerable<Stack> att, HeroState? attHero, IEnumerable<Stack> def, HeroState? defHero, int defOwner,
        bool noMinimum, bool siege, bool unseen, double? coef = null)
    {
        var defList = def.ToList();
        double a = AiArmyPower(g, st, diff, att, attHero, siege ? 1 : 0);
        double e = AiArmyPower(g, st, diff, defList, defHero, siege ? 2 : 0);
        int owner = defHero?.Owner ?? defOwner;
        double c = coef ?? 1.0;
        if (coef == null && owner >= 0 && g.Player(owner) is { } op)
            c = (float)(op.Human ? diff.AttackHumanAggressivityCoef : diff.AttackComputerAggressivityCoef);
        if (a < 1.0 || a <= e) return (-1, 0);
        double f = Survival(a, e, unseen);
        if (!noMinimum && f < 0.75) return (-1, 0);
        int gained = 0;
        foreach (var s in defList)
            if (s.Count > 0 && g.Core.Creatures.TryGetValue(s.Creature, out var cr))
                gained = unchecked(gained + s.Count * (cr.Exp != 0 ? cr.Exp : cr.Health));
        double gain = 0.0;
        if (attHero != null)
        {
            int exp = (int)Math.Min(int.MaxValue, attHero.Experience);
            gain = LevelValue(g, st, unchecked(exp + gained)) - LevelValue(g, st, exp);
        }
        if (defHero is { Alive: true })
        {
            int n = attHero == null ? 1 : Math.Max(1, g.S.Players.Count(p => !SameTeam(g, attHero.Owner, p.Index)));
            long est = new OriginalAdventureAI().HeroEstimate(g, st, defHero, HeroRole.Freelancer) / n;
            return (0, (long)((double)est + gain * c));
        }
        if (!noMinimum && attHero != null)
        {
            double m = StrongestEnemyArmies(g, st, diff, attHero.Owner);
            if (m > 0)
            {
                double total = PlayerHeroArmies(g, st, diff, attHero.Owner);
                if (total <= m) return (0, (long)(((f * total - m) * 0.1 + gain) * c));
                return (0, f * total >= m ? (long)(gain * c) : 0);
            }
        }
        return (0, (long)(gain * c));
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
