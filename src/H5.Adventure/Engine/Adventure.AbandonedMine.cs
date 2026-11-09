using H5.Adventure.Map;

namespace H5.Adventure.Engine;

public sealed partial class Adventure
{
    /// <summary>
    /// The abandoned mine's resource, chosen when it is captured: the flagged interface's vf 0 (008b59d0, after the
    /// mine's 00b081d0) calls 008b5840 — a weighted draw over the mine's resource entries (weight clamped to ≥ 0; world
    /// random vf 0x98 → vf 0x20 modulo the total; none when the total is 0), stored by 00b08450. The weights are the map
    /// instance's &lt;AvailableResources&gt; by resource index (the entry list is reached through SAdvMapAbanMine +0x7c →
    /// 004a4e20 → +0xec; that it is this list is UNVERIFIED, entries past the 7th are ignored); our RNG stream.
    /// </summary>
    int AbandonedMineResource(MapObject o)
    {
        var w = (o.AbanMineWeights ?? Array.Empty<int>()).Take(7).Select(x => Math.Max(0, x)).ToArray();
        int total = w.Sum();
        if (total <= 0) return -1;
        int r = S.Rng.Next(total), acc = 0;
        for (int i = 0; i < w.Length; i++) { acc += w[i]; if (r < acc) return i; }
        return -1;
    }

    /// <summary>008b54a0 (jump table 008b55e4): 2 mercury 1, 3 crystal 1, 4 sulfur 1, 5 gems 1, 6 gold 1000; else none.</summary>
    public static (int Kind, int Amount) AbandonedMineYield(int resource) => resource switch
    {
        2 or 3 or 4 or 5 => (resource, 1),
        6 => (6, 1000),
        _ => (-1, 0),
    };
}
