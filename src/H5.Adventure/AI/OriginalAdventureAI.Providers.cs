using H5.Adventure.Data;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.AI;

// Direct value providers of treasures (FUN_00a832b0), map buildings (FUN_00b01af0) and enemy heroes (00bc3740 /
// 00bc3ee0). All values are int32 arithmetic (C# int) unless noted; state 0.
public sealed partial class OriginalAdventureAI
{
    /// <summary>The building kinds whose provider asks for a simulation (00b00c70 returns 0 for them).</summary>
    static readonly HashSet<string> SimulatedBuildings = new()
    {
        "BUILDING_TRADING_POST", "BUILDING_HILL_FORT", "BUILDING_WITCH_HUT", "BUILDING_OBELISK", "BUILDING_SIRENS",
        "BUILDING_WARMACHINE_FACTORY", "BUILDING_NOMADS_SHAMAN", "BUILDING_FORTUITOUS_SANCTUARY",
        // Other exe classes behind the same xdb kind, simulated (CAdvMapBlackMarket, CAdvMapSpellShop, CAdvMapTavern).
        "BUILDING_BLACK_MARKET", "BUILDING_SPELL_SHOP", "BUILDING_TAVERN",
    };

    /// <summary>Teleports (object vf 0x4c) are never destinations (FUN_00a78b60 returns false).</summary>
    static bool IsTeleport(MapObject o) => o.Shared.Type is "BUILDING_SUBTERRA_GATE" or "BUILDING_MONOLITH_ONE_WAY_ENTRANCE"
        or "BUILDING_MONOLITH_ONE_WAY_EXIT" or "BUILDING_MONOLITH_TWO_WAY" or "BUILDING_WHIRLPOOL";

    static int Adv(Engine.Adventure g, string field, int d) => XmlHelpers.Int(g.Db.Stats.Root.Element("adventure"), field, d);

    /// <summary>
    /// The "average resource value" (CPlayerAIInformation vf 0xc): (v0 + … + v5) / 7 of the AI money values before their
    /// clamp to ≥ 1. GAP-detail: the money values (FUN_00a325b0) are not decoded; with all weights 1.0 this is 6 / 7.
    /// </summary>
    static double AverageResourceValue => (1.0 + 1.0 + 1.0 + 1.0 + 1.0 + 1.0) * (1.0 / 7.0);

    /// <summary>
    /// Treasure value (FUN_00a832b0) from the shared descriptor only (MinResource / MaxResource; neither the rolled
    /// amount nor Fortunate Adventurer): A = max(1, (Min + 2 · Max) / 3); a resource pile A (gold 100 · A); campfire
    /// 100 · A gold and 1 of every resource; chest with p = ChestMinorArtifactChance: the better of gold
    /// ((A + 1)(100 − p) · 500) / 100 and experience ((100 − p) · A · 500) / 100, + ValueOfMinorArtifact · p / 100;
    /// sea chest 1500 · (Max + Min + 1) / max(1, Max − Min) gold; flotsam 5 wood + 175 gold; soulstone
    /// ValueOfMajorArtifact; shipwreck 400 (Min 1) or 450 gold + (9 · major + 6 · minor + relic) · 5 / 100.
    /// </summary>
    long TreasureValue(Engine.Adventure g, AdventureAiStats st, HeroState h, MapObject o)
    {
        var sh = o.Shared;
        string kind = sh.Type.StartsWith("TREASURE_", StringComparison.Ordinal) ? sh.Type[9..] : sh.Type;
        if (kind == "SOULSTONE") return st.ValueOfMajorArtifact;
        var amt = new Purse();
        int a = Math.Max(1, (sh.MinResource + sh.MaxResource * 2) / 3);
        int exp = 0, extra = 0;
        switch (kind)
        {
            case "CRYSTAL": amt[Purse.Crystal] = a; break;
            case "GEMS": amt[Purse.Gem] = a; break;
            case "GOLD": amt[Purse.Gold] = unchecked(100 * a); break;
            case "MERCURY": amt[Purse.Mercury] = a; break;
            case "ORE": amt[Purse.Ore] = a; break;
            case "SULFUR": amt[Purse.Sulfur] = a; break;
            case "WOOD": amt[Purse.Wood] = a; break;
            case "CAMPFIRE":
                amt[Purse.Gold] = unchecked(100 * a);
                for (int i = 0; i < 6; i++) amt[i] = 1;
                break;
            case "CHEST":
            {
                int p = Math.Clamp(Adv(g, "ChestMinorArtifactChance", 2), 0, 100);
                amt[Purse.Gold] = unchecked((a + 1) * (100 - p) * 500) / 100;
                exp = unchecked((100 - p) * a * 500) / 100;
                extra = unchecked(st.ValueOfMinorArtifact * p) / 100;
                break;
            }
            case "SEA_CHEST":
                amt[Purse.Gold] = unchecked(1500 * (sh.MaxResource + sh.MinResource + 1)) / Math.Max(1, sh.MaxResource - sh.MinResource);
                break;
            case "FLOATSAM": amt[Purse.Wood] = 5; amt[Purse.Gold] = 175; break;
            case "SHIPWRECK":
                amt[Purse.Gold] = sh.MinResource == 1 ? 400 : 450;
                extra = unchecked((9 * st.ValueOfMajorArtifact + 6 * st.ValueOfMinorArtifact + st.ValueOfRelicArtifact) * 5) / 100;
                break;
        }
        double v = ResourceValue(g, h.Owner, amt);
        if (exp > 0)
        {
            double e = ExpGain(g, st, h, exp);
            if (e > v) v = e;
        }
        return (long)(extra + v);
    }

