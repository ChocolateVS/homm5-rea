using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

// Towns: building, recruiting, taverns, blacksmith, mage guild, armies.
public sealed partial class Adventure
{
    public const int HeroHireCost = 2500;   // TODO(exe): confirm (2500 gold as in the stock tavern UI)
    public const int MaxHeroesPerPlayer = 8; // TODO(exe): confirm the TotE hero cap

    void VisitTown(HeroState h, TownState t)
    {
        if (t.Owner == h.Owner || Allied(t.Owner, h.Owner))
        {
            h.InTown = t.Object;
            LearnFromGuild(h, t);
            if (t.Level("TB_MAGIC_GUILD") > 0) h.Mana = Math.Max(h.Mana, MaxMana(h));
            Emit(new ObjectVisited(h.Id, t.Object, "town"));
            return;
        }
        var garrisonHero = S.Heroes.Find(x => x.Alive && x.InTown == t.Object && x.Garrisoned);
        var defenders = garrisonHero != null ? garrisonHero.Army.Live().ToList() : t.Garrison.Live().ToList();
        if (defenders.Count == 0 && garrisonHero == null)
        {
            CaptureTown(h, t);
            return;
        }
        StartBattle(h, garrisonHero?.Id ?? -1, t.Object, t.Pos, defenders.Select(s => new Stack(s.Creature, s.Count)).ToList(),
            siege: t.Level("TB_FORT") > 0);
    }

    void CaptureTown(HeroState h, TownState t)
    {
        var prev = t.Owner;
        t.Owner = h.Owner;
        h.InTown = t.Object;
        Emit(new ObjectCaptured(t.Object, h.Owner));
        // Conquering a town is worth 500 experience (p.298).
        GainExperience(h, 500);
        if (prev >= 0 && PlayerByIndex(prev) is { } pp) RevealOwned(pp);
        RevealOwned(PlayerByIndex(h.Owner)!);
        CheckEliminations();
    }

    void LearnFromGuild(HeroState h, TownState t)
    {
        int lvl = t.Level("TB_MAGIC_GUILD");
        for (int i = 0; i < lvl && i < t.MageGuild.Count; i++)
            foreach (var sp in t.MageGuild[i]) LearnSpell(h, sp, force: false);
    }

    string? DoBuild(BuildCmd c)
    {
        var t = S.Town(c.TownObject);
        if (t == null || t.Owner != Current.Index) return "not your town";
        if (t.BuiltToday) return "already built today";
        int next = t.Level(c.Building) + 1;
        if (next > t.MaxLevel.GetValueOrDefault(c.Building)) return "cannot build further";
        var def = Db.Building(t.Type, c.Building, next);
        if (def == null) return "unknown building";
        if (t.DevLevel < def.DevLevelNeeded) return $"requires town level {def.DevLevelNeeded}";
        foreach (var (type, level) in def.Dependencies)
            if (t.Level(type) < level) return $"requires {type} {level}";
        var p = Current;
        if (!p.Purse.CanAfford(def.Cost)) return "not enough resources";
        Give(p, Purse.Scale(def.Cost, -1));
        int trainingBefore = TrainingPointsMax(t);
        t.Built[c.Building] = next;
        TrainingPointsAfterBuild(t, trainingBefore);
        t.BuiltToday = true;
        // A new dwelling starts with one week of creatures (TODO(exe): confirm for H5).
        if (c.Building.StartsWith("TB_DWELLING_") && next == 1 && int.TryParse(c.Building[^1..], out var tier))
            t.Available[tier - 1] += WeeklyGrowth(t, tier);
        Emit(new BuildingBuilt(t.Object, c.Building, next));
        if (c.Building == ArtifactMerchantBuilding) StockArtifactMerchants();
        foreach (var h in S.Heroes.Where(h => h.Alive && h.InTown == t.Object && c.Building == "TB_MAGIC_GUILD")) LearnFromGuild(h, t);
        return null;
    }

    /// <summary>Town state shown by the hero/town list marker (UI TownHeroPanel: BuildDone, BuildAllComplete, NothingBuild, BuildNoResources).</summary>
    public enum TownBuildState { BuiltToday, AllComplete, CanBuild, NoResources }

