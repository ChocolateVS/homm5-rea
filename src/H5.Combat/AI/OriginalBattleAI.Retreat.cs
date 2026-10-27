namespace H5.Combat.AI;

// The original AI's flee decision, H5_Game.exe FUN_00c972d0 (called by CCombatAI vf 0 = FUN_00c97930 before each think and
// by the quick-combat loop at 0x00c1ff8f). It writes only the "retreat" flag (CCombatAI +0x10, read by vf 8); the
// "surrender" flag (+0x11, vf 0xc) is cleared and never set, so the AI always retreats in mode 2 (0054b650), never pays a
// ransom. Ledger: docs/rules/AUDIT_AI.md, "AI flee / surrender decision".
public sealed partial class OriginalBattleAI
{
    /// <summary>Runs the flee decision before each think (false for the simulations of rule 2, which call the think directly).</summary>
    public bool Retreats { get; init; } = true;

    /// <summary>Why the last decision retreated ("rule 2" or "ratio"), null when it did not.</summary>
    public string? LastRetreat { get; private set; }

    /// <summary>
    /// FUN_00c972d0 for the side to act; null = fight on. In order:
    /// the army may retreat (CCombatArmy vf 0xf0(0, false), which also carries the DenyAIHeroFlee lists as reason 7) and has
    /// an adventure hero; a computer-owned army only on engine difficulty 2+ (army vf 0x34 = owner vf 0x190 "is human",
    /// else FUN_004f6670); rule 1 (FUN_00c97040, siege attacker that cannot reach the walled defenders) is not ported;
    /// artifact value (FUN_00c96ea0) &lt; 1000 with experience &lt; 2000 → fight on; rule 2 (FUN_00c971b0) → retreat;
    /// the army may surrender (vf 0xf0(0, true)) and the player's gold covers ransom (vf 0xf4) + hire price (00b4ced0);
    /// threshold t = 0.16 / 0.2 (value &gt; 0) / 0.21 (&gt; 5000) / 0.22 (&gt; 10000) − FUN_004f6680(engine difficulty: 0, 1 → 0.45,
    /// 2 → 0.4, 3 → 0.35, else 0) + min(0.3, experience · 5e-6), − 0.06 for the attacker (combat vf 0x84(1)), at most 0.16;
    /// retreat when own power / (1.1 · enemy power + own power) &lt; t (FUN_00bf9b80 on the combat value FUN_00bfc2a0).
    /// GAP-details: FUN_00c96d40 (owner test 00c214d0) is taken as false; the week argument of the gold cost 00ca6410 is
    /// not applied.
    /// </summary>
    public string? RetreatDecision(Battle b, int side)
    {
        var s = b.State;
        if (s.Phase != BattlePhase.Combat || s.AiRetreat[side] is not { } info) return null;
        if (b.RetreatRefusal(side, false) != 0) return null;
        bool human = HumanSides is { } hs ? hs[side] : !s.AiControlled[side];
        int engine = EngineDifficulty(GameDifficulty);
        if (!human && engine is 0 or 1) return null;
        if (info.ArtifactValue < 1000 && info.Experience < 2000) return null;
        if (SimulatedRoundLoses(b, side)) return "rule 2";
        if (b.RetreatRefusal(side, true) != 0) return null;
        var h = s.Heroes[side];
        if (h.Resources is { } res)
        {
            long gold = res[6] - h.ResourcesSpent[6];
            if (gold < b.SurrenderCost(side) + info.HireCost) return null;
        }
        float t = RetreatThreshold(info.ArtifactValue, info.Experience, engine, attacker: side == 0);
        var v = CombatValue.Of(b, side);
        double my = v.MyPower, enemy = v.EnemyPower;
        return my / (enemy * 1.1f + my) < t ? "ratio" : null;
    }

