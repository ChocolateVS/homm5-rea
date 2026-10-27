using H5.Adventure.Engine;
using HeroState = H5.Adventure.State.HeroState;

namespace H5.Adventure.AI;

// Adventure-map spells of the computer players: Instant Travel (SPELL_DIMENSION_DOOR) in the path search and in
// the step decision. Exe: settings FUN_00ceb190 (called with the spell flag 1 by 00ceb670 for the AI), flood
// FUN_00c08890 / 00c07f80 / 00c09d90 (see Adventure.SpellFlood), step decision FUN_008675f0 (from 0086b6d0).
// GAP: Town Portal (+0x34: extra start nodes from pathfinder +0x98 at priority land move + 200 + (n − 1)·500,
// flag 0x20, in 00c08be0) and Vessel of Shalassa (+0x35: boarding edge 0x2000002 +500 through the boat provider
// pathfinder +0xa4 vf 0xc, executed by 008675f0 with command +0x32) are decoded but not ported.
public sealed partial class OriginalAdventureAI
{
    /// <summary>
    /// Instant Travel settings of a computer hero (exe FUN_00ceb190 with flag 1): enabled when the hero knows the spell
    /// (vf 0xdc(0x32)) and can cast it (vf 0x154); movement cost vf 0xfc / 2; count (mana − 20) / cost (≥ 0);
    /// DimDoorStartCostLimit from the AI block (+0x228).
    /// GAP-details: vf 0x154 is taken as "mana ≥ cost"; the cost is the spell data's (FUN_00a34740 modifiers not
    /// applied); vf 0xfc is taken as MaxMovePoints.
    /// </summary>
    public static DimensionDoorSettings DimensionDoorSettingsFor(Engine.Adventure g, HeroState h, AdventureAiStats st)
    {
        int cost = g.AdventureSpellCost(Engine.Adventure.SpellDimensionDoor);
        bool can = h.Spells.Contains(Engine.Adventure.SpellDimensionDoor) && h.Mana >= cost;
        int count = cost > 0 ? Math.Max(0, (h.Mana - 20) / cost) : 0;
        return new DimensionDoorSettings(can, h.MaxMovePoints / 2, count, st.DimDoorStartCostLimit);
    }

    /// <summary>Saving thresholds of 008675f0 (table 00f44124 indexed by min(count, 3)), in percent of the day's movement.</summary>
    static readonly int[] DimDoorSavingPercent = { -1, 200, 100, 50 };

    public enum DimDoorStepKind { Normal, Walk, Cast, Stop }