    /// <summary>
    /// What the adventure HUD marker shows for a town: already built today, every building at its
    /// maximum level, something affordable and allowed today, or nothing that can be built now.
    /// Uses the same checks as <see cref="DoBuild"/> against the owner's purse.
    /// </summary>
    public TownBuildState BuildState(TownState t)
    {
        if (t.BuiltToday) return TownBuildState.BuiltToday;
        var purse = PlayerByIndex(t.Owner)?.Purse;
        bool anyLeft = false;
        foreach (var (building, max) in t.MaxLevel)
        {
            int next = t.Level(building) + 1;
            if (next > max) continue;
            var def = Db.Building(t.Type, building, next);
            if (def == null) continue;
            anyLeft = true;
            if (t.DevLevel < def.DevLevelNeeded) continue;
            if (def.Dependencies.Any(d => t.Level(d.Type) < d.Level)) continue;
            if (purse != null && purse.CanAfford(def.Cost)) return TownBuildState.CanBuild;
        }
        return anyLeft ? TownBuildState.NoResources : TownBuildState.AllComplete;
    }

    /// <summary>Creature offered by a town dwelling tier: variant 0 base, 1 upgrade, 2 alternative upgrade (TotE).</summary>
    public string? DwellingCreature(TownState t, int tier, int variant)
    {
        int lvl = t.Level($"TB_DWELLING_{tier}");
        if (lvl <= 0 || (variant > 0 && lvl < 2)) return null;
        var def = Db.Building(t.Type, $"TB_DWELLING_{tier}", variant == 0 ? 1 : 2);
        return variant switch { 0 => def?.Creature, 1 => def?.Creature, 2 => def?.Creature2, _ => null };
    }

    string? DoRecruit(RecruitCmd c)
    {
        if (c.Count <= 0) return "invalid count";
        var p = Current;
        string? creature;
        int[] pool;
        int slot;
        Stack?[] target;
        HeroState? visitor = null;
        if (S.Town(c.TownObject) is { } t)
        {
            if (t.Owner != p.Index) return "not your town";
            creature = DwellingCreature(t, c.Tier, c.Variant);
            pool = t.Available;
            slot = c.Tier - 1;
            visitor = S.Heroes.Find(h => h.Alive && h.InTown == t.Object && !h.Garrisoned && h.Owner == p.Index);
            target = visitor?.Army ?? t.Garrison;
        }
        else if (S.Objects.TryGetValue(c.TownObject, out var os) && O(c.TownObject).Kind == ObjectKind.Dwelling)
        {
            if (os.Owner != p.Index) return "not your dwelling";
            var list = O(c.TownObject).Shared.DwellingCreatures;
            slot = c.Tier - 1;
            if (slot < 0 || slot >= list.Count) return "invalid tier";
            creature = list[slot];
            pool = os.Available;
            visitor = S.Heroes.Find(h => h.Alive && h.Owner == p.Index && O(c.TownObject).ActiveTiles.Contains(h.Pos));
            if (visitor == null) return "a hero must visit the dwelling";
            target = visitor.Army;
        }
        else return "no such town or dwelling";

        if (creature == null || !Core.Creatures.TryGetValue(creature, out var cd)) return "creature not available";
        if (slot < 0 || slot >= pool.Length || pool[slot] < c.Count) return "not enough creatures";
        var cost = Purse.Scale(cd.Cost, c.Count);
        if (!p.Purse.CanAfford(cost)) return "not enough resources";
        var trial = target.Select(s => s == null ? null : new Stack(s.Creature, s.Count)).ToArray();
        if (!trial.TryAdd(creature, c.Count)) return "no free army slot";
        target.TryAdd(creature, c.Count);
        pool[slot] -= c.Count;
        Give(p, Purse.Scale(cost, -1));
        Emit(new CreaturesRecruited(c.TownObject, creature, c.Count));
        Emit(new ArmyChanged(visitor?.Id ?? -1, visitor == null ? c.TownObject : -1));
        return null;
    }

    string? DoHire(HireHeroCmd c)
    {
        var t = S.Town(c.TownObject);
        var p = Current;
        if (t == null || t.Owner != p.Index) return "not your town";
        if (t.Level("TB_TAVERN") <= 0) return "no tavern";
        // The slot indexes the heroes this town's tavern shows (town hiring filter, exe FUN_00636a10).
        var offers = TownTavernOffers(p, t);
        if (c.TavernSlot < 0 || c.TavernSlot >= offers.Length || offers[c.TavernSlot] is not { } defId) return "no hero in that slot";
        if (S.Heroes.Count(h => h.Alive && h.Owner == p.Index) >= HiringHeroLimit(p)) return "too many heroes";
        var entrance = EntranceOf(t);
        if (HeroAt(entrance) != null) return "the town entrance is occupied";
        if (p.Purse[Purse.Gold] < HeroHireCost) return "not enough gold";
        var def = Core.Heroes[defId];
        var h = HeroFromDef(def, p.Index, entrance);
        GiveStartingArmy(h);
        h.InTown = t.Object;
        h.MaxMovePoints = ComputeMaxMovePoints(h);
        h.MovePoints = h.MaxMovePoints;
        S.Heroes.Add(h);
        S.HeroPool.Remove(defId);
        foreach (var other in S.Players) for (int i = 0; i < other.Tavern.Length; i++) if (other.Tavern[i] == defId) other.Tavern[i] = null;
        GiveOne(p, Purse.Gold, -HeroHireCost);
        Emit(new HeroHired(h.Id, defId, t.Object));
        LearnFromGuild(h, t);
        RevealAround(h);
        return null;
    }

