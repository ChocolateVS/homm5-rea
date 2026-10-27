namespace H5.Core.Ui;

/// <summary>
/// The range model of the exe's CWindowSlider (H5_Game.exe, slider interface at window +0xf8, vtable 00e13ef8;
/// constructor 00490ef0: min 0, max 100, page 0, position 0). Positions are floats; the scrollable range is
/// [<see cref="Min"/>, <see cref="Max"/>] where Max is the range end minus the page.
/// </summary>
public sealed class UiSliderModel
{
    public float Min { get; private set; }
    public float Max { get; private set; } = 100;
    public float Page { get; private set; }
    public float Pos { get; private set; }
    /// <summary>Instance SpecialPositions (WindowSlider +0xec): above 1 the slider moves in that many discrete steps.</summary>
    public int SpecialPositions { get; private set; }
    /// <summary>Slot 0 (0048f160, flag +0x4d): the range exceeds the page, so the lever and arrows are usable.</summary>
    public bool Scrollable { get; private set; }

    /// <summary>
    /// SetRange (0048f730): lo/hi are ordered; a page ≤ 0 means the whole range; an empty range becomes 0..100
    /// with page 100. The slider is scrollable only when the range exceeds the page; otherwise the page is
    /// clamped to the range. The scrollable maximum is hi − page; the position is clamped again (0048f440).
    /// </summary>
    public void SetRange(float a, float b, float page)
    {
        float lo = Math.Min(a, b), hi = Math.Max(a, b);
        page = page > 0 ? page : hi - lo;
        if (lo == hi) { lo = 0; hi = 100; page = 100; }
        float range = hi - lo;
        if (range > page) Scrollable = true;
        else { page = range; Scrollable = false; }
        Min = lo;
        Max = hi - page;
        Page = page;
        Pos = Clamp(Pos);
    }

    /// <summary>SetPos (slot 3, 0048f850, and 0048ff70 which also notifies): clamped to [Min, Max] (0048f440 / 0048f1a0).</summary>
    public bool SetPos(float p)
    {
        float n = Clamp(p);
        bool changed = n != Pos;
        Pos = n;
        return changed;
    }

    public float Clamp(float p) => p < Min ? Min : p > Max ? Max : p;

    /// <summary>SetSpecialPositions (slot 8, 0048f2a0): counts below 2 switch the discrete mode off.</summary>
    public void SetSpecialPositions(int n) => SpecialPositions = n < 2 ? 0 : n;

    /// <summary>GetIndex (slot 11, 0048f2e0): ceil((N − 1) · pos / (max − min)).</summary>
    public int Index => SpecialPositions > 1 && Max > Min ? (int)Math.Ceiling((SpecialPositions - 1) * Pos / (Max - Min)) : 0;

    /// <summary>SetIndex (slot 10, 0048f370): pos = (max − min) · i / (N − 1) (the minimum is not added).</summary>
    public bool SetIndex(int i) => SpecialPositions > 1 && SetPos((Max - Min) * i / (SpecialPositions - 1));

    /// <summary>
    /// Lever length along the slider (0048f440): length · page / (max − min + page), clamped to
    /// [MinLeverSize, MaxLeverSize] (shared +0xd4 / +0xd8; a zero maximum means the slider length), rounded.
    /// </summary>
    public int LeverLength(int length, int minLever, int maxLever)
    {
        float cap = maxLever == 0 ? length : maxLever;
        float denom = Max - Min + Page;
        float l = denom > 0 ? length * Page / denom : length;
        l = Math.Max(minLever, Math.Min(l, cap));
        return (int)Math.Round(l, MidpointRounding.ToEven);
    }

    /// <summary>Lever offset (0048f440): round((pos − min) · (length − lever) / (max − min)), 0 when the range is below 1.</summary>
    public int LeverOffset(int length, int lever)
    {
        float range = Max - Min;
        if (range < 1) return 0;
        return (int)Math.Round((Pos - Min) * (length - lever) / range, MidpointRounding.ToEven);
    }

    /// <summary>
    /// Position under a lever dragged to <paramref name="offset"/> (lever start, slider-relative; 0048f9b0):
    /// the fraction of the free track, mapped to [Min, Max]; with special positions it snaps to the nearest step.
    /// </summary>
    public float ValueAt(float offset, int length, int lever)
    {
        float free = length - lever;
        float t = free > 0 ? Math.Clamp(offset / free, 0f, 1f) : 0f;
        if (SpecialPositions > 1)
        {
            int i = (int)Math.Round(t * (SpecialPositions - 1), MidpointRounding.ToEven);
            return (Max - Min) * i / (SpecialPositions - 1);
        }
        return Min + t * (Max - Min);
    }
}
