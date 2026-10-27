using H5.Adventure.State;

namespace H5.Adventure.Engine;

// Spells from worn artifacts (H5_Game.exe): FUN_00ca6930 lists an artifact's spells; FUN_00b541f0 puts them in the
// hero's book (+0x1e0, entries {artifact, spell, sources}) when the artifact's effects go on (FUN_00b5a410 with on: worn;
// hero set-up FUN_00b56cd0 for the worn list), FUN_00b53730 takes them out again (FUN_00b5a410 with off). They are book
// entries only, never in the known list (+0x204). FUN_00b5a410 runs the book rebuild FUN_00b53b10 after the entry
// change, so the artifact spells get their mass / empowered versions (FUN_00b4bb40); the hero set-up FUN_00b56cd0 runs
// 00b53b10 before it puts the worn artifacts' entries in, so those wait for the next rebuild.
// Wands, scrolls and the Rune of Flame carry their own spell (artifact +0x20), set by the artifact set-up FUN_00ca7e40;
// a wand also its charge object (+0x1c). See HeroState.ArtifactInstances.
public sealed partial class Adventure
{
    public const string WandArtifact = "WAND_OF_X";
    public const string RuneOfFlameArtifact = "ARTIFACT_RUNE_OF_FLAME";

    /// <summary>Artifact types whose objects carry their own spell (FUN_00ca7e40 cases 3, 0x34, 0x56).</summary>
    public static bool HasInstanceSpell(string artifact) => artifact is WandArtifact or ScrollArtifact or RuneOfFlameArtifact;

    /// <summary>FUN_00ca7e40 case 3: the wand table 00e93128 (10 spells) and its charges 00e93150 (FUN_00ca6360, 6 otherwise).</summary>
    static readonly (string Spell, int Charges)[] WandTable =
    {
        ("SPELL_MAGIC_ARROW", 6), ("SPELL_FIREBALL", 3), ("SPELL_ICE_BOLT", 3), ("SPELL_SLOW", 5), ("SPELL_WEAKNESS", 5),
        ("SPELL_DISRUPTING_RAY", 10), ("SPELL_HASTE", 4), ("SPELL_STONESKIN", 4), ("SPELL_DEFLECT_ARROWS", 6), ("SPELL_PHANTOM", 6),
    };

    /// <summary>FUN_00ca7e40 case 0x56: the Rune of Flame table 00e933b8, drawn over 0..3.</summary>
    static readonly string[] RuneOfFlameTable = { "SPELL_RUNE_OF_CHARGE", "SPELL_RUNE_OF_BERSERKING", "SPELL_RUNE_OF_MAGIC_CONTROL", "SPELL_RUNE_OF_EXORCISM" };

    /// <summary>FUN_00ca6360: a wand's charges for its spell (table 00e93150), 6 for a spell not in the table.</summary>
    public static int WandCharges(string? spell)
    {
        foreach (var (s, c) in WandTable) if (s == spell) return c;
        return 6;
    }

    /// <summary>
    /// A new artifact object's own data (artifact set-up FUN_00ca7e40, after FUN_00ca8300 for a map artifact):
    /// WAND_OF_X (case 3): the map's spellID (SAdvMapArtifact +0x9c, kept by 00ca8300 for types 3 and 0x34 only), else
    /// one of <see cref="WandTable"/> (008e7840 over 0..9); then a charge object, full (FUN_00ca7ce0: max = current =
    /// FUN_00ca6360(spell)). SCROLL_OF_SPELL_X (0x34): the map's spellID, else <see cref="RandomScrollSpell"/>.
    /// ARTIFACT_RUNE_OF_FLAME (0x56): always one of <see cref="RuneOfFlameTable"/> (0..3; 00ca8300 clears +0x20).
    /// The exe draws on the artifact's own stream (world vf 0x124 + 008e7860 over the object id); the game RNG here.
    /// </summary>
    public ArtifactInstance NewArtifactInstance(string artifact, bool worn, string? mapSpell = null)
    {
        var inst = new ArtifactInstance { Artifact = artifact, Worn = worn };
        switch (artifact)
        {
            case WandArtifact:
                inst.Spell = mapSpell ?? WandTable[S.Rng.Next(WandTable.Length)].Spell;
                inst.Charges = inst.MaxCharges = WandCharges(inst.Spell);
                break;
            case ScrollArtifact:
                inst.Spell = mapSpell ?? RandomScrollSpell();
                break;
            case RuneOfFlameArtifact:
                inst.Spell = RuneOfFlameTable[S.Rng.Next(RuneOfFlameTable.Length)];
                break;
        }
        return inst;
    }

