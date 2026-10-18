"""Necromancers set in the stat functions (exe 008a4800 / 008a22f0 / 004e4270)."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8').read().replace('\r\n', '\n')

# Attack / defence: with the debuff the final value is trunc(value * (1 - pct/100)) (008a4800 / 008a22f0).
a_old = '''        if (h.HasPerk(Sk.OffensiveFormation)) a += FormationBonus(u, Stats.Get("combat/HeroSkills/Runemage/Offensive_MaxOffenseBonus", 10));
        return Math.Max(0, (int)Math.Floor(a));'''
a_new = '''        if (h.HasPerk(Sk.OffensiveFormation)) a += FormationBonus(u, Stats.Get("combat/HeroSkills/Runemage/Offensive_MaxOffenseBonus", 10));
        if (u.HasEffect(NecromancersDebuff)) a = NecromancersDebuffed(Math.Floor(a));
        return Math.Max(0, (int)Math.Floor(a));'''
d_old = '''                d *= Math.Max(0, 1 - Stats.Get("combat/Creatures/Dungeon/RiderCharge_DefenceReducePerPathCell", 0.2) * attackerCellsWalked);
        }
        return Math.Max(0, (int)Math.Floor(d));'''
d_new = '''                d *= Math.Max(0, 1 - Stats.Get("combat/Creatures/Dungeon/RiderCharge_DefenceReducePerPathCell", 0.2) * attackerCellsWalked);
        }
        if (u.HasEffect(NecromancersDebuff)) d = NecromancersDebuffed(Math.Floor(d));
        return Math.Max(0, (int)Math.Floor(d));'''
# Death Scream initiative: 1 - penalty * (caster has 2+ Necromancers pieces ? DeInitiativeMultiplier : 1) (004e4270).
i_old = '''        if (u.HasEffect(H5.Combat.Fx.BansheeHowl)) f *= 1 - Stats.Get("combat/HeroSkills/Necromancer/DeathScream_InitiativePenalty", 0.1);'''
i_new = '''        if (u.GetEffect(H5.Combat.Fx.BansheeHowl) is { } scream)
        {
            var ch = Caster(scream);
            double mult = ch.Present && Specials.Pieces(ch, Specials.Necromancers) > 1
                ? (int)Stats.Get("ArtifactsSetsEffectsConsts/Necromancers_2Necromancer_BansheeWhail_DeInitiativeMultiplier", 2) : 1;
            f *= 1 - mult * Stats.Get("combat/HeroSkills/Necromancer/DeathScream_InitiativePenalty", 0.1);
        }'''
helper_anchor = '''    int AmmoCartAttackBonus(int side)'''
helper = '''    internal const string NecromancersDebuff = "SPELL_ARTFSET_NECROMANCERS_DEBUFF";

    /// <summary>Necromancers 4-piece debuff: trunc((1 − pct·0.01)·value) (exe 008a4800 / 008a22f0, pct int at RPGStats+0x1198).</summary>
    double NecromancersDebuffed(double value) =>
        (int)((1 - (int)Stats.Get("ArtifactsSetsEffectsConsts/Necromancers_4_EnemyCreature_BadMoralePenaltyToAttackDefencePercents", 20) * 0.01) * value);

    int AmmoCartAttackBonus(int side)'''
for old, new in [(a_old, a_new), (d_old, d_new), (i_old, i_new), (helper_anchor, helper)]:
    if new in t: continue
    if t.count(old) != 1: sys.exit('anchor not unique/found: ' + old[:60])
    t = t.replace(old, new)
open(p, 'w', encoding='utf-8', newline='').write(t)
