using H5.Core.Data;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

// Caravans (TotE): the rule layer traced so far. The caravan object (CAdvMapCaravan, vtables 00e96494.., built by
// 00ce5b30 → 00ce5880), its daily walk (00ce49b0), the purchase commands (CPurchaseCreaturesToCaravan 00428f20,
// CMoveTownOrTownHeroCreaturesToCaravan 0042c630, CPurchaseAll / Multiple 0042cb30 / 0042cbf0), meeting (CMeetCaravanCmd
// 0042f870) and dismissing (CDismissCaravanCmd 004291d0) are not built yet (AUDIT_ADVENTURE "Caravans").
public sealed partial class Adventure
{
    /// <summary>
    /// A caravan's movement points per day, 00ce38c0: BaseHeroMovement (RPGStats +0x5c) × 130 / 100 (2500 without
    /// the stats).
    /// </summary>
    public int CaravanSpeed()
    {
        int b = Db.Stats.Root.Element("adventure").Int("BaseHeroMovement", 2500);
        return b * 0x82 / 100;
    }

    /// <summary>
    /// Days a caravan needs for a route of the given movement cost: (cost − 1) / speed + 1 (unsigned), as shown on the
    /// hire rows (00ce6110) and kept as the caravan's days left (00ce4d40, +0x178).
    /// </summary>
    public int CaravanDays(int cost) => (int)((uint)(cost - 1) / (uint)CaravanSpeed()) + 1;

    /// <summary>A place the destination town can hire from by caravan (00ce6110 entry: source object and days).</summary>
    public readonly record struct CaravanSource(int Object, bool IsTown, int Days);

    /// <summary>
    /// The caravan sources of a town (00ce6110, used by the hire screen 006e23a0, EnterFort 0061a390, the script
    /// query 00598970 and the AI 0086a350): every map dwelling or town of the town's owner (dwelling: owner of the
    /// flagged part; town: vf 0xbc) other than the town itself with a route to its entrance; days =
    /// <see cref="CaravanDays"/> of the route cost. The exe floods from the town with path mode 8 (00c076d0(8, 0)) for
    /// the owner; the step costs of that mode are not traced, so the route is our hero path cost without skills
    /// (UNVERIFIED).
    /// </summary>
    public List<CaravanSource> CaravanSources(TownState dest)
    {
        var list = new List<CaravanSource>();
        if (dest.Owner < 0) return list;
        var target = EntranceOf(dest);
        int? Days(Tile from)
        {
            var proxy = new HeroState { Owner = dest.Owner, Pos = from, Alive = true };
            var path = FindPath(proxy, target);
            if (path == null) return null;
            int cost = 0;
            for (int i = 1; i < path.Count; i++) cost += StepCost(proxy, path[i - 1], path[i]);
            return CaravanDays(cost);
        }
        foreach (var t in S.Towns)
        {
            if (t == dest || t.Owner != dest.Owner) continue;
            if (Days(EntranceOf(t)) is { } d) list.Add(new CaravanSource(t.Object, true, d));
        }
        foreach (var (idx, os) in S.Objects)
        {
            if (os.Removed || os.Owner != dest.Owner) continue;
            var o = O(idx);
            if (o.Kind != ObjectKind.Dwelling) continue;
            if (Days(o.ActiveTiles.FirstOrDefault(o.Pos)) is { } d) list.Add(new CaravanSource(idx, false, d));
        }
        return list;
    }

    /// <summary>
    /// EnterFort's caravan alternative (0061a390): the fort (hire screen) opens when the town has any dwelling built
    /// (building interface vf 0x34 over the dwelling types 7..13) or, failing that (after a check over the town's
    /// vf 0x90 list that is not decoded), when a caravan source of the town
    /// (00ce6110) has creatures to hire.
    /// </summary>
    public bool CanEnterFort(TownState t)
    {
        for (int tier = 1; tier <= 7; tier++) if (t.Level($"TB_DWELLING_{tier}") > 0) return true;
        foreach (var s in CaravanSources(t))
        {
            int[] avail = s.IsTown ? S.Towns.First(x => x.Object == s.Object).Available : S.Objects[s.Object].Available;
            if (avail.Any(n => n > 0)) return true;
        }
        return false;
    }
}
