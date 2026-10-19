using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>
/// Battle-start auto placement per H5_Game.exe FUN_0085a190 (iterative placer FUN_00859eb0, even spread FUN_00857380);
/// worked examples of docs/rules/audit/combat_abilities_d.md (regular 12×10 field, no obstacles, 2 deploy columns).
/// </summary>
[Collection("game")]
public class ExeAutoPlacementTests(GameDataFixture f)
{
    BattleState Field(bool hero0 = true)
    {
        var s = new BattleState { Width = 12, Height = 10, Obstacles = new List<Cell>() };
        s.Heroes[0].Present = hero0;
        return s;
    }

    static List<ArmySlot> Many(string id, int n, int count = 10) => Enumerable.Range(0, n).Select(_ => new ArmySlot(id, count)).ToList();

    [Fact]
    public void SevenEqualMeleeStacksTakeTheFrontColumnInMergeSortOrder()   // FUN_00859670 cat 3, FUN_008585c0 / FUN_0085a700
    {
        var cells = ExeAutoPlacement.Place(f.Db, Field(), 0, Many("CREATURE_FOOTMAN", 7));
        Assert.Equal(new int[] { 2, 5, 1, 7, 6, 3, 9 }, cells.Select(c => c!.Value.Y).ToArray());
        Assert.All(cells, c => Assert.Equal(1, c!.Value.X));
        var right = ExeAutoPlacement.Place(f.Db, Field(), 1, Many("CREATURE_FOOTMAN", 7));
        Assert.Equal(new int[] { 2, 5, 1, 7, 6, 3, 9 }, right.Select(c => c!.Value.Y).ToArray());
        Assert.All(right, c => Assert.Equal(10, c!.Value.X));
    }

    [Fact]
    public void ShootersTakeTheBackColumnAndMeleeTheFront()   // FUN_00857e60 shooter rows, FUN_00858030
    {
        var army = Many("CREATURE_ARCHER", 3).Concat(Many("CREATURE_FOOTMAN", 4)).ToList();
        var cells = ExeAutoPlacement.Place(f.Db, Field(), 0, army);
        Assert.Equal(new Cell?[] { new Cell(0, 0), new Cell(0, 1), new Cell(0, 2), new Cell(1, 9), new Cell(1, 3), new Cell(1, 5), new Cell(1, 7) }, cells.ToArray());
        var shooters = ExeAutoPlacement.Place(f.Db, Field(), 0, Many("CREATURE_ARCHER", 7));
        Assert.Equal(Enumerable.Range(0, 7).Select(i => (Cell?)new Cell(0, i)).ToArray(), shooters.ToArray());
    }

    [Fact]
    public void ASingleStackUsesTheEvenSpread()   // FUN_0085a190 (< 2 stacks) → FUN_00857380 / FUN_00857060
    {
        Assert.Equal(new Cell(1, 5), ExeAutoPlacement.Place(f.Db, Field(), 0, Many("CREATURE_FOOTMAN", 1))[0]);
        Assert.Equal(new Cell(0, 5), ExeAutoPlacement.Place(f.Db, Field(hero0: false), 0, Many("CREATURE_FOOTMAN", 1))[0]);
        Assert.Equal(new Cell(0, 5), ExeAutoPlacement.Place(f.Db, Field(), 0, Many("CREATURE_ARCHER", 1))[0]);
        Assert.Equal(new Cell(0, 4), ExeAutoPlacement.Place(f.Db, Field(), 0, Many("CREATURE_CAVALIER", 1))[0]);
    }

    [Fact]
    public void EvenSpreadWhenTheConsoleVariableIsOff()   // combat_new_autoplacing = 0 (DAT_00f43ea4)
    {
        try
        {
            ExeAutoPlacement.CombatNewAutoplacing = false;
            var cells = ExeAutoPlacement.Place(f.Db, Field(), 0, Many("CREATURE_FOOTMAN", 7));
            Assert.Equal(new int[] { 0, 2, 3, 5, 6, 7, 9 }, cells.Select(c => c!.Value.Y).ToArray());
            Assert.All(cells, c => Assert.Equal(1, c!.Value.X));
        }
        finally { ExeAutoPlacement.CombatNewAutoplacing = true; }
    }

    [Fact]
    public void StacksThatDoNotFitAreRemovedWeakestFirst()   // FUN_00859b40 "removing stack from arena"
    {
        var cells = ExeAutoPlacement.Place(f.Db, Field(), 0, Many("CREATURE_CAVALIER", 7));
        Assert.Null(cells[0]); Assert.Null(cells[1]);
        Assert.Equal(new Cell?[] { new Cell(0, 0), new Cell(0, 2), new Cell(0, 4), new Cell(0, 6), new Cell(0, 8) }, cells.Skip(2).ToArray());
        // At battle start the removed stacks stay out of the fight.
        var s = BattleSetup.Create(f.Db, new ArmySetup { Army = Many("CREATURE_CAVALIER", 7) }, new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 5) } },
            1, obstacles: Array.Empty<Cell>(), skipTactics: true);
        Assert.Equal(5, s.Units.Count(u => u.Side == 0 && u.Alive));
    }
}