    /// <summary>Exe FUN_00bc3700: ExpValue(exp + n) − ExpValue(exp) as a double.</summary>
    static double ExpGain(Engine.Adventure g, AdventureAiStats st, HeroState h, int n)
    {
        int e = (int)Math.Min(int.MaxValue, h.Experience);
        return LevelValue(g, st, unchecked(e + n)) - LevelValue(g, st, e);
    }

    /// <summary>
    /// Map building value (FUN_00b01af0) by kind (see docs/rules/AUDIT_AI.md "Map building values"); null for the kinds
    /// the provider simulates and the creature banks (CAdvMapBuildingGuarded).
    /// UNVERIFIED-details: "hero has this type's buff" (hero vf 0x1fc) is taken as the next-battle visited set; the
    /// Tree of Knowledge price before the first visit is taken as free.
    /// </summary>
    long? BuildingValue(Engine.Adventure g, AdventureAiStats st, HeroState h, MapObject o, ObjState? os)
    {
        string kind = o.Shared.Type;
        if (SimulatedBuildings.Contains(kind) || AdventureDb.BankFor(kind) != null) return null;
        if (g.Player(h.Owner) is not { } p) return 0;
        bool heroVisited = h.VisitedOnce.Contains(o.Index);
        bool heroBuff = h.VisitedSinceBattle.Contains(o.Index);
        bool playerVisited = p.VisitedObjects.Contains(o.Index);
        bool taken = os != null && os.LastVisitWeek == g.S.Week;
        int day = g.S.Day - 1, dow1 = g.S.DayOfWeek;
        int maxMana = g.ScriptMaxMana(h);
        double w6 = ResourceWeights(g, h.Owner)[Purse.Gold], w5 = ResourceWeights(g, h.Owner)[Purse.Gem], avg = AverageResourceValue;
        switch (kind)
        {
            case "BUILDING_WATER_WHEEL": return taken ? 0 : (long)(w6 * (g.S.Month == 1 && g.S.WeekOfMonth == 1 ? 500 : 1000));
            case "BUILDING_WINDMILL": return taken ? 0 : (long)(avg * 9.0 * 0.5);
            case "BUILDING_LEAN_TO": return playerVisited ? 0 : (long)(avg * 5.0 * 0.5);
            case "BUILDING_WAGON": return playerVisited ? 0 : (long)((unchecked(st.ValueOfMinorArtifact * 50) + avg * 7.0 * 0.5 * 40.0) * 0.01);
            case "BUILDING_SKELETON": return playerVisited ? 0 : unchecked(st.ValueOfMinorArtifact * 20) / 100;
            case "BUILDING_STABLES":
                return h.StablesThisWeek ? 0 : unchecked(st.ValueOfHundredMovePoints * ((7 - dow1) * Adv(g, "ForWeekStablesMaxLandMovementBonus", 0) + Adv(g, "VisitStablesMovementBonus", 0))) / 100;
            case "BUILDING_RALLY_FLAG":
                return heroBuff ? 0 : unchecked(st.ValueOfHundredMovePoints * Adv(g, "VisitRallyFlagMovementBonus", 0)) / 100 + st.ValueOfMoraleBuff + st.ValueOfLuckBuff;
            case "BUILDING_OASIS": return heroBuff ? 0 : unchecked(st.ValueOfHundredMovePoints * Adv(g, "VisitOasisMovementBonus", 0)) / 100 + st.ValueOfMoraleBuff;
            case "BUILDING_LAKE_OF_SCARLET_SWAN": return heroBuff ? 0 : unchecked(st.ValueOfLuckBuff * 2 - st.ValueOfHundredMovePoints * h.MovePoints / 100);
            case "BUILDING_FONTAIN_OF_FORTUNE": return heroBuff ? 0 : st.ValueOfLuckBuff;
            case "BUILDING_MAGIC_SPRING": return taken ? 0 : unchecked(st.ValueOfManaPoint * (maxMana * 2 - h.Mana));
            case "BUILDING_FONTAIN_OF_YOUTH": return heroBuff ? 0 : st.ValueOfMoraleBuff;
            case "BUILDING_FAERIE_RING": return heroBuff ? 0 : st.ValueOfLuckBuff;
            case "BUILDING_IDOL_OF_FORTUNE":
                return heroBuff ? 0 : (day % 7) switch { 0 or 2 or 4 => st.ValueOfLuckBuff, 1 or 3 or 5 => st.ValueOfMoraleBuff, _ => st.ValueOfMoraleBuff + st.ValueOfLuckBuff };
            case "BUILDING_TEMPLE": return heroBuff ? 0 : (day % 7 + 1 == 7 ? 2 : 1) * (long)st.ValueOfMoraleBuff;
            case "BUILDING_MAGIC_WELL": return h.DrankFromWellToday || h.Spells.Count == 0 ? 0 : unchecked((maxMana - h.Mana) * st.ValueOfManaPoint);
            case "BUILDING_STAR_AXIS": return heroVisited ? 0 : st.ValueOfAttributeSpellPower;
            case "BUILDING_GARDEN_OF_REVELATION": return heroVisited ? 0 : st.ValueOfAttributeKnowledge;
            case "BUILDING_MARLETTO_TOWER": return heroVisited ? 0 : st.ValueOfAttributeDefence;
            case "BUILDING_MERCENARY_CAMP": return heroVisited ? 0 : st.ValueOfAttributeAttack;
            case "BUILDING_LEARNING_STONE": return heroVisited ? 0 : (long)ExpGain(g, st, h, 1000);
            case "BUILDING_TREE_OF_KNOWLEDGE": return heroVisited ? 0 : TreeValue(g, st, h, o, os, p, w6, w5);
            case "BUILDING_BUOY": return heroBuff ? 0 : st.ValueOfMoraleBuff;
            case "BUILDING_MERMAIDS": return heroBuff ? 0 : st.ValueOfLuckBuff;
            case "BUILDING_MYSTICAL_GARDEN": return taken ? 0 : (long)((w6 * 500.0 + w5 * 5.0) * 0.5);
            case "BUILDING_ARENA": return heroVisited ? 0 : (st.ValueOfAttributeDefence + st.ValueOfAttributeAttack) / 2;
            case "BUILDING_SCHOOL_OF_MAGIC": return heroVisited ? 0 : (st.ValueOfAttributeKnowledge + st.ValueOfAttributeSpellPower) / 2;
            case "BUILDING_WAR_ACADEMY": return heroVisited ? 0 : (st.ValueOfAttributeDefence + st.ValueOfAttributeAttack) / 2;
            case "BUILDING_TOMB_OF_THE_WARRIOR": return playerVisited ? 0 : unchecked(st.ValueOfMajorArtifact - 3 * st.ValueOfMoraleBuff);
            default: return 0;
        }
    }

