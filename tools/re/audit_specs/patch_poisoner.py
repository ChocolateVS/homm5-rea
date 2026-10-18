"""Poisoner: per enemy stack an ability roll of trunc(100*clamp(Base + level*PerLevel, 0, 1)) (exe 009844e0)."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8').read().replace('\r\n', '\n')
old = '''                double p = Stats.Get("combat/HeroSpecializations/Warlock/Poisoner_PlagueProb", 0.1) + Stats.Get("combat/HeroSpecializations/Warlock/Poisoner_PlagueProbPerLevel", 0.02) * pl;
                foreach (var u in S.LivingOf(1 - side).Where(u => u.Kind == UnitKind.Creature).ToList())
                    if (S.Rng.Chance(p)) ApplyEffectSpell(Actor.Hero(side), side, plague, HeroMastery(h, plague), HeroSpellPower(h, plague), u, h.Level, h);'''
new = '''                // exe 009844e0: chance = trunc(100·clamp(Poisoner_PlagueProb + level·PerLevel, 0, 1)), ability roll
                // Range(0,100) < chance per enemy stack (the exe pre-filters valid targets with 00982010 — not ported).
                int chance = PoisonerChance(pl);
                foreach (var u in S.LivingOf(1 - side).Where(u => u.Kind == UnitKind.Creature).ToList())
                    if (RollPercent(u, chance, secondRoll: false)) ApplyEffectSpell(Actor.Hero(side), side, plague, HeroMastery(h, plague), HeroSpellPower(h, plague), u, h.Level, h);'''
helper_anchor = '''    /// <summary>Blooddrinker: each enemy stack killed gives the whole army Attack.</summary>'''
helper = '''    /// <summary>Poisoner chance (exe 009844e0): trunc(100·clamp(Poisoner_PlagueProb + level·PerLevel, 0, 1)).</summary>
    internal int PoisonerChance(int level) =>
        (int)(100 * Math.Clamp(Stats.Get("combat/HeroSpecializations/Warlock/Poisoner_PlagueProb", 0.1)
                               + level * Stats.Get("combat/HeroSpecializations/Warlock/Poisoner_PlagueProbPerLevel", 0.02), 0, 1));

''' + helper_anchor
if 'internal int PoisonerChance' not in t:
    if t.count(helper_anchor) != 1: sys.exit('helper anchor not found')
    t = t.replace(helper_anchor, helper)
if new in t: open(p, 'w', encoding='utf-8', newline='').write(t); sys.exit(0)
if t.count(old) != 1: sys.exit('anchor not found')
open(p, 'w', encoding='utf-8', newline='').write(t.replace(old, new))
