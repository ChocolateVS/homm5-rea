using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>
/// The parts of a town dwelling's weekly growth as the exe keeps them (0x2c-byte growth record, filled by
/// FUN_00cd5180 and FUN_00bce140; hire screen tooltip 006dc57d shows them as T_GROWTH_WEEKLY, _CASTLE, _HORDE,
/// _DWELLING, _SPECIALIZATION, _GRAIL, _SACRIFICE and _MOONWEEK_POSITIVE / _NEGATIVE).
/// </summary>
public readonly record struct TownGrowth(int Weekly, int Castle, int Horde, int Dwelling, int Specialization, int Grail,
    int Sacrifice, int MoonWeek)
{
    public int Total => Weekly + Castle + Horde + Dwelling + Specialization + Grail + Sacrifice + MoonWeek;
}

public sealed partial class Adventure
{
    /// <summary>
    /// Horde buildings (FUN_00cce610, keyed by the dwelling's base creature): the town ability checked through
    /// CAdvMapTown +0x44 (FUN_00cce1c0, table 00f7c998: town type, building, level) and the bonus. The last three
    /// read DefaultStats TownBuildingBonuses (RPGStats +0x154 / +0x158 / +0x18c).
    /// </summary>
    static readonly (string Creature, string Town, string Building, int Bonus, string? Stat)[] HordeBuildings =
    {
        ("CREATURE_PEASANT", "TOWN_HEAVEN", "TB_SPECIAL_5", 5, null),        // ability 4: Farms
        ("CREATURE_DEMON", "TOWN_INFERNO", "TB_SPECIAL_2", 2, null),         // 6
        ("CREATURE_NIGHTMARE", "TOWN_INFERNO", "TB_SPECIAL_4", 1, null),     // 8
        ("CREATURE_SKELETON", "TOWN_NECROMANCY", "TB_SPECIAL_3", 6, null),   // 0x11
        ("CREATURE_BONE_DRAGON", "TOWN_NECROMANCY", "TB_SPECIAL_4", 1, null),// 0x13
        ("CREATURE_PIXIE", "TOWN_PRESERVE", "TB_SPECIAL_4", 4, null),        // 0xe
        ("CREATURE_TREANT", "TOWN_PRESERVE", "TB_SPECIAL_5", 1, null),       // 0xf
        ("CREATURE_GENIE", "TOWN_ACADEMY", "TB_SPECIAL_4", 2, null),         // 0x18
        ("CREATURE_BROWLER", "TOWN_FORTRESS", "TB_SPECIAL_2", 0, "Fortress_Arena_BrawlerGrowthAddition"),     // 0x23
        ("CREATURE_RUNE_MAGE", "TOWN_FORTRESS", "TB_SPECIAL_5", 0, "Fortress_Arena_RuneMageGrowthAddition"),  // 0x26
        ("CREATURE_GOBLIN", "TOWN_STRONGHOLD", "TB_SPECIAL_2", 0, "Stronghold_GarbagePile_GoblinGrowthAddition"), // 0x2a
    };

    /// <summary>
    /// A town dwelling tier's weekly growth record (FUN_00cd5180 then FUN_00bce140 with the week in effect):
    /// weekly = the base creature's WeeklyGrowth; castle = weekly / 2 with a Citadel (TB_FORT 2), weekly with a
    /// Castle (3); horde = <see cref="HordeBuildings"/>; dwelling (town +0x154) is not traced and stays 0;
    /// specialization = FUN_00cd3030; grail = (weekly + castle + horde + dwelling + specialization) / 2 with
    /// TB_GRAIL; sacrifice = the Ritual Pit bonus of CREATURE_WITCH (0x49) / CREATURE_MINOTAUR (0x4b);
    /// moon week = <see cref="MoonWeekGrowth"/>(weekly + castle + horde + specialization) − that sum.
    /// </summary>
    public TownGrowth GrowthOf(TownState t, int tier)
    {
        var def = Db.Building(t.Type, $"TB_DWELLING_{tier}", 1);
        if (def?.Creature == null || !Core.Creatures.TryGetValue(def.Creature, out var cr)) return default;
        int weekly = cr.WeeklyGrowth;
        int castle = t.Level("TB_FORT") switch { 2 => weekly / 2, 3 => weekly, _ => 0 };
        int horde = 0;
        foreach (var hb in HordeBuildings)
            if (hb.Creature == def.Creature && hb.Town == t.Type && t.Level(hb.Building) >= 1)
                horde = hb.Stat != null ? (int)Core.Stats.Get("adventure/TownBuildingBonuses/" + hb.Stat, 0) : hb.Bonus;
        int dwelling = 0;
        int spec = SpecializationGrowth(t, tier);
        int grail = t.Level("TB_GRAIL") != 0 ? (weekly + castle + horde + dwelling + spec) / 2 : 0;
        int sacrifice = 0;
        if (def.Creature is "CREATURE_WITCH" or "CREATURE_MINOTAUR")
        {
            var (witches, minotaurs) = RitualPitBonus(t.SacrificedHitPoints);
            sacrifice = def.Creature == "CREATURE_WITCH" ? witches : minotaurs;
        }
        int sum = weekly + castle + horde + spec;
        int moon = MoonWeekGrowth(Db.MoonWeekIndex(S.MoonWeek), sum, def.Creature) - sum;
        return new TownGrowth(weekly, castle, horde, dwelling, spec, grail, sacrifice, moon);
    }

