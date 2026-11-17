using H5.Adventure.State;
using H5.Combat;
using H5.Core.Data;
using H5.Core.Numerics;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.Engine;

// Army power of H5_Game.exe (diplomacy, stack splitting, AI values): stack value 004e9b90, army sum 004eaaa0,
// hero / army ratio 004eabb0. Every float step is chopped to 24 bits (the game's x87 control word, X87.cs).
public sealed partial class Adventure
{
    /// <summary>
    /// Stack value (exe FUN_004e9b90; ECX = stack, EDX = hero or 0, arg = week = hero vf 0x22c). No creature record → 1
    /// (not × count). Otherwise, with ftol = truncation and every FMUL / FADD chopped:
    /// v = ftol(max(Power, 1) · 100.0);
    /// v = ftol(v + chop(hit points (004bc470, hero) − record +0x64) · CreatureOverHealthCoef (AI +0x250));
    /// v = ftol(v + chop(min + max damage (004bbfb0 / 004bc020, hero) − record +0x50 − +0x54) · CreatureOverDamageCoef (+0x254));
    /// v = ftol(v + (attack + defence (004bb170 / 004bb2a0, no hero) − record +0x44 − +0x48) · CreatureOverAttributesCoef (+0x258));
    /// v = ftol(v + artifact armor crushing · CreatureMAECoef_DefenceCrush (+0x25c)), magic protection · _SpellReduce (+0x260),
    /// speed · _Movement (+0x264) (summary bytes 8 / 9 / 0xc of 00c0a2c0, the layout of 00c0a560);
    /// v = ftol(v · (1 + luck · (luck ≥ 0 ? CreatureGoodLuckCoef +0x26c : CreatureBadLuckCoef +0x270))), luck = 004bb3c0;
    /// v = ftol(v · (1 + morale · (morale ≥ 0 ? CreatureGoodMoraleCoef +0x274 : CreatureBadMoraleCoef +0x278))), morale =
    /// 004bb3f0 − 1 when the hero's vf 0x54 object has vf 0x34(4) (TB_TAVERN) &gt; 0;
    /// v = ftol(v · (1 + (initiative (004bc190, hero) − record +0x5c) · CreatureOverInitiativeCoef (+0x268)));
    /// result = count (stack +0x20) · v (64-bit __allmul).
    /// GAP-details: the ×140 / 100 shooter step (creature +0x4c &gt; 0 and hero vf 0x324 = CHero +0x264 &gt; 0) is not
    /// applied — the only writers of +0x264 found are the constructors (00b58950 / 00b58dc0, zero); the stack's own
    /// morale / luck bases (army stack +0x28 / +0x24) are taken as 0; hero vf 0x54 is read as the town the hero stands in.
    /// </summary>
    public long StackPower(Stack s, HeroState? hero, AI.AdventureAiStats? st = null) => StackPower(new PowerContext(this, hero, st), s);

    /// <summary>Army sum (exe FUN_004eaaa0): Σ <see cref="StackPower"/> over the live stacks (64-bit ADD / ADC).</summary>
    public long ArmyPowerSum(IEnumerable<Stack?> army, HeroState? hero, AI.AdventureAiStats? st = null)
    {
        var ctx = new PowerContext(this, hero, st);
        long sum = 0;
        foreach (var s in army) if (s is { Count: > 0 }) sum += StackPower(ctx, s);
        return sum;
    }

    /// <summary>
    /// Hero / army strength ratio (exe FUN_004eabb0, ECX = hero, EDX = the other army): FILD qword Σ(hero's army, with the
    /// hero) / FILD qword Σ(other army, no hero, the hero's week), FDIVR chopped; the caller stores it as a float.
    /// </summary>
    public float ArmyPowerRatio(HeroState hero, IEnumerable<Stack?> other)
    {
        long a = ArmyPowerSum(hero.Army.Live(), hero), b = ArmyPowerSum(other, null);
        return b == 0 ? (float)((double)a / b) : X87.Div(a, b);
    }

    /// <summary>
    /// Hero / one creature ratio (exe FUN_004eab10, ECX = hero, EDX = creature id): FILD qword Σ(hero's army, with the
    /// hero, the hero's week vf 0x22c) stored as a double; no creature record (0089fbf0) → FDIVR by FILD 1; else by
    /// ftol(max(Power (record +0x84), 1.0f) · 100.0) (00d11a60), chopped. Only use: the hero trace 008e28c0 (creature 1).
    /// </summary>
    public float ArmyPowerPerCreature(HeroState hero, string creature)
    {
        long a = ArmyPowerSum(hero.Army.Live(), hero);
        if (!Core.Creatures.TryGetValue(creature, out var c)) return X87.Div(a, 1);
        return X87.Div(a, Ftol(X87.Mul(Math.Max((double)c.Power, 1.0), 100.0)));
    }

