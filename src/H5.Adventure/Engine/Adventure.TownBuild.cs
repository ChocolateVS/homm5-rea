using H5.Adventure.State;
using Towns = H5.Combat.Towns;

namespace H5.Adventure.Engine;

/// <summary>
/// Why a town building can or cannot be upgraded now: the codes CTown returns from its build check (town vtable
/// 00e95844 slot +0xac = 00cd6a90, special cases 00cd0e20). The town build screen shows them with the
/// UI/TownBuildNew/Texts entry of the same name (switch at 00659ad0, jump table 0065a36c).
/// </summary>
public enum BuildDenial
{
    CanBuild = 0,        // can_build
    CantBuild = 1,       // cant_build (no such building in this town)
    LimitUpgrade = 2,    // limit_upgrade (the map's level limit)
    AlreadyBuilt = 3,    // already_build (the building's last level)
    Prereq = 4,          // prereq
    NoMoney = 5,         // no_money
    BuiltThisTurn = 6,   // build_thisturn
    NoWater = 7,         // no_water (shipyard)
    OnlyOneCapitol = 8,  // only_one_capitol
    Grail = 9,           // grail (Tear of Asha needed)
    TownPoints = 10,     // town_points (town level)
}

// Town building checks and town income per H5_Game.exe (see docs/rules/AUDIT_TOWN.md).
public sealed partial class Adventure
{
    /// <summary>The UI/TownBuildNew/Texts entry for a denial code (00659ad0).</summary>
    public static string BuildDenialText(BuildDenial d) => d switch
    {
        BuildDenial.CanBuild => "can_build", BuildDenial.CantBuild => "cant_build", BuildDenial.LimitUpgrade => "limit_upgrade",
        BuildDenial.AlreadyBuilt => "already_build", BuildDenial.Prereq => "prereq", BuildDenial.NoMoney => "no_money",
        BuildDenial.BuiltThisTurn => "build_thisturn", BuildDenial.NoWater => "no_water", BuildDenial.OnlyOneCapitol => "only_one_capitol",
        BuildDenial.Grail => "grail", _ => "town_points",
    };

    /// <summary>
    /// The town build check (00cd6a90), in the exe's order: no building of that type → cant_build (also a Stronghold
    /// magic guild); the special cases of 00cd0e20 (shipyard without water, a second capitol, the grail without a
    /// Tear of Asha); the building's last level → already_build; the map's level limit → limit_upgrade; the town
    /// level (sum of built levels, 00cd2120) below DevLevelNeeded → town_points; a prerequisite missing → prereq;
    /// not affordable (00cd4880) → no_money; already built today (00cce4e0) → build_thisturn.
    /// <paramref name="purse"/> null skips the money test.
    /// </summary>
    public BuildDenial BuildCheck(TownState t, string type, Purse? purse)
    {
        int dataMax = Db.MaxLevel(t.Type, type);
        if (dataMax <= 0 || !t.MaxLevel.ContainsKey(type) || (t.Type == Towns.Stronghold && type == "TB_MAGIC_GUILD"))
            return BuildDenial.CantBuild;
        int level = t.Level(type);
        // 00cd0e20
        if (type == "TB_SHIPYARD" && (SlipwayOf(t.Object) is not { } slip || !IsWaterTile(slip))) return BuildDenial.NoWater;
        if (type == "TB_TOWN_HALL" && level + 1 > 3 && PlayerHasCapitol(t.Owner))
            return t.Level("TB_TOWN_HALL") >= 4 ? BuildDenial.AlreadyBuilt : BuildDenial.OnlyOneCapitol;
        if (type == "TB_GRAIL")
        {
            if (level == dataMax) return BuildDenial.AlreadyBuilt;
            if (GrailBearer(t) == null) return BuildDenial.Grail;
        }
        if (level >= dataMax) return BuildDenial.AlreadyBuilt;
        if (level >= t.MaxLevel[type]) return BuildDenial.LimitUpgrade;
        var def = Db.Building(t.Type, type, level + 1);
        if (def == null) return BuildDenial.CantBuild;
        if (t.DevLevel < def.DevLevelNeeded) return BuildDenial.TownPoints;
        foreach (var (dep, dl) in def.Dependencies)
            if (t.Level(dep) < dl) return BuildDenial.Prereq;
        if (purse != null && !purse.CanAfford(def.Cost)) return BuildDenial.NoMoney;
        if (t.BuiltToday) return BuildDenial.BuiltThisTurn;
        return BuildDenial.CanBuild;
    }

