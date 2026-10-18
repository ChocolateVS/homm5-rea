"""Lions set (exe 008cf7ff-008cf86d, 00989fd0 case 0x129): a Knight hero (class 1) with 2+ Lions pieces marks every
target of its strikes with SPELL_ARTFSET_LIONS_DEMORALIZED (once, permanent); the mark lowers morale by
Lions_2_HeroAttack_DeMorale. Usage: patch_lions.py <file> abilities|stats"""
import sys
p, which = sys.argv[1], sys.argv[2]
t = open(p, encoding='utf-8').read().replace('\r\n', '\n')
if which == 'abilities':
    old = '''        RageEvent(t, "ReceiveHeroAttackRP");
        if (!t.Alive || friendly) return;'''
    new = '''        RageEvent(t, "ReceiveHeroAttackRP");
        if (!t.Alive || friendly) return;
        LionsMark(h, t);'''
    m_anchor = '''    void HeroStrike(HeroState h, Unit t, int levelBonus, bool friendly, double extraMult = 1, bool plain = false, bool powerfulBlow = false)'''
    method = '''    /// <summary>Lions set (exe 008cf7ff): a Knight (class 1) with 2+ Lions pieces marks the strike's target with
    /// SPELL_ARTFSET_LIONS_DEMORALIZED (0x129) — permanent, never stacked.</summary>
    internal void LionsMark(HeroState h, Unit t)
    {
        if (h.Class == "HERO_CLASS_KNIGHT" && Specials.Pieces(h, Specials.Lions) >= 2 && !t.HasEffect(LionsDemoralized))
            AddEffect(t, new Effect { Id = LionsDemoralized, SourceSide = h.Side, Dispellable = false });
    }

''' + m_anchor
    if 'internal void LionsMark' not in t:
        if t.count(m_anchor) != 1: sys.exit('lions method anchor not found')
        t = t.replace(m_anchor, method)
else:
    old = '''        if (u.HasEffect(H5.Combat.Fx.BansheeHowl)) m -= (int)Fx(u, H5.Combat.Fx.BansheeHowl);'''
    new = '''        if (u.HasEffect(H5.Combat.Fx.BansheeHowl)) m -= (int)Fx(u, H5.Combat.Fx.BansheeHowl);
        if (u.HasEffect(LionsDemoralized)) m -= (int)Stats.Get("ArtifactsSetsEffectsConsts/Lions_2_HeroAttack_DeMorale", 2);   // exe 00989fd0 case 0x129'''
    anchor = '''    internal const string NecromancersDebuff = "SPELL_ARTFSET_NECROMANCERS_DEBUFF";'''
    if anchor not in t: sys.exit('apply patch_necro_stats first')
    if 'LionsDemoralized =' not in t:
        t = t.replace(anchor, anchor + '\n    internal const string LionsDemoralized = "SPELL_ARTFSET_LIONS_DEMORALIZED";')
if new in t:
    open(p, 'w', encoding='utf-8', newline='').write(t); sys.exit(0)
if t.count(old) != 1: sys.exit('anchor not unique/found')
open(p, 'w', encoding='utf-8', newline='').write(t.replace(old, new))
