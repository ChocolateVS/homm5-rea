"""SKILLS audit patch for src/H5.Combat/Battle.Stats.cs: Familiar Ground speed bonus in SpeedOf (exe FUN_008a0e30)."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8', newline='').read()
nl = '\r\n' if '\r\n' in t else '\n'
t = t.replace('\r\n', '\n')
old = '''        if (u.HasEffect(H5.Combat.Fx.Battlecry)) sp += Stats.Get("combat/Spells/Warcries/Battlecry_SpeedBonus/Base", 1);'''
new = '''        sp += RoadHomeSpeedBonus(u);   // Familiar Ground (exe FUN_008a0e30)
        if (u.HasEffect(H5.Combat.Fx.Battlecry)) sp += Stats.Get("combat/Spells/Warcries/Battlecry_SpeedBonus/Base", 1);'''
if old not in t:
    raise SystemExit('speed pattern not found')
t = t.replace(old, new, 1)
open(p, 'w', encoding='utf-8', newline='').write(t.replace('\n', nl))