    /// <summary>The instance of the <paramref name="k"/>-th worn (or backpack) <paramref name="artifact"/> of the hero.</summary>
    public static ArtifactInstance? InstanceOf(HeroState h, string artifact, bool worn, int k) =>
        h.ArtifactInstances.Where(i => i.Artifact == artifact && i.Worn == worn).ElementAtOrDefault(k);

    /// <summary>Takes the instance of the first worn (backpack) <paramref name="artifact"/> off the hero (the one a List.Remove drops).</summary>
    public static ArtifactInstance? TakeInstance(HeroState h, string artifact, bool worn)
    {
        var inst = h.ArtifactInstances.Find(i => i.Artifact == artifact && i.Worn == worn);
        if (inst != null) h.ArtifactInstances.Remove(inst);
        return inst;
    }

    /// <summary>Moves the first worn (backpack) <paramref name="artifact"/>'s instance to the end of the target's list.</summary>
    public static void MoveInstance(HeroState from, string artifact, bool fromWorn, HeroState to, bool toWorn)
    {
        if (TakeInstance(from, artifact, fromWorn) is not { } inst) return;
        inst.Worn = toWorn;
        to.ArtifactInstances.Add(inst);
    }

    /// <summary>
    /// Keeps every hero's instances in step with the artifacts it carries (after each command and at game start):
    /// a carried wand / scroll / Rune of Flame without one is a new artifact object (<see cref="NewArtifactInstance"/>),
    /// an instance whose artifact left is dropped; then a changed worn list re-derives the book (FUN_00b5a410 → 00b53b10).
    /// </summary>
    void SyncArtifactInstances()
    {
        foreach (var h in S.Heroes.Concat(S.OffMapHeroes)) SyncArtifactInstances(h);
    }

    public void SyncArtifactInstances(HeroState h)
    {
        foreach (var art in new[] { WandArtifact, ScrollArtifact, RuneOfFlameArtifact })
            foreach (bool worn in new[] { true, false })
            {
                int n = (worn ? h.Artifacts : h.Backpack).Count(a => a == art);
                var have = h.ArtifactInstances.Where(i => i.Artifact == art && i.Worn == worn).ToList();
                for (int k = have.Count - 1; k >= n; k--) h.ArtifactInstances.Remove(have[k]);
                for (int k = have.Count; k < n; k++) h.ArtifactInstances.Add(NewArtifactInstance(art, worn));
            }
        if (h.ArtifactsOn == null) { h.ArtifactsOn = h.Artifacts.ToList(); return; }
        if (h.ArtifactsOn.Count == h.Artifacts.Count && h.ArtifactsOn.OrderBy(a => a, StringComparer.Ordinal)
                .SequenceEqual(h.Artifacts.OrderBy(a => a, StringComparer.Ordinal))) return;
        h.ArtifactsOn = h.Artifacts.ToList();
        RebuildBook(h);
    }

    /// <summary>
    /// FUN_00ca6930(list, world, artifact), types with spells per FUN_00ca64d0 (3, 0x34, 0x3c, 0x4c..0x4f, 0x56, 0x5d):
    /// the tomes ARTIFACT_TOME_OF_DESTRUCTION / _LIGHT_MAGIC / _DARK_MAGIC / _SUMMONING_MAGIC (0x4c..0x4f) → school 0 / 2 /
    /// 1 / 3, manager slot 0x10(circle, school) for circles 1..5 (the map pool's non-adventure spells of that school);
    /// any other the artifact's own spell (+0x20), set at artifact set-up FUN_00ca7e40: ARTIFACT_BAND_OF_CONJURER (0x3c)
    /// SPELL_SUMMON_ELEMENTALS (0x2b), ARTIFACT_RING_OF_UNSUMMONING (0x5d) SPELL_ABILITY_UNSUMMON (0x42); a wand, scroll
    /// or Rune of Flame its instance's spell (<paramref name="inst"/>).
    /// </summary>
    public List<string> SpellsOfArtifact(string artifact, ArtifactInstance? inst = null)
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
        if (HasInstanceSpell(artifact)) return inst?.Spell is { } s ? new() { s } : new();
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

