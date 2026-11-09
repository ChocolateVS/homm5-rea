using H5.Adventure.Engine;

namespace H5.Adventure.Tests;

/// <summary>The build screen's cell drawing (H5_Game.exe 0065bab0 → 0065b190) in the simple and the full view (00783330).</summary>
public class TownBuildScreenTests
{
    static BuildCellDraw? Cell((string, int)[] cell, int level, BuildDenial next, bool full, int cap = 3) =>
        Engine.Adventure.BuildScreenCell(cell, _ => level, _ => cap, (_, l) => l <= level ? BuildDenial.AlreadyBuilt : next, full);

    [Fact]
    public void BuildableLevelsAreGreenOrBlueAndAlwaysShown()
    {
        // Code 0: state 2, or state 4 when the cell's level list (+0x10) holds a lower level.
        var one = new[] { ("B", 1) };
        Assert.Equal(new BuildCellDraw("B", 1, BuildCellTint.New, true), Cell(one, 0, BuildDenial.CanBuild, false));
        var two = new[] { ("B", 1), ("B", 2) };
        Assert.Equal(new BuildCellDraw("B", 2, BuildCellTint.Upgrade, true), Cell(two, 1, BuildDenial.CanBuild, false));
    }

    [Fact]
    public void TheSimpleViewHidesWhatCannotBeBuilt()
    {
        var one = new[] { ("B", 1) };
        foreach (var code in new[] { BuildDenial.CantBuild, BuildDenial.Grail, BuildDenial.Prereq, BuildDenial.NoWater,
                     BuildDenial.OnlyOneCapitol, BuildDenial.TownPoints })
        {
            Assert.False(Cell(one, 0, code, false)!.Value.Shown);
            Assert.True(Cell(one, 0, code, true)!.Value.Shown);
        }
        // 5, 6: state 1 in both views.
        Assert.Equal(new BuildCellDraw("B", 1, BuildCellTint.Denied, true), Cell(one, 0, BuildDenial.NoMoney, false));
        Assert.Equal(new BuildCellDraw("B", 1, BuildCellTint.Denied, true), Cell(one, 0, BuildDenial.BuiltThisTurn, false));
        Assert.Equal(BuildCellTint.Dim, Cell(one, 0, BuildDenial.Prereq, true)!.Value.Tint);
        Assert.Equal(BuildCellTint.Denied, Cell(one, 0, BuildDenial.Grail, true)!.Value.Tint);
    }

    [Fact]
    public void ALimitedUpgradeKeepsTheBuiltLevelInTheSimpleView()
    {
        // Code 2: full view state 3 shown; simple view hidden only for the cell's first level, else the earlier drawing stays.
        var two = new[] { ("B", 1), ("B", 2) };
        Assert.Equal(new BuildCellDraw("B", 1, BuildCellTint.Built, true), Cell(two, 1, BuildDenial.LimitUpgrade, false));
        Assert.Equal(new BuildCellDraw("B", 2, BuildCellTint.Dim, true), Cell(two, 1, BuildDenial.LimitUpgrade, true));
        Assert.False(Cell(new[] { ("B", 1) }, 0, BuildDenial.LimitUpgrade, false)!.Value.Shown);
    }

    [Fact]
    public void LaterLevelsOnlyFillAnEmptyCellInTheFullView()
    {
        // L > level + 1: 00f37cfc × 0.5 into a cell not drawn yet, shown only in the full view; levels above the cap skipped.
        var later = new[] { ("B", 3) };
        Assert.Equal(new BuildCellDraw("B", 3, BuildCellTint.Dim, false), Cell(later, 0, BuildDenial.Prereq, false));
        Assert.Equal(new BuildCellDraw("B", 3, BuildCellTint.Dim, true), Cell(later, 0, BuildDenial.Prereq, true));
        Assert.Null(Cell(later, 0, BuildDenial.Prereq, true, cap: 2));
        var built = new[] { ("B", 1), ("B", 3) };
        Assert.Equal(new BuildCellDraw("B", 1, BuildCellTint.Built, true), Cell(built, 1, BuildDenial.Prereq, true));
    }
}
