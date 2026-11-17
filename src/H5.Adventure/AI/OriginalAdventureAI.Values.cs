using H5.Adventure.State;
using H5.Combat;
using H5.Core.Data;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.AI;

// The AI's value estimates (exe 004e9b90 … 004eb790, 00872220): gold equivalents of stacks, heroes, towns and a whole
// player, used by the visit simulation, recruiting, hiring, threats and the supplier test. Every int × int product is a
// 32-bit multiply (wraps), sums are 64-bit, ftol = truncation toward zero of the x87 double.
public sealed partial class OriginalAdventureAI
{
    // ------------------------------------------------------------------ resources

    /// <summary>
    /// Resource value (exe FUN_004ea050): acc = ftol(w(i) · amount[i] + acc) for gold first, then wood … gems, with the
    /// player's resource weights (player vf 0x28 → weights object vf 8(i)).
    /// </summary>
    public static long ResourceValue(Engine.Adventure g, int player, Purse amounts)
    {
        var w = ResourceWeights(g, player);
        long acc = (long)(w[Purse.Gold] * (double)amounts[Purse.Gold] + acc0);
        for (int i = 0; i < 6; i++) acc = (long)(w[i] * (double)amounts[i] + (double)acc);
        return acc;
    }
    const double acc0 = 0.0;

    // ------------------------------------------------------------------ stacks, armies

    /// <summary>
    /// Stack value (exe FUN_004e9b90, ECX = stack, EDX = hero or 0): the exact port <see cref="Engine.Adventure.StackPower"/>
    /// (x87 chop, hero getters through BattleSetup.GetterStack; adventure_x87.md "Army power") with this AI's stats. Every
    /// caller here passes EDX as the exe does: the hero estimate / hire score / town value / rumour statistics the army's
    /// hero (004eb050 0x004eb21f, 004eae60 0x004eaf99, 004eb480 0x004eb697 the garrison hero, 008e8de7 / 008e8e83 /
    /// 008e90fb), the supplier slot test the taker (00cdac75), the forced garrison merge the hero (0045afbe), the
    /// hero-less purchase score 0 (00cde23b / 00cde2d3).
    /// </summary>
    public static long StackValue(Engine.Adventure g, AdventureAiStats st, Stack s, HeroState? hero)
        => g.StackPower(s, hero, st);

    /// <summary>Army value (exe FUN_004eaaa0): Σ <see cref="StackValue"/> over the live stacks in slot order.</summary>
    public static long ArmyValue(Engine.Adventure g, AdventureAiStats st, IEnumerable<Stack?> army, HeroState? hero)
        => g.ArmyPowerSum(army, hero, st);

    // ------------------------------------------------------------------ heroes

    static int Worn(Engine.Adventure g, HeroState h, Func<ArtifactDef, int> f) =>
        h.Artifacts.Sum(a => g.Core.Artifacts.TryGetValue(a, out var d) ? f(d) : 0);

    /// <summary>Hero attribute value (exe FUN_004e9f60) over hero vf 0x10..0x1c (with worn artifacts, as <see cref="PrimarySum"/>).</summary>
    public static long HeroAttributeValue(Engine.Adventure g, AdventureAiStats st, HeroState h) =>
        (long)unchecked(Math.Max(0, h.Attack + Worn(g, h, a => a.Attack)) * st.ValueOfAttributeAttack)
        + unchecked(Math.Max(0, h.Defence + Worn(g, h, a => a.Defence)) * st.ValueOfAttributeDefence)
        + unchecked(Math.Max(1, h.SpellPower + Worn(g, h, a => a.SpellPower)) * st.ValueOfAttributeSpellPower)
        + unchecked(Math.Max(1, h.Knowledge + Worn(g, h, a => a.Knowledge)) * st.ValueOfAttributeKnowledge);

    /// <summary>Spell part of the hero values: SPELL_SUMMON_CREATURES (0xea) ValueOfSpellSummonCreatures, else level · ValueOfSpellPerLevel.</summary>
    static long SpellsValue(Engine.Adventure g, AdventureAiStats st, HeroState h)
    {
        long v = 0;
        foreach (var sp in h.Spells) v += SpellValue(g, st, sp);
        return v;
    }

    static long SpellValue(Engine.Adventure g, AdventureAiStats st, string spell) =>
        spell == Engine.Adventure.SpellSummonCreatures ? st.ValueOfSpellSummonCreatures
        : unchecked((g.Core.Spells.TryGetValue(spell, out var d) ? d.Level : 0) * st.ValueOfSpellPerLevel);

