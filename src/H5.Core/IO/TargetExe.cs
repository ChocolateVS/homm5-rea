namespace H5.Core.IO;

/// <summary>
/// Which original executable H5Reborn reproduces. The target install (TotE 3.1 + MMH55 data and scripts) is
/// launched with <c>bin/H5_Game_PEST.exe</c>: the stock GOG 3.1 <c>H5_Game.exe</c> with 16 bytes changed (the PEST
/// patch, docs/rules/SIMULTANEOUS.md §7) and no MMH55 DLL. The stock 3.1 install runs the unpatched exe, but H5Reborn
/// applies the same PEST behaviour there too unless the player turns it off (user-requested deviation, SIMULTANEOUS.md §7.4).
/// </summary>
public static class TargetExe
{
    /// <summary>
    /// True when the PEST behaviour (simultaneous turns, once on, never break) applies: <c>H5_EXE=pest</c> /
    /// <c>H5_EXE=stock</c> decide explicitly; otherwise the MMH55 target always runs PEST, and the stock 3.1 target runs
    /// it when <paramref name="stockUninterrupted"/> (the H5Reborn option, default on) is set.
    /// </summary>
    public static bool Pest(string? installRoot, bool stockUninterrupted)
    {
        var exe = Environment.GetEnvironmentVariable("H5_EXE");
        if (string.Equals(exe, "pest", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(exe, "stock", StringComparison.OrdinalIgnoreCase)) return false;
        return PakLoadOrder.IsMmh55Install(installRoot) || stockUninterrupted;
    }
}
