"""Sacred Hammer: ability roll of trunc(100*clamp(Base + level*PerLevel, 0, 1)) (exe 00bbebd0)."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8').read().replace('\r\n', '\n')
old = '''        double p = Stats.Get("combat/HeroSpecializations/Runemage/SacredHammer_ChanceToCastBase", 0.2) + Stats.Get("combat/HeroSpecializations/Runemage/SacredHammer_ChanceToCastPerLevel", 0.03) * l;
        var spellId = Stats.Str("combat/HeroSpecializations/Runemage/SacredHammer_SpellToCast", "SPELL_BLOODLUST");
        if (S.Rng.Chance(p) && Db.Spell(spellId) is { } sp)'''
new = '''        // exe 00bbebd0: chance = trunc(100·clamp(Base + level·PerLevel, 0, 1)), ability roll Range(0,100) < chance.
        int chance = SacredHammerChance(l);
        var spellId = Stats.Str("combat/HeroSpecializations/Runemage/SacredHammer_SpellToCast", "SPELL_BLOODLUST");
        if (RollPercent(target, chance, secondRoll: false) && Db.Spell(spellId) is { } sp)'''
helper_anchor = '''    /// <summary>Sacred Hammer: light spells on friends may also cast a free Righteous Might.</summary>'''
helper = '''    /// <summary>Sacred Hammer chance (exe 00bbebd0): trunc(100·clamp(ChanceToCastBase + level·PerLevel, 0, 1)).</summary>
    internal int SacredHammerChance(int level) =>
        (int)(100 * Math.Clamp(Stats.Get("combat/HeroSpecializations/Runemage/SacredHammer_ChanceToCastBase", 0.2)
                               + level * Stats.Get("combat/HeroSpecializations/Runemage/SacredHammer_ChanceToCastPerLevel", 0.03), 0, 1));

''' + helper_anchor
if 'internal int SacredHammerChance' not in t:
    if t.count(helper_anchor) != 1: sys.exit('helper anchor not found')
    t = t.replace(helper_anchor, helper)
if new in t: open(p, 'w', encoding='utf-8', newline='').write(t); sys.exit(0)
if t.count(old) != 1: sys.exit('anchor not found')
open(p, 'w', encoding='utf-8', newline='').write(t.replace(old, new))
