using H5.Adventure.Map;

namespace H5.Adventure.Tests;

/// <summary>
/// The AI's destination flood (<c>ReachableCosts(…, attacks: true)</c>, used by the destination search for the exe's wave
/// spread 0086c010 → 00c08be0): a guarded tile ends a move, but a monster next to it can be attacked from there, so the
/// monster's tile is a destination at the cost of its cheapest guarded tile. Without it no neutral stack the hero does not
/// already stand next to was ever a destination (the AI never fought; A2M7 / A2M13 autoplay).
/// </summary>
[Collection("adv")]
public class AiFloodTests(Fixture f)
{
    [StockFact]
    public void MonstersBeyondTheirGuardZoneAreDestinationsAtTheGuardedTileCost()
    {
        var g = f.NewGame("A2M13", 1);
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == 0);
        var plain = g.ReachableCosts(h, 32000);
        var attack = g.ReachableCosts(h, 32000, attacks: true);
        var monsters = g.Map.Objects.Where(o => o.Kind == ObjectKind.Monster && o.Pos.Floor == h.Pos.Floor
                                               && !(g.S.Objects.TryGetValue(o.Index, out var os) && os.Removed)).ToList();
        var reached = monsters.Where(m => attack.ContainsKey(m.Pos)).ToList();
        Assert.NotEmpty(reached);
        foreach (var m in reached)
        {
            Assert.False(plain.ContainsKey(m.Pos));
            int best = int.MaxValue;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if ((dx, dy) != (0, 0) && plain.TryGetValue(m.Pos.Offset(dx, dy), out int c)) best = Math.Min(best, c);
            Assert.Equal(best, attack[m.Pos]);
        }
        // Every other tile keeps its cost: the attack step only adds the monsters' tiles.
        foreach (var (t, c) in plain) Assert.Equal(c, attack[t]);
        Assert.Equal(plain.Count + reached.Count, attack.Count);
    }
}