    /// <summary>The worn artifacts with the instance each one carries (k-th occurrence ↔ k-th worn instance).</summary>
    IEnumerable<(string Artifact, ArtifactInstance? Inst)> WornWithInstances(HeroState h)
    {
        var seen = new Dictionary<string, int>();
        foreach (var a in h.Artifacts)
        {
            int k = seen.GetValueOrDefault(a);
            seen[a] = k + 1;
            yield return (a, HasInstanceSpell(a) ? InstanceOf(h, a, true, k) : null);
        }
    }

    /// <summary>
    /// The book entries of the worn artifacts cast with mana (FUN_00b541f0): none for a hero of class 8 (vf 0x258 =
    /// HERO_CLASS_BARBARIAN); each spell of <see cref="SpellsOfArtifact"/> that passes <see cref="SpellFitsRage"/>, worn
    /// order. Wands are separate entries (<see cref="WandEntries"/>; FUN_00b53730 keys them by the wand, FUN_00ca6550).
    /// </summary>
    public List<string> ArtifactSpells(HeroState h)
    {
        var l = new List<string>();
        if (h.Class == "HERO_CLASS_BARBARIAN") return l;
        foreach (var (a, inst) in WornWithInstances(h))
        {
            if (a == WandArtifact) continue;
            foreach (var sp in SpellsOfArtifact(a, inst))
                if (SpellFitsRage(h, sp) && !l.Contains(sp)) l.Add(sp);
        }
        return l;
    }

    /// <summary>The worn wands' book entries (instance with a spell passing <see cref="SpellFitsRage"/>), worn order.</summary>
    public List<ArtifactInstance> WandEntries(HeroState h)
    {
        var l = new List<ArtifactInstance>();
        if (h.Class == "HERO_CLASS_BARBARIAN") return l;
        foreach (var (a, inst) in WornWithInstances(h))
            if (a == WandArtifact && inst?.Spell is { } sp && SpellFitsRage(h, sp)) l.Add(inst);
        return l;
    }

    /// <summary>The hero's book (+0x1e0): learned and sourced spells, the worn artifacts' spells, then the wands'.</summary>
    public List<string> BookSpells(HeroState h)
    {
        var l = ManaBookSpells(h);
        foreach (var w in WandEntries(h)) if (!l.Contains(w.Spell!)) l.Add(w.Spell!);
        return l;
    }

    /// <summary>The book without the wand entries: what the combat hero casts with mana.</summary>
    public List<string> ManaBookSpells(HeroState h)
    {
        var l = h.Spells.ToList();
        foreach (var sp in ArtifactSpells(h)) if (!l.Contains(sp)) l.Add(sp);
        return l;
    }

    /// <summary>FUN_00a34110: the base of a mass (FUN_00a33e20) or empowered (FUN_00a33f70) spell, else the spell.</summary>
    static string BaseSpell(string spell) =>
        MassSpellPerk.ContainsKey(spell) ? "SPELL_" + spell["SPELL_MASS_".Length..]
        : spell.StartsWith("SPELL_EMPOWERED_", StringComparison.Ordinal) ? "SPELL_" + spell["SPELL_EMPOWERED_".Length..]
        : spell;

    /// <summary>
    /// The book rebuild FUN_00b53b10, over the entries present when it starts (h.Spells, then the artifact entries):
    /// an entry that is no mass / empowered version (FUN_00a33f60 / FUN_00a34100), or has an artifact, gets FUN_00b4bb40
    /// (<see cref="LearnFollowUps"/>) when vf 0x158 = FUN_00b526f0 allows it (artifact or sourced entry: yes; otherwise the
    /// can-learn test vf 0xe4 of its base when the base is not known). A version without artifact stays (and gets 00b4bb40)
    /// while it is known (+0x204 / +0x310) or its base (FUN_00a34110) is known or in the book; otherwise its perk (0x46
    /// Empowered Spells, or the mass spell's Master perk FUN_00a33cf0) leaves its sources (FUN_00b597a0), and the entry is
    /// erased (FUN_0057cb80) when no source is left and it is not known (vf 0xdc).
    /// </summary>
    public void RebuildBook(HeroState h)
    {
        var art = ArtifactSpells(h).Concat(WandEntries(h).Select(w => w.Spell!)).ToList();
        var entries = h.Spells.ToList();
        foreach (var sp in art) if (!entries.Contains(sp)) entries.Add(sp);
        foreach (var e in entries)
        {
            bool hasArt = art.Contains(e);
            bool mass = MassSpellPerk.ContainsKey(e), emp = e.StartsWith("SPELL_EMPOWERED_", StringComparison.Ordinal);
            if (!(mass || emp) || hasArt || KnowsSpell(h, e))
            {
                if (BookEntryFollows(h, e, hasArt)) LearnFollowUps(h, e);
                continue;
            }
            var bas = BaseSpell(e);
            if (KnowsSpell(h, bas) || h.Spells.Contains(bas) || art.Contains(bas))
            {
                if (BookEntryFollows(h, e, hasArt)) LearnFollowUps(h, e);
                continue;
            }
            string perk = emp ? "HERO_SKILL_EMPOWERED_SPELLS" : MassSpellPerk[e];
            if (h.SpellSources.TryGetValue(e, out var src) && src.Remove(perk) && src.Count == 0 && !KnowsSpell(h, e))
            {
                h.SpellSources.Remove(e);
                h.Spells.Remove(e);
            }
        }
    }