    /// <summary>
    /// Artifact value (exe FUN_004ea2f0): the artifact's CostOfGold, or its class value (FUN_004eb840) when that is ≤ 0.
    /// GAP-details: spell scrolls (id 0x34, value of the scroll's spell) and the charged wand (id 3, 0.5 + 0.5 · charges /
    /// max) are not modelled by the engine's artifact list and get the plain value.
    /// </summary>
    public static long ArtifactItemValue(Engine.Adventure g, AdventureAiStats st, string art)
    {
        if (!g.Core.Artifacts.TryGetValue(art, out var d)) return 0;
        long v = d.CostOfGold;
        if (v <= 0) v = st.ArtifactValue(d.Type);
        return v;
    }

    /// <summary>Exe FUN_004eac20: worn artifacts, then the backpack.</summary>
    static long ArtifactsValue(Engine.Adventure g, AdventureAiStats st, HeroState h) =>
        h.Artifacts.Sum(a => ArtifactItemValue(g, st, a)) + h.Backpack.Sum(a => ArtifactItemValue(g, st, a));

    /// <summary>
    /// Skills value (exe FUN_004ead50): per learned skill mastery (hero vf 0x174, Pendant of Mastery included) ·
    /// ValueOfSkillPerMastery, plus ValueOfPerkFeat per perk of that skill.
    /// </summary>
    static long SkillsValue(Engine.Adventure g, AdventureAiStats st, HeroState h)
    {
        long v = 0;
        foreach (var sk in h.Skills)
        {
            if (sk.Mastery <= Mastery.None) continue;
            v += unchecked(Engine.Adventure.EffectiveMastery(h, sk.SkillId) * st.ValueOfSkillPerMastery);
            int perks = h.Perks.Count(p => g.Core.Skills.TryGetValue(p, out var pd) && pd.BasicSkillId == sk.SkillId);
            v += (long)perks * st.ValueOfPerkFeat;
        }
        return v;
    }

    /// <summary>
    /// Hero value (exe FUN_004eae60, the hire score): ValueOfHero + attributes + experience value + spells + artifacts +
    /// skills + army value / 4 (signed 64-bit division).
    /// </summary>
    public static long HeroValue(Engine.Adventure g, AdventureAiStats st, HeroState h) =>
        st.ValueOfHero + HeroAttributeValue(g, st, h) + LevelValue(g, st, (int)Math.Min(int.MaxValue, h.Experience))
        + SpellsValue(g, st, h) + ArtifactsValue(g, st, h) + SkillsValue(g, st, h) + ArmyValue(g, st, h.Army, h) / 4;

    /// <summary>
    /// Hero estimate (exe FUN_004eb050, player value / supplier test / town defender): sum = attributes + experience
    /// value + (spells known ? mana · ValueOfManaPoint : 0) + spells + artifacts + (talisman · ValueOfTalismanBase +
    /// ValueOfTalismanPerLevel) + skills + war machines · ValueOfWarMachine + ftol(Σ max move points of the three move
    /// types · MovePointsMaxAllTypesSumCoef); f = (sum + 100000) · 1e-5, doubled for a commander (role 1);
    /// result = ftol(ValueOfHero + f · (army value + 1)).
    /// GAP-details: the "talisman" level (hero vf 0x300, +0x230) is not identified (0 here, so the PerLevel constant is
    /// added); the move types are taken as land (MaxMovePoints) for all three; war machines = ballista, first aid tent,
    /// ammo cart (the catapult is not counted).
    /// </summary>
    public long HeroEstimate(Engine.Adventure g, AdventureAiStats st, HeroState h, HeroRole? role = null)
    {
        long a = HeroAttributeValue(g, st, h) + LevelValue(g, st, (int)Math.Min(int.MaxValue, h.Experience));
        a += h.Spells.Count == 0 ? 0 : unchecked(h.Mana * st.ValueOfManaPoint);
        long sp = SpellsValue(g, st, h), art = ArtifactsValue(g, st, h);
        long tal = unchecked(0 * st.ValueOfTalismanBase + st.ValueOfTalismanPerLevel);
        long sk = SkillsValue(g, st, h);
        long wm = (long)((h.Ballista ? 1 : 0) + (h.FirstAidTent ? 1 : 0) + (h.AmmoCart ? 1 : 0)) * st.ValueOfWarMachine;
        int mpi = unchecked(h.MaxMovePoints * 3);
        long mp = (long)((double)(float)mpi * (double)(float)st.MovePointsMaxAllTypesSumCoef);
        double armyF = ArmyValue(g, st, h.Army, h) + 1.0;
        long sum = mp + wm + sk + tal + art + sp + a;
        double f = (sum + 100000.0) * 1.0e-5;
        if ((role ?? _role.GetValueOrDefault(h.Id, HeroRole.Freelancer)) == HeroRole.Commander) f += f;
        return (long)(st.ValueOfHero + f * armyF);
    }

    // ------------------------------------------------------------------ towns, players

