"""SKILLS audit patch for src/H5.Combat/Battle.Stats.cs: Death Scream luck penalty per exe FUN_00989c40."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8', newline='').read()
nl = '\r\n' if '\r\n' in t else '\n'
t = t.replace('\r\n', '\n')
old = '''        if (u.HasEffect(H5.Combat.Fx.BansheeHowl)) l -= 1;
        return l;'''
new = '''        if (u.GetEffect(H5.Combat.Fx.BansheeHowl) is { } howl) l -= DeathScreamLuckPenalty(howl);
        return l;'''
if old not in t:
    raise SystemExit('death scream luck pattern not found')
t = t.replace(old, new, 1)
open(p, 'w', encoding='utf-8', newline='').write(t.replace('\n', nl))
