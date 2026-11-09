using H5.Adventure.AI;
using H5.Adventure.Map;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Adventure AI part 6 (docs/rules/AUDIT_AI.md): the grail guess, the road field, the flood rewrite.</summary>
[Collection("adv")]
public class AiPart6Tests(Fixture f)
{
    [Fact]
    public void Road_type_is_the_tile_record_plus_4()
    {
        // 00990800: the map edge keeps the 00bcc5d0 default 9, an object's passable tile gets 7 (gravel), others 00c0f0a0.
        var g = f.NewGame("A2M13");
        var fl = g.Map.Floor(0)!;
        Assert.Equal(fl.Width * fl.Height, fl.Road.Length);
        for (int i = 0; i < fl.Road.Length; i++)
        {
            if (fl.Kind[i] == TileKind.Edge && fl.Ground[i] == 1) Assert.Equal(9, fl.Road[i]);
            if (fl.Kind[i] == TileKind.ObjectPassable) Assert.Equal(TerrainPassability.TypeGravelRoad, fl.Road[i]);
            Assert.True(fl.Road[i] == 9 || fl.Road[i] is >= 6 and <= 8, $"road type {fl.Road[i]}");
        }
        // Some tiles of a stock map lie on roads.
        Assert.Contains(fl.Road, r => r is >= 6 and <= 8);
    }

    static void ExploreAll(PlayerState p) { foreach (var a in p.Explored) Array.Fill(a, true); }

    static void VisitAllObelisks(Engine.Adventure g, PlayerState p)
    {
        foreach (var o in g.Map.Objects.Where(o => o.Shared.Type == "BUILDING_OBELISK")) p.VisitedObjects.Add(o.Index);
    }

    [Fact]
    public void Grail_guess_with_the_whole_puzzle_open_finds_the_grail()
    {
        // 00bc6530: with every piece open and the whole map explored, the true window matches every template cell
        // (score 221 − 0), and it is the only survivor here.
        var g = f.NewGame("A2M13");
        var st = AdventureAiStats.Load(f.Core);
        var p = g.Current;
        ExploreAll(p);
        VisitAllObelisks(g, p);
        Assert.Equal(36, g.PuzzlePiecesOpen(p));
        var state = new OriginalAdventureAI.GrailSearchState();
        var guess = OriginalAdventureAI.GuessGrail(g, st.Difficulty[g.S.Difficulty], p.Index, state);
        Assert.NotNull(guess);
        Assert.Equal(g.S.Grail, guess!.Value.Tile);
        Assert.InRange(guess.Value.Probability, 0.0, 1.0);
        // The surviving windows are kept per floor for the next day.
        Assert.NotNull(state.Floors[g.S.Grail!.Value.Floor]);
    }

    [Fact]
    public void Grail_guess_needs_the_open_parts_minimum_unless_forced()
    {
        var g = f.NewGame("A2M13");
        var st = AdventureAiStats.Load(f.Core);
        var p = g.Current;
        Assert.True(g.PuzzlePiecesOpen(p) < st.Difficulty[g.S.Difficulty].GrailSearchOpenPartsMinimum);
        Assert.Null(OriginalAdventureAI.GuessGrail(g, st.Difficulty[g.S.Difficulty], p.Index, new OriginalAdventureAI.GrailSearchState()));
    }

    [Fact]
    public void Grail_template_blocks_follow_the_piece_order()
    {
        // DAT_00f78250 is a permutation of the 36 pieces.
        Assert.Equal(Enumerable.Range(0, 36), OriginalAdventureAI.PuzzlePieceOrder.OrderBy(x => x));
    }

    [Fact]
    public void Puzzle_phase_stores_the_guess_and_a_hero_on_it_digs()
    {
        // 00428790 (phase 0): the grail value 00bc7340 (at least ValueOfGrailMin) and the guess are stored; the hero's
        // search gets the grail record (00a7ae20) and, standing on it, digs (0086c760 → CDigCmd).
        var g = f.NewGame("A2M13");
        var st = AdventureAiStats.Load(f.Core);
        var p = g.Current;
        ExploreAll(p);
        VisitAllObelisks(g, p);
        var ai = new OriginalAdventureAI();
        var diff = st.Difficulty[Math.Clamp(g.S.Difficulty, 0, 3)];
        ai.SolvePuzzleMap(g, st, diff, p.Index);
        var state = ai.GrailState(p.Index)!;
        Assert.Equal(g.S.Grail, state.Guess);
        Assert.True(state.Value >= st.ValueOfGrailMin);
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == p.Index);
        foreach (var x in g.S.Heroes.Where(x => x.Owner == p.Index && x.Id != h.Id)) x.Alive = false;
        h.Pos = g.S.Grail!.Value;
        h.InTown = -1;
        h.Garrisoned = false;
        h.MovePoints = h.MaxMovePoints;
        // The grail is worth only ValueOfGrailMin here (no grail objective, no Tear for the building): take the map's
        // other destinations away so the record is the only one.
        foreach (var o in g.Map.Objects) if (o.Kind is not ObjectKind.Town) g.S.Obj(o.Index).Removed = true;
        foreach (var x in g.S.Heroes.Where(x => x.Owner != p.Index)) x.Alive = false;
        ai.PlayTurn(g, 77);
        Assert.True(g.S.GrailFound || g.S.Holes.Contains(g.S.Grail!.Value), $"value {state.Value}, probability {state.Probability}");
    }
}
