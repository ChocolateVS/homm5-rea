namespace H5.Adventure.Engine;

/// <summary>Build-screen cell tint states (0065b190, colour table 00f37cd8 with 12 bytes per state).</summary>
public enum BuildCellTint
{
    /// <summary>State 0 (255,255,255) × 1.</summary>
    Built,
    /// <summary>State 1 (255,90,90).</summary>
    Denied,
    /// <summary>State 2 (128,255,128).</summary>
    New,
    /// <summary>State 4 (0,128,255).</summary>
    Upgrade,
    /// <summary>State 3, or the 00f37cfc colour (255,255,255), with the factor 00f37cd0 (0.5).</summary>
    Dim,
}

/// <summary>What the build screen draws into one cell: a building level, its tint and whether the cell is shown.</summary>
public readonly record struct BuildCellDraw(string Type, int Level, BuildCellTint Tint, bool Shown);

public sealed partial class Adventure
{
    /// <summary>
    /// The cell drawing of the build screen refresh 0065bab0. Per building of the town's level map (+0x14 level, +0x18
    /// cap) and per level L = 1..cap, into the cell of (building, L) (00658ec0), with 0065b190(cell, icon, state, shown)
    /// (shown = the cell window's vf 0x44): L ≤ level → state 0, shown; L = level + 1 → by the check code (town vf 0xac):
    /// 0 → state 2, or state 4 when the cell's level list (+0x10) holds a level below L, shown; 1, 9 → state 1, shown
    /// only in the full view; 2 → full view: state 3, shown; simple view: state 3 hidden when L is the cell's first level
    /// (list front = the level record's +0x60), else the cell keeps its earlier drawing; 3 → state 0, shown; 5, 6 →
    /// state 1, shown; 4, 7, 8, 10 → 00f37cfc × 0.5, full view only; L &gt; level + 1 → only into a cell not drawn yet
    /// (entry +0 still 0): 00f37cfc × 0.5, full view only. The full view is mode 0 of the jog dial's view toggle
    /// (00783330; DAT_00fdc638 = mode == 0). <paramref name="cell"/> lists the (building, level) pairs the faction's
    /// TownBuildDefinition places in the cell; levels above <paramref name="cap"/> are not drawn.
    /// </summary>
    public static BuildCellDraw? BuildScreenCell(IReadOnlyList<(string Type, int Level)> cell, Func<string, int> level,
        Func<string, int> cap, Func<string, int, BuildDenial?> check, bool fullView)
    {
        var sorted = cell.OrderBy(e => e.Level).ToList();
        if (sorted.Count == 0) return null;
        int first = sorted[0].Level;
        BuildCellDraw? draw = null;
        foreach (var (type, l) in sorted)
        {
            if (l > cap(type) || check(type, l) is not { } code) continue;
            int cur = level(type);
            if (l <= cur) { draw = new(type, l, BuildCellTint.Built, true); continue; }
            if (l > cur + 1) { draw ??= new(type, l, BuildCellTint.Dim, fullView); continue; }
            switch (code)
            {
                case BuildDenial.CanBuild:
                    draw = new(type, l, sorted.Any(e => e.Type == type && e.Level < l) ? BuildCellTint.Upgrade : BuildCellTint.New, true);
                    break;
                case BuildDenial.CantBuild or BuildDenial.Grail:
                    draw = new(type, l, BuildCellTint.Denied, fullView);
                    break;
                case BuildDenial.LimitUpgrade:
                    if (fullView) draw = new(type, l, BuildCellTint.Dim, true);
                    else if (l == first) draw = new(type, l, BuildCellTint.Dim, false);
                    break;
                case BuildDenial.AlreadyBuilt:
                    draw = new(type, l, BuildCellTint.Built, true);
                    break;
                case BuildDenial.NoMoney or BuildDenial.BuiltThisTurn:
                    draw = new(type, l, BuildCellTint.Denied, true);
                    break;
                default:
                    draw = new(type, l, BuildCellTint.Dim, fullView);
                    break;
            }
        }
        return draw;
    }
}
