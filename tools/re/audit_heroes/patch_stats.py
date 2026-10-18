"""Applies the HEROES audit changes to Battle.Stats.cs text (idempotent). Usage: patch_stats.py <file>"""
import sys
p = sys.argv[1]; t = open(p, encoding='utf-8').read()
old_att = """        if (h.Present && !u.IsWarMachine && !u.IsObject) a += h.Attack;
        if (!u.IsWarMachine && !u.IsObject) a += Side(u).Attack;
"""
new_att = """        if (h.Present && !u.IsWarMachine && !u.IsObject) a += h.Attack;
        if (!u.IsWarMachine && !u.IsObject) a += Side(u).Attack;
        a += NativeTerrainBonus(u);   // exe FUN_008a4800: +1 when the battlefield is the creature race's native terrain
"""
old_def = """        double d = raw;
        if (h.Present && !u.IsWarMachine && !u.IsObject) d += h.Defence;
        if (!u.IsWarMachine && !u.IsObject) d += Side(u).Defence;
        d += Fx(u, "SPELL_STONESKIN");
        d += Fx(u, H5.Combat.Fx.Benediction);
"""
new_def = """        double d = raw;
        if (h.Present && !u.IsWarMachine && !u.IsObject) d += h.Defence;
        if (!u.IsWarMachine && !u.IsObject) d += Side(u).Defence;
        d += Fx(u, H5.Combat.Fx.Benediction);
        d += NativeTerrainBonus(u);
        // Defend / Take Roots posture (exe FUN_008a22f0): bonus = max(1, trunc(d·f + 0.001)) on the base + hero +
        // Prayer + native-terrain defence only (spells and other effects are added afterwards, FUN_008d9390).
        // f is DefenceBonusOnTakeRoots for ABILITY_TAKE_ROOTS creatures, else DefenceBonusOnDefend (not both);
        // Hold Ground multiplies it by the matching HoldGround_* multiplier.
        if (u.Defending || u.HasEffect(H5.Combat.Fx.TakeRoots)) d += DefendPostureBonus(u, h, Math.Floor(d));
        d += Fx(u, "SPELL_STONESKIN");
"""
old_mult = """        double mult = 1;
        if (u.Defending)
        {
            double bonus = Stats.Get("combat/Creatures/DefenceBonusOnDefend", 0.3);
            if (h.HasPerk(Sk.HoldGround)) bonus *= Stats.Get("combat/HeroSkills/Knight/HoldGround_DefendBonusMultiplier", 2);
            mult += bonus;
        }
        if (u.HasEffect(H5.Combat.Fx.TakeRoots))
        {
            double tr = Stats.Get("combat/Creatures/Sylvan/DefenceBonusOnTakeRoots", 0.5);
            if (h.HasPerk(Sk.HoldGround)) tr *= Stats.Get("combat/HeroSkills/Knight/HoldGround_TakeRootsBonusMultiplier", 2);
            mult += tr;
        }
        d *= mult;
"""
new_mult = ""
old_form = """    /// <summary>Dwarven formations (p.323)"""
new_form = """    /// <summary>Exe FUN_008a22f0 defend bonus: max(1, trunc(defence·f + 0.001)) (FPU truncation mode).</summary>
    int DefendPostureBonus(Unit u, HeroState h, double defence)
    {
        bool roots = u.Has(Ab.TakeRoots);
        double f = roots ? Stats.Get("combat/Creatures/Sylvan/DefenceBonusOnTakeRoots", 0.5)
                         : Stats.Get("combat/Creatures/DefenceBonusOnDefend", 0.3);
        if (h.Present && h.HasPerk(Sk.HoldGround))
            f *= roots ? Stats.Get("combat/HeroSkills/Knight/HoldGround_TakeRootsBonusMultiplier", 2)
                       : Stats.Get("combat/HeroSkills/Knight/HoldGround_DefendBonusMultiplier", 2);
        return Math.Max(1, (int)(defence * f + 0.001));
    }

    /// <summary>
    /// Native terrain (exe FUN_008a4800 / FUN_008a22f0: battlefield vfunc 0x54(race) → +1 attack and +1 defence;
    /// fan manual p.260 for the race ↔ terrain table). Terrain indices per <see cref="BattleState.Terrain"/>.
    /// </summary>
    public int NativeTerrainBonus(Unit u)
    {
        // Only on a real battlefield (an arena was chosen); synthetic battles without an arena have no terrain.
        if (S.Arena == null || u.Kind != UnitKind.Creature || u.IsWarMachine || u.IsObject) return 0;
        bool native = S.Terrain switch
        {
            0 => u.Town == Towns.Necropolis,
            1 => u.Town is Towns.Haven or Towns.Sylvan,
            2 => u.Town == Towns.Academy,
            3 or 7 => u.Town == Towns.Fortress,
            4 => u.Town == Towns.Inferno,
            5 => u.Town == Towns.Dungeon,
            8 or 9 => u.Town == Towns.Stronghold,
            _ => false,
        };
        return native ? 1 : 0;
    }

    /// <summary>Dwarven formations (p.323)"""
for o, n in ((old_att, new_att), (old_def, new_def), (old_mult, new_mult), (old_form, new_form)):
    if n and n in t: continue
    if o not in t: sys.exit('anchor not found:\n' + o[:120])
    t = t.replace(o, n, 1)
open(p, 'w', encoding='utf-8').write(t)
print('patched', p)
