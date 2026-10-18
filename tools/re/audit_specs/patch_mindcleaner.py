"""Mind Cleaner (exe 00983790 @ 0098431f-009843f2): after an enemy hero's spell resolves, an ability roll of
100/max(1, circle+1) − caster level + Mind Cleaner level blocks that spell in the caster's book for the combat."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8').read().replace('\r\n', '\n')
a_old = '''        return set.Distinct();
    }

    public Mastery HeroMastery(HeroState h, SpellDef sp)'''
a_new = '''        // Mind Cleaner blocks (exe 00983790 → caster hero vf 0x2b8): unusable for the rest of the combat.
        return set.Distinct().Where(s => !h.AbilityUses.ContainsKey(MindCleanerBlock + s));
    }

    const string MindCleanerBlock = "MIND_CLEANER:";

    /// <summary>
    /// Mind Cleaner (exe 00983790 @ 0098431f-009843f2): chance = 100 / max(1, circle + 1) − caster level + Mind Cleaner
    /// level (integer division), exe ability roll Range(0,100) &lt; chance; success blocks the spell for the caster.
    /// </summary>
    internal void MindCleanerCheck(HeroState caster, SpellDef sp)
    {
        var mc = S.Heroes[1 - caster.Side];
        if (!mc.Present || mc.Specialization != "HERO_SPEC_MIND_CLEANER") return;
        int chance = 100 / Math.Max(1, sp.Level + 1) - caster.Level + mc.Level;
        var anyUnit = S.Units.FirstOrDefault(x => x.Alive);
        if (anyUnit == null || !RollPercent(anyUnit, chance, secondRoll: false)) return;
        caster.AbilityUses[MindCleanerBlock + sp.Id] = 1;
        Emit(new AbilityTriggered(Actor.Hero(mc.Side), "HERO_SPEC_MIND_CLEANER", Actor.Hero(caster.Side)));
    }

    public Mastery HeroMastery(HeroState h, SpellDef sp)'''
b_old = '''            ResolveSpell(c.ActorId, h.Side, sp, mastery, power, target, c.Cell, fromHero: true);
            _spellDamageScale = 1;'''
b_new = '''            ResolveSpell(c.ActorId, h.Side, sp, mastery, power, target, c.Cell, fromHero: true);
            _spellDamageScale = 1;
            MindCleanerCheck(h, sp);'''
for old, new in [(a_old, a_new), (b_old, b_new)]:
    if new in t: continue
    if t.count(old) != 1: sys.exit('anchor not unique/found: ' + old[:50])
    t = t.replace(old, new)
open(p, 'w', encoding='utf-8', newline='').write(t)
