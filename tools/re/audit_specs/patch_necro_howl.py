"""Death Scream morale penalty per exe 00989fd0 case 0x3b: trunc(MoralePenalty * f), f = (Dark Emissary ?
DeathScreamMultiplier : 1) + (2+ Necromancers pieces ? BansheeWhail_DeMorale : 0); Howl of Terror −6."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8').read().replace('\r\n', '\n')
old = '''                double howl = 1;
                if (h.Specialization == "HERO_SPEC_DARK_EMISSARY") howl *= Stats.Get("combat/HeroSkills/Necromancer/DarkEissary_DeathScreamMultiplier", 2);
                if (Specials.Pieces(h, Specials.Necromancers) >= 2) howl = Math.Max(howl, 2);
                foreach (var u in S.LivingOf(1 - c.Side).Where(IsLiving).ToList())
                    AddEffect(u, new Effect { Id = H5.Combat.Fx.BansheeHowl, Value = howl + (h.HasPerk(Sk.HowlOfTerror) ? 6 : 0), Value2 = howl,'''
new = '''                double howl = 1;
                if (h.Specialization == "HERO_SPEC_DARK_EMISSARY") howl *= Stats.Get("combat/HeroSkills/Necromancer/DarkEissary_DeathScreamMultiplier", 2);
                if (Specials.Pieces(h, Specials.Necromancers) >= 2) howl = Math.Max(howl, 2);
                int moralePenalty = DeathScreamMoralePenalty(h);
                foreach (var u in S.LivingOf(1 - c.Side).Where(IsLiving).ToList())
                    AddEffect(u, new Effect { Id = H5.Combat.Fx.BansheeHowl, Value = moralePenalty + (h.HasPerk(Sk.HowlOfTerror) ? 6 : 0), Value2 = howl,'''
helper_anchor = '''    void HeroStrike(HeroState h, Unit t, int levelBonus, bool friendly, double extraMult = 1, bool plain = false, bool powerfulBlow = false)'''
helper = '''    /// <summary>
    /// Death Scream morale penalty (exe 00989fd0 case 0x3b): trunc(DeathScream_MoralePenalty · f) with f = the Dark
    /// Emissary multiplier (else 1) plus BansheeWhail_DeMorale when the screaming hero has 2+ Necromancers pieces
    /// (Howl of Terror's −6 is added separately).
    /// </summary>
    internal int DeathScreamMoralePenalty(HeroState h)
    {
        double f = (h.Specialization == "HERO_SPEC_DARK_EMISSARY" ? Stats.Get("combat/HeroSkills/Necromancer/DarkEissary_DeathScreamMultiplier", 2) : 1)
                   + (Specials.Pieces(h, Specials.Necromancers) > 1 ? (int)Stats.Get("ArtifactsSetsEffectsConsts/Necromancers_2Necromancer_BansheeWhail_DeMorale", 1) : 0);
        return (int)((int)Stats.Get("combat/HeroSkills/Necromancer/DeathScream_MoralePenalty", 1) * f);
    }

''' + helper_anchor
if 'internal int DeathScreamMoralePenalty' not in t:
    if t.count(helper_anchor) != 1: sys.exit('helper anchor not found')
    t = t.replace(helper_anchor, helper)
if new in t: open(p, 'w', encoding='utf-8', newline='').write(t); sys.exit(0)
if old not in t: sys.exit('anchor not found')
open(p, 'w', encoding='utf-8', newline='').write(t.replace(old, new))
