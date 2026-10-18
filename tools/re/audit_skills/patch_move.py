"""SKILLS audit patch for src/H5.Combat/Battle.Move.cs: Corrupted Soil (HERO_SKILL_SOIL_BURN) per exe FUN_00a61a60."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8', newline='').read()
nl = '\r\n' if '\r\n' in t else '\n'
t = t.replace('\r\n', '\n')
old = '''        // Corrupted Soil: damage each time an enemy stack moves (p.109).
        if (eh.Present && (eh.HasPerk(Sk.CorruptedSoil) || eh.HasPerk(Sk.BarbCorruptedSoil)))
            DealDamage(u, (int)(Stats.Get("combat/HeroSkills/Ranger/SoilBurnDamagePerHeroLevel", 3) * eh.Level), Actor.Hero(1 - u.Side), AttackKind.Spell, "SOIL_BURN");'''
new = '''        // Corrupted Soil: damage each time an enemy stack moves (p.109); flyers are exempt (exe FUN_00a61a60).
        if (SoilBurnDamage(u) is > 0 and var soil)
            DealDamage(u, soil, Actor.Hero(1 - u.Side), AttackKind.Spell, "SOIL_BURN");'''
if old not in t:
    raise SystemExit('soil burn pattern not found')
t = t.replace(old, new, 1)
open(p, 'w', encoding='utf-8', newline='').write(t.replace('\n', nl))
