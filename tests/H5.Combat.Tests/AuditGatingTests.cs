using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Inferno Gating confirmed against H5_Game.exe (docs/rules/audit/combat_gating.md).</summary>
[Collection("game")]
public class AuditGatingTests(GameDataFixture f)
{
    Battle Make(string inferno, int count, Mastery m, ulong seed = 3)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new() { new ArmySlot("CREATURE_FOOTMAN", 10) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new() { new ArmySlot(inferno, count) } },
            seed, obstacles: Array.Empty<Cell>(), skipTactics: true);
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); h.SetPieces = new(); }
        s.Heroes[1].Skills = new() { new SkillEntry("HERO_SKILL_GATING", m) };
        var b = new Battle(f.Db, s);
        b.Begin();
        var d = Demon(b);
        d.Pos = d.StartPos = new Cell(9, 4);
        b.State.Units.First(u => u.CreatureId == "CREATURE_FOOTMAN").Pos = new Cell(2, 4);
        b.State.Active = d.Id;
        return b;
    }

    static Unit Demon(Battle b) => b.State.Units.First(u => u.Side == 1 && u.Kind == UnitKind.Creature);

    static bool Offers(Battle b) => b.GetLegalActions()!.Abilities.Any(a => a.Ability == Ab.Gating && a.Targeting == Battle.AbilityTargeting.Cell);

    // exe FUN_00c54f50 / FUN_00860ce0: tier (record +0x8c) ≤ 2 × the hero's Gating mastery (vf 0x174(0xe)).
    [Fact]
    public void TierIsAtMostTwiceTheMastery()
    {
        Assert.True(Offers(Make("CREATURE_DEMON", 20, Mastery.Basic)));          // tier 2
        Assert.False(Offers(Make("CREATURE_HELL_HOUND", 20, Mastery.Basic)));    // tier 3
        Assert.True(Offers(Make("CREATURE_HELL_HOUND", 20, Mastery.Advanced)));
        Assert.False(Offers(Make("CREATURE_DEMON", 20, Mastery.None)));
    }

    // exe FUN_00860ce0 → FUN_00860c30 / vf 0xcc: the caster's footprint at the cursor cell must be free of obstacles and
    // units other than the caster (FUN_00b3fac0(0, 1, 0, caster)) and of the stacks still gating in (0x9f, vf 0xc8).
    [Fact]
    public void TheTargetCellMustBeFree()
    {
        var b = Make("CREATURE_IMP", 40, Mastery.Basic);
        var imp = Demon(b);
        var foot = b.State.Units.First(u => u.CreatureId == "CREATURE_FOOTMAN");
        Assert.False(b.CanGateAt(imp.Id, foot.Pos));
        Assert.True(b.CanGateAt(imp.Id, imp.Pos));                 // its own cell is not in the map
        Assert.True(b.CanGateAt(imp.Id, new Cell(6, 7)));
        Assert.False(b.CanGateAt(imp.Id, new Cell(-1, 7)));
    }

    // exe FUN_00861840: the stack is created at the chosen cell off the field (FUN_00c56750 state 1) with 0x9f, no gating
    // charge (vf 0x38(0)) and ATB 0; FUN_00bbb390 places it there at its first turn start and it does not act.
    [Fact]
    public void TheGatedStackWaitsOffTheFieldAndArrivesAtTheChosenCell()
    {
        var b = Make("CREATURE_IMP", 40, Mastery.Basic);
        var imp = Demon(b);
        var cell = new Cell(6, 7);
        var r = b.Apply(new UseAbilityCmd(imp.Id, Ab.Gating, null, cell) { Side = 1 });
        Assert.True(r.Ok, r.Error);
        var g = b.State.Units.Single(u => u.Summoned && u.CreatureId == "CREATURE_IMP");
        Assert.Equal(cell, g.Pos);
        Assert.True(g.OffField);
        Assert.True(g.HasEffect(Battle.GatedEffect));
        Assert.Null(b.State.At(cell));                           // it occupies nothing yet
        Assert.False(b.CanGateAt(imp.Id, cell));                  // but a second gate there is refused
        Assert.Equal(12, g.Count);                                // 40 · PortionOfSummonedByGatingLevel[0] (0.3)
        // A second gating by the same stack, or by the gated stack, is not offered (charge spent / 0).
        b.State.Active = imp.Id;
        Assert.False(Offers(b));
        b.State.Active = g.Id;
        Assert.False(Offers(b));
        g.Atb = 1;
        Assert.True(b.ExeTurnStartTail(g));
        Assert.False(g.OffField);
        Assert.False(g.HasEffect(Battle.GatedEffect));
        Assert.Equal(cell, g.Pos);
        Assert.Equal(g.Id, b.State.At(cell)!.Id);
        Assert.True(g.Atb < 1);
    }

    // exe FUN_00bbb390 → FUN_00c558b0: an occupied anchor → the nearest free one (squared distance, battle RNG ties).
    [Fact]
    public void AnOccupiedCellSendsTheArrivalToTheNearestFreeOne()
    {
        var b = Make("CREATURE_IMP", 40, Mastery.Basic);
        var imp = Demon(b);
        var cell = new Cell(6, 7);
        Assert.True(b.Apply(new UseAbilityCmd(imp.Id, Ab.Gating, null, cell) { Side = 1 }).Ok);
        var g = b.State.Units.Single(u => u.Summoned);
        var foot = b.State.Units.First(u => u.CreatureId == "CREATURE_FOOTMAN");
        foot.Pos = cell;                                           // something walked onto the gate
        g.Atb = 1;
        b.ExeTurnStartTail(g);
        Assert.False(g.OffField);
        Assert.NotEqual(cell, g.Pos);
        Assert.Equal(1, Math.Abs(g.Pos.X - cell.X) + Math.Abs(g.Pos.Y - cell.Y));
    }

    // exe FUN_00a6af10: a unit in placement state 1 is not on the map — no melee, shot or spell target.
    [Fact]
    public void AStackGatingInCannotBeTargeted()
    {
        var b = Make("CREATURE_IMP", 40, Mastery.Basic);
        var imp = Demon(b);
        Assert.True(b.Apply(new UseAbilityCmd(imp.Id, Ab.Gating, null, new Cell(3, 4)) { Side = 1 }).Ok);
        var g = b.State.Units.Single(u => u.Summoned);
        var foot = b.State.Units.First(u => u.CreatureId == "CREATURE_FOOTMAN");
        b.State.Active = foot.Id;
        var la = b.GetLegalActions()!;
        Assert.False(la.Melee.ContainsKey(g.Id));
        Assert.True(la.Melee.ContainsKey(imp.Id) || la.Moves.Count > 0);
    }

    // exe FUN_008d1010: a later duplicate is overwritten by the last entry and the list shrinks.
    [Fact]
    public void AiCellListLosesDuplicatesTheExeWay()
    {
        var l = new List<Cell> { new(1, 1), new(2, 2), new(1, 1), new(3, 3), new(4, 4) };
        Battle.ExeDedupe(l);
        Assert.Equal(new List<Cell> { new(1, 1), new(2, 2), new(4, 4), new(3, 3) }, l);
    }

    // exe FUN_008d28e0: the AI's cells are ring anchors around the field's creatures that pass the cast test.
    [Fact]
    public void AiCellsAreRingCellsThatPassTheCastTest()
    {
        var b = Make("CREATURE_IMP", 40, Mastery.Basic);
        var imp = Demon(b);
        var cells = b.GatingAiCells(imp, defensive: false);
        Assert.NotEmpty(cells);
        Assert.All(cells, c => Assert.True(b.CanGateAt(imp.Id, c)));
        Assert.Equal(cells.Count, cells.Distinct().Count());
        Assert.All(cells, c => Assert.Contains(b.State.Units.Where(u => u.Alive && u.Kind == UnitKind.Creature),
            u => Math.Max(Math.Abs(u.Pos.X - c.X), Math.Abs(u.Pos.Y - c.Y)) == 1));
    }
}