    /// <summary>Player vcall +0xd4 in 00cd0e20: one of the player's towns has a Capitol (town ability 0x2e = hall level 4).</summary>
    bool PlayerHasCapitol(int player) => player >= 0 && S.Towns.Any(x => x.Owner == player && x.Level("TB_TOWN_HALL") >= 4);

    /// <summary>
    /// The hero whose Tear of Asha builds the grail (00cd0e20 / 00cd4a40): the visiting hero (town hero slot 1) first,
    /// else the garrisoned hero (slot 0); hero vcall +0xa4 = carries the Tear, +0xa8 = gives it up.
    /// </summary>
    HeroState? GrailBearer(TownState t)
    {
        bool Has(HeroState h) => h.Artifacts.Contains(GrailArtifact) || h.Backpack.Contains(GrailArtifact);
        var here = S.Heroes.Where(h => h.Alive && h.InTown == t.Object).ToList();
        return here.FirstOrDefault(h => !h.Garrisoned && Has(h)) ?? here.FirstOrDefault(h => h.Garrisoned && Has(h));
    }

    /// <summary>Building the grail uses up the bearer's Tear of Asha (00cd4a40, type 0xe).</summary>
    void ConsumeTearOfAsha(TownState t)
    {
        if (GrailBearer(t) is not { } h) return;
        if (!h.Artifacts.Remove(GrailArtifact)) h.Backpack.Remove(GrailArtifact);
        Emit(new HeroStatChanged(h.Id, "artifact_removed:" + GrailArtifact, 0));
    }

    /// <summary>
    /// A town's daily income before the moon week (CTown 00cd43f0 with the silo part 00cd31b0): nothing at all without
    /// a town hall; hall gold 500 / 1000 / 2000 / 4000 (Capitol: 2000 + 2000); specialisation TOWN_BONUS_ELVEN_CAPITAL
    /// +500, TOWN_BONUS_250_GOLD +250; with a Resource Silo (marketplace level ≥ 2) the TownTypesInfo silo income, and
    /// +1 ore / +1 wood for TOWN_BONUS_EXTRA_ORE / _EXTRA_WOOD; a built grail +5000; +1 gold per creature with
    /// ABILITY_TAXPAYER (0x46) in the town's garrison; town ability 0x18 (Academy Treasure Cave, TB_SPECIAL_4) +500.
    /// TODO(exe): the per-town extra gold/resource fields (+0xe0 / +0xe4 / +0xe8, vcall +0x88 = 00cce3c0) are not traced.
    /// </summary>
    public Purse TownIncome(TownState t)
    {
        var inc = new Purse();
        int hall = t.Level("TB_TOWN_HALL");
        if (hall <= 0) return inc;
        inc[Purse.Gold] += hall switch { 1 => 500, 2 => 1000, 3 => 2000, _ => 4000 };
        var spec = SpecializationBonus(t);
        if (spec == "TOWN_BONUS_ELVEN_CAPITAL") inc[Purse.Gold] += 500;
        else if (spec == "TOWN_BONUS_250_GOLD") inc[Purse.Gold] += 250;
        if (t.Level("TB_MARKETPLACE") >= 2)
        {
            inc.Add(SiloIncome(t.Type));
            if (spec == "TOWN_BONUS_EXTRA_ORE") inc[Purse.Ore] += 1;
            else if (spec == "TOWN_BONUS_EXTRA_WOOD") inc[Purse.Wood] += 1;
        }
        if (t.Level("TB_GRAIL") > 0) inc[Purse.Gold] += 5000;
        foreach (var s in t.Garrison)
            if (s is { Count: > 0 } && Core.Creatures.TryGetValue(s.Creature, out var cd) && cd.Has("ABILITY_TAXPAYER"))
                inc[Purse.Gold] += s.Count;
        if (t.Type == Towns.Academy && t.Level("TB_SPECIAL_4") >= 1) inc[Purse.Gold] += 500;
        return inc;
    }
}
