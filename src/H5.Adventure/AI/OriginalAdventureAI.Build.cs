using H5.Adventure.Engine;
using H5.Adventure.State;

namespace H5.Adventure.AI;

// Town building of the AI's town phase 6 (exe FUN_0086d4e0 → FUN_009c5c60 → FUN_009c5650 → FUN_009c33d0).
// The exe's simulated "estimate" builder FUN_009c5e20 ("Start build towns.", WeakMoneyForDeniedBuildings,
// SupplyDayIncomes) is only called from FUN_009c7d00, which nothing references: dead code in 3.1, not ported.
public sealed partial class OriginalAdventureAI
{
    /// <summary>Building type numbers of the exe (0 hall … 13 dwelling 7, 16 + n TB_SPECIAL_n; 0x1a = none).</summary>
    public static string? BuildingName(int type) => type switch
    {
        0 => "TB_TOWN_HALL", 1 => "TB_FORT", 2 => "TB_MARKETPLACE", 4 => "TB_TAVERN", 5 => "TB_BLACKSMITH", 6 => "TB_MAGIC_GUILD",
        >= 7 and <= 13 => $"TB_DWELLING_{type - 6}",
        >= 16 and <= 25 => $"TB_SPECIAL_{type - 16}",
        _ => null,
    };

    const int NoBuilding = 0x1a;

    /// <summary>
    /// Town ability table at 00f7c998 (16-byte records: ability, town type, building type, level; town type 2 = any),
    /// read from the exe image. Town types 3 Haven … 10 Stronghold.
    /// </summary>
    static readonly (int Ability, int Town, int Building, int Level)[] AbilityTable =
    {
        (0, 3, 17, 1), (1, 3, 18, 1), (2, 3, 19, 1), (3, 3, 20, 1), (4, 3, 21, 1),
        (5, 8, 17, 1), (6, 8, 18, 1), (7, 8, 19, 1), (8, 8, 20, 1), (9, 8, 21, 1),
        (10, 4, 16, 1), (11, 4, 16, 2), (12, 4, 18, 1), (13, 4, 18, 2), (14, 4, 20, 1), (15, 4, 21, 1),
        (16, 7, 17, 1), (17, 7, 19, 1), (18, 7, 18, 1), (19, 7, 20, 1), (20, 7, 21, 1),
        (21, 5, 17, 1), (22, 5, 18, 1), (23, 5, 19, 1), (24, 5, 20, 1), (25, 5, 21, 1),
        (26, 6, 17, 1), (27, 6, 17, 2), (28, 6, 19, 1), (29, 6, 20, 1), (30, 6, 21, 1), (31, 6, 22, 1),
        (32, 9, 17, 1), (33, 9, 17, 2), (34, 9, 17, 3), (35, 9, 18, 1), (36, 9, 19, 1), (37, 9, 20, 1), (38, 9, 21, 1),
        (39, 10, 17, 1), (40, 10, 17, 2), (41, 10, 17, 3), (42, 10, 18, 1), (43, 10, 19, 1), (44, 10, 20, 1), (45, 10, 21, 1),
        (46, 2, 0, 4),
    };

    /// <summary>Exe town type number of an engine town type (TOWN_HEAVEN 3 … TOWN_STRONGHOLD 10; −1 otherwise).</summary>
    public static int TownTypeNumber(string type) { int i = Array.IndexOf(TownTypes, type); return i < 0 ? -1 : i + 3; }

    /// <summary>Town vf 0x4c (00cce260): the (building, level) giving ability <paramref name="a"/> in this town type, or none.</summary>
    static (int Building, int Level) AbilityBuilding(TownState t, int a)
    {
        int tt = TownTypeNumber(t.Type);
        foreach (var e in AbilityTable)
            if (e.Ability == a && (e.Town == 2 || e.Town == tt)) return (e.Building, e.Level);
        return (NoBuilding, 0);
    }

    /// <summary>
    /// "Level l of building type exists here" (town vf 0xb4 = 00cd0de0): the town data has that level, the map allows
    /// it (BLD_UPG limit) — no prerequisite, money or town-level test.
    /// </summary>
    static bool CanHave(Engine.Adventure g, TownState t, int type, int level)
    {
        if (BuildingName(type) is not { } b || level < 1) return false;
        if (t.Type == "TOWN_STRONGHOLD" && b == "TB_MAGIC_GUILD") return false;
        return t.MaxLevel.TryGetValue(b, out int lim) && level <= lim && level <= g.Db.MaxLevel(t.Type, b);
    }

