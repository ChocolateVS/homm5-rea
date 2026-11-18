using H5.Combat;

namespace H5Reborn.Combat;

/// <summary>
/// Abilities as spellbook entries, and the combat Special / Spellbook button rule.
/// In the exe an ability is an entry of the caster's spell list (hero vf 0xcc / creature vf 0x248) of school SPECIAL
/// (FUN_00a34610: school 7 or entry 0x43); the book lists it like a spell (006867c0 / 00684830: the Combat tab and the
/// special bookmark 6; orc book 0067cd10: bookmark 1 with the warcries).
/// </summary>
public static class AbilityBook
{
    /// <summary>
    /// The hero entries the perks add (CHero 00b54770, vf 0xec(entry)): Encourage 0x4b → 0x34 SPELL_ENCOURAGE, Holy Charge
    /// 0x37 → 0x35, Prayer 0x38 → 0x36, Demonic Strike 0x3c → 0x38, Consume Corpse 0x3a → 0x39, Spirit Link 0x6c → 0x3a,
    /// Death Scream 0x3f → 0x3b, Multishot 0x40 → 0x3d, Imbue Arrow 0x42 → 0x3e, Magic Bond 0x43 → 0x3f, Melt Artifact
    /// 0x44 → 0x40, Counterspell 0x84 → 0x41, Unsummon 0x88 → 0x42 (also the Ring of Unsummoning, 00ca7e40 case 0x5d).
    /// </summary>
    static readonly Dictionary<string, string> Hero = new()
    {
        ["DIVINE_GUIDANCE"] = "SPELL_ENCOURAGE", ["RETALIATION_STRIKE"] = "SPELL_HOLY_CHARGE", ["BENEDICTION"] = "SPELL_PRAYER",
        ["MARK_OF_DAMNED"] = "SPELL_DEMONIC_STRIKE", ["CONSUME_CORPSE"] = "SPELL_CONSUME_CORPSE", ["MARK_OF_NECROMANCER"] = "SPELL_SPIRIT_LINK",
        ["BANSHEE_HOWL"] = "SPELL_DEATH_SCREAM", ["RAIN_OF_ARROWS"] = "SPELL_MULTISHOT", ["IMBUE_ARROW"] = "SPELL_IMBUE_ARROW",
        ["MARK_OF_WIZARD"] = "SPELL_ABILITY_MAGIC_BOND", ["CONSUME_ARTIFACT"] = "SPELL_ABILITY_MELT_ARTIFACT",
        ["COUNTERSPELL"] = "SPELL_ABILITY_COUNTERSPELL", ["BANISH"] = "SPELL_ABILITY_UNSUMMON",
        // GAP: the perk Powerful Blow adds no entry in 00b54770; its effect record is used for the book.
        ["POWERFUL_BLOW"] = "SPELL_EFFECT_POWERFULL_BLOW",
    };

    /// <summary>Creature ability ids whose entry name differs from SPELL_ + id (the creature records' spell lists, e.g.
    /// ArchDevil SPELL_ABILITY_SUMMON_BALOR, Genie SPELL_ABILITY_RANDOM_CAST_DARK).</summary>
    static readonly Dictionary<string, string> Creature = new()
    {
        [Ab.BalorSummoning] = "SPELL_ABILITY_SUMMON_BALOR", [Ab.RandomCaster] = "SPELL_ABILITY_RANDOM_CAST_DARK",
        [Ab.RandomCasterBless] = "SPELL_ABILITY_RANDOM_CAST_DARK_LIGHT", [Ab.Gating] = "SPELL_ABILITY_GATING",
        // GAP: the tent's entries (its castable list, 00548895) are not traced; the records of the two tent effects are used.
        ["FIRST_AID"] = "SPELL_EFFECT_FIRST_AID_TENT_HEAL", ["PLAGUE_TENT"] = "SPELL_EFFECT_FIRST_AID_TENT_PLAGUE",
    };

    /// <summary>
    /// Abilities that are no book entry: Goblin Thrower is the cyclops' shot (008a4b70 → shot builder 00b3bfc0, aimed with
    /// the shoot cursor); the catapult's shot is aimed in sieges.
    /// </summary>
    public static bool IsBookAbility(string ability) => ability is not (Ab.GoblinThrower or "CATAPULT_SHOT");

    /// <summary>The spell record of an ability's book entry, or null.</summary>
    public static string? EntryFor(string ability)
    {
        if (ability.StartsWith("IMBUE_ARROW:")) ability = "IMBUE_ARROW";
        var db = Game.Instance.Db;
        if (Hero.TryGetValue(ability, out var h)) return db.Spell(h) != null ? h : null;
        if (Creature.TryGetValue(ability, out var c)) return db.Spell(c) != null ? c : null;
        if (ability.StartsWith("ABILITY_") && db.Spell("SPELL_" + ability) != null) return "SPELL_" + ability;
        if (db.Spell("SPELL_ABILITY_" + ability) != null) return "SPELL_ABILITY_" + ability;
        return null;
    }

    public enum Button { None, Special, Spellbook }

    /// <summary>
    /// FUN_005464a0 from FUN_0053f730: a hero gets the Spellbook button (Special only for a hero holding entry 0xf0,
    /// SPELL_TIME_SHIFT, when actor vf 0xb0 holds — no hero has it). A creature / war machine counts its list (vf 0x248):
    /// runic entries (00a345b0) are skipped, abilities (00a34610) and warcries (00a345e0) are abilities, the rest spells.
    /// Special when it has no spell and exactly one ability; when the list holds exactly one entry and 00982be0 refuses
    /// it, neither; Spellbook otherwise; neither when the list is empty.
    /// The list here: the creature's spells and ability entries plus the entries of the abilities usable now.
    /// </summary>
    public static Button ButtonFor(Battle b, LegalActions la)
    {
        if (la.IsHero) return Button.Spellbook;
        if (b.State.Unit(la.Actor) is not { } u) return Button.None;
        var db = Game.Instance.Db;
        var entries = new HashSet<string>();
        foreach (var ks in u.Spells)
            if (db.Spell(ks.Spell)?.School is { } sc && sc != "MAGIC_SCHOOL_RUNIC") entries.Add(ks.Spell);
        foreach (var ab in la.Abilities)
            if (IsBookAbility(ab.Ability)) entries.Add(EntryFor(ab.Ability) ?? ab.Ability);
        int abilities = entries.Count(e => db.Spell(e)?.School is "MAGIC_SCHOOL_SPECIAL" or "MAGIC_SCHOOL_WARCRIES" || db.Spell(e) == null);
        int spells = entries.Count - abilities;
        if (entries.Count == 0) return Button.None;
        bool usable = la.Spells.Count > 0 || la.Abilities.Any(a => IsBookAbility(a.Ability));
        if (entries.Count == 1 && !usable) return Button.None;
        return spells == 0 && abilities == 1 ? Button.Special : Button.Spellbook;
    }
}
