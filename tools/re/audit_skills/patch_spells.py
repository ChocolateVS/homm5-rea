"""SKILLS audit patch for src/H5.Combat/Battle.Spells.cs: Cold Death and Exorcism act on the spell damage before
protection (exe FUN_0097d600 → FUN_00977a80 → FUN_0097d2a0)."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8', newline='').read()
nl = '\r\n' if '\r\n' in t else '\n'
t = t.replace('\r\n', '\n')

def rep(old, new):
    global t
    if old not in t:
        raise SystemExit(f'pattern not found: {old[:90]!r}')
    t = t.replace(old, new, 1)

rep('''            dmg = ExeChop(dmg * (double)(float)Stats.Get("combat/Creatures/Neutral/SpellDamageMultiplier", 0.3));
        if (!ignoreResist)
        {''',
'''            dmg = ExeChop(dmg * (double)(float)Stats.Get("combat/Creatures/Neutral/SpellDamageMultiplier", 0.3));
        dmg = PreProtectionPerks(dmg, t, sp, hero);
        if (!ignoreResist)
        {''')
rep('''            if (hero.HasPerk(Sk.Exorcism) && t.Summoned && sp.School == "MAGIC_SCHOOL_DESTRUCTIVE") dmg *= 2;
''', '')
rep('''            if (hero.HasPerk(Sk.ColdDeath) && id is "SPELL_ICE_BOLT" or "SPELL_FROST_RING" or "SPELL_DEEP_FREEZE" && !ElementImmune(t, Elements.Water))
                dmg += MaxHealthOf(t) * (ignoreResist ? 1 : SpellDamageMultiplier(t, sp, side));
''', '')
open(p, 'w', encoding='utf-8', newline='').write(t.replace('\n', nl))
