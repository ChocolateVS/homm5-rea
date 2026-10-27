using H5.Core.Ui;

namespace H5.Scene.Tests;

/// <summary>The exe's markup text layout (H5_Game.exe CMLStream 00cc0820, CMLLayout 00cc0c20, font table 00a54310).</summary>
public class TextLayoutTests(InstallFixture install) : IClassFixture<InstallFixture>
{
    [Fact]
    public void FontTablePicksSmallestNominalNotBelowTheSize()
    {
        if (install.Xdb == null) return;
        var fonts = UiFontTable.Load(install.Xdb);
        // Header atlases are 22/26/32/38 nominal: 28 uses 32 (scaled 0.875), 40 the largest (38).
        Assert.Equal(32, fonts.Find("Header", 28)!.Nominal);
        Assert.Equal(38, fonts.Find("Header", 40)!.Nominal);
        Assert.Equal(18, fonts.Find("Default", 18)!.Nominal);
        // Unknown faces fall back to System 16; the name match is exact.
        Assert.Equal("System", fonts.Find("NoSuchFace", 20)!.Name);
        Assert.Equal(16, fonts.Find("NoSuchFace", 20)!.Nominal);
    }

    [Fact]
    public void TokenizerFollowsTheExeCategories()
    {
        if (install.Xdb == null) return;
        var m = new UiMarkup(install.Xdb);
        static string Kinds(UiMlStream s) => string.Join(",", s.Items.Select(i => i.Kind switch
        {
            UiMlKind.Word => "W:" + i.Text, UiMlKind.Space => "S", UiMlKind.Break => "B", _ => "T",
        }));
        Assert.Equal("W:a,S,W:b,c,B", Kinds(m.Tokenize("a  b,c")));
        // Consecutive LF make one break; CR LF pairs make one each.
        Assert.Equal("W:a,B,W:b,B", Kinds(m.Tokenize("a\n\nb")));
        Assert.Equal("W:a,B,B,W:b,B", Kinds(m.Tokenize("a\r\n\r\nb")));
        // A CR LF right after a tag is swallowed: <br> then CR LF breaks once.
        Assert.Equal("W:a,B,W:b,B", Kinds(m.Tokenize("a<br>\r\nb")));
        // A tag splits a word; the stream always ends with a break; <br> at the end adds an empty line.
        Assert.Equal("W:ab,W:cd,B", Kinds(m.Tokenize("ab<color=FFFF0000>cd")));
        Assert.Equal("W:a,B,B", Kinds(m.Tokenize("a<br>")));
        // Defaults: System 16pt white, left, middle.
        var st = m.Tokenize("x").Items[0].State;
        Assert.Equal(("System", 16, 0xFFFFFFFFu, UiMlMode.Left, 2), (st.Face, st.Size, st.Color, st.Mode, st.VAlign));
        // <font> with arguments resets the outline; <outline> after it keeps it.
        var o = m.Tokenize("<outline><h2>x").Items[0].State;
        Assert.Equal(0, o.OutlineSize);
        var o2 = m.Tokenize("<h2><outline>x").Items[0].State;
        Assert.Equal((1, true), (o2.OutlineSize, o2.ForceFontSize));
    }

    [Fact]
    public void LinePitchIsTheNominalSizeAfterTheFirstCell()
    {
        if (install.Xdb == null) return;
        var m = new UiMarkup(install.Xdb);
        var fonts = UiFontTable.Load(install.Xdb);
        // Default 18: cell 21, internal leading 3. One line = 21, three lines = 21 + 2 * 18.
        var one = UiTextLayout.Layout(m.Tokenize("<font face=Default size=18>Word"), fonts, 500);
        Assert.Equal(21f, one.Height);
        var three = UiTextLayout.Layout(m.Tokenize("<font face=Default size=18>a<br>b<br>c"), fonts, 500);
        Assert.Equal(21f + 2 * 18f, three.Height);
        // The second line's glyph cells start internal-leading above its pen y (21 - 3 = 18).
        Assert.Contains(three.Quads, q => q.Y == 18f);
        // Rounded size: value + 0.5 rounded half-even (21 -> 22, 57 -> 58, 18 -> 18).
        Assert.Equal(22, one.RoundedHeight);
    }

