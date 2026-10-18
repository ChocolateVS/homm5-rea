"""Feather Mage hero initiative: 1 + clamp(Base + level*PerLevel, 0, 5) (exe FUN_00bc1eb0)."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8').read().replace('\r\n', '\n')
old = '''        return l < 0 ? 1 : 1 + Stats.Get("combat/HeroSpecializations/Wizard/FeatherMage_InitiativeBonusPerLevel", 0.005) * l;'''
new = '''        // exe FUN_00bc1eb0: 1 + clamp(FeatherMage_InitiativeBonusBase + level·PerLevel, 0, 5)
        return l < 0 ? 1 : 1 + Math.Clamp(Stats.Get("combat/HeroSpecializations/Wizard/FeatherMage_InitiativeBonusBase", 0)
                                          + l * Stats.Get("combat/HeroSpecializations/Wizard/FeatherMage_InitiativeBonusPerLevel", 0.005), 0, 5);'''
if new in t: sys.exit(0)
if t.count(old) != 1: sys.exit('anchor not found')
open(p, 'w', encoding='utf-8', newline='').write(t.replace(old, new))
