"""Bombardier: Fireball at fixed Expert mastery, power = (level-1)*PerLevel/Divisor + Base (exe 0085f0f0)."""
import sys
p = sys.argv[1]
t = open(p, encoding='utf-8').read().replace('\r\n', '\n')
old = '''        double power = Stats.Get("combat/HeroSpecializations/Demonlord/Bombardier_FireballSpellpower", -1)
                       + Math.Floor(l / Math.Max(1, Stats.Get("combat/HeroSpecializations/Demonlord/Bombardier_FireballSpellpowerLevelDivisor", 5)))
                       * Stats.Get("combat/HeroSpecializations/Demonlord/Bombardier_FireballSpellpowerPerLevel", 1);
        ResolveSpell(ballista.Id, ballista.Side, fb, HeroMastery(h, fb), Math.Max(0, power), null, target.Pos, fromHero: false);'''
new = '''        // exe 0085f0f0: cast at a fixed mastery 3 (Expert) with BombardierPower(level).
        ResolveSpell(ballista.Id, ballista.Side, fb, Mastery.Expert, BombardierPower(l), null, target.Pos, fromHero: false);
    }

    /// <summary>Bombardier fireball power (exe 0085f0f0): integer (level − 1)·PerLevel / Divisor + Base, not clamped.</summary>
    internal int BombardierPower(int level)
    {
        int div = Math.Max(1, (int)Stats.Get("combat/HeroSpecializations/Demonlord/Bombardier_FireballSpellpowerLevelDivisor", 5));
        return (level - 1) * (int)Stats.Get("combat/HeroSpecializations/Demonlord/Bombardier_FireballSpellpowerPerLevel", 1) / div
               + (int)Stats.Get("combat/HeroSpecializations/Demonlord/Bombardier_FireballSpellpower", -1);'''
if new in t: sys.exit(0)
if old not in t: sys.exit('anchor not found')
open(p, 'w', encoding='utf-8', newline='').write(t.replace(old, new))