    /// <summary>
    /// FUN_00cd3030: Recruitment (skill 0x1c) of the town's garrison or visiting hero gives max(4 − tier, 0)
    /// (times 2 when the hero's vfunc 0x310 is positive — not identified, taken as 1); the town specialisation
    /// TOWN_BONUS_TIER1..4_CREATURE adds 2 / 1 / 1 / 1 to that tier, +1 more for tiers 1 and 2 in a Fortress town
    /// (town type 9, FUN_00cd35c0).
    /// </summary>
    int SpecializationGrowth(TownState t, int tier)
    {
        int n = 0;
        if (S.Heroes.Any(h => h.Alive && h.InTown == t.Object && (h.HasPerk("HERO_SKILL_RECRUITMENT") || h.MasteryOf("HERO_SKILL_RECRUITMENT") > H5.Core.Data.Mastery.None)))
            n = Math.Max(4 - tier, 0);
        bool fortress = t.Type == "TOWN_FORTRESS";
        n += (SpecializationBonus(t), tier) switch
        {
            ("TOWN_BONUS_TIER1_CREATURE", 1) => 2 + (fortress ? 1 : 0),
            ("TOWN_BONUS_TIER2_CREATURE", 2) => 1 + (fortress ? 1 : 0),
            ("TOWN_BONUS_TIER3_CREATURE", 3) => 1,
            ("TOWN_BONUS_TIER4_CREATURE", 4) => 1,
            _ => 0,
        };
        return n;
    }

    /// <summary>
    /// FUN_00bcdb90: the moon week applied to a growth sum. PLAGUE (0x2b) → 0; DISEASE (0x2c) / FEVER (0x2d): a
    /// positive sum becomes max(sum / 3, 1) / max(sum / 2, 1); LIFE (0x36) ×2; CONJUNCTION (0x37) ×3; then ×2 when
    /// the week's creature (00bcd640) and the dwelling's creature have the same base creature (004baf90).
    /// </summary>
    public int MoonWeekGrowth(int week, int sum, string creature)
    {
        switch (week)
        {
            case 0x2b: sum = 0; break;
            case 0x2c: if (sum >= 1) sum = Math.Max(sum / 3, 1); break;
            case 0x2d: if (sum >= 1) sum = Math.Max(sum / 2, 1); break;
            case 0x36: sum *= 2; break;
            case 0x37: sum *= 3; break;
        }
        int wc = MoonCalendar.CreatureOf(week);
        if (wc != 0 && Db.EnumName("CreatureType", wc) is { } weekCreature && SameBaseCreature(weekCreature, creature)) sum *= 2;
        return sum;
    }

    bool SameBaseCreature(string a, string b)
    {
        string Base(string c) => Core.Creatures.TryGetValue(c, out var d) && d.BaseCreature.Length > 0 && d.BaseCreature != "CREATURE_UNKNOWN" ? d.BaseCreature : c;
        return Base(a) == Base(b);
    }

    /// <summary>
    /// FUN_00bce0d0 (start of the weekly update 00cd27b0, before the growth is added): PLAGUE empties a town's
    /// creature pools, DISEASE / FEVER take max(n / 3, 1) / max(n / 2, 1) from a positive pool.
    /// </summary>
    public static int MoonWeekPool(int week, int n) => week switch
    {
        0x2b => 0,
        0x2c when n > 0 => n - Math.Max(n / 3, 1),
        0x2d when n > 0 => n - Math.Max(n / 2, 1),
        _ => n,
    };
}