    string? DoDismiss(DismissHeroCmd c)
    {
        var h = S.Hero(c.HeroId);
        if (h == null || h.Owner != Current.Index) return "not your hero";
        if (S.Heroes.Count(x => x.Alive && x.Owner == h.Owner) == 1 && !S.Towns.Any(t => t.Owner == h.Owner))
            return "cannot dismiss your last hero";
        RemoveHero(h, "dismissed");
        return null;
    }

    void RemoveHero(HeroState h, string reason)
    {
        h.Alive = false;
        if (h.DefId.Length > 0 && !S.HeroPool.Contains(h.DefId)) S.HeroPool.Add(h.DefId);
        Emit(new HeroRemoved(h.Id, reason));
    }

    /// <summary>Blacksmith: the town's native war machine at normal price, the others at triple price (p.225).</summary>
    string? DoBuyWarMachine(BuyWarMachineCmd c)
    {
        var t = S.Town(c.TownObject);
        var h = S.Hero(c.HeroId);
        var p = Current;
        if (t == null || t.Owner != p.Index || h == null || h.Owner != p.Index || h.InTown != t.Object) return "hero must be in your town";
        if (t.Level("TB_BLACKSMITH") <= 0) return "no blacksmith";
        if (!Core.WarMachines.TryGetValue(c.Machine, out var wm)) return "unknown war machine";
        bool owned = c.Machine switch
        {
            "WAR_MACHINE_BALLISTA" => h.Ballista,
            "WAR_MACHINE_FIRST_AID_TENT" => h.FirstAidTent,
            "WAR_MACHINE_AMMO_CART" => h.AmmoCart,
            _ => true,
        };
        if (owned) return "already owned";
        int mul = Db.NativeWarMachine.GetValueOrDefault(t.Type) == c.Machine ? 1 : 3;
        var cost = Purse.Scale(wm.Cost, mul);
        if (!p.Purse.CanAfford(cost)) return "not enough resources";
        Give(p, Purse.Scale(cost, -1));
        switch (c.Machine)
        {
            case "WAR_MACHINE_BALLISTA": h.Ballista = true; break;
            case "WAR_MACHINE_FIRST_AID_TENT": h.FirstAidTent = true; break;
            case "WAR_MACHINE_AMMO_CART": h.AmmoCart = true; break;
        }
        Emit(new ArmyChanged(h.Id, -1));
        return null;
    }

    /// <summary>Moves or splits creatures between two armies that are at the same place.</summary>
    string? DoTransfer(TransferStackCmd c)
    {
        Stack?[]? Army(int hero, int town, out Tile? where)
        {
            where = null;
            if (hero >= 0 && S.Hero(hero) is { } h && h.Owner == Current.Index) { where = h.Pos; return h.Army; }
            if (town >= 0 && S.Town(town) is { } t && t.Owner == Current.Index) { where = EntranceOf(t); return t.Garrison; }
            return null;
        }
        var from = Army(c.FromHero, c.FromTown, out var wFrom);
        var to = Army(c.ToHero, c.ToTown, out var wTo);
        if (from == null || to == null) return "invalid armies";
        if (wFrom is { } a && wTo is { } b && Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)) > 1) return "armies are not together";
        if (c.FromSlot is < 0 or >= State.Army.Slots || c.ToSlot is < 0 or >= State.Army.Slots) return "invalid slot";
        var src = from[c.FromSlot];
        if (src is not { Count: > 0 } || c.Count <= 0 || c.Count > src.Count) return "invalid count";
        var dst = to[c.ToSlot];
        if (dst is { Count: > 0 } && dst.Creature != src.Creature) return "slot holds other creatures";
        // A hero must keep at least one stack.
        if (ReferenceEquals(from, to) == false && c.FromHero >= 0 && c.Count == src.Count && from.StackCount() == 1) return "a hero cannot be left without troops";
        if (dst is { Count: > 0 }) dst.Count += c.Count; else to[c.ToSlot] = new Stack(src.Creature, c.Count);
        src.Count -= c.Count;
        if (src.Count == 0) from[c.FromSlot] = null;
        Emit(new ArmyChanged(c.FromHero, c.FromTown));
        Emit(new ArmyChanged(c.ToHero, c.ToTown));
        return null;
    }
}