    /// <summary>The threshold of the last test (0x00c97599..0x00c97663), float steps as the exe stores them.</summary>
    public static float RetreatThreshold(long artifactValue, long experience, int engineDifficulty, bool attacker)
    {
        int p = (int)Math.Clamp(artifactValue, int.MinValue, int.MaxValue);
        float t = p > 10000 ? 0.22f : p > 5000 ? 0.21f : p > 0 ? 0.2f : 0.16f;
        float offset = engineDifficulty switch { 0 or 1 => 0.45f, 2 => 0.4f, 3 => 0.35f, _ => 0f };
        t = t - offset;
        double e = (int)Math.Clamp(experience, int.MinValue, int.MaxValue) * (double)5e-6f;
        if (e > 0.3f) e = 0.3f;
        t = (float)(e + t);
        if (attacker) t = t - 0.06f;
        if (t > 0.16f) t = 0.16f;
        return t;
    }

    /// <summary>
    /// Rule 2, FUN_00c971b0: when the enemy's damage would destroy our troops within two rounds (FUN_00bfa0a0(2.0) on the
    /// combat value), play the battle on a copy with the think for every unit (FUN_00b3ab70, ≤ 2000 commands) until the
    /// time passes the limit of FUN_00c96e10, and retreat when the enemy would then destroy them within one round
    /// (FUN_00bfa0a0(DAT_00f7b55c = 1.0)).
    /// </summary>
    public bool SimulatedRoundLoses(Battle b, int side)
    {
        if (NextOwnTurnLimit(b, side) is not double limit) return false;
        if (!DiesWithin(CombatValue.Of(b, side), 2.0)) return false;
        var sim = new Battle(b.Db, b.State.Clone());
        var ai = new OriginalBattleAI
        {
            GameDifficulty = GameDifficulty, HumanSides = HumanSides, RowOverride = RowOverride, Retreats = false, _stats = _stats,
        };
        for (int i = 0; i < 2000 && sim.State.Phase == BattlePhase.Combat && sim.State.Time <= limit; i++)
        {
            if (sim.SideToAct is not int who) break;
            if (!sim.Apply(ai.Decide(sim, who)).Ok) break;
        }
        return DiesWithin(CombatValue.Of(sim, side), 1.0);
    }

    /// <summary>FUN_00bfa0a0(k): own troop hit points (+0x68) &lt; enemy damage (+0xc) · factor(enemy attack +0x10, own defence +0x64) · k.</summary>
    public static bool DiesWithin(CombatValue v, double k) =>
        v.My.TroopsHealth < v.Enemy.Damage * SideValue.Factor(v.Enemy.AttackLevel, v.My.Defence) * k;

    /// <summary>
    /// FUN_00c96e10: walks the turn queue (combat vf 0xfc +0x20, (unit, time) pairs) from the entry after the current one;
    /// the first unit of our army (unit vf 0xc) with morale (vf 0x1b0, clamped to ±5) ≥ 0 — or the second one of ours —
    /// stops the walk; the result is the time of the last entry passed (null = 0: our next turn comes at once).
    /// GAP-detail: the queue length is ours (one turn per actor), a hero's morale is its own Morale.
    /// </summary>
    public static double? NextOwnTurnLimit(Battle b, int side)
    {
        var s = b.State;
        int actors = s.Units.Count(u => u.Alive && (!u.IsObject || u.BaseInitiative > 0)) + 2;
        var order = b.PredictTurnOrder(actors + 1);
        double? last = null;
        bool seen = false;
        for (int i = 1; i < order.Count; i++)
        {
            var (a, time) = order[i];
            int owner;
            int morale;
            if (Actor.IsHero(a)) { owner = Actor.HeroSide(a); morale = s.Heroes[owner].Morale; }
            else { var u = s.Unit(a)!; owner = u.Side; morale = b.MoraleOf(u); }
            if (owner == side)
            {
                if (Math.Clamp(morale, -5, 5) >= 0 || seen) return last;
                seen = true;
            }
            last = time;
        }
        return last;
    }
}
