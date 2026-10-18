"""HEROES audit: creature HP specialisations per exe FUN_004bc470. Usage: <BattleSetup.cs>"""
import sys
p = sys.argv[1]; t = open(p, encoding='utf-8').read()
old = """            if (h.Specialization == "HERO_SPEC_WYVERN_TAMER" && (u.CreatureId.Contains("WYVERN") || u.CreatureId.Contains("PAOKAI")))
                u.BaseHealth += (int)Specials.LevelFormula(db.Stats, "combat/HeroSpecializations/Barbarian/WyvernTamer_HealthBonus", h.Level, 1, 1, 2);
"""
new = """            // Creature HP specialisations (exe FUN_004bc470, keyed on the base creature): Wyvern Tamer
            // +((level−1)/LevelDivisor)·PerDividedLevel + Base for Wyverns; Defender +level/5 for Defenders.
            string hpBase = c.BaseCreature is { Length: > 0 } bc && bc != "CREATURE_UNKNOWN" ? bc : c.Id;
            if (h.Specialization == "HERO_SPEC_WYVERN_TAMER" && hpBase == "CREATURE_WYVERN")
            {
                const string w = "combat/HeroSpecializations/Barbarian/WyvernTamer_HealthBonus";
                int div = Math.Max(1, (int)db.Stats.Get(w + "/LevelDivisor", 1));
                u.BaseHealth += (h.Level - 1) / div * (int)db.Stats.Get(w + "/PerDividedLevel", 2) + (int)db.Stats.Get(w + "/Base", 1);
            }
            if (h.Specialization == "HERO_SPEC_DEFENDER" && hpBase == "CREATURE_DEFENDER") u.BaseHealth += h.Level / 5;
"""
if new not in t:
    if old not in t: sys.exit('anchor missing')
    t = t.replace(old, new, 1)
open(p, 'w', encoding='utf-8').write(t); print('patched', p)

# Tactics (exe FUN_004d8c10): a side qualifies with the Tactics skill unless the ENEMY hero has the
# Flagbearer of Darkness specialisation; the extra column goes to a side only when exactly one qualifies.
t = open(p, encoding='utf-8').read()
old_t = """        bool t0 = s.Heroes[0].HasPerk(Sk.Tactics), t1 = s.Heroes[1].HasPerk(Sk.Tactics);
        s.DeployColumns0 = t0 && !t1 ? 3 : 2;
        s.DeployColumns1 = t1 && !t0 ? 3 : 2;
"""
new_t = """        (s.DeployColumns0, s.DeployColumns1) = TacticsColumns(s.Heroes[0], s.Heroes[1]);
"""
helper_anchor = "    public static BattleState CreateDuel("
helper = """    /// <summary>
    /// Deployment columns per side (exe FUN_004d8c10): a side qualifies with Tactics unless the ENEMY hero is a
    /// Flagbearer of Darkness; only when exactly one side qualifies does it get the third column.
    /// </summary>
    internal static (int Side0, int Side1) TacticsColumns(HeroState h0, HeroState h1)
    {
        bool t0 = h0.HasPerk(Sk.Tactics) && h1.Specialization != "HERO_SPEC_FLAGBEARER_OF_DARKNESS";
        bool t1 = h1.HasPerk(Sk.Tactics) && h0.Specialization != "HERO_SPEC_FLAGBEARER_OF_DARKNESS";
        return (t0 && !t1 ? 3 : 2, t1 && !t0 ? 3 : 2);
    }

"""
if new_t not in t:
    if old_t not in t: sys.exit('tactics anchor missing')
    t = t.replace(old_t, new_t, 1)
if 'TacticsColumns(HeroState h0' not in t:
    if helper_anchor not in t: sys.exit('helper anchor missing')
    t = t.replace(helper_anchor, helper + helper_anchor, 1)
open(p, 'w', encoding='utf-8').write(t); print('patched tactics', p)
