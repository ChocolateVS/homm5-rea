using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.AI;

// Hero hiring (exe phase 7 FUN_0086b0a0, score FUN_0086aa50, race coefficients FUN_008683f0).
public sealed partial class OriginalAdventureAI
{
    /// <summary>
    /// The hire phase (FUN_0086b0a0), at most one hire per day (CPlayerAI +0x60): the count gates of
    /// <see cref="HiringAllowed"/> (heroes ≥ HireHeroesBase + HireHeroesPerTown · towns only sets the "over limit" flag);
    /// the race coefficients; the two tavern offers in order, an offer skipped when the player has heroes and less gold
    /// than the hire price without a town; the offer with the strictly higher <see cref="HireScore"/> wins (ties keep
    /// slot 0; a score of 0 is still taken); its commands are run: the tavern if the chosen town has none, then the hire,
    /// then the role task (role 1 for a script-forced hero, else collector of the first commander nobody helps).
    /// </summary>
    bool HireHeroes(Engine.Adventure g, List<TownState> towns, AdventureAiStats st, AdventureDifficulty diff)
    {
        var me = g.Current;
        int heroes = g.S.Heroes.Count(h => h.Alive && h.Owner == me.Index);
        if (heroes >= g.HiringHeroLimit(me)) return false;
        bool overLimit = heroes >= diff.HireHeroesBase + diff.HireHeroesPerTown * towns.Count;
        if (heroes > 0 && ComputerHeroes(g) >= diff.HireHeroesGlobalLimit) return false;
        var coef = RaceCoefficients(g, me.Index);
        double best = 0;
        (string Id, TownState Town, bool Tavern, bool Forced)? choice = null;
        for (int slot = 0; slot < me.Tavern.Length && slot < 2; slot++)
        {
            if (me.Tavern[slot] is not { } id || !g.Core.Heroes.ContainsKey(id)) continue;
            if (heroes > 0 && me.Purse[Purse.Gold] < g.HireCost(me)) continue;
            var r = HireScore(g, st, diff, towns, id, coef, overLimit);
            if (r == null) continue;
            if (choice == null || r.Value.Score > best) { best = r.Value.Score; choice = (id, r.Value.Town, r.Value.Tavern, r.Value.Forced); }
        }
        if (choice is not { } c) return false;
        if (c.Tavern && !Send(g, new BuildCmd(c.Town.Object, "TB_TAVERN") { Player = me.Index }).Ok) return false;
        int shown = Array.IndexOf(g.TownTavernOffers(me, c.Town), c.Id);
        if (shown < 0 || !Send(g, new HireHeroCmd(c.Town.Object, shown) { Player = me.Index }).Ok) return false;
        var hired = g.S.Heroes.LastOrDefault(h => h.Alive && h.Owner == me.Index && h.DefId == c.Id);
        if (hired != null)
        {
            if (c.Forced) _role[hired.Id] = HeroRole.Commander;
            else if (UnhelpedCommander(g, me.Index) is { } cmd && cmd.Id != hired.Id)
            {
                _role[hired.Id] = HeroRole.Collector;
                _partner[hired.Id] = cmd.Id;
                _partner[cmd.Id] = hired.Id;
            }
        }
        return true;
    }

    /// <summary>FUN_00866e40: the first own commander that no collector / supplier has as its partner.</summary>
    HeroState? UnhelpedCommander(Engine.Adventure g, int me) =>
        g.S.Heroes.FirstOrDefault(h => h.Alive && h.Owner == me && _role.GetValueOrDefault(h.Id) == HeroRole.Commander &&
            !g.S.Heroes.Any(x => x.Alive && x.Owner == me && _role.GetValueOrDefault(x.Id) is HeroRole.Collector or HeroRole.Supplier &&
                                 _partner.GetValueOrDefault(x.Id, -1) == h.Id));

