using H5.Adventure.State;

namespace H5.Adventure.Engine;

// Necromancy raise after a won battle, H5_Game.exe:
//   FUN_00c26be0  loops over the killed enemy creature types (entries flagged as summoned are skipped)
//   FUN_00c264d0  raised = min(killed, (percent x Health(dead) x killed / 100) / Health(risen))
//   FUN_00c25ff0  necromancy level = Necromancy mastery (hero vf 0x174(15)) when > 0, else the level a script gave
//                 with MakeHeroNecromancer (hero vf 0x368, +0x2a8) when > 0, else none
//   FUN_00c25f90  risen type from DefaultStats Necromancy/TransformTable; none when the dead creature is a
//                 Necropolis (TOWN_NECROMANCY = 7) creature
//   FUN_00c26040  percent = RaisePercentBase + RaisePercentPerSkillLevel x mastery (each term clamped 0..100)
//                 + LordOfUndeadBonus (perk) + NecromancyAmplifierBonus x amplifiers + GrailBonus x
//                 Obelisks of Confined Souls + NecroPendantBonus (ARTIFACT_NECROMANCER_PENDANT, id 71)
//                 + Necromancers_4Necromancer_NecromancyBonusPercents (4+ Death's Embrace set pieces)
public sealed partial class Adventure
{
    const string NecroPath = "combat/HeroSkills/Necromancer/Necromancy/";

    static int Clamp100(double v) => Math.Clamp((int)v, 0, 100);

    /// <summary>FUN_00c25ff0: Necromancy mastery if above 0, else MakeHeroNecromancer's level if above 0, else 0.</summary>
    public static int NecromancyLevel(HeroState h)
    {
        int mastery = (int)h.MasteryOf("HERO_SKILL_NECROMANCY");
        if (mastery >= 1) return mastery;
        return Math.Max(0, h.ScriptNecromancyLevel);
    }

    /// <summary>Total necromancy raise percent of a hero (exe FUN_00c26040).</summary>
    public int NecromancyPercent(HeroState h)
    {
        int mastery = NecromancyLevel(h);
        int pct = Clamp100(Core.Stats.Get(NecroPath + "RaisePercentBase", 10))
                  + Clamp100(Core.Stats.Get(NecroPath + "RaisePercentPerSkillLevel", 10)) * Math.Max(0, mastery);
        if (h.HasPerk("HERO_SKILL_LORD_OF_UNDEAD")) pct += Clamp100(Core.Stats.Get(NecroPath + "LordOfUndeadBonus", 0));
        int amplifiers = 0, obelisks = 0;
        foreach (var t in S.Towns)
        {
            if (t.Owner != h.Owner || t.Owner < 0 || t.Type != "TOWN_NECROMANCY") continue;
            if (t.Level("TB_SPECIAL_1") > 0) amplifiers++;
            if (t.Level("TB_GRAIL") > 0) obelisks++;
        }
        pct += Clamp100(Core.Stats.Get(NecroPath + "NecromancyAmplifierBonus", 10)) * amplifiers;
        pct += Clamp100(Core.Stats.Get(NecroPath + "GrailBonus", 0)) * obelisks;
        if (h.HasArtifact("ARTIFACT_NECROMANCER_PENDANT")) pct += Clamp100(Core.Stats.Get(NecroPath + "NecroPendantBonus", 10));
        if (Core.ArtifactSets.TryGetValue("ARTFSET_EFFECT_NECROMANCERS", out var set) && h.Artifacts.Count(set.Contains) > 3)
            pct += (int)Core.Stats.Get("ArtifactsSetsEffectsConsts/Necromancers_4Necromancer_NecromancyBonusPercents", 20);
        return pct;
    }

    /// <summary>The creature a killed creature rises as (TransformTable), or null.</summary>
    public string? NecromancyRiseOf(string dead)
    {
        if (!Core.Creatures.TryGetValue(dead, out var cd) || cd.Town == "TOWN_NECROMANCY") return null;
        foreach (var it in Core.Stats.Node(NecroPath + "TransformTable")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
            if ((string?)it.Element("Dead") == dead) return ((string?)it.Element("Rise"))?.Trim();
        return null;
    }

    /// <summary>
    /// Raises undead from the enemy creatures killed (killed counts per creature type) into the hero's army
    /// (exe FUN_00c26be0 / FUN_00c264d0). Returns what was raised.
    /// </summary>
    public List<(string Creature, int Count)> NecromancyRaise(HeroState h, IEnumerable<(string Creature, int Killed)> killed)
    {
        var raised = new List<(string, int)>();
        if (NecromancyLevel(h) <= 0) return raised;
        int pct = NecromancyPercent(h);
        foreach (var (dead, count) in killed)
        {
            if (count <= 0 || NecromancyRiseOf(dead) is not { } rise) continue;
            if (!Core.Creatures.TryGetValue(dead, out var d) || !Core.Creatures.TryGetValue(rise, out var r) || r.Health <= 0) continue;
            long hp = (long)d.Health * count;
            int n = (int)Math.Min(count, pct * hp / 100 / r.Health);
            if (n <= 0 || !h.Army.TryAdd(rise, n)) continue;
            raised.Add((rise, n));
        }
        return raised;
    }
}
