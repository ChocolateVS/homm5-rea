"""HEROES audit: Offence/Defence mastery switches handle levels 1..3 only (exe 00a58de0 / 00a5a900). Usage: <file>"""
import sys
p = sys.argv[1]; t = open(p, encoding='utf-8').read()
for skill in ('SkillOffence/ExpertDamageBonus', 'SkillDefence/ExpertDamageReduce'):
    old = f'        >= Mastery.Expert => Stats.Get("combat/HeroSkills/{skill}"'
    new = f'        Mastery.Expert => Stats.Get("combat/HeroSkills/{skill}"'
    if new in t: continue
    if old not in t: sys.exit('anchor missing: ' + skill)
    t = t.replace(old, new, 1)
anchor = '    double OffenceBonus(HeroState h) => h.SkillLevel(Sk.Offence) switch'
note = '    // exe 00a58de0 / 00a5a900: the mastery switch has cases 1..3 only — any other level gives no bonus.\n'
if note not in t: t = t.replace(anchor, note + anchor, 1)
open(p, 'w', encoding='utf-8').write(t); print('patched', p)