    static int Lv(TownState t, int type) => BuildingName(type) is { } b ? t.Level(b) : 0;

    /// <summary>
    /// The wanted building (exe FUN_009c33d0), a fixed priority list: town hall up to the Capitol (City Hall when the
    /// player already owns a Capitol); unless something was built this turn every level-1 dwelling, then the fort
    /// (highest level first); the marketplace; abilities 0x1d and 0x18; unless built, every dwelling upgrade, then the
    /// magic guild (only where a level 5 exists); the fort up to the castle; then any other ability building in ability
    /// order −1 … 0x2d. Returns the building type number, 0x1a for nothing.
    /// UNVERIFIED-detail: "ability buildable in principle" (town vf 0x40, 00ccee50) is taken as <see cref="CanHave"/>.
    /// </summary>
    public static int WantedBuilding(Engine.Adventure g, TownState t, bool built)
    {
        bool capitol = g.S.Towns.Any(x => x.Owner == t.Owner && x.Level("TB_TOWN_HALL") >= 4);
        int max = capitol ? 3 : 4;
        for (int l = max; l >= 1; l--) if (CanHave(g, t, 0, l) && Lv(t, 0) < l) return 0;
        if (!built)
        {
            for (int d = 7; d <= 13; d++) if (CanHave(g, t, d, 1) && Lv(t, d) < 1) return d;
            for (int l = 3; l >= 1; l--) if (CanHave(g, t, 1, l) && Lv(t, 1) < l) return 1;
        }
        for (int l = 2; l >= 1; l--) if (CanHave(g, t, 2, l) && Lv(t, 2) < l) return 2;
        foreach (int ab in new[] { 0x1d, 0x18 })
            if (WantedAbility(g, t, ab) is int b1) return b1;
        if (!built)
        {
            for (int d = 7; d <= 13; d++) if (CanHave(g, t, d, 2) && Lv(t, d) < 2) return d;
            if (CanHave(g, t, 6, 5) && Lv(t, 6) < 5) return 6;
        }
        if (CanHave(g, t, 1, 3) && Lv(t, 1) < 3) return 1;
        for (int a = -1; a <= 0x2d; a++)
            if (WantedAbility(g, t, a) is int b2) return b2;
        return NoBuilding;
    }

    static int? WantedAbility(Engine.Adventure g, TownState t, int a)
    {
        var (b, l) = AbilityBuilding(t, a);
        if (b == NoBuilding || !CanHave(g, t, b, l) || Lv(t, b) >= l) return null;
        return b;
    }

    /// <summary>
    /// Build weight of a town (FUN_009c3a60): days the town has had its owner (FUN_00451d70: current day − day of
    /// capture, 336 for a town owned since the start) × town level (int multiply), × 2.7 for the player's main town.
    /// </summary>
    public static double BuildWeight(Engine.Adventure g, TownState t, bool mainTown)
    {
        int days = t.OwnedSinceDay < 0 ? 336 : g.S.Day - t.OwnedSinceDay;
        double d = unchecked(days * t.DevLevel);
        return mainTown ? d * 2.7 : d;
    }

    /// <summary>
    /// The player's main town (player vf 4 = +0x1d4, compared with the town pointers in FUN_009c5c60).
    /// UNVERIFIED: taken as the map's MainTown of the player.
    /// </summary>
    static TownState? MainTown(Engine.Adventure g, int player)
    {
        var mp = g.Map.Players.FirstOrDefault(m => m.Index == player);
        if (mp?.MainTownUid is not { } uid) return null;
        return g.S.Towns.FirstOrDefault(t => t.Owner == player && g.Map.Objects[t.Object].Uid == uid);
    }

    /// <summary>
    /// The town building pass (exe FUN_009c5c60): towns under threat other than the main town are skipped ("build: skip
    /// due to threat"); the rest are sorted by <see cref="BuildWeight"/> descending (merge sort taking the left element
    /// only on a strictly greater weight: ties put the later town first) and each tries one build (FUN_009c5650);
    /// after the first success, the first failing town ends the pass.
    /// </summary>
    void BuildInTowns(Engine.Adventure g, int me, AdventureDifficulty diff, ISet<int> threatened)
    {
        var main = MainTown(g, me);
        var list = new List<(TownState T, double W)>();
        foreach (var t in g.S.Towns.Where(t => t.Owner == me))
        {
            if (t != main && threatened.Contains(t.Object)) continue;
            list.Add((t, BuildWeight(g, t, t == main)));
        }
        list.Reverse();
        list = list.OrderByDescending(e => e.W).ToList();   // stable: equal weights keep the reversed order
        bool built = false;
        foreach (var (t, _) in list)
        {
            if (BuildOne(g, t, diff, built)) built = true;
            else if (built) break;
        }
    }