    /// <summary>Exe 00b01af0 case 59 (Tree of Knowledge): the experience of one level minus 33 % of the gold + gems prices, if the price is affordable and worth less.</summary>
    static long TreeValue(Engine.Adventure g, AdventureAiStats st, HeroState h, MapObject o, ObjState? os, PlayerState p, double w6, double w5)
    {
        var price = os == null || os.Variant < 0 ? new Purse() : new Purse(g.TreeOfKnowledgePrice(o.Index));
        if (Affordable(p.Purse, price) < 1.0f) return 0;
        int goldPart = (int)(long)(w6 * 2000.0), gemsPart = (int)(long)(w5 * 10.0);
        var t = g.Core.Experience;
        int need = (int)(t.For(h.Level + 1) - t.For(h.Level));
        int e = (int)ExpGain(g, st, h, need);
        int v = unchecked(e * 100 - (goldPart + gemsPart) * 33) / 100;
        if ((int)ResourceValue(g, h.Owner, price) >= v) return 0;
        return v;
    }

    /// <summary>
    /// Enemy hero as a destination (provider 00bc3740 / 00bc3ee0): not on our team and not in a garrison → the battle
    /// estimate FUN_00bc3af0 against its army (a refusal is state −1), with the aggressivity coefficient of its owner;
    /// otherwise null (simulated). GAP-detail: hero vf 0x218 in the direct test is not identified (taken as 0).
    /// </summary>
    (int, long)? HeroTargetValue(Engine.Adventure g, AdventureAiStats st, AdventureDifficulty diff, HeroState h, HeroState other)
    {
        if (SameTeam(g, other.Owner, h.Owner) || other.Garrisoned) return null;
        double c = g.Player(other.Owner)?.Human == true ? diff.AttackHumanAggressivityCoef : diff.AttackComputerAggressivityCoef;
        var v = FightValue(g, st, diff, h, other.Army.Live().ToList(), other, (float)c, UnseenByHumans(g, other.Pos));
        return v is long x ? (0, x) : (-1, 0);
    }
}