    /// <summary>The floats 00f78f2c[0..2] of 00c030e0: 0, 1100, 1600.</summary>
    static readonly float[] HeroTraceThresholds = { 0f, 1100f, 1600f };

    /// <summary>
    /// 00c030e0: the highest i of 2, 1, 0 with 00f78f2c[i] &lt; r (float compare), none (r ≤ 0 or NaN) → −1.
    /// </summary>
    public static int HeroTraceLevel(float r)
    {
        for (int i = 2; i >= 0; i--) if (HeroTraceThresholds[i] < r) return i;
        return -1;
    }

    /// <summary>
    /// The hero's trace (path effect) level (exe FUN_008e28c0, reached through the this-adjusting thunk 008e5510; the hero
    /// is this − 0x1ac's interface +0x14 vf 0x14): nothing when this − 0x58 vf 0x104 is 2 (read as the hero's movement
    /// type, not verified) or without a trace set (the hero shared 004a6740 +0x1e0 → SAdvMapHeroTraceSet, 00a150c0) with a
    /// non-empty traces list (+0x44, 8-byte items); otherwise <see cref="HeroTraceLevel"/> of
    /// <see cref="ArmyPowerPerCreature"/>(hero, creature 1 = CREATURE_PEASANT) — the army in peasants: &gt; 1600 → 2,
    /// &gt; 1100 → 1, &gt; 0 → 0 — and the traces item min(level, count − 1) (00a16340 → SAdvMapHeroTrace) is shown.
    /// GAP: the render side (the trace's Objects effects and Decals, ObjectsDensity) is not drawn, and when the exe calls
    /// 008e28c0 is not traced. Returns the level (−1 = none) before the clamp to the list.
    /// </summary>
    public int HeroTrace(HeroState hero) => HeroTraceLevel(ArmyPowerPerCreature(hero, "CREATURE_PEASANT"));

    /// <summary>The army power of the exe (<see cref="ArmyPowerSum"/>, 004eaaa0) as a double for comparisons.</summary>
    public double ArmyPower(IEnumerable<Stack> army, HeroState? hero) => ArmyPowerSum(army, hero);

    sealed class PowerContext
    {
        public readonly Adventure G;
        public readonly HeroState? Hero;
        public readonly AI.AdventureAiStats St;
        BattleState? _with, _without;
        public PowerContext(Adventure g, HeroState? hero, AI.AdventureAiStats? st)
        {
            G = g; Hero = hero; St = st ?? g.AiStats;
        }
        BattleState WithoutCtx => _without ??= BattleSetup.GetterContext(G.Core, null, G.S.MoonWeek);
        BattleState WithCtx => _with ??= Hero == null ? WithoutCtx : BattleSetup.GetterContext(G.Core, G.ToInstance(Hero), G.S.MoonWeek);
        public Unit With(Stack s) => BattleSetup.GetterStack(G.Core, WithCtx, s.Creature, s.Artifact);
        public Unit Without(Stack s) => Hero == null ? With(s) : BattleSetup.GetterStack(G.Core, WithoutCtx, s.Creature, s.Artifact);
    }

    static long Ftol(float x) => (long)x;

    // Per-unit values without a hero and without a creature artifact depend only on the creature and the week.
    readonly Dictionary<string, long> _bareStackPower = new();
    string? _bareStackPowerWeek;
    AI.AdventureAiStats? _bareStackPowerStats;

