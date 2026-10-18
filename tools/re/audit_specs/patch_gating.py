"""Gating portion per exe FUN_0085fe60 (base, Gating Mastery, Critical Gating, Gate Keeper, Demonic set)."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8').read().replace('\r\n', '\n')
old = '''                var portions = Stats.Array("combat/HeroSkills/DemonLord/Gating/PortionOfSummonedByGatingLevel");
                int lvl = Math.Clamp((int)h.SkillLevel(Sk.Gating) - 1, 0, Math.Max(0, portions.Length - 1));
                double portion = portions.Length > 0 ? portions[lvl] : 0.3;
                if (h.HasPerk(Sk.GateMaster)) portion += Stats.Get("combat/HeroSkills/DemonLord/Gating/GatingMasteryPortionBonus", 0.2);
                if (h.Specialization == "HERO_SPEC_GATE_KEEPER") portion += Stats.Get("combat/HeroSkills/DemonLord/Gating/GatekeeperHeroSpecPerLevelPortionBonus", 0.01) * h.Level;
                if (h.HasPerk(Sk.SwarmingGate) && S.Rng.Chance(0.1 + Math.Max(0, h.Luck) * 0.05)) portion *= 2;
                if (Specials.Pieces(h, Specials.Demonic) >= 2) portion *= 1 + Stats.Get("ArtifactsSetsEffectsConsts/Demonic_2DemonLord_GatingBonusPercents", 25) / 100.0;
                int n = Math.Max(1, (int)(u.Count * portion));'''
new = '''                int n = Math.Max(1, (int)Math.Floor(u.Count * GatingPortion(u, h)));'''
anchor = '''    IEnumerable<Cell> AttackPositionsAnywhere(Unit u, Unit t)'''
method = '''    /// <summary>
    /// Gating portion (exe FUN_0085fe60): base = PortionOfSummonedByGatingLevel[mastery−1] (0.25 unless the table has
    /// 4 entries); additive bonuses: Gating Mastery, Critical Gating (CriticalGatingPortionBonus × base, on an
    /// ability roll of trunc(100·clamp(Const + Luck·PerLuck, 0, 1))), Infernal Looms (town-side, absent in duels) and
    /// Gate Keeper (level × per-level bonus); the Demonic 2-piece set multiplies only the base (×(1 + pct·0.01));
    /// the total is clamped to [0, 1000]. Bonus fields are clamped to [−1, 1000] as the exe does.
    /// </summary>
    internal double GatingPortion(Unit u, HeroState h)
    {
        double Cl(double v) => Math.Clamp(v, -1, 1000);
        const string gp = "combat/HeroSkills/DemonLord/Gating/";
        var portions = Stats.Array(gp + "PortionOfSummonedByGatingLevel");
        int lvl = Math.Clamp((int)h.SkillLevel(Sk.Gating) - 1, 0, Math.Max(0, portions.Length - 1));
        double basePortion = portions.Length == 4 ? portions[lvl] : 0.25;
        double bonus = 0;
        if (h.HasPerk(Sk.GateMaster)) bonus = Cl(Stats.Get(gp + "GatingMasteryPortionBonus", 0.2));
        if (h.HasPerk(Sk.SwarmingGate))
        {
            double chance = Math.Clamp(Stats.Get(gp + "CriticalGatingConstantProb", 0.1), 0, 1);
            if (h.Luck > 0) chance += h.Luck * Math.Clamp(Stats.Get(gp + "CriticalGatingPerLuckPointProb", 0.05), 0, 1);
            if (RollPercent(u, (int)Math.Clamp(chance * 100, 0, 100), secondRoll: false))
                bonus += Cl(Stats.Get(gp + "CriticalGatingPortionBonus", 1)) * basePortion;
        }
        if (h.Specialization == "HERO_SPEC_GATE_KEEPER") bonus += h.Level * Cl(Stats.Get(gp + "GatekeeperHeroSpecPerLevelPortionBonus", 0.01));
        if (Specials.Pieces(h, Specials.Demonic) >= 2)
            basePortion *= 1 + (int)Stats.Get("ArtifactsSetsEffectsConsts/Demonic_2DemonLord_GatingBonusPercents", 25) * 0.01;
        return Math.Clamp(basePortion + bonus, 0, 1000);
    }

    IEnumerable<Cell> AttackPositionsAnywhere(Unit u, Unit t)'''
if new not in t:
    if t.count(old) != 1: sys.exit('anchor not found')
    t = t.replace(old, new)
if 'internal double GatingPortion' not in t:
    if t.count(anchor) != 1: sys.exit('method anchor not found')
    t = t.replace(anchor, method)
open(p, 'w', encoding='utf-8', newline='').write(t)
