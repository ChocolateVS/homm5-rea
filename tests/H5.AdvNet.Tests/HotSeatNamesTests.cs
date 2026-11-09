using H5.AdvNet;

namespace H5.AdvNet.Tests;

/// <summary>UI/HotSeatWait row names: 0077c5c0 / 00713a00 (defaults), 0077bb50 (validity), 0077be40 / 00713d40 (fix).</summary>
public class HotSeatNamesTests
{
    static string Name(string key) => key.Length == 0 ? "" : char.ToUpperInvariant(key[0]) + key[1..];

    static List<SetupSlot> Slots() => new()
    {
        new SetupSlot { MapPlayer = 0, State = SetupSlot.SlotState.Human, Colour = "PCOLOR_RED" },
        new SetupSlot { MapPlayer = 1, State = SetupSlot.SlotState.Human, Colour = "PCOLOR_BLUE" },
        new SetupSlot { MapPlayer = 2, State = SetupSlot.SlotState.Computer, Colour = "PCOLOR_TEAL" },
    };

    [Fact]
    public void EveryRowStartsWithItsColourName()
    {
        var s = Slots();
        HotSeatNames.Init(s, Name);
        Assert.Equal(new[] { "Red", "Blue", "Teal" }, s.Select(x => x.Name));
        Assert.All(s, x => Assert.True(HotSeatNames.Valid(x, Name)));
    }

    [Fact]
    public void BlankNamesAreNotValidAndGetTheDefault()
    {
        var s = Slots();
        HotSeatNames.Init(s, Name);
        s[0].Name = " \t ";
        s[1].Name = "  Anna ";
        Assert.False(HotSeatNames.Valid(s[0], Name));
        Assert.True(HotSeatNames.Valid(s[1], Name));
        HotSeatNames.Fix(s, Name);
        Assert.Equal("Red", s[0].Name);
        Assert.Equal("Anna", s[1].Name);
    }

    [Fact]
    public void ColourNamesFollowTheRowColour()
    {
        var s = Slots();
        HotSeatNames.Init(s, Name);
        // A colour change swaps the colours; 0077bd40 then runs 0077be40: a row named after any colour gets its own.
        s[0].Colour = "PCOLOR_GREEN";
        s[1].Name = "Teal";
        HotSeatNames.Fix(s, Name);
        Assert.Equal(new[] { "Green", "Blue", "Teal" }, s.Select(x => x.Name));
    }

    /// <summary>006666f0 / 006664c0 type 0: 2..10 units after trimming, none of &lt; &gt; &amp; " =; else the text counts as empty.</summary>
    [Fact]
    public void NamesOutsideTheTypeZeroLimitsCountAsEmpty()
    {
        Assert.Equal("Anna", HotSeatNames.Trim("  Anna "));
        Assert.Equal("", HotSeatNames.Trim(" A "));
        Assert.Equal("Bo", HotSeatNames.Trim("Bo"));
        Assert.Equal("ABCDEFGHIJ", HotSeatNames.Trim("ABCDEFGHIJ"));
        Assert.Equal("", HotSeatNames.Trim("ABCDEFGHIJK"));
        Assert.Equal("", HotSeatNames.Trim("A<b>"));
        Assert.Equal("", HotSeatNames.Trim("a=b"));
        var s = Slots();
        HotSeatNames.Init(s, Name);
        s[1].Name = "X";
        Assert.False(HotSeatNames.Valid(s[1], Name));
        HotSeatNames.Fix(s, Name);
        Assert.Equal("Blue", s[1].Name);
    }
}
