import sys
p=sys.argv[1]; t=open(p,encoding='utf-8',newline='').read()
nl='\r\n' if '\r\n' in t else '\n'
old='''                Emit(new SpellResisted(target?.Id ?? c.ActorId, sp.Id));
                EndAction(c.ActorId, HeroCastAtbReset(h, sp), false);'''.replace('\n',nl)
new='''                Emit(new SpellResisted(target?.Id ?? c.ActorId, sp.Id));
                // exe 00978670 / FUN_00977260: a countered caster with Dark Renewal (HERO_SKILL_PAYBACK) and a
                // non-mass spell keeps ATB 0.2 + 0.1·Sorcery mastery ("Payback!"); otherwise the normal reset.
                double counteredReset = h.HasPerk(Sk.DarkRenewal) && !IsMass(sp.Id)
                    ? 0.2f + 0.1f * (int)h.SkillLevel("HERO_SKILL_SORCERY") : HeroCastAtbReset(h, sp);
                EndAction(c.ActorId, counteredReset, false);'''.replace('\n',nl)
assert t.count(old)==1
open(p,'w',encoding='utf-8',newline='').write(t.replace(old,new))
