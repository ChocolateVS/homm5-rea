"""Necromancers 4-piece: a stack failing its morale check gets SPELL_ARTFSET_NECROMANCERS_DEBUFF until its next
turn start (exe 008a8140 adds effect 0xce when the opposing hero has > 3 pieces; 008a6c40 removes it)."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8').read().replace('\r\n', '\n')
a_old = '''        RemoveEffect(u, H5.Combat.Fx.TakeRoots);
'''
a_new = '''        RemoveEffect(u, H5.Combat.Fx.TakeRoots);
        RemoveEffect(u, NecromancersDebuff);   // exe 008a6c40: lasts until the stack's next turn start
'''
b_old = '''            Emit(new MoraleProc(u.Id, false));
            RageEvent(u, "BadMoraleRP");'''
b_new = '''            Emit(new MoraleProc(u.Id, false));
            RageEvent(u, "BadMoraleRP");
            // exe 008a8140: the opposing hero's Necromancers set (4+ pieces) cripples a stack that fails its morale.
            if (Specials.Pieces(Enemy(u.Side), Specials.Necromancers) > 3)
                AddEffect(u, new Effect { Id = NecromancersDebuff, SourceSide = 1 - u.Side, Dispellable = false });'''
if a_new not in t:
    if t.count(a_old) != 1: sys.exit('anchor a not unique/found')
    t = t.replace(a_old, a_new)
if b_new not in t:
    if b_old not in t: sys.exit('anchor b not found')
    t = t.replace(b_old, b_new)
open(p, 'w', encoding='utf-8', newline='').write(t)