    /// <summary>
    /// The Instant Travel part of the step decision (exe FUN_008675f0 from LAB_00867945) over the route to the chosen
    /// destination. Threshold T = table[min(count, 3)] · day's movement / 100 (−1 %, 200 %, 100 %, 50 %), or 200 when the
    /// command's +0x38 is &gt; 0 and count &gt; 1. From a start node s (first the hero's), every later node j that may
    /// end a move (P, not an action tile), has the start's boat state, is on its floor with dx² + dy² &lt; 65 and passes
    /// the landing check is a candidate when its saving cost(j) − spell move cost − (cost(s) + 200) exceeds T (T ≥ 1)
    /// or when the route reached a landing node (w1 0x4000000) at or before j; the last candidate wins. With a
    /// candidate, the first better saving keeps it; a later start node (the next P node of the same floor and boat
    /// state before the candidate, not a landing) is tried and, when its saving is higher, the hero walks to it first.
    /// Otherwise the spell is cast from the hero's tile to the candidate (when the node is not over the limit and the
    /// cast check 008652f0 passes; else the hero stops). With no candidate but a landing on the route, the hero walks to
    /// the last node before it; with neither, it moves normally.
    /// GAP-details: the command's +0x38 count (AI +0xb0) is not traced (callers pass 0 here); the first loop of 008675f0
    /// (boat nodes 0x1000000 / 0x2000000 and the re-search every third node via 00a7b630) is not ported.
    /// </summary>
    public static (DimDoorStepKind Kind, int Index) DimensionDoorStep(IReadOnlyList<FloodNode> route, int moveCost, int count,
                                                                      int dailyMove, int command38, Func<FloodNode, bool> landingOk)
    {
        int n = route.Count;
        if (n <= 0) return (DimDoorStepKind.Normal, 0);
        int k = Math.Min(count, 3);
        int thr = DimDoorSavingPercent[Math.Max(0, k)] * dailyMove / 100;
        if (command38 > 0 && k > 1) thr = 200;
        bool boat0 = route[0].Boat;
        var startPos = route[0].Tile;
        int s = 0, best = -1, bestSave = 0;
        while (true)
        {
            int baseCost = route[s].Cost + 200, save = 0, cand = -1, lastBefore = s;
            bool seen = false;
            for (int j = s + 1; j < n; j++)
            {
                var r = route[j];
                if (!r.Landing) { if (!seen) lastBefore = j; }
                else seen = true;
                if (!r.CanStop || r.Action || r.Boat != boat0 || r.Tile.Floor != startPos.Floor) continue;
                int dx = r.Tile.X - startPos.X, dy = r.Tile.Y - startPos.Y;
                if (dx * dx + dy * dy >= 0x41 || !landingOk(r)) continue;
                save = r.Cost - moveCost - baseCost;
                bool big = thr >= 1 && save > thr;
                if (command38 < 1 || !big || j + 1 != n)
                {
                    if (!seen && !big) continue;
                }
                else seen = true;
                cand = j;
            }
            if (s == 0 && cand == -1)
                return seen ? (DimDoorStepKind.Walk, lastBefore) : (DimDoorStepKind.Normal, 0);
            if (best == -1 || bestSave < save)
            {
                if (s > 0) return (DimDoorStepKind.Walk, s);
                best = cand;
                bestSave = save;
            }
            int s2 = s + 1;
            bool found = false;
            while (s2 < best)
            {
                var r = route[s2];
                if (!r.CanStop || r.Landing) break;
                if (r.Tile.Floor == startPos.Floor && r.Boat == boat0) { found = true; break; }
                s2++;
            }
            if (!found) return best < 0 ? (DimDoorStepKind.Stop, 0) : (DimDoorStepKind.Cast, best);
            s = s2;
            startPos = route[s].Tile;
        }
    }

    /// <summary>The flood and settings of the last <see cref="ChooseDestination"/> call (for the step decision).</summary>
    SpellFloodResult? _spellFlood;
    DimensionDoorSettings _dd;

    /// <summary>
    /// Sends the step towards <paramref name="target"/>: the Instant Travel decision when the spell is available, else
    /// (and for <see cref="DimDoorStepKind.Normal"/>) the plain move. Returns the result and whether the destination
    /// itself was the move's goal (only then is it claimed).
    /// </summary>
    (AdvResult? Result, bool Whole) StepTowards(Engine.Adventure g, HeroState h, Destination target, int me)
    {
        if (_dd.Enabled && _spellFlood?.Route(target.Tile) is { } route)
        {
            var (kind, i) = DimensionDoorStep(route, _dd.MoveCost, _dd.Count, h.MaxMovePoints, 0,
                                              n => g.DimensionDoorLandingOk(h, n.Tile, n.Boat));
            switch (kind)
            {
                case DimDoorStepKind.Stop:
                    return (null, false);
                case DimDoorStepKind.Cast:
                    return (Send(g, new CastAdventureSpellCmd(h.Id, Engine.Adventure.SpellDimensionDoor, route[i].Tile) { Player = me }), false);
                case DimDoorStepKind.Walk:
                    return (Send(g, new MoveHeroCmd(h.Id, route[i].Tile) { Player = me }), false);
            }
        }
        return (Send(g, new MoveHeroCmd(h.Id, target.Tile) { Player = me }), true);
    }
}