    static long StackPower(PowerContext ctx, Stack s)
    {
        var g = ctx.G;
        if (!g.Core.Creatures.TryGetValue(s.Creature, out var c)) return 1;
        if (ctx.Hero == null && MicroArtifact.IsEmpty(s.Artifact))
        {
            if (g._bareStackPowerWeek != g.S.MoonWeek || !SameCoefs(g._bareStackPowerStats, ctx.St))
            {
                g._bareStackPower.Clear();
                g._bareStackPowerWeek = g.S.MoonWeek;
                g._bareStackPowerStats = ctx.St;
            }
            if (!g._bareStackPower.TryGetValue(s.Creature, out long unit))
                g._bareStackPower[s.Creature] = unit = UnitPower(ctx, new Stack(s.Creature, 1), c);
            return (long)s.Count * unit;
        }
        if (ctx.Hero is { } h && MicroArtifact.IsEmpty(s.Artifact))
        {
            long fp = HeroFingerprint(g, h, ctx.St);
            if (!g._heroStackPower.TryGetValue((h, s.Creature), out var e) || e.Fp != fp)
                g._heroStackPower[(h, s.Creature)] = e = (fp, UnitPower(ctx, new Stack(s.Creature, 1), c));
            return (long)s.Count * e.Unit;
        }
        return (long)s.Count * UnitPower(ctx, s, c);
    }

    // Per-unit values with a hero (no creature artifact), valid while every hero input of the getters is unchanged.
    readonly Dictionary<(HeroState, string), (long Fp, long Unit)> _heroStackPower = new(new HeroKeyComparer());