    /// <summary>
    /// Hire score (FUN_0086aa50): over the limit only a hero the AI settings mark (forced, coefficient 10.0) counts;
    /// hv = hero value (FUN_004eae60) · coef[race]; per own town (index order; settings: AI hiring enabled there, the
    /// player's and the town's hiring filters; not <see cref="TownInDanger"/>; the hire check 004f4570 without money):
    /// total = hire price at the town + the tavern's cost when the town has none (then the tavern must be buildable
    /// today), which must be affordable; s = f · hv / (resource value of total + 1), f = 1 for a hero of the town's race,
    /// else 0.1; the best s wins, the later town on ties (s ≥ best).
    /// UNVERIFIED-detail: "forced" (settings vf 0x88) is taken as the hero being in the AI filter's allowed names.
    /// </summary>
    (double Score, TownState Town, bool Tavern, bool Forced)? HireScore(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff,
        List<TownState> towns, string id, double[] coef, bool overLimit)
    {
        var me = g.Current;
        var def = g.Core.Heroes[id];
        int ri = Array.IndexOf(TownTypes, def.Town);
        if (ri < 0) return null;
        bool forced = me.AiHireFilter.AllowedHeroes.Contains(id);
        if (overLimit && !forced) return null;
        double c = forced ? 10.0 : coef[ri];
        var sim = Simulation(g);
        var preview = sim.TavernHeroPreview(id, me.Index, towns.Count > 0 ? g.EntranceOf(towns[0]) : default);
        double hv = (double)HeroValue(sim, st, preview) * c;
        (double, TownState, bool)? best = null;
        foreach (var t in towns)
        {
            if (me.AiHiringDisabledTowns.Contains(t.Object)) continue;
            if (!Engine.Adventure.FilterAccepts(g.Core, me.AiHireFilter, id) || !Engine.Adventure.FilterAccepts(g.Core, t.HireFilter, id)) continue;
            if (TownInDanger(g, st, diff, preview, t)) continue;
            var total = new Purse();
            total[Purse.Gold] = g.HireCost(me, t);
            bool tavern = t.Level("TB_TAVERN") <= 0;
            if (tavern)
            {
                if (t.BuiltToday || !t.MaxLevel.TryGetValue("TB_TAVERN", out int lim) || lim < 1) continue;
                if (g.Db.Building(t.Type, "TB_TAVERN", 1) is not { } tb) continue;
                total.Add(tb.Cost);
            }
            if (!Enumerable.Range(0, 7).All(i => me.Purse[i] >= total[i])) continue;
            // The engine's hire also needs a free town entrance (HireHeroCmd).
            var gate = g.EntranceOf(t);
            if (g.S.Heroes.Any(x => x.Alive && x.Pos == gate && !(x.InTown >= 0 && x.Garrisoned))) continue;
            double f = def.Town == t.Type ? 1.0 : 0.1;
            double s = f * hv / ((double)ResourceValue(g, me.Index, total) + 1.0);
            if (best == null || !(s < best.Value.Item1)) best = (s, t, tavern);
        }
        if (best is not { } b) return null;
        return (b.Item1, b.Item2, b.Item3, forced);
    }

    /// <summary>
    /// Race coefficients (FUN_008683f0): P[race] = creature power (ftol(max(Power, 1) · 100) · count) of the own heroes'
    /// armies, the garrisons of towns without a garrison hero, 0.1 × the dwellings' current stock and one week of growth
    /// after building every buildable building once (on a copy, unlimited resources); H[race] = own heroes of that race;
    /// both normalised by max(1, max |v|); coef = P / H, or P + 1 for a race without heroes.
    /// </summary>
    public static double[] RaceCoefficients(Engine.Adventure g, int me)
    {
        int n = TownTypes.Length;
        var P = new double[n];
        var H = new double[n];
        void AddCre(string? id, long count, double factor)
        {
            if (id == null || !g.Core.Creatures.TryGetValue(id, out var c)) return;
            int r = Array.IndexOf(TownTypes, c.Town);
            if (r < 0) return;
            P[r] += (double)((long)(Math.Max((double)c.Power, 1.0) * 100.0) * count) * factor;
        }
        foreach (var h in g.S.Heroes.Where(h => h.Alive && h.Owner == me))
        {
            foreach (var s in h.Army.Live()) AddCre(s.Creature, s.Count, 1.0);
            int r = Array.IndexOf(TownTypes, h.Town);
            if (r >= 0) H[r] += 1.0;
        }
        foreach (var t in g.S.Towns.Where(t => t.Owner == me))
        {
            if (TownHeroes(g, t).Garrison == null) foreach (var s in t.Garrison.Live()) AddCre(s.Creature, s.Count, 1.0);
            for (int tier = 1; tier <= 7; tier++) AddCre(TopCreature(g, t, tier), t.Available[tier - 1], 0.1);
            var sim = Simulation(g);
            var st2 = sim.S.Town(t.Object)!;
            var rich = sim.Player(me)!;
            foreach (var key in st2.MaxLevel.Keys.ToList())
            {
                rich.Purse.R = Enumerable.Repeat(1000000, 7).ToArray();
                st2.BuiltToday = false;
                var r = sim.BuildCheck(st2, key, rich.Purse);
                if (r is BuildDenial.CanBuild or BuildDenial.NoMoney) sim.Apply(new BuildCmd(st2.Object, key) { Player = me });
            }
            for (int tier = 1; tier <= 7; tier++)
                if (st2.Level($"TB_DWELLING_{tier}") > 0) AddCre(TopCreature(sim, st2, tier), sim.WeeklyGrowthOf(st2, tier), 1.0);
        }
        Normalise(P);
        Normalise(H);
        var coef = new double[n];
        for (int i = 0; i < n; i++) coef[i] = H[i] > 0.0 ? P[i] / H[i] : P[i] + 1.0;
        return coef;
    }

    static string? TopCreature(Engine.Adventure g, TownState t, int tier) =>
        g.DwellingCreature(t, tier, 2) ?? g.DwellingCreature(t, tier, 1) ?? g.DwellingCreature(t, tier, 0);

    /// <summary>FUN_00866a50: v · (1 / max(1, max |v|)).</summary>
    static void Normalise(double[] v)
    {
        double m = 1.0;
        foreach (var x in v) m = Math.Max(m, Math.Abs(x));
        double k = 1.0 / m;
        for (int i = 0; i < v.Length; i++) v[i] *= k;
    }
}
