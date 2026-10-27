using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

/// <summary>
/// Stronghold Walker's Hut: upgrade a Barbarian's Talisman of Adventure to a higher level.
/// Exe: CUpgradeHeroTalismanOfAdventureAtTownCmd (vtable 0xe0ed44, execute 0x429730): hero, town, target level.
/// </summary>
public sealed record UpgradeTalismanCmd(int TownObject, int HeroId, int Level) : AdvCommand;

/// <summary>
/// Result of the town's talisman quote (CAdvMapTown vtable 00e95844 slot 0x118 = FUN_00ccf7b0).
/// </summary>
public enum TalismanStatus
{
    Ok = 0,
    /// <summary>No hero, a first level below 1, a last level past the town's maximum, or first &gt; last.</summary>
    Invalid = 1,
    /// <summary>The town lacks ability 0x2b (Stronghold TB_SPECIAL_3).</summary>
    NoHut = 2,
    /// <summary>The hero is not a Barbarian (hero vf 0x258 = class, 8).</summary>
    NotBarbarian = 3,
    /// <summary>The last level is not above the hero's level (LEVEL_ALREADY_UPGRADED_TOOLTIP).</summary>
    AlreadyUpgraded = 4,
    /// <summary>The first level is not above the hero's level.</summary>
    FirstLevelOwned = 5,
    /// <summary>A level in the range needs a higher hero level (NOT_ENOUGH_HERO_LEVEL_TOOLTIP).</summary>
    HeroLevelTooLow = 6,
}

/// <summary>A talisman quote: status, summed cost of the range, and the range's spells split by map availability.</summary>
public sealed record TalismanQuote(TalismanStatus Status, Resources Cost, List<string> Allowed, List<string> Forbidden);

/// <summary>One TalismanOfAdventure level (DefaultStats adventure/TalismanOfAdventure/Levels, RPGStats +0x194, 0x2c bytes).</summary>
public sealed record TalismanLevel(string Spell, Resources Cost, int HeroLevel);

public sealed partial class Adventure
{
    /// <summary>Town ability 0x2b of the table at 00f7c998: Stronghold TB_SPECIAL_3, the Walker's Hut.</summary>
    public const string WalkersHut = "TB_SPECIAL_3";

    List<TalismanLevel>? _talismanLevels;

    /// <summary>Levels 1..n of the talisman (entry n − 1 is level n): spell (+4), CostOfUpgrade (+8), HeroLevel (+0x28).</summary>
    public IReadOnlyList<TalismanLevel> TalismanLevels => _talismanLevels ??= Core.Stats.Node("adventure/TalismanOfAdventure/Levels")?
        .Elements("Item").Select(e => new TalismanLevel(e.Element("Spell")?.Value.Trim() ?? "",
            Resources.Parse(e.Element("CostOfUpgrade")), (int)XmlHelpers.ParseDouble(e.Element("HeroLevel")?.Value, 0))).ToList() ?? new();

    /// <summary>The town's highest talisman level (slot 0x11c = FUN_00ccec40): the number of levels, whatever the hut's level.</summary>
    public int TalismanMaxLevel => TalismanLevels.Count;

    /// <summary>Hero vf 0x304 = FUN_00b4ce30: the level clamped to 0..levels.</summary>
    public void SetTalismanLevel(HeroState h, int level) => h.TalismanLevel = Math.Max(Math.Min(level, TalismanMaxLevel), 0);

    /// <summary>
    /// FUN_00ccf7b0 (town, hero, first, last). Checks in the exe's order: hero present (1), ability 0x2b (2), Barbarian
    /// (3), first ≥ 1, last ≤ the town maximum and first ≤ last (1); then over the levels first..last the costs are
    /// summed, a HeroLevel above the hero's level is noted and the spells are split by availability; last ≤ owned
    /// (min(hero level, maximum), at least 0) → 4, first ≤ owned → 5, a level too high → 6, else 0.
    /// </summary>
    public TalismanQuote TalismanQuoteFor(TownState t, HeroState? h, int first, int last)
    {
        var zero = new Resources(0, 0, 0, 0, 0, 0, 0);
        TalismanQuote Fail(TalismanStatus s) => new(s, zero, new(), new());
        if (h == null) return Fail(TalismanStatus.Invalid);
        if (t.Type != "TOWN_STRONGHOLD" || t.Level(WalkersHut) < 1) return Fail(TalismanStatus.NoHut);
        if (h.Class != "HERO_CLASS_BARBARIAN") return Fail(TalismanStatus.NotBarbarian);
        int max = TalismanMaxLevel;
        int owned = Math.Max(Math.Min(h.TalismanLevel, max), 0);
        if (first < 1 || last > max || first > last) return Fail(TalismanStatus.Invalid);
        var cost = new Purse();
        var allowed = new List<string>();
        var forbidden = new List<string>();
        bool levelOk = true;
        for (int i = first; i <= last; i++)
        {
            var l = TalismanLevels[i - 1];
            if (h.Level < l.HeroLevel) levelOk = false;
            cost.Add(l.Cost);
            (SpellAvailableOnMap(l.Spell) ? allowed : forbidden).Add(l.Spell);
        }
        var status = last <= owned ? TalismanStatus.AlreadyUpgraded
            : first <= owned ? TalismanStatus.FirstLevelOwned
            : !levelOk ? TalismanStatus.HeroLevelTooLow : TalismanStatus.Ok;
        return new(status, ToResources(cost), allowed, forbidden);
    }

    static Resources ToResources(Purse p) => new(p[0], p[1], p[2], p[3], p[4], p[5], p[6]);

    /// <summary>
    /// Hero vf 0x384 = FUN_00b56b50 (Barbarians only): for levels 1..talisman level, stopping at the first level whose
    /// HeroLevel exceeds the hero's level, an available spell is learned and an unavailable one removed. Called by the
    /// upgrade command and at hero creation (009aacb0 / 009aafa0), not on level-up.
    /// </summary>
    public void GrantTalismanSpells(HeroState h)
    {
        if (h.Class != "HERO_CLASS_BARBARIAN") return;
        for (int i = 1; i <= h.TalismanLevel && i <= TalismanLevels.Count; i++)
        {
            var l = TalismanLevels[i - 1];
            if (h.Level < l.HeroLevel) return;
            if (SpellAvailableOnMap(l.Spell)) LearnSpell(h, l.Spell, force: true);
            else h.Spells.Remove(l.Spell);
        }
    }

    /// <summary>
    /// 00429730: owned level &lt; target ≤ town maximum, quote (owned + 1 .. target) = 0 and affordable (FUN_009d0720) →
    /// pay (player vf 0xe0), set the level (hero vf 0x304), grant the spells (vf 0x384).
    /// </summary>
    string? DoUpgradeTalisman(UpgradeTalismanCmd c)
    {
        var p = Current;
        var t = S.Town(c.TownObject);
        if (t == null || t.Owner != p.Index) return "not your town";
        var h = S.Hero(c.HeroId);
        if (h == null || h.Owner != p.Index || h.InTown != t.Object) return "hero must be in your town";
        int owned = h.TalismanLevel;
        if (owned >= c.Level || c.Level > TalismanMaxLevel) return "cannot upgrade";
        var q = TalismanQuoteFor(t, h, owned + 1, c.Level);
        if (q.Status != TalismanStatus.Ok) return "cannot upgrade";
        if (!p.Purse.CanAfford(q.Cost)) return "not enough resources";
        Give(p, q.Cost, -1);
        SetTalismanLevel(h, c.Level);
        GrantTalismanSpells(h);
        return null;
    }
}
