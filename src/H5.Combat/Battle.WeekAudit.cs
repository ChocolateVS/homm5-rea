using H5.Core.Data;

namespace H5.Combat;

// Combat effects of the week of the month (GameMechanics/RefTables/MoonWeeks.xdb, enum MoonWeek in types.xml),
// audited against H5_Game.exe (docs/rules/audit/combat_heroes_skills_h.md). The battle asks its week through
// battle vcall +0x3c and passes it to the small helpers at 00bcdc90..00bcdf30 / 00bce190.

/// <summary>Week ids the combat code tests (exe values in comments) and the exe's week helpers.</summary>
public static class MoonWeek
{
    public const string Nothing = "WEEK_OF_NOTHING",
        Idleness = "WEEK_OF_IDLENESS",          // 0x3d
        Magic = "WEEK_OF_MAGIC",                // 0x3e
        Feebleness = "WEEK_OF_FEEBLENESS",      // 0x3f
        Sorrow = "WEEK_OF_SORROW",              // 0x40
        Chaos = "WEEK_OF_CHAOS",                // 0x41
        Calm = "WEEK_OF_CALM",                  // 0x42
        Hope = "WEEK_OF_HOPE",                  // 0x43
        Water = "WEEK_OF_WATER",                // 0x44
        Fire = "WEEK_OF_FIRE",                  // 0x45
        Earth = "WEEK_OF_EARTH",                // 0x46
        Air = "WEEK_OF_AIR",                    // 0x47
        FireNIce = "WEEK_OF_FIRENICE",          // 0x48
        Might = "WEEK_OF_MIGHT",                // 0x49
        Balance = "WEEK_OF_BALANCE",            // 0x4a
        MightNMagic = "WEEK_OF_MIGHTNMAGIC",    // 0x4b
        Infirmity = "WEEK_OF_INFIRMITY",        // 0x4c
        Light = "WEEK_OF_LIGHT",                // 0x4d
        Evocation = "WEEK_OF_EVOCATION",        // 0x4e
        Abjuration = "WEEK_OF_ABJURATION",      // 0x4f
        Alteration = "WEEK_OF_ALTERATION",      // 0x50
        Conjuration = "WEEK_OF_CONJURATION",    // 0x51
        Ether = "WEEK_OF_ETHER",                // 0x52
        Toughness = "WEEK_OF_TOUGHNESS";        // 0x53

    /// <summary>Exe FUN_0045a0e0: creature town → week group (1 Haven/Sylvan/Academy/Fortress, 2 Dungeon/
    /// Necropolis/Inferno/Stronghold, 0 otherwise — neutrals are never affected).</summary>
    public static int Group(string town) => town switch
    {
        Towns.Haven or Towns.Sylvan or Towns.Academy or Towns.Fortress => 1,
        "TOWN_DUNGEON" or "TOWN_NECROMANCY" or "TOWN_INFERNO" or "TOWN_STRONGHOLD" => 2,
        _ => 0,
    };

    /// <summary>Exe FUN_00bcdf10: Might / Might and Magic weeks: x + x/2 (integer). Creature min/max damage
    /// (004bbfb0/004bc020, on the creature's base value), war machine damage (004bc710/004bc780), the hero strike
    /// (00bc1b90) and tower damage (00bfdfb0).</summary>
    public static int MightDamage(int x, string? week) => week is Might or MightNMagic ? x + x / 2 : x;

    /// <summary>Exe FUN_00bcdf30 (from 004bc470): Week of Toughness: base hit points + hp/5 (integer).</summary>
    public static int Health(int hp, string? week) => week == Toughness ? hp + hp / 5 : hp;

    /// <summary>Exe FUN_00bcdd80 called with flag 1 from the defence getter 004bb2a0 (the attack getter 004bb170
    /// passes 0, so attack is never changed): Feebleness (group 1) / Infirmity (group 2):
    /// trunc(defence · 0.8f + 0.5); otherwise unchanged.</summary>
    public static int Defence(int def, string town, string? week)
    {
        int g = Group(town);
        bool hit = week == Feebleness && g == 1 || week == Infirmity && g == 2;
        return hit ? (int)(def * 0.8f + 0.5f) : def;
    }

