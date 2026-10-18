using H5.Core.Ui;
using H5.Core.Ui.Binary;
using H5.Core.Ui.Imaging;

namespace H5.Scene.Tests;

public class ChunkTests
{
    [Fact]
    public void ParsesShortAndLongLengths()
    {
        // [4] short len 4 = 7, then [1] long-form len 3 = "abc"
        var data = new byte[] { 4, 4 << 1, 7, 0, 0, 0, 1, (3 << 1) | 1, 0, 0, 0, (byte)'a', (byte)'b', (byte)'c' };
        var kids = new ChunkSpan(data).Children();
        Assert.Equal(2, kids.Count);
        Assert.Equal(4, kids[0].Id);
        Assert.Equal(7, kids[0].Data.AsInt32());
        Assert.Equal("abc", kids[1].Data.AsString());
    }

    [Fact]
    public void RejectsOverrun()
    {
        var data = new byte[] { 1, 10 << 1, 0 };
        Assert.False(new ChunkSpan(data).TryChildren(out _));
    }
}

public class DdsTests
{
    static byte[] Header(int w, int h, uint fourCC)
    {
        var b = new byte[128];
        BitConverter.GetBytes(0x20534444u).CopyTo(b, 0);
        BitConverter.GetBytes(h).CopyTo(b, 12);
        BitConverter.GetBytes(w).CopyTo(b, 16);
        BitConverter.GetBytes(4u).CopyTo(b, 80);
        BitConverter.GetBytes(fourCC).CopyTo(b, 84);
        return b;
    }

    [Fact]
    public void DecodesSolidDxt1Block()
    {
        // c0 = pure red (0xF800), c1 = black, indices all 0.
        var block = new byte[] { 0x00, 0xF8, 0x00, 0x00, 0, 0, 0, 0 };
        var img = DdsImage.Decode(Header(4, 4, 0x31545844).Concat(block).ToArray());
        Assert.Equal((255, 0, 0, 255), (img.Pixels[0], img.Pixels[1], img.Pixels[2], img.Pixels[3]));
    }

    [Fact]
    public void Dxt3AlphaIsExplicit4Bit()
    {
        var alpha = Enumerable.Repeat((byte)0x88, 8); // every texel alpha 8/15
        var color = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 };
        var img = DdsImage.Decode(Header(4, 4, 0x33545844).Concat(alpha).Concat(color).ToArray());
        Assert.Equal(8 * 17, img.Pixels[3]);
        Assert.Equal(255, img.Pixels[0]);
    }

    [Fact]
    public void PngHasSignature()
    {
        var png = PngWriter.Encode(new RgbaImage(2, 2));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }
}

public class LayoutTests
{
    static UiPlacement P(UiAlign h, UiAlign v, int x, int y, int w, int hgt) => new()
    {
        HorAlign = (h, true), VerAlign = (v, true),
        Position = (new Int2(x, y), true), Size = (new Int2(w, hgt), true),
    };

    [Fact]
    public void HighEndAnchorsToBottomRight()
    {
        // MainMenu2/Menu: HIGH_END both axes, pos (-8,-24), size 327x289 in 1024x768.
        var r = UiLayout.Place(P(UiAlign.HighEnd, UiAlign.HighEnd, -8, -24, 327, 289), new UiRect(0, 0, 1024, 768));
        Assert.Equal(new UiRect(689, 455, 327, 289), r);
    }

    [Fact]
    public void MarginStretches()
    {
        var p = new UiPlacement
        {
            HorAlign = (UiAlign.Margin, true), VerAlign = (UiAlign.Margin, true),
            LowerMargin = (new Int2(10, 20), true), UpperMargin = (new Int2(5, 5), true),
        };
        Assert.Equal(new UiRect(110, 220, 85, 75), UiLayout.Place(p, new UiRect(100, 200, 100, 100)));
    }

    [Fact]
    public void InstanceOverridesOnlySetFields()
    {
        var shared = P(UiAlign.Center, UiAlign.Center, 1, 2, 30, 40);
        var inst = new UiPlacement { Position = (new Int2(9, 9), true) };
        var m = shared.MergedWith(inst);
        Assert.Equal(new Int2(9, 9), m.Position.Value);
        Assert.Equal(new Int2(30, 40), m.Size.Value);
        Assert.Equal(UiAlign.Center, m.HorAlign.Value);
    }

    [Fact]
    public void PaintOrderPutsFirstChildAndHigherPriorityOnTop()
    {
        var root = new UiWindow();
        var a = new UiWindow { Name = "a" };
        var b = new UiWindow { Name = "b" };
        var c = new UiWindow { Name = "c", Priority = 1 };
        root.Children.AddRange(new[] { a, b, c });
        Assert.Equal(new[] { "b", "a", "c" }, UiLayout.PaintOrder(root).Select(w => w.Name));
    }
}