    /// <summary>
    /// Town value (exe FUN_004eb480): (a) next growth: Σ over dwellings of the weekly growth × the top upgrade's
    /// ftol(max(Power, 1) · 100), × 1 / (15 − 2 · day of week); (b) creatures for hire now (FUN_004ea3b0, from the
    /// highest tier down: an affordable one reserves its cost and counts × 0.25, otherwise × 0.1); (c) the town's income
    /// value × IncomeMultiplier; (d) the garrison's army value when the town is the player's; (e) ValueOfTown.
    /// GAP-details: the exe's eight growth counts per dwelling are taken as the engine's weekly growth of the tier.
    /// </summary>
    public static long TownValue(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, TownState t, int player)
    {
        if (g.Player(player) is not { } p) return 0;
        long gw = 0;
        var offers = new List<(int Count, CreatureDef C)>();
        for (int tier = 1; tier <= 7; tier++)
        {
            string? top = g.DwellingCreature(t, tier, 2) ?? g.DwellingCreature(t, tier, 1) ?? g.DwellingCreature(t, tier, 0);
            if (top == null || !g.Core.Creatures.TryGetValue(top, out var cd)) continue;
            long pw = (long)(Math.Max((double)cd.Power, 1.0) * 100.0);
            if (t.Level($"TB_DWELLING_{tier}") > 0) gw += g.WeeklyGrowthOf(t, tier) * pw;
            offers.Add((t.Available[tier - 1], cd));
        }
        int dow = g.S.DayOfWeek;
        long h = HireValue(offers, p.Purse);
        long v = h + (long)(gw * (1.0 / (15 - 2 * dow)));
        v += ResourceValue(g, player, g.TownIncome(t)) * st.IncomeMultiplier;
        if (t.Owner == player)
        {
            var gh = g.S.Heroes.FirstOrDefault(x => x.Alive && x.Garrisoned && x.InTown == t.Object);
            v += ArmyValue(g, st, gh?.Army ?? t.Garrison, gh);
        }
        return v + diff.ValueOfTown;
    }

    /// <summary>Exe FUN_004ea3b0 over the offers (highest entry first).</summary>
    static long HireValue(List<(int Count, CreatureDef C)> offers, Purse purse)
    {
        long acc = 0;
        var spent = new Purse();
        for (int i = offers.Count - 1; i >= 0; i--)
        {
            var (n, c) = offers[i];
            var need = new Purse { R = (int[])spent.R.Clone() }; need.Add(c.Cost);
            double f = 0.1;
            if (Affordable(purse, need) >= 1.0f) { spent = need; f = 0.25; }
            long pw = (long)(Math.Max((double)c.Power, 1.0) * 100.0);
            acc = (long)((double)((long)n * pw) * f + acc);
        }
        return acc;
    }

    /// <summary>Exe FUN_009d0500: min over the needed resources of have / need (negative → 0), as float.</summary>
    static float Affordable(Purse have, Purse need)
    {
        float m = float.MaxValue;
        for (int i = 0; i < 7; i++)
        {
            if (need[i] == 0) continue;
            float q = (float)have[i] / need[i];
            if (q < 0) q = 0;
            m = Math.Min(m, q);
        }
        return m;
    }

    /// <summary>
    /// Player value (exe FUN_004eb790): resources + daily income value × IncomeMultiplier + Σ hero estimates + Σ town
    /// values + the grail part (FUN_004ea180).
    /// GAP-detail: the grail part (puzzle map pieces × the weights object vf 0x34 / 0x4c) is not ported (0).
    /// </summary>
    public long PlayerValue(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, int player)
    {
        if (g.Player(player) is not { } p) return 0;
        long r = ResourceValue(g, player, p.Purse);
        r += ResourceValue(g, player, g.ExpectedIncome(p)) * st.IncomeMultiplier;
        foreach (var h in g.S.Heroes) if (h.Alive && h.Owner == player) r += HeroEstimate(g, st, h);
        foreach (var t in g.S.Towns) if (t.Owner == player) r += TownValue(g, st, diff, t, player);
        return r;
    }

    /// <summary>
    /// Position value (exe FUN_00872220): state 3 when the player has won, −2 when lost; value = ftol(P · c − Q · c) with
    /// Q the opponent (or 0) and c = AttackHuman/ComputerAggressivityCoef by Q's type (1 without an opponent).
    /// </summary>
    public (int State, long Value) PositionValue(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, int player, int opponent)
    {
        int state = 0;
        var p = g.Player(player);
        if (p is { Alive: false }) state = -2;
        else if (g.S.Finished && p != null && g.S.WinnerTeam == p.Team) state = 3;
        float c = 1.0f;
        var q = opponent >= 0 ? g.Player(opponent) : null;
        if (q is { Alive: true }) c = (float)(q.Human ? diff.AttackHumanAggressivityCoef : diff.AttackComputerAggressivityCoef);
        double a = PlayerValue(g, st, diff, player) * (double)c;
        double b = q is { Alive: true } ? PlayerValue(g, st, diff, q.Index) * (double)c : 0.0;
        return (state, (long)(a - b));
    }
}
