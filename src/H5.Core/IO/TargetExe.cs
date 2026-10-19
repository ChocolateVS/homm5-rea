namespace H5.Core.IO;

/// <summary>
/// Which original executable H5Reborn reproduces. The target install (TotE 3.1 + MMH55 data and scripts) is
/// launched with <c>bin/H5_Game_PEST.exe</c>: the stock GOG 3.1 <c>H5_Game.exe</c> with 16 bytes changed (the PEST
/// patch, docs/rules/SIMULTANEOUS.md §7) and no MMH55 DLL. The stock 3.1 install runs the unpatched exe.
/// </summary>
public static class TargetExe
{
    /// <summary>
    /// True when the PEST patch is part of the target: <c>H5_EXE=pest</c> / <c>H5_EXE=stock</c> decide explicitly;
    /// otherwise an MMH55 install means PEST (the user's 5.5 setup) and anything else, or <c>H5_TARGET=stock</c>, the stock exe.
    /// </summary>
    public static bool Pest(string? installRoot)
    {
        var exe = Environment.GetEnvironmentVariable("H5_EXE");
        if (string.Equals(exe, "pest", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(exe, "stock", StringComparison.OrdinalIgnoreCase)) return false;
        return !PakLoadOrder.StockOnlyRequested && PakLoadOrder.IsMmh55Install(installRoot);
    }
}