    sealed class HeroKeyComparer : IEqualityComparer<(HeroState, string)>
    {
        public bool Equals((HeroState, string) a, (HeroState, string) b) => ReferenceEquals(a.Item1, b.Item1) && a.Item2 == b.Item2;
        public int GetHashCode((HeroState, string) k) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Item1), k.Item2);
    }

    /// <summary>Everything of the hero the getter context reads (ToInstance → MakeHero → AddCreature) and the tavern test.</summary>
    static long HeroFingerprint(Adventure g, HeroState h, AI.AdventureAiStats st)
    {
        var hc = new HashCode();
        hc.Add(g.S.MoonWeek); hc.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(st));
        hc.Add(h.DefId); hc.Add(h.Level); hc.Add(h.InTown);
        hc.Add(h.InTown >= 0 && g.S.Town(h.InTown) is { } t ? t.Level("TB_TAVERN") : 0);
        hc.Add(h.NextBattleAttack); hc.Add(h.NextBattleDefence); hc.Add(h.NextBattleHitPoints);
        hc.Add(h.NextBattleInitiative); hc.Add(h.NextBattleSpeed);
        hc.Add(h.Attack); hc.Add(h.Defence); hc.Add(h.SpellPower); hc.Add(h.Knowledge);
        foreach (var a in h.Artifacts) hc.Add(a);
        hc.Add(h.Artifacts.Count);
        foreach (var p in h.Perks) hc.Add(p);
        hc.Add(h.Perks.Count);
        foreach (var sk in h.Skills) { hc.Add(sk.SkillId); hc.Add(sk.Mastery); }
        return ((long)hc.ToHashCode() << 32) ^ (uint)(h.Artifacts.Count * 31 + h.Perks.Count * 7 + h.Skills.Count);
    }

    static bool SameCoefs(AI.AdventureAiStats? a, AI.AdventureAiStats b) => a != null && (ReferenceEquals(a, b) ||
        a.CreatureOverHealthCoef == b.CreatureOverHealthCoef && a.CreatureOverDamageCoef == b.CreatureOverDamageCoef
        && a.CreatureOverAttributesCoef == b.CreatureOverAttributesCoef && a.CreatureMaeDefenceCrush == b.CreatureMaeDefenceCrush
        && a.CreatureMaeSpellReduce == b.CreatureMaeSpellReduce && a.CreatureMaeMovement == b.CreatureMaeMovement
        && a.CreatureOverInitiativeCoef == b.CreatureOverInitiativeCoef && a.CreatureGoodLuckCoef == b.CreatureGoodLuckCoef
        && a.CreatureBadLuckCoef == b.CreatureBadLuckCoef && a.CreatureGoodMoraleCoef == b.CreatureGoodMoraleCoef
        && a.CreatureBadMoraleCoef == b.CreatureBadMoraleCoef);

    /// <summary>004e9b90 before the final × count.</summary>
    static long UnitPower(PowerContext ctx, Stack s, CreatureDef c)
    {
        var g = ctx.G;
        var st = ctx.St;
        var hero = ctx.Hero;
        string week = g.S.MoonWeek;
        int M(MicroEffect e) => s.Artifact?.Bonus(e) ?? 0;
        var w = ctx.With(s);
        var o = ctx.Without(s);
        // 004e9bdc: FILD Power, max with 1.0f, FMUL 100.0 (00e0d608), ftol.
        long v = Ftol(X87.Mul(Math.Max((double)c.Power, 1.0), 100.0));
        // 004e9c58: hit points with the hero (FILD → FSTP float) × +0x250.
        v = Ftol(X87.AddExact(v, X87.Mul(X87.Chop(w.BaseHealth - c.Health), (float)st.CreatureOverHealthCoef)));
        // 004e9c96: min + max damage with the hero (FILD → FSTP float) × +0x254.
        v = Ftol(X87.AddExact(v, X87.Mul(X87.Chop(w.BaseMinDamage - c.MaxDamage - c.MinDamage + w.BaseMaxDamage), (float)st.CreatureOverDamageCoef)));
        // 004e9ceb: attack + defence without the hero, FILD × +0x258.
        v = Ftol(X87.AddExact(v, X87.Mul(o.BaseAttack - c.Defence - c.Attack + o.BaseDefence, (float)st.CreatureOverAttributesCoef)));
        // 004e9d32: the creature artifact's summary bytes 8, 9, 0xc.
        v = Ftol(X87.AddExact(v, X87.Mul(M(MicroEffect.ArmorCrushing), (float)st.CreatureMaeDefenceCrush)));
        v = Ftol(X87.AddExact(v, X87.Mul(M(MicroEffect.MagicProtection), (float)st.CreatureMaeSpellReduce)));
        v = Ftol(X87.AddExact(v, X87.Mul(M(MicroEffect.Speed), (float)st.CreatureMaeMovement)));
        // 004e9dcb: −1 morale when the hero's vf 0x54 object (its town) has a tavern (vf 0x34(4)).
        int tavern = hero != null && hero.InTown >= 0 && g.S.Town(hero.InTown) is { } ht && ht.Level("TB_TAVERN") > 0 ? -1 : 0;
        // 004e9e1d: luck 004bb3c0 × good (+0x26c) / bad (+0x270), + 1.0, × v.
        int luck = StackLuck(c, s.Artifact, week);
        float fl = X87.Add(X87.Mul(luck, (float)(luck >= 0 ? st.CreatureGoodLuckCoef : st.CreatureBadLuckCoef)), 1f);
        v = Ftol(X87.Mul(v, fl));
        // 004e9e73: morale 004bb3f0 (+ the tavern −1) × good (+0x274) / bad (+0x278).
        int morale = StackMorale(c, s.Artifact, hero, week) + tavern;
        float mp = X87.Mul(morale, (float)(morale >= 0 ? st.CreatureGoodMoraleCoef : st.CreatureBadMoraleCoef));
        // 004e9e98: initiative 004bc190 (hero) FISUB record +0x5c, FSTP float, × +0x268, + 1.0.
        float di = X87.Chop((double)(float)w.BaseInitiative - c.Initiative);
        float fi = X87.Add(X87.Mul(di, (float)st.CreatureOverInitiativeCoef), 1f);
        float fm = X87.Add(mp, 1f);
        v = Ftol(X87.Mul(v, fm));
        v = Ftol(X87.Mul(v, fi));
        return v;
    }

    /// <summary>004bb3c0 → 004ba970: week (00bcdcf0) on the stack's base luck (0 here) + the artifact's luck, clamped −5..5.</summary>
    static int StackLuck(CreatureDef c, MicroArtifact? art, string week) =>
        Math.Clamp(MoonWeek.MoraleLuck(0, c.Town, week) + (art?.Bonus(MicroEffect.Luck) ?? 0), -5, 5);

    /// <summary>
    /// 004bb3f0 → 004ba9d0(hero, week): ABILITY_UNDEAD → 0; m = week (00bcdd00) on the stack's base morale (0 here) + the
    /// artifact's morale; ABILITY_MECHANICAL → max(0, m) with a hero having HERO_SKILL_ARTIFICIAL_GLORY (vf 0x174(0x80)),
    /// else 0; ABILITY_BRAVERY → at least 1; clamped −5..5.
    /// </summary>
    static int StackMorale(CreatureDef c, MicroArtifact? art, HeroState? hero, string week)
    {
        if (c.Has("ABILITY_UNDEAD")) return 0;
        int m = MoonWeek.MoraleLuck(0, c.Town, week) + (art?.Bonus(MicroEffect.Morale) ?? 0);
        if (c.Has("ABILITY_MECHANICAL"))
            m = hero != null && hero.HasPerk("HERO_SKILL_ARTIFICIAL_GLORY") ? Math.Max(0, m) : 0;
        else if (c.Has("ABILITY_BRAVERY") && m < 2) m = 1;
        return Math.Clamp(m, -5, 5);
    }
}
