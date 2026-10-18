using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Engine;

// Town/hero screen operations: swapping whole army slots, moving a hero between a town's
// visiting and garrison rows, and moving artifacts between worn slots, backpack and heroes.
public sealed partial class Adventure
{
    /// <summary>Worn-slot capacity per artifact slot type (Artifacts.xdb Slot); FINGER takes two rings.</summary>
    public static int SlotCapacity(string slot) => slot switch
    {
        "FINGER" => 2,
        "INVENTORY" or "" => 0,
        _ => 1,
    };

    /// <summary>A town dwelling tier's weekly growth (for the fort screen's plates).</summary>
    public int WeeklyGrowthOf(TownState t, int tier) => WeeklyGrowth(t, tier);

    Stack?[]? ArmyAt(int hero, int town, out Tile? where)
    {
        where = null;
        if (hero >= 0 && S.Hero(hero) is { } h && h.Owner == Current.Index) { where = h.Pos; return h.Army; }
        if (town >= 0 && S.Town(town) is { } t && t.Owner == Current.Index) { where = EntranceOf(t); return t.Garrison; }
        return null;
    }

    string? DoSwap(SwapStacksCmd c)
    {
        var from = ArmyAt(c.FromHero, c.FromTown, out var wFrom);
        var to = ArmyAt(c.ToHero, c.ToTown, out var wTo);
        if (from == null || to == null) return "invalid armies";
        if (wFrom is { } a && wTo is { } b && Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)) > 1) return "armies are not together";
        if (c.FromSlot is < 0 or >= Army.Slots || c.ToSlot is < 0 or >= Army.Slots) return "invalid slot";
        if (ReferenceEquals(from, to) && c.FromSlot == c.ToSlot) return "same slot";
        var x = from[c.FromSlot];
        var y = to[c.ToSlot];
        if (x is not { Count: > 0 } && y is not { Count: > 0 }) return "both slots are empty";
        // Same creature in different armies merges instead (the stock drag-and-drop behaviour).
        if (!ReferenceEquals(from, to) && x is { Count: > 0 } && y is { Count: > 0 } && x.Creature == y.Creature)
            return DoTransfer(new TransferStackCmd(c.FromHero, c.FromTown, c.FromSlot, c.ToHero, c.ToTown, c.ToSlot, x.Count) { Player = c.Player });
        // A hero must keep at least one stack.
        if (!ReferenceEquals(from, to))
        {
            if (c.FromHero >= 0 && y is not { Count: > 0 } && from.StackCount() == 1) return "a hero cannot be left without troops";
            if (c.ToHero >= 0 && x is not { Count: > 0 } && to.StackCount() == 1) return "a hero cannot be left without troops";
        }
        from[c.FromSlot] = y is { Count: > 0 } ? y : null;
        to[c.ToSlot] = x is { Count: > 0 } ? x : null;
        Emit(new ArmyChanged(c.FromHero, c.FromTown));
        if (!ReferenceEquals(from, to)) Emit(new ArmyChanged(c.ToHero, c.ToTown));
        return null;
    }

    /// <summary>
    /// Garrisoning merges the town's garrison troops into the hero's army (the garrison row then shows the
    /// hero's army); it fails when the merged army needs more than seven slots. Leaving the garrison needs
    /// a free visiting row (no other visiting hero).
    /// </summary>
    string? DoGarrison(GarrisonHeroCmd c)
    {
        var t = S.Town(c.TownObject);
        var h = S.Hero(c.HeroId);
        if (t == null || t.Owner != Current.Index || h == null || h.Owner != Current.Index || h.InTown != t.Object) return "hero must be in your town";
        if (c.Garrison == h.Garrisoned) return null;
        if (c.Garrison)
        {
            if (S.Heroes.Any(x => x.Alive && x != h && x.InTown == t.Object && x.Garrisoned)) return "another hero holds the garrison";
            var merged = h.Army.Select(s => s == null ? null : new Stack(s.Creature, s.Count)).ToArray();
            foreach (var s in t.Garrison.Live())
                if (!merged.TryAdd(s.Creature, s.Count)) return "not enough army slots to merge the garrison";
            h.Army = merged;
            Array.Fill(t.Garrison, null);
            h.Garrisoned = true;
        }
        else
        {
            if (S.Heroes.Any(x => x.Alive && x != h && x.InTown == t.Object && !x.Garrisoned)) return "a hero is already visiting";
            h.Garrisoned = false;
        }
        Emit(new ArmyChanged(h.Id, t.Object));
        return null;
    }

    string? DoMoveArtifact(MoveArtifactCmd c)
    {
        var h = S.Hero(c.HeroId);
        var target = S.Hero(c.ToHero);
        if (h == null || h.Owner != Current.Index || target == null || target.Owner != Current.Index) return "not your hero";
        if (h != target && Math.Max(Math.Abs(h.Pos.X - target.Pos.X), Math.Abs(h.Pos.Y - target.Pos.Y)) > 1) return "heroes are not together";
        var src = c.FromBackpack ? h.Backpack : h.Artifacts;
        if (!src.Contains(c.Artifact)) return "hero does not carry that artifact";
        if (!c.ToBackpack)
        {
            if (h == target && !c.FromBackpack) return "already worn";
            if (!Core.Artifacts.TryGetValue(c.Artifact, out var def)) return "unknown artifact";
            int cap = SlotCapacity(def.Slot);
            if (cap == 0) return "this artifact cannot be worn";
            int used = target.Artifacts.Count(a => Core.Artifacts.TryGetValue(a, out var d) && d.Slot == def.Slot);
            if (used >= cap) return "that slot is occupied";
        }
        else if (h == target && c.FromBackpack) return "already in the backpack";
        src.Remove(c.Artifact);
        (c.ToBackpack ? target.Backpack : target.Artifacts).Add(c.Artifact);
        Emit(new ArmyChanged(h.Id, -1));
        if (target != h) Emit(new ArmyChanged(target.Id, -1));
        return null;
    }
}
