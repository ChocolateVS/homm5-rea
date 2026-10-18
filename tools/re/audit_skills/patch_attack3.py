"""SKILLS audit patch for src/H5.Combat/Battle.Attack.cs: battle-start rage per exe FUN_008aaec0 (Absolute Rage replaces
Memory of our Blood; Memory's amount from the DemonRage data)."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8', newline='').read()
nl = '\r\n' if '\r\n' in t else '\n'
t = t.replace('\r\n', '\n')
old = '''                if (mine.HasPerk(Sk.MemoryOfBlood)) AddRage(u, 10 * S.Rng.Range(5, 10));
                if (mine.HasPerk(Sk.AbsoluteRage)) AddRage(u, (int)Stats.Get("combat/Creatures/Stronghold/DemonRage/AbsoluteRageRP", 500));'''
new = '''                // Battle start (exe FUN_008aaec0): Absolute Rage gives AbsoluteRageRP and then Memory of our Blood does
                // nothing; otherwise Memory of our Blood gives Range(MinRP/Step, MaxRP/Step)·Step.
                if (mine.HasPerk(Sk.AbsoluteRage)) AddRage(u, (int)Stats.Get("combat/Creatures/Stronghold/DemonRage/AbsoluteRageRP", 500));
                else if (mine.HasPerk(Sk.MemoryOfBlood)) AddRage(u, MemoryOfBloodRage());'''
if old not in t:
    raise SystemExit('rage pattern not found')
t = t.replace(old, new, 1)
open(p, 'w', encoding='utf-8', newline='').write(t.replace('\n', nl))
