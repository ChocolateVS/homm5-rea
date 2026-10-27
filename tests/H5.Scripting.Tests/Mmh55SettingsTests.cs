using H5.Core.IO;
using H5.Scripting;
using H5.Scripting.Lua;
using Xunit;

namespace H5.Scripting.Tests;

/// <summary>MMH55 player settings (scripts/H55-Settings.lua): parsing and the override overlay the options screen uses.</summary>
public class Mmh55SettingsTests
{
    const string Sample = """
        ----------------------------------------
        --	MIGHT & MAGIC: HEROES 5.5 - PLAYER SETTINGS
        ----------------------------------------
        H55_TownGateEnabled = 1; --Activates Town Gate (0 = deactivated).

        H55_AICheatMode = 4; --Only accepts numbers 0,1,2,3,4,5,6;
        --	GUIDELINES for AICheatMode setting:
        --	4 = 100% Default
        H55_BanksDifficulty = 1.25; --Multiplier
        H55_TownGateEnabled = 0; -- duplicate: the first assignment is the listed one
        """;

    [Fact]
    public void ParsesNamesValuesCommentsAndNotes()
    {
        var s = Mmh55Settings.Parse(Sample);
        Assert.Equal(new[] { "H55_TownGateEnabled", "H55_AICheatMode", "H55_BanksDifficulty" }, s.Select(x => x.Name));
        Assert.Equal("1", s[0].Default);
        Assert.Equal("Activates Town Gate (0 = deactivated).", s[0].Comment);
        Assert.Equal("GUIDELINES for AICheatMode setting:\n4 = 100% Default", s[1].Notes);
        Assert.Equal("1.25", s[2].Default);
    }

    [Fact]
    public void OverridesAreAppendedAndWinInLua()
    {
        var text = Mmh55Settings.Apply(Sample, new Dictionary<string, string>
        {
            ["H55_TownGateEnabled"] = "0", ["H55_BanksDifficulty"] = "1.5",
            ["os.exit"] = "1", ["H55_AICheatMode"] = "2; os.exit()", // rejected: not an H55_ global / not a number
        });
        Assert.DoesNotContain("os.exit", text[Sample.Length..]);
        var L = new LuaState();
        L.DoString(text, "H55-Settings.lua");
        L.Step();
        Assert.Equal(0, L.GetGlobal("H55_TownGateEnabled").N);
        Assert.Equal(1.5, L.GetGlobal("H55_BanksDifficulty").N);
        Assert.Equal(4, L.GetGlobal("H55_AICheatMode").N);
        Assert.Same(Sample, Mmh55Settings.Apply(Sample, null));
        Assert.True(Mmh55Settings.IsScript("/scripts/H55-Settings.lua"));
        Assert.True(Mmh55Settings.IsScript("Scripts\\h55-settings.lua"));
        Assert.False(Mmh55Settings.IsScript("scripts/H55-Core.lua"));
    }

    /// <summary>The 5.5 install's own file (MMH55-Index default copy, or the Utility-written MMH55-Settings.pak one).</summary>
    [Fact]
    public void RealSettingsFileParsesEverySetting()
    {
        const string root = "G:/HoMM 5 - Tribes of the East (5.5)";
        if (!PakLoadOrder.IsMmh55Install(root)) return;
        using var fs = PakFileSystem.OpenInstall(GameInstall.At(root));
        Assert.True(fs.TryReadAllBytes(Mmh55Settings.ScriptPath, out var b));
        var s = Mmh55Settings.Parse(H5.Scripting.Runtime.ScriptText.FromBytes(b));
        Assert.Equal(38, s.Count);
        Assert.Contains(s, x => x.Name == "H55_AICheatMode" && x.Default == "4" && x.Notes.Contains("140%"));
        Assert.Contains(s, x => x.Name == "H55_Duel_BankDifficulty" && x.Default == "6");
    }
}
