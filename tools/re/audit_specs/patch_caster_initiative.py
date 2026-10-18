"""Expediter / White Knight initiative factors follow the effect's caster hero (exe 008a4330 → 004e4270)."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8').read().replace('\r\n', '\n')
old = '''        if (u.GetEffect("SPELL_HASTE") is { } haste)
        {
            double hf = 1 + Math.Max(0, haste.Value);
            if (h.Present && h.Specialization == "HERO_SPEC_HASTE")
                hf += h.Level * Math.Clamp(Stats.Get("combat/HeroSpecializations/Knight/Expediter_HasteBonusPerLevel", 0.01), 0, 100);
            f *= hf;
        }
        if (u.GetEffect("SPELL_HYPNOTIZE") is { } hyp) f *= Math.Clamp(hyp.Value, 0, 1);
        if (u.HasEffect(H5.Combat.Fx.Benediction))
        {
            double bonus = Stats.Get("combat/HeroSkills/Knight/Prayer_InitiativeBonus", 0.1);
            if (h.Present && h.Specialization == "HERO_SPEC_WHITE_KNIGHT") bonus *= Stats.Get("combat/HeroSkills/Knight/WhiteKnight_PrayerMultiplier", 2);
            f *= 1 + bonus;
        }'''
new = '''        // 008a4330 passes each effect's own caster hero (effect vf 0xc) to 004e4270, so Expediter and
        // White Knight follow the hero who cast the effect, not the creature's owner.
        HeroState Caster(Effect e) => e.SourceSide >= 0 ? S.Heroes[e.SourceSide] : h;
        if (u.GetEffect("SPELL_HASTE") is { } haste)
        {
            double hf = 1 + Math.Max(0, haste.Value);
            var ch = Caster(haste);
            if (ch.Present && ch.Specialization == "HERO_SPEC_HASTE")
                hf += ch.Level * Math.Clamp(Stats.Get("combat/HeroSpecializations/Knight/Expediter_HasteBonusPerLevel", 0.01), 0, 100);
            f *= hf;
        }
        if (u.GetEffect("SPELL_HYPNOTIZE") is { } hyp) f *= Math.Clamp(hyp.Value, 0, 1);
        if (u.GetEffect(H5.Combat.Fx.Benediction) is { } prayer)
        {
            double bonus = Stats.Get("combat/HeroSkills/Knight/Prayer_InitiativeBonus", 0.1);
            var ch = Caster(prayer);
            if (ch.Present && ch.Specialization == "HERO_SPEC_WHITE_KNIGHT") bonus *= Stats.Get("combat/HeroSkills/Knight/WhiteKnight_PrayerMultiplier", 2);
            f *= 1 + bonus;
        }'''
if new in t: sys.exit(0)
if old not in t: sys.exit('anchor not found')
open(p, 'w', encoding='utf-8', newline='').write(t.replace(old, new))