    /// <summary>
    /// One build in one town (exe FUN_009c5650): a town blocked by its build period (town vf 0x9c, TownBuildPeriodInDays
    /// for a computer owner) counts as a success without building; otherwise the wanted building is checked
    /// (00cd6a90): buildable → built; no money → success when something was built already, otherwise the exe tries a
    /// market exchange (FUN_008e70d0, GAP: not ported, so it fails); a missing prerequisite → success when built,
    /// otherwise the first unmet prerequisite (FUN_009c4f60, in the building's dependency order) is wanted instead;
    /// town level too low → the cheapest building that can be built or only lacks money (FUN_009c5140); anything
    /// else fails.
    /// </summary>
    bool BuildOne(Engine.Adventure g, TownState t, AdventureDifficulty diff, bool built)
    {
        if (BuildBlocked(g, t, diff)) return true;
        if (g.Player(t.Owner) is not { } p) return false;
        int code = WantedBuilding(g, t, built);
        if (code == NoBuilding) return false;
        for (int guard = 0; guard < 32; guard++)
        {
            string key = BuildingName(code)!;
            var r = g.BuildCheck(t, key, p.Purse);
            switch (r)
            {
                case BuildDenial.CanBuild:
                    return Send(g, new BuildCmd(t.Object, key) { Player = t.Owner }).Ok;
                case BuildDenial.NoMoney:
                    return built;
                case BuildDenial.Prereq:
                {
                    if (built) return true;
                    var def = g.Db.Building(t.Type, key, t.Level(key) + 1);
                    if (def == null || def.Dependencies.FindIndex(d => t.Level(d.Type) < d.Level) is not (>= 0 and var ri)) return false;
                    var rq = def.Dependencies[ri].Type;
                    int rc = Enumerable.Range(0, 26).FirstOrDefault(i => BuildingName(i) == rq, -1);
                    if (rc < 0) return false;
                    code = rc;
                    continue;
                }
                case BuildDenial.TownPoints:
                {
                    var sel = GainPoints(g, t, p);
                    if (sel == null) return false;
                    return Send(g, new BuildCmd(t.Object, sel) { Player = t.Owner }).Ok;
                }
                default:
                    return false;
            }
        }
        return false;
    }

    /// <summary>
    /// "Gain points" (FUN_009c5140): among the town's buildings whose check gives "can build" or "no money", the one of
    /// least resource value of its next level (the town hall counts 0), the first on ties; a buildable one is preferred
    /// over a money-short one (which then fails at execution). GAP-detail: the exe iterates its building hash map; we
    /// use the town's building list order.
    /// </summary>
    string? GainPoints(Engine.Adventure g, TownState t, PlayerState p)
    {
        string? ok = null, money = null;
        long okV = long.MaxValue, moneyV = long.MaxValue;
        foreach (var key in t.MaxLevel.Keys)
        {
            var r = g.BuildCheck(t, key, p.Purse);
            if (r is not (BuildDenial.CanBuild or BuildDenial.NoMoney)) continue;
            var def = g.Db.Building(t.Type, key, t.Level(key) + 1);
            if (def == null) continue;
            long v = key == "TB_TOWN_HALL" ? 0 : ResourceValue(g, t.Owner, new Purse(def.Cost));
            if (r == BuildDenial.CanBuild) { if (v < okV) { ok = key; okV = v; } }
            else if (v < moneyV) { money = key; moneyV = v; }
        }
        return ok ?? money;
    }

    /// <summary>
    /// Build period (town vf 0x80 = 00ccedf0 feeding vf 0x9c = 00cce4e0): a computer owner's town waits
    /// TownBuildPeriodInDays (row +0x2c) between builds, a human's 1.
    /// UNVERIFIED-detail: the counter town +0x80 is not traced; taken as "(day − 1) % period ≠ 0, or built today".
    /// </summary>
    static bool BuildBlocked(Engine.Adventure g, TownState t, AdventureDifficulty diff) =>
        t.BuiltToday || (g.S.Day - 1) % Math.Max(1, diff.TownBuildPeriodInDays) != 0;
}
