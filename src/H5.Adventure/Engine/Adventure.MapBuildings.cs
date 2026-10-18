using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

/// <summary>
/// Upgrade a whole stack to its Upgrade-th upgrade (1 or 2, index into the base creature's upgrade list), in a
/// town with the matching upgraded dwelling (HillFort = -1) or at a Hill Fort the hero stands at. An already
/// upgraded stack is "regraded" to the other upgrade. Exe: CUpgradeCreatureCmd (vtable 0xe197fc, execute 0x4f47e0).
/// </summary>
public sealed record UpgradeCreatureCmd(int HeroId, int TownObject, int Slot, int Upgrade, int HillFort = -1) : AdvCommand;
/// <summary>Buy a war machine at a War Machine Factory the hero stands at (standard price, any machine).</summary>
public sealed record BuyFactoryWarMachineCmd(int HeroId, int Factory, string Machine) : AdvCommand;
/// <summary>Accept a Cartographer's offer (exe CAcceptCartographerCmd, execute 0x42ef10).</summary>
public sealed record BuyCartographerMapCmd(int HeroId, int Cartographer) : AdvCommand;

public sealed record CreatureUpgraded(int HeroId, int TownObject, int Slot, string From, string To) : AdvEvent;

// Hill Fort, War Machine Factory, Cartographer, Obelisk.
public sealed partial class Adventure
{
    /// <summary>
    /// Hill Fort price factor by creature tier 1..7 (exe table 0xe7a548, read by FUN_008b4360/008b4430;
    /// tier clamped to 1..7). Tier 1 upgrades for free.
    /// </summary>
    static readonly float[] HillFortRatio = { 0f, 0.5f, 1f, 1.25f, 1.5f, 1.75f, 2f };

    /// <summary>Gold cost a Cartographer asks when the map instance does not set one (fan manual p.266).</summary>
    const int DefaultCartographerCost = 1000;

    bool HeroAt(HeroState h, MapObject o) => h.Pos == o.Pos || o.ActiveTiles.Contains(h.Pos);

    static Resources Round(Resources r, float k)
    {
        // FUN_009d0d10: each resource (int)ROUND((float)v * k), x87 round-to-nearest-even.
        int R(int v) => (int)MathF.Round(v * k, MidpointRounding.ToEven);
        return new(R(r.Wood), R(r.Ore), R(r.Mercury), R(r.Crystal), R(r.Sulfur), R(r.Gem), R(r.Gold));
    }

    static Resources Diff(Resources a, Resources b) =>
        // FUN_009d0ad0 then FUN_009d0770 (negative components become 0).
        new(Math.Max(0, a.Wood - b.Wood), Math.Max(0, a.Ore - b.Ore), Math.Max(0, a.Mercury - b.Mercury),
            Math.Max(0, a.Crystal - b.Crystal), Math.Max(0, a.Sulfur - b.Sulfur), Math.Max(0, a.Gem - b.Gem),
            Math.Max(0, a.Gold - b.Gold));

    /// <summary>
    /// Target creature and price of upgrading <paramref name="count"/> creatures of <paramref name="creature"/>
    /// to upgrade <paramref name="upgrade"/> (1-based). Normal upgrade (FUN_004bb570): max(0, upgraded cost -
    /// base cost) × count. Regrade of an upgraded creature to its base's other upgrade (FUN_004bbce0):
    /// ROUND(target cost × UpgradesParams/RegradeCostCoef) × count. At a Hill Fort the result is then scaled by
    /// <see cref="HillFortRatio"/> of the tier, rounded per resource.
    /// </summary>
    public (string? Target, Resources Cost) UpgradeQuote(string creature, int upgrade, int count, bool hillFort)
    {
        var none = (default(string), new Resources(0, 0, 0, 0, 0, 0, 0));
        if (!Core.Creatures.TryGetValue(creature, out var cd)) return none;
        bool regrade = cd.Upgrade;
        var baseDef = regrade ? (Core.Creatures.GetValueOrDefault(cd.BaseCreature) ?? Core.Creatures.Values.FirstOrDefault(b => b.Upgrades.Contains(creature))) : cd;
        if (baseDef == null || upgrade < 1 || upgrade > baseDef.Upgrades.Count) return none;
        // A regrade needs at least two upgrades (FUN_004bbb10 checks the list holds more than one).
        if (regrade && baseDef.Upgrades.Count < 2) return none;
        string target = baseDef.Upgrades[upgrade - 1];
        if (target == creature || !Core.Creatures.TryGetValue(target, out var td)) return none;
        Resources cost;
        if (regrade)
        {
            float coef = (float)Core.Stats.Get("UpgradesParams/RegradeCostCoef", 0.333);
            cost = Purse.Scale(Round(td.Cost, coef), count);
        }
        else cost = Purse.Scale(Diff(td.Cost, cd.Cost), count);
        if (hillFort) cost = Round(cost, HillFortRatio[Math.Clamp(baseDef.Tier, 1, 7) - 1]);
        return (target, cost);
    }

