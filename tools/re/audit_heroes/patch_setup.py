"""HEROES audit: creature HP specialisations per exe FUN_004bc470. Usage: <BattleSetup.cs>"""
import sys
p = sys.argv[1]; t = open(p, encoding='utf-8').read()
old = """            if (h.Specialization == "HERO_SPEC_WYVERN_TAMER" && (u.CreatureId.Contains("WYVERN") || u.CreatureId.Contains("PAOKAI")))
                u.BaseHealth += (int)Specials.LevelFormula(db.Stats, "combat/HeroSpecializations/Barbarian/WyvernTamer_HealthBonus", h.Level, 1, 1, 2);
"""
new = """            // Creature HP specialisations (exe FUN_004bc470, keyed on the base creature): Wyvern Tamer
            // +((level−1)/LevelDivisor)·PerDividedLevel + Base for Wyverns; Defender +level/5 for Defenders.
            string hpBase = c.BaseCreature is { Length: > 0 } bc && bc != "CREATURE_UNKNOWN" ? bc : c.Id;
            if (h.Specialization == "HERO_SPEC_WYVERN_TAMER" && hpBase == "CREATURE_WYVERN")
            {
                const string w = "combat/HeroSpecializations/Barbarian/WyvernTamer_HealthBonus";
                int div = Math.Max(1, (int)db.Stats.Get(w + "/LevelDivisor", 1));
                u.BaseHealth += (h.Level - 1) / div * (int)db.Stats.Get(w + "/PerDividedLevel", 2) + (int)db.Stats.Get(w + "/Base", 1);
            }
            if (h.Specialization == "HERO_SPEC_DEFENDER" && hpBase == "CREATURE_DEFENDER") u.BaseHealth += h.Level / 5;
"""
if new not in t:
    if old not in t: sys.exit('anchor missing')
    t = t.replace(old, new, 1)
open(p, 'w', encoding='utf-8').write(t); print('patched', p)