    /// <summary>Exe FUN_00bcdd40 (from 004bc190, on the base initiative as float): Idleness (group 1) / Calm
    /// (group 2): × 0.8f.</summary>
    public static double Initiative(double init, string town, string? week)
    {
        int g = Group(town);
        bool hit = week == Idleness && g == 1 || week == Calm && g == 2;
        return hit ? (float)((float)init * 0.8f) : init;
    }

    /// <summary>Exe FUN_00bcdc90 (via 00bcdcf0 luck 004ba970, 00bcdd00 morale 004ba9d0) on the army's base
    /// morale/luck: Sorrow −2 (group 1), Light −2 (group 2), Hope +1, Balance → 0.</summary>
    public static int MoraleLuck(int v, string town, string? week)
    {
        int g = Group(town);
        return week switch
        {
            Sorrow when g == 1 => v - 2,
            Light when g == 2 => v - 2,
            Hope => v + 1,
            Balance => 0,
            _ => v,
        };
    }

    /// <summary>Exe FUN_00bcdd20 / 00bcdd30: Week of Balance also skips the morale/luck effects
    /// (00989fd0 / 00989c40) in the stack's morale (00a6acd0) and luck (00a6abf0).</summary>
    public static bool IsBalance(string? week) => week == Balance;

    /// <summary>Exe FUN_00bcddf0 (from the spell cost 00a34740, after the base cost and before every
    /// discount, for heroes and creatures alike): Magic / Might and Magic: cost / 2 (integer), Ether: × 2.</summary>
    public static int SpellCost(int cost, string? week) => week switch
    {
        Magic or MightNMagic => cost / 2,
        Ether => cost * 2,
        _ => cost,
    };

    /// <summary>Exe FUN_00bcde10 (from the caster mastery getter 00a6a700, after the caster's own mastery):
    /// Evocation/Abjuration/Alteration/Conjuration (school 0/2/1/3 = Destructive/Light/Dark/Summoning):
    /// a mastery above none becomes 3 (Expert).</summary>
    public static Mastery SpellMastery(Mastery m, string school, string? week)
    {
        string want = week switch
        {
            Evocation => "MAGIC_SCHOOL_DESTRUCTIVE", Abjuration => "MAGIC_SCHOOL_LIGHT",
            Alteration => "MAGIC_SCHOOL_DARK", Conjuration => "MAGIC_SCHOOL_SUMMONING", _ => "",
        };
        return want.Length > 0 && m > Mastery.None && school == want ? Mastery.Expert : m;
    }

    /// <summary>Exe FUN_00bcde80 (end of the spell base damage 00976b90, after the empowered ×1.5): Water/Fire/
    /// Earth/Air weeks: x + x/2 for spells of that element; Chaos: x + x/2 for every spell; Fire and Ice: × 2 for
    /// EVERY spell (the exe does not test the element for this week, unlike its description).</summary>
    public static int SpellDamage(int x, string element, string? week)
    {
        string el = week switch
        {
            Water => Elements.Water, Fire => Elements.Fire, Earth => Elements.Earth, Air => Elements.Air, _ => "",
        };
        if (el.Length > 0 && element == el) x += x / 2;
        if (week == Chaos) x += x / 2;
        else if (week == FireNIce) x *= 2;
        return x;
    }

    /// <summary>Exe FUN_00bce190 (Summon Elementals 00861630, on the stack size): Water/Fire/Earth/Air weeks:
    /// n + n/2 when the summoned creature's element (record +0xa0) is the week's element.</summary>
    public static int SummonCount(int n, string creatureElement, string? week)
    {
        string el = week switch
        {
            Water => Elements.Water, Fire => Elements.Fire, Earth => Elements.Earth, Air => Elements.Air, _ => "",
        };
        return el.Length > 0 && creatureElement == el ? n + n / 2 : n;
    }
}
