using H5.Adventure.State;

namespace H5.Adventure.Engine;

// Spells from worn artifacts (H5_Game.exe): FUN_00ca6930 lists an artifact's spells; FUN_00b541f0 puts them in the
// hero's book (+0x1e0, entries {artifact, spell, sources}) when the artifact's effects go on (FUN_00b5a410 with on: worn;
// hero set-up FUN_00b56cd0 for the worn list), FUN_00b53730 takes them out again (FUN_00b5a410 with off). They are book
// entries only, never in the known list (+0x204), and no FUN_00b4bb40 follows (no mass / empowered versions).
public sealed partial class Adventure
{
    /// <summary>
    /// FUN_00ca6930(list, world, artifact), types with spells per FUN_00ca64d0 (3, 0x34, 0x3c, 0x4c..0x4f, 0x56, 0x5d):
    /// the tomes ARTIFACT_TOME_OF_DESTRUCTION / _LIGHT_MAGIC / _DARK_MAGIC / _SUMMONING_MAGIC (0x4c..0x4f) → school 0 / 2 /
    /// 1 / 3, manager slot 0x10(circle, school) for circles 1..5 (the map pool's non-adventure spells of that school);
    /// any other the artifact's own spell (+0x20), set at artifact set-up FUN_00ca7e40: ARTIFACT_BAND_OF_CONJURER (0x3c)
    /// SPELL_SUMMON_ELEMENTALS (0x2b), ARTIFACT_RING_OF_UNSUMMONING (0x5d) SPELL_ABILITY_UNSUMMON (0x42). The per-instance
    /// spells of WAND_OF_X, ARTIFACT_RUNE_OF_FLAME and SCROLL_OF_SPELL_X are not modelled (artifacts carry no instance).
    /// </summary>
    public List<string> SpellsOfArtifact(string artifact)
    {
        int school = artifact switch
        {
            "ARTIFACT_TOME_OF_DESTRUCTION" => 0,
            "ARTIFACT_TOME_OF_LIGHT_MAGIC" => 2,
            "ARTIFACT_TOME_OF_DARK_MAGIC" => 1,
            "ARTIFACT_TOME_OF_SUMMONING_MAGIC" => 3,
            _ => -1,
        };
        if (school >= 0)
        {
            var l = new List<string>();
            for (int lvl = 1; lvl <= 5; lvl++) l.AddRange(SpellsOf(lvl, school));
            return l;
        }
        return artifact switch
        {
            "ARTIFACT_BAND_OF_CONJURER" => new() { "SPELL_SUMMON_ELEMENTALS" },
            "ARTIFACT_RING_OF_UNSUMMONING" => new() { "SPELL_ABILITY_UNSUMMON" },
            _ => new(),
        };
    }

    /// <summary>
    /// FUN_00b4b810: a spell the hero's Blood Rage state allows — not valid (FUN_00a349f0) → yes; a rune needs Runelore
    /// (0x97, vf 0x174 &gt; 0); a warcry needs Blood Rage (0xac); any other spell a hero without it.
    /// </summary>
    bool SpellFitsRage(HeroState h, string spell)
    {
        if (Core.Spell(spell) is not { } def || !SpellValid(def)) return true;
        bool Has(string id) => EffectiveMastery(h, id) > 0 || h.HasPerk(id);
        if (def.School == "MAGIC_SCHOOL_RUNIC" && !Has("HERO_SKILL_RUNELORE")) return false;
        bool rage = Has("HERO_SKILL_DEMONIC_RAGE");
        return def.School == "MAGIC_SCHOOL_WARCRIES" ? rage : !rage;
    }

    /// <summary>
    /// The book entries of the worn artifacts (FUN_00b541f0): none for a hero of class 8 (vf 0x258 = HERO_CLASS_BARBARIAN);
    /// each spell of <see cref="SpellsOfArtifact"/> that passes <see cref="SpellFitsRage"/>, worn order.
    /// </summary>
    public List<string> ArtifactSpells(HeroState h)
    {
        var l = new List<string>();
        if (h.Class == "HERO_CLASS_BARBARIAN") return l;
        foreach (var a in h.Artifacts)
            foreach (var sp in SpellsOfArtifact(a))
                if (SpellFitsRage(h, sp) && !l.Contains(sp)) l.Add(sp);
        return l;
    }

    /// <summary>The hero's book (+0x1e0): learned and sourced spells, then the worn artifacts' spells.</summary>
    public List<string> BookSpells(HeroState h)
    {
        var l = h.Spells.ToList();
        foreach (var sp in ArtifactSpells(h)) if (!l.Contains(sp)) l.Add(sp);
        return l;
    }
}
