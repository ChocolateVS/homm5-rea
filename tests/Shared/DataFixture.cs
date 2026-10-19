global using H5.TestSupport;
using H5.Core.IO;
using Xunit;

namespace H5.TestSupport;

/// <summary>
/// Which game data the tests run on: stock TotE 3.1 (default, tests/stock.runsettings) or the MMH55 5.5 install
/// (<c>H5_GAME_DIR=…(5.5)</c>). docs/mmh55/W1_DATA.md §4.
/// </summary>
public static class DataFixture
{
    static readonly Lazy<bool> _mmh55 = new(() =>
    {
        try { return PakLoadOrder.IsMmh55Install(GameInstall.Locate().Root); }
        catch (DirectoryNotFoundException) { return false; }
    });

    /// <summary>True when the located install carries MMH55 (data/MMH55-Data.pak).</summary>
    public static bool Mmh55 => _mmh55.Value;

    /// <summary>Stock value on stock data, MMH55 value on MMH55 data.</summary>
    public static T Pick<T>(T stock, T mmh55) => Mmh55 ? mmh55 : stock;

    public const string StockOnly = "asserts stock 3.1 data values; MMH55 data differs (docs/mmh55/W1_DATA.md §4)";
}

/// <summary>A fact whose expected values are stock 3.1 data: skipped on the MMH55 fixture.</summary>
public sealed class StockFactAttribute : FactAttribute
{
    public StockFactAttribute() { if (DataFixture.Mmh55) Skip = DataFixture.StockOnly; }
}

/// <summary>A theory whose expected values are stock 3.1 data: skipped on the MMH55 fixture.</summary>
public sealed class StockTheoryAttribute : TheoryAttribute
{
    public StockTheoryAttribute() { if (DataFixture.Mmh55) Skip = DataFixture.StockOnly; }
}