    /// <summary>
    /// Hero vf 0x158 = FUN_00b526f0 on a book entry: a valid spell (FUN_00a349f0) without artifact, not a custom ability
    /// (FUN_00a34220: 0x15c..0x15f), without sources and whose base is not in the list FUN_00b59640 checks (taken as the
    /// known list) answers the can-learn test vf 0xe4 for the base; anything else yes.
    /// </summary>
    bool BookEntryFollows(HeroState h, string spell, bool hasArtifact)
    {
        if (hasArtifact || Core.Spell(spell) is not { } def || !SpellValid(def)) return true;
        if (spell.StartsWith("SPELL_ABILITY_CUSTOM", StringComparison.Ordinal)) return true;
        if (h.SpellSources.TryGetValue(spell, out var src) && src.Count > 0) return true;
        var bas = BaseSpell(spell);
        return KnowsSpell(h, bas) || CanLearnSpell(h, bas);
    }

    /// <summary>
    /// The book part of the hero set-up FUN_00b56cd0 (hero interface vf 0x30; vf 0x2c = 00b56f40 calls it): with the
    /// specialization HERO_SPEC_TIME_SHIFT (0x39) SPELL_TIME_SHIFT (0xf0), with HERO_SPEC_AVATAR_OF_DEATH (0x51)
    /// SPELL_ABILITY_AVATAR_OF_DEATH (200) is learned (vf 0xe8(spell, 1) = 00b54720 → 00b545a0: the known list after the
    /// Blood Rage test 00b4b810, then 00b4bb40); the spell grants of 00b54770 for each skill / perk held
    /// (<see cref="PerkSpellsOnLearn"/>); the rebuild FUN_00b53b10; then the hero's artifacts are set up (FUN_00ca8080 /
    /// FUN_00ca81f0 → 00ca7e40) and the worn ones' entries put in (00b541f0) — with no rebuild after them.
    /// The stat and one-time effects of 00b54770 at set-up are not applied here (the caller of vf 0x30 / 0x2c is not
    /// traced). Run once when the engine creates a hero.
    /// </summary>
    void SetUpHeroBook(HeroState h)
    {
        void Learn(string spell)
        {
            if (Core.Spell(spell) == null || !SpellFitsRage(h, spell)) return;
            LearnSpell(h, spell, force: true);
        }
        if (h.Specialization == "HERO_SPEC_TIME_SHIFT") Learn("SPELL_TIME_SHIFT");
        if (h.Specialization == "HERO_SPEC_AVATAR_OF_DEATH") Learn("SPELL_ABILITY_AVATAR_OF_DEATH");
        foreach (var id in h.Skills.Where(s => (int)s.Mastery > 0).Select(s => s.SkillId).Concat(h.Perks).ToList())
            PerkSpellsOnLearn(h, CanonicalSkill(id));
        RebuildBook(h);
        SyncArtifactInstances(h);
        h.ArtifactsOn = h.Artifacts.ToList();
    }

    /// <summary>
    /// Hero vf 0x9c = FUN_00b4db10: every carried artifact (worn +0x18 list and the backpack +0x200 rows) with a charge
    /// object gets its charges back (FUN_00ca6310: current = max). Called by the town guild visit FUN_00cced80.
    /// </summary>
    public void RechargeWands(HeroState h)
    {
        foreach (var i in h.ArtifactInstances)
            if (i.Artifact == WandArtifact) i.Charges = i.MaxCharges;
    }
}