    [Fact]
    public void WrappingKeepsTrailingSpaceAndDropsLeadingSpace()
    {
        if (install.Xdb == null) return;
        var m = new UiMarkup(install.Xdb);
        var fonts = UiFontTable.Load(install.Xdb);
        var f = fonts.Find("Default", 18)!;
        int W(string s) => s.Sum(c => f.Glyph(c)!.Value.Advance);
        // Width fits "aaa aaa" exactly: the space element fits, so no wrap even without room for a trailing space.
        float width = W("aaa aaa");
        var b = UiTextLayout.Layout(m.Tokenize("<font face=Default size=18>aaa aaa aaa"), fonts, width);
        Assert.Equal(21f + 18f, b.Height);
        // Centred: the second line "aaa" is centred alone (its leading space dropped).
        var c = UiTextLayout.Layout(m.Tokenize("<font face=Default size=18><center>aaa aaa aaa"), fonts, width);
        Assert.Equal(21f + 18f, c.Height);
    }

    [Fact]
    public void HeaderTwentyEightIsScaledFromTheThirtyTwoAtlas()
    {
        if (install.Xdb == null) return;
        var m = new UiMarkup(install.Xdb);
        var fonts = UiFontTable.Load(install.Xdb);
        var b = UiTextLayout.Layout(m.Tokenize("<font face=Header size=28>H"), fonts, 500);
        var f = fonts.Find("Header", 28)!;
        var q = Assert.Single(b.Quads);
        Assert.Equal(f.CellHeight * 28f / 32f, q.H, 3);
        Assert.Equal(f.CellHeight * 28f / 32f, b.Height, 3);
        // At UI scale 1.5 the pt size becomes 42 -> the 38 atlas scaled 42/38.
        var big = UiTextLayout.Layout(m.Tokenize("<font face=Header size=28>H"), fonts, 500, 1.5f);
        Assert.Equal(fonts.Find("Header", 42)!.CellHeight * 42f / 38f, Assert.Single(big.Quads).H, 3);
    }

    [Fact]
    public void PlacementCentreRoundsHalfToEven()
    {
        // 00a4d630: round-half-even((parent - size) / 2).
        Assert.Equal((2, 5), UiLayout.Axis(UiAlign.Center, 0, 5, 0, 0, 0, 10));
        Assert.Equal((2, 7), UiLayout.Axis(UiAlign.Center, 0, 7, 0, 0, 0, 10));
        Assert.Equal((4, 3), UiLayout.Axis(UiAlign.Center, 0, 3, 0, 0, 0, 10));
        Assert.Equal(2, UiLayout.HalfEven(3));   // 1.5 -> 2
        Assert.Equal(2, UiLayout.HalfEven(5));   // 2.5 -> 2
        Assert.Equal(4, UiLayout.HalfEven(7));   // 3.5 -> 4
        // Text blocks: an unset alignment keeps the window edge and ignores Position.
        var p = new UiPlacement { Position = (new Int2(5, 7), true), VerAlign = (UiAlign.Center, true) };
        Assert.Equal((0, UiLayout.HalfEven(40 - 21) + 7), UiLayout.PlaceText(p, new UiRect(0, 0, 100, 40), 30, 21));
    }
}

/// <summary>WindowMSButton AutoChangeState (instance +0xfc, reader 00be4a60; click handling 00b924d0 / 00b929f0).</summary>
public class ButtonStateTests(InstallFixture install) : IClassFixture<InstallFixture>
{
    [Fact]
    public void AutoChangeStateIsLoadedPerButton()
    {
        if (install.Xdb == null) return;
        var loader = new UiLoader(install.Xdb);
        var check = loader.Load("UI/GameOptions/Templates/CheckBox/Button.(WindowMSButton).xdb");
        Assert.True(check.AutoChangeState);
        var rate = loader.Load("UI/Marketplace2/Resource/Rate_0.(WindowMSButton).xdb");
        Assert.False(rate.AutoChangeState);
        Assert.Equal(2, rate.ButtonStates.Count);
    }
}