    string? DoUpgradeCreature(UpgradeCreatureCmd c)
    {
        var p = Current;
        Stack?[] army;
        HeroState? h = c.HeroId >= 0 ? S.Hero(c.HeroId) : null;
        TownState? t = null;
        if (c.HillFort >= 0)
        {
            var o = Map.Objects.ElementAtOrDefault(c.HillFort);
            if (o == null || o.Kind != ObjectKind.HillFort || S.Obj(c.HillFort).Removed) return "not a hill fort";
            if (h == null || !h.Alive || h.Owner != p.Index || !HeroAt(h, o)) return "hero must visit the hill fort";
            army = h.Army;
        }
        else
        {
            t = S.Town(c.TownObject);
            if (t == null || t.Owner != p.Index) return "not your town";
            if (h != null)
            {
                if (!h.Alive || h.Owner != p.Index || h.InTown != t.Object) return "hero must be in your town";
                army = h.Army;
            }
            else army = t.Garrison;
        }
        if (c.Slot is < 0 or >= State.Army.Slots || army[c.Slot] is not { Count: > 0 } s) return "empty slot";
        var (target, cost) = UpgradeQuote(s.Creature, c.Upgrade, s.Count, c.HillFort >= 0);
        if (target == null) return "cannot upgrade";
        if (t != null)
        {
            // Town: the creature must belong to the town and the dwelling must offer the target creature.
            var td = Core.Creatures[target];
            int tier = Core.Creatures[s.Creature].Tier;
            if (td.Town != t.Type) return "creature of another faction";
            if (DwellingCreature(t, tier, 1) != target && DwellingCreature(t, tier, 2) != target) return "no upgraded dwelling";
        }
        if (!p.Purse.CanAfford(cost)) return "not enough resources";
        Give(p, Purse.Scale(cost, -1));
        string from = s.Creature;
        s.Creature = target;
        Emit(new CreatureUpgraded(h?.Id ?? -1, t?.Object ?? -1, c.Slot, from, target));
        Emit(new ArmyChanged(h?.Id ?? -1, h == null ? t?.Object ?? -1 : -1));
        return null;
    }

    /// <summary>War Machine Factory: every machine at its standard price (fan manual p.266).</summary>
    string? DoBuyFactoryWarMachine(BuyFactoryWarMachineCmd c)
    {
        var p = Current;
        var h = S.Hero(c.HeroId);
        var o = Map.Objects.ElementAtOrDefault(c.Factory);
        if (o == null || o.Shared.Type != "BUILDING_WARMACHINE_FACTORY" || S.Obj(c.Factory).Removed) return "not a war machine factory";
        if (h == null || !h.Alive || h.Owner != p.Index || !HeroAt(h, o)) return "hero must visit the factory";
        if (!Core.WarMachines.TryGetValue(c.Machine, out var wm)) return "unknown war machine";
        ref bool owned = ref h.Ballista;
        switch (c.Machine)
        {
            case "WAR_MACHINE_BALLISTA": break;
            case "WAR_MACHINE_FIRST_AID_TENT": owned = ref h.FirstAidTent; break;
            case "WAR_MACHINE_AMMO_CART": owned = ref h.AmmoCart; break;
            default: return "not for sale";
        }
        if (owned) return "already owned";
        if (!p.Purse.CanAfford(wm.Cost)) return "not enough resources";
        Give(p, Purse.Scale(wm.Cost, -1));
        owned = true;
        Emit(new ArmyChanged(h.Id, -1));
        return null;
    }

    /// <summary>Gold a Cartographer asks for its map (instance &lt;Cost&gt;).</summary>
    public int CartographerCost(int cartographer)
    {
        var o = Map.Objects.ElementAtOrDefault(cartographer);
        return o != null && o.Props.TryGetValue("Cost", out var v) && int.TryParse(v, out int n) ? n : DefaultCartographerCost;
    }

    /// <summary>Whether the player already bought this Cartographer's map (exe vtable slot +0x11c).</summary>
    public bool CartographerBought(PlayerState p, int cartographer) => p.VisitedObjects.Contains(cartographer);

    /// <summary>
    /// Pays the price and reveals the map: a water Cartographer (shared WaterBased) every water tile, a land one
    /// every land tile, on the Cartographer's floor. TODO(exe): which tiles the reveal covers is not traced
    /// (the command only marks the purchase, slot +0x120); the HoMM5 manual's land/sea split is used.
    /// </summary>
    string? DoBuyCartographerMap(BuyCartographerMapCmd c)
    {
        var p = Current;
        var h = S.Hero(c.HeroId);
        var o = Map.Objects.ElementAtOrDefault(c.Cartographer);
        if (o == null || o.Kind != ObjectKind.Cartographer) return "not a cartographer";
        if (h == null || !h.Alive || h.Owner != p.Index || !HeroAt(h, o)) return "hero must visit the cartographer";
        if (CartographerBought(p, c.Cartographer)) return "already bought";
        int cost = CartographerCost(c.Cartographer);
        if (p.Purse[Purse.Gold] < cost) return "not enough gold";
        GiveOne(p, Purse.Gold, -cost);
        p.VisitedObjects.Add(c.Cartographer);
        var f = Map.Floor(o.Pos.Floor);
        if (f != null && o.Pos.Floor < p.Explored.Count)
        {
            var ex = p.Explored[o.Pos.Floor];
            int n = 0;
            for (int y = 0; y < f.Height; y++)
                for (int x = 0; x < f.Width; x++)
                {
                    int i = y * f.Width + x;
                    if (!ex[i] && f.IsWater(x, y) == o.Shared.WaterBased) { ex[i] = true; n++; }
                }
            if (n > 0) Emit(new TilesRevealed(p.Index, o.Pos.Floor, n));
        }
        Emit(new ObjectVisited(h.Id, o.Index, "cartographer_bought"));
        return null;
    }

    /// <summary>Obelisks the player has visited (one puzzle-map piece each); the visit is recorded per player.</summary>
    public int ObelisksVisited(PlayerState p) =>
        p.VisitedObjects.Count(i => Map.Objects.ElementAtOrDefault(i)?.Shared.Type == "BUILDING_OBELISK");

    public int ObeliskCount => Map.Objects.Count(o => o.Shared.Type == "BUILDING_OBELISK");

    void VisitObelisk(HeroState h, PlayerState p, MapObject o)
    {
        p.VisitedObjects.Add(o.Index);
        Emit(new ObjectVisited(h.Id, o.Index, "BUILDING_OBELISK"));
    }
}
