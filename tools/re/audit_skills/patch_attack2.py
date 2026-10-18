"""SKILLS audit patch for src/H5.Combat/Battle.Attack.cs: Fiery Wrath amount rounded like FUN_0097d740 (exe FUN_00a5ed90)."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8', newline='').read()
nl = '\r\n' if '\r\n' in t else '\n'
t = t.replace('\r\n', '\n')
old = '''                DealElementalDamage(t, (int)(dmg * 0.1), Elements.Fire, a.Id, "FIERY_WRATH");'''
new = '''                DealElementalDamage(t, SpellLikeAmount(dmg * (double)0.1f), Elements.Fire, a.Id, "FIERY_WRATH");   // exe FUN_00a5ed90'''
if old not in t:
    raise SystemExit('fiery wrath pattern not found')
t = t.replace(old, new, 1)
open(p, 'w', encoding='utf-8', newline='').write(t.replace('\n', nl))
