using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>
/// The army screens' drop and split rules (town rows 00615670, hero screen 006c5e60, garrison 00652b70, hero meeting
/// 00649e70) and the adventure split controller (base 00786c00, hero-screen vtable 00e35028), turned into commands.
/// </summary>
public static class ArmySplit
{
    /// <summary>One army slot: a hero, a town garrison / flagged building (Town), or a caravan, with that army's stacks.</summary>
    public readonly record struct Side(int Hero, int Town, int Caravan, int Slot, Stack?[]? Army)
    {
        public Stack? Stack => Army != null && Slot >= 0 && Slot < Army.Length && Army[Slot] is { Count: > 0 } s ? s : null;
        public bool SameArmy(Side o) => ReferenceEquals(Army, o.Army);
    }

    /// <summary>
    /// A plain drop of <paramref name="a"/>'s slot on <paramref name="b"/>: a stack dropped on the same creature merges
    /// whole into it (merge command 0061f970, chosen by every screen when the two creature types match — inside one army
    /// too); anything else moves or exchanges the slots (0061f8b0). Null when there is nothing to do.
    /// </summary>
    public static AdvCommand? Drop(Side a, Side b)
    {
        if (a.SameArmy(b) && a.Slot == b.Slot) return null;
        var x = a.Stack;
        var y = b.Stack;
        if (x == null && y == null) return null;
        if (x != null && y != null && x.Creature == y.Creature)
            return new TransferStackCmd(a.Hero, a.Town, a.Slot, b.Hero, b.Town, b.Slot, x.Count) { FromCaravan = a.Caravan, ToCaravan = b.Caravan };
        return new SwapStacksCmd(a.Hero, a.Town, a.Slot, b.Hero, b.Town, b.Slot) { FromCaravan = a.Caravan, ToCaravan = b.Caravan };
    }

    /// <summary>
    /// The split controller's minimum for one side (vf 0x2c, 007868d0): 1 when that side belongs to a hero, the other
    /// side is another holder and the hero's army holds at most one stack (a hero keeps a creature); otherwise 0.
    /// </summary>
    public static int Min(Side side, Side other) =>
        side.Hero >= 0 && side.Hero != other.Hero && side.Army != null && side.Army.Count(s => s is { Count: > 0 }) <= 1 ? 1 : 0;

    /// <summary>
    /// Whether the split window opens (controller vf 0xc, 00786800; 00587cf0 opens nothing otherwise): the dragged slot
    /// holds a stack, and the target is empty with the stack above the dragged side's minimum, or holds the same creature
    /// with the two minimums below the total. Two different creatures never split.
    /// </summary>
    public static bool CanSplit(Side from, Side to)
    {
        if (from.SameArmy(to) && from.Slot == to.Slot) return false;
        var x = from.Stack;
        var y = to.Stack;
        if (x == null) return false;
        if (y == null) return Min(from, to) < x.Count;
        return x.Creature == y.Creature && Min(to, from) + Min(from, to) < x.Count + y.Count;
    }

    /// <summary>
    /// The window's starting value, the target's new size (controller init 007869a0): (total + 1) / 2 for an empty
    /// target, else the target's size; clamped to [<paramref name="lo"/> = target minimum, <paramref name="hi"/> =
    /// total − dragged side's minimum].
    /// </summary>
    public static int StartValue(Side from, Side to, out int lo, out int hi)
    {
        int total = (from.Stack?.Count ?? 0) + (to.Stack?.Count ?? 0);
        lo = Min(to, from);
        hi = total - Min(from, to);
        int v = to.Stack == null ? (total + 1) / 2 : to.Stack.Count;
        return Math.Max(Math.Min(v, hi), lo);
    }

    /// <summary>
    /// The commands that leave the target with <paramref name="value"/> creatures (apply 00786af0, range test as there):
    /// the difference moves from the dragged stack to the target, or back. Empty when nothing changes or out of range.
    /// </summary>
    public static List<AdvCommand> SplitCommands(Side from, Side to, int value)
    {
        var list = new List<AdvCommand>();
        if (!CanSplit(from, to)) return list;
        int total = from.Stack!.Count + (to.Stack?.Count ?? 0);
        if (value < Min(to, from) || total - value < Min(from, to)) return list;
        int diff = value - (to.Stack?.Count ?? 0);
        if (diff > 0)
            list.Add(new TransferStackCmd(from.Hero, from.Town, from.Slot, to.Hero, to.Town, to.Slot, diff) { FromCaravan = from.Caravan, ToCaravan = to.Caravan });
        else if (diff < 0)
            list.Add(new TransferStackCmd(to.Hero, to.Town, to.Slot, from.Hero, from.Town, from.Slot, -diff) { FromCaravan = to.Caravan, ToCaravan = from.Caravan });
        return list;
    }
}
