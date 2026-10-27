using H5.Core.Ui;

namespace H5.Tests;

/// <summary>CWindowSlider range model (H5_Game.exe 0048f730 / 0048f440 / 0048f370 / 0048f2e0).</summary>
public class UiSliderModelTests
{
    [Fact]
    public void ContainerRangeScrollsUpToContentMinusPage()
    {
        var m = new UiSliderModel();
        m.SetRange(0, 400, 173);
        Assert.True(m.Scrollable);
        Assert.Equal(227, m.Max);
        m.SetPos(1000);
        Assert.Equal(227, m.Pos);
        m.SetPos(-5);
        Assert.Equal(0, m.Pos);
    }

    [Fact]
    public void ContentSmallerThanPageIsNotScrollable()
    {
        var m = new UiSliderModel();
        m.SetRange(0, 100, 173);
        Assert.False(m.Scrollable);
        Assert.Equal(0, m.Max);
        Assert.Equal(100, m.Page);
    }

    [Fact]
    public void EmptyRangeBecomesZeroToHundred()
    {
        var m = new UiSliderModel();
        m.SetRange(5, 5, 0);
        Assert.Equal(0, m.Min);
        Assert.Equal(0, m.Max);
        Assert.False(m.Scrollable);
    }

    [Fact]
    public void LeverGeometry()
    {
        var m = new UiSliderModel();
        m.SetRange(0, 400, 100);
        Assert.Equal(16, m.LeverLength(232, 16, 16));
        Assert.Equal(58, m.LeverLength(232, 0, 0)); // 232 · 100 / 400
        m.SetPos(150);
        Assert.Equal(108, m.LeverOffset(232, 16)); // 150 · 216 / 300
        Assert.Equal(150, m.ValueAt(108, 232, 16), 1);
    }

    [Fact]
    public void SpecialPositions()
    {
        var m = new UiSliderModel();
        m.SetRange(0, 46, 1);
        m.SetSpecialPositions(46);
        m.SetIndex(45);
        Assert.Equal(45, m.Pos);
        Assert.Equal(45, m.Index);
        m.SetIndex(3);
        Assert.Equal(3, m.Index);
        Assert.Equal(45, m.ValueAt(1000, 203, 16));
        m.SetSpecialPositions(1);
        Assert.Equal(0, m.SpecialPositions);
    }
}
