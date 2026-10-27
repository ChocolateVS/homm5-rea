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

/// <summary>Pay a Tree of Knowledge (Sylanna's Ancients) its price for one level (exe CUseTreeOfKnowledgeCmd, execute 0x427cc0).</summary>
public sealed record UseTreeOfKnowledgeCmd(int HeroId, int Tree) : AdvCommand;

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

    /// <summary>The hero visits the object as if it had stepped on it (Lua MakeHeroInteractWithObject; tests).</summary>
    public void InteractWithObject(HeroState h, int obj) => Visit(h, O(obj));

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

    /// <summary>
    /// Hill Fort "can upgrade" (vfunc 0 = 008b4320 → 004bb440): the creature has an <paramref name="upgrade"/>-th
    /// upgrade, is not NotUpgradeable, its tier is not forbidden for that upgrade (1 basic, 2 alternative) and the
    /// target is not a ForbiddenUpgrade.
    /// </summary>
    public bool HillFortCanUpgrade(MapObject fort, string creature, int upgrade)
    {
        if (!Core.Creatures.TryGetValue(creature, out var cd) || cd.Upgrades.Count == 0) return false;
        if (upgrade < 1 || upgrade > cd.Upgrades.Count) return false;
        var f = fort.HillFortFilter;
        if (f == null) return true;
        if (f.NotUpgradeable.Contains(creature)) return false;
        if (upgrade == 1 && f.ForbiddenBasicUpgradeTiers.Contains(cd.Tier)) return false;
        if (upgrade == 2 && f.ForbiddenAlterUpgradeTiers.Contains(cd.Tier)) return false;
        return !f.ForbiddenUpgrades.Contains(cd.Upgrades[upgrade - 1]);
    }

    /// <summary>
    /// Hill Fort "can regrade" (vfunc 8 = 008b43f0 → 004bbb10): the base creature has at least two upgrades, the
    /// <paramref name="upgrade"/>-th is not the creature itself, the base is not NotUpgradeable, the upgrade the
    /// creature leaves (the other index) is not forbidden for its tier, the creature is not a ForbiddenUpgrade, and the
    /// target upgrade is allowed for the tier and not a ForbiddenUpgrade.
    /// </summary>
    public bool HillFortCanRegrade(MapObject fort, string creature, int upgrade)
    {
        if (!Core.Creatures.TryGetValue(creature, out var cd)) return false;
        var baseDef = cd.Upgrade ? Core.Creatures.GetValueOrDefault(cd.BaseCreature) ?? Core.Creatures.Values.FirstOrDefault(b => b.Upgrades.Contains(creature)) : cd;
        if (baseDef == null || baseDef.Upgrades.Count < 2 || upgrade < 1 || upgrade > baseDef.Upgrades.Count) return false;
        if (baseDef.Upgrades[upgrade - 1] == creature) return false;
        var f = fort.HillFortFilter;
        if (f == null) return true;
        if (f.NotUpgradeable.Contains(baseDef.Id)) return false;
        if (upgrade == 1 && f.ForbiddenAlterUpgradeTiers.Contains(cd.Tier)) return false;
        if (upgrade == 2 && f.ForbiddenBasicUpgradeTiers.Contains(cd.Tier)) return false;
        if (f.ForbiddenUpgrades.Contains(creature)) return false;
        if (upgrade == 1 && f.ForbiddenBasicUpgradeTiers.Contains(cd.Tier)) return false;
        if (upgrade == 2 && f.ForbiddenAlterUpgradeTiers.Contains(cd.Tier)) return false;
        return !f.ForbiddenUpgrades.Contains(baseDef.Upgrades[upgrade - 1]);
    }

    /// <summary>
    /// One Hill Fort option's status (0045ba60, the CHillFortDialogLogEntry list): 0 upgrade affordable, 1 regrade
    /// affordable, 2 possible but not affordable (purse / cost below 1 on some resource, 009d0500), 3 impossible.
    /// The upgrade test comes first; the regrade test only when the upgrade is impossible.
    /// </summary>
    public int HillFortOptionStatus(MapObject fort, PlayerState p, Stack s, int upgrade, out Resources cost)
    {
        bool up = HillFortCanUpgrade(fort, s.Creature, upgrade);
        if (!up && !HillFortCanRegrade(fort, s.Creature, upgrade)) { cost = new Resources(0, 0, 0, 0, 0, 0, 0); return 3; }
        cost = UpgradeQuote(s.Creature, upgrade, s.Count, true).Cost;
        if (!p.Purse.CanAfford(cost)) return 2;
        return up ? 0 : 1;
    }

    /// <summary>
    /// The Hill Fort screen's state of one stack (006d4950): over the options 1..n of the creature's upgrade list (the
    /// base's list for an upgraded creature) the best-ranked status wins (0/1 rank 2, 2 rank 1, 3 rank 0; a later option
    /// replaces an equal rank); 4 when the slot is empty. Returns the status, the option and its cost.
    /// </summary>
    public (int Status, int Upgrade, Resources Cost) HillFortSlotState(MapObject fort, PlayerState p, Stack? s)
    {
        var none = new Resources(0, 0, 0, 0, 0, 0, 0);
        if (s is not { Count: > 0 } || !Core.Creatures.TryGetValue(s.Creature, out var cd)) return (4, 0, none);
        var list = cd.Upgrades.Count > 0 ? cd.Upgrades
            : (Core.Creatures.GetValueOrDefault(cd.BaseCreature) ?? Core.Creatures.Values.FirstOrDefault(b => b.Upgrades.Contains(s.Creature)))?.Upgrades ?? new List<string>();
        (int Status, int Upgrade, Resources Cost) best = (4, 0, none);
        int bestRank = 0;
        for (int i = 1; i <= list.Count; i++)
        {
            int st = HillFortOptionStatus(fort, p, s, i, out var cost);
            int rank = st is 0 or 1 ? 2 : st == 2 ? 1 : 0;
            if (bestRank <= rank) { best = (st, i, cost); bestRank = rank; }
        }
        return best;
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
        // Hill Fort (CUpgradeCreatureCmd execute 004f47e0 with the fort as provider): the fort's filter decides.
        if (c.HillFort >= 0)
        {
            var fort = Map.Objects[c.HillFort];
            bool upgraded = Core.Creatures.TryGetValue(s.Creature, out var scd) && scd.Upgrade;
            if (upgraded ? !HillFortCanRegrade(fort, s.Creature, c.Upgrade) : !HillFortCanUpgrade(fort, s.Creature, c.Upgrade))
                return "upgrade forbidden here";
        }
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

    // ------------------------------------------------------------------ Witch Hut

    /// <summary>
    /// Secondary skills a Witch Hut can teach: the common skills, i.e. those more than two hero classes can
    /// learn (racial skills and the Stronghold shatter skills belong to one faction's two classes).
    /// TODO(exe): the hut's roll (CAdvMapBuilding slot +0x1c8, not traced) — uniform over this list.
    /// </summary>
    public IReadOnlyList<string> WitchHutSkills =>
        Db.SkillProbs.Values.SelectMany(list => list.Where(x => x.Prob > 0).Select(x => x.Skill).Distinct())
            .GroupBy(x => x).Where(gr => gr.Count() > 2).Select(gr => gr.Key)
            .Where(id => Db.Perks.TryGetValue(id, out var d) && d.IsSkill)
            .OrderBy(id => id, StringComparer.Ordinal).ToList();

    /// <summary>The skill a Witch Hut teaches, rolled once per hut on first visit.</summary>
    public string? WitchHutSkill(int hut) => S.Objects.TryGetValue(hut, out var os) ? os.SkillId : null;

    /// <summary>
    /// Witch Hut: teaches its skill, once per hero: a new skill at Basic if a skill slot is free, or one
    /// mastery level more of a known one (stock texts Text/Game/Buildings/WitchHut: Description,
    /// FirstVisit, Visited = "already known", NoLinesForSkill = no free slot). The player remembers the
    /// hut's skill after the visit.
    /// </summary>
    void VisitWitchHut(HeroState h, PlayerState p, MapObject o, ObjState os)
    {
        if (os.SkillId == null)
        {
            var pool = WitchHutSkills;
            if (pool.Count > 0) os.SkillId = pool[S.Rng.Next(pool.Count)];
        }
        p.VisitedObjects.Add(o.Index);
        string? skill = os.SkillId;
        var mastery = skill == null ? Mastery.None : h.MasteryOf(skill);
        string result;
        if (skill == null || h.VisitedOnce.Contains(o.Index) || mastery >= Mastery.Expert) result = "known";
        else if (mastery == Mastery.None && h.Skills.Count(x => x.Mastery > Mastery.None) >= MaxSkills) result = "full";
        else
        {
            h.VisitedOnce.Add(o.Index);
            var learned = mastery + 1;
            h.Skills.RemoveAll(x => x.SkillId == skill);
            h.Skills.Add(new SkillEntry(skill, learned));
            Emit(new SkillLearned(h.Id, skill, learned));
            result = "learned";
        }
        Emit(new ObjectVisited(h.Id, o.Index, $"BUILDING_WITCH_HUT:{result}:{skill}"));
    }

    // ------------------------------------------------------------------ Tree of Knowledge

    /// <summary>
    /// A tree's price, rolled once per tree on first visit: free, 2000 gold or 10 gems (the three offers of
    /// Text/Interface/TreeOfKnowledge desc_free/desc_gold/desc_gems). TODO(exe): the odds are not traced
    /// (the command carries the price, +0x18); one in three each.
    /// </summary>
    public Resources TreeOfKnowledgePrice(int tree)
    {
        var os = S.Obj(tree);
        return os.Variant switch
        {
            1 => new Resources(0, 0, 0, 0, 0, 0, 2000),
            2 => new Resources(0, 0, 0, 0, 0, 10, 0),
            _ => new Resources(0, 0, 0, 0, 0, 0, 0),
        };
    }

    void VisitTreeOfKnowledge(HeroState h, MapObject o)
    {
        var os = S.Obj(o.Index);
        if (os.Variant < 0) os.Variant = S.Rng.Next(3);
        Emit(new ObjectVisited(h.Id, o.Index, h.VisitedOnce.Contains(o.Index) ? "BUILDING_TREE_OF_KNOWLEDGE:visited" : "BUILDING_TREE_OF_KNOWLEDGE"));
    }

    /// <summary>Pays the price and grants exactly the experience to the next level (FUN_00b4c6b0 difference), once per hero.</summary>
    string? DoUseTreeOfKnowledge(UseTreeOfKnowledgeCmd c)
    {
        var p = Current;
        var h = S.Hero(c.HeroId);
        var o = Map.Objects.ElementAtOrDefault(c.Tree);
        if (o == null || o.Shared.Type != "BUILDING_TREE_OF_KNOWLEDGE") return "not a tree of knowledge";
        if (h == null || !h.Alive || h.Owner != p.Index || !HeroAt(h, o)) return "hero must visit the tree";
        if (h.VisitedOnce.Contains(c.Tree)) return "already visited";
        int maxLevel = Map.HeroMaxLevel > 0 ? Map.HeroMaxLevel : 40;
        if (h.Level >= maxLevel) return "maximum level reached";
        if (S.Obj(c.Tree).Variant < 0) return "visit the tree first";
        var price = TreeOfKnowledgePrice(c.Tree);
        if (!p.Purse.CanAfford(price)) return "not enough resources";
        Give(p, Purse.Scale(price, -1));
        h.VisitedOnce.Add(c.Tree);
        long need = Core.Experience.For(h.Level + 1) - h.Experience;
        if (need > 0)
        {
            h.Experience += need;
            Emit(new ExperienceGained(h.Id, need, h.Experience));
        }
        LevelUp(h);
        return null;
    }
}
