using Godot;
using H5.Adventure.Map;
using H5.Core.Xdb;
using H5Reborn.Ui;

namespace H5Reborn.Adventure;

/// <summary>
/// The map preview of the custom-game screens: the map descriptor's <c>thumbnailImages</c> textures shown by the
/// stock thumbnail slideshow in the <c>screenshoot1</c> / <c>screenshoot2</c> windows.
/// </summary>
public static class MapThumbnails
{
    /// <summary>
    /// The thumbnails of a map: the list builder reads the map's descriptor from <c>map-tag.xdb</c>
    /// (#xpointer(/AdvMapDescTag), H5_Game.exe 007c2ce0 from 00705800), whose <c>thumbnailImages</c> (+0x3c) are
    /// (Texture) xdb refs relative to it — the stock maps' Trumb_*.xdb (240x180), the RMG's minimap_floor_NN.xdb.
    /// A map without a map-tag.xdb falls back to the same list in its map.xdb (not traced).
    /// </summary>
    public static List<Texture2D> FromMap(MapFiles files)
    {
        var list = new List<Texture2D>();
        try
        {
            var dir = Path.GetDirectoryName(files.MapXdbPath)?.Replace('\\', '/') ?? "";
            var tag = (dir.Length > 0 ? dir + "/" : "") + "map-tag.xdb";
            var desc = files.Exists(tag) ? tag : files.MapXdbPath;
            var items = files.LoadXml(desc).Root?.Element("thumbnailImages")?.Elements("Item") ?? Enumerable.Empty<System.Xml.Linq.XElement>();
            foreach (var item in items)
                if (Load(files, (string?)item.Attribute("href"), desc) is { } t) list.Add(t);
        }
        catch (Exception e) { GD.PushWarning($"[H5] map thumbnails {files.MapXdbPath}: {e.Message}"); }
        return list;
    }

    static Texture2D? Load(MapFiles files, string? href, string from)
    {
        if (string.IsNullOrEmpty(href)) return null;
        var xdb = XdbRef.Parse(href, from).Path;
        string dds = xdb;
        if (!xdb.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            if (!files.Exists(xdb)) return null;
            var dest = (string?)files.LoadXml(xdb).Root?.Element("DestName")?.Attribute("href");
            if (string.IsNullOrEmpty(dest)) return null;
            dds = XdbRef.Parse(dest, xdb).Path;
        }
        if (!files.TryReadAllBytes(dds, out var bytes)) return null;
        var img = Game.LoadDds(bytes, dds);
        return img == null ? null : ImageTexture.CreateFromImage(img);
    }

    /// <summary>
    /// Thumbnails of a generated map: the exe saves the map with one minimap texture per floor as
    /// minimap_floor_01.., listed in thumbnailImages (map save 00c1dad0; RMGTemp/CurrentMap/map-tag.xdb). H5Reborn
    /// draws them as its adventure minimap shows the start: terrain colours, impassable ground darker, owned
    /// objects in their player's colour (the exe's minimap texture rendering is not ported).
    /// </summary>
    public static List<Texture2D> FromGenerated(AdvMap map)
    {
        var list = new List<Texture2D>();
        foreach (var floor in map.Floors)
        {
            if (floor?.Data == null) continue;
            var img = AdvTerrain.MinimapImage(floor.Data);
            for (int y = 0; y < floor.Height; y++)
            for (int x = 0; x < floor.Width; x++)
                if (floor.IsBlocked(x, y) && !floor.IsWater(x, y))
                    img.SetPixel(x, y, img.GetPixel(x, y).Darkened(0.45f));
            foreach (var o in map.Objects)
            {
                if (o.Pos.Floor != floor.Index || o.Kind is ObjectKind.Static or ObjectKind.Hero || o.Owner < 0) continue;
                var mp = map.Players.ElementAtOrDefault(o.Owner);
                if (mp == null) continue;
                var c = AdvColors.Rgb(mp.Colour);
                foreach (var t in o.BlockedTiles.Append(o.Pos))
                    if (floor.InBounds(t.X, t.Y)) img.SetPixel(t.X, t.Y, c);
            }
            img.FlipY();
            list.Add(ImageTexture.CreateFromImage(img));
        }
        return list;
    }
}

/// <summary>
/// The stock thumbnail slideshow (H5_Game.exe vtable 00e37948, made by 006f5520 over two windows): the first
/// window (<c>screenshoot1</c>) is put above the second (priority + 1, 006f5260). 006f5220 sets the texture list and
/// the hold / fade times (3000 / 3000 ms on both custom-game screens: 00707920, 00722140), resets the clock and
/// the index, fits the windows (006f5150 / 006f4d10) and advances once (006f4fd0: the lower window takes image
/// i, the index steps with wrap, the upper window takes the next). 006f5070 per frame: the clock advances by the
/// frame time modulo hold + fade; during the fade the upper window's transparency is (t − hold) / fade; past
/// hold + fade the clock wraps and the images advance; the upper window shows only after the hold, the lower one
/// always. With no images both windows are hidden.
/// </summary>
public sealed partial class ThumbnailSlideshow : Node
{
    readonly UiNode? _upper, _lower;
    readonly TextureRect _upperTex = Rect(), _lowerTex = Rect();
    List<Texture2D> _images = new();
    int _hold = 3000, _fade = 3000, _clock, _index;
    double _carry;

    public ThumbnailSlideshow(UiNode? screenshoot1, UiNode? screenshoot2)
    {
        Name = "ThumbnailSlideshow";
        _upper = screenshoot1;
        _lower = screenshoot2;
        _upper?.AddChild(_upperTex);
        _lower?.AddChild(_lowerTex);
        // 006f5260: screenshoot1's priority = screenshoot2's + 1 (drawn above it).
        if (_upper?.GetParent() is { } p && _lower?.GetParent() == p && _upper.GetIndex() < _lower.GetIndex())
            p.MoveChild(_upper, _lower.GetIndex());
        Show(false, false);
    }

    static TextureRect Rect() => new()
    {
        AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore,
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
    };

    public int Index => _index;
    public int Clock => _clock;

    /// <summary>006f5220 (vf 0x1c): new list and times.</summary>
    public void SetImages(List<Texture2D> images, int holdMs = 3000, int fadeMs = 3000)
    {
        _images = images;
        _hold = holdMs;
        _fade = fadeMs;
        _clock = 0;
        _index = 0;
        _carry = 0;
        if (_images.Count == 0) { Show(false, false); return; }
        Fit(_upper, _upperTex, _images[0]);
        Fit(_lower, _lowerTex, _images[Math.Min(1, _images.Count - 1)]);
        Advance();
        Tick(0);
    }

    void Advance()
    {
        // 006f4fd0
        _lowerTex.Texture = _images[_index];
        if (++_index >= _images.Count) _index = 0;
        _upperTex.Texture = _images[_index];
    }

    /// <summary>006f5070 (vf 0x20): one frame of <paramref name="ms"/> milliseconds.</summary>
    public void Tick(int ms)
    {
        if (_images.Count == 0) { Show(false, false); return; }
        int period = _hold + _fade;
        _clock += ms % period;
        if (_clock > _hold && _clock < period)
            _upperTex.Modulate = new Color(1, 1, 1, (float)(_clock - _hold) / _fade);
        else if (_clock >= period)
        {
            _clock = _clock - _hold - _fade;
            Advance();
        }
        Show(_clock > _hold, true);
    }

    void Show(bool upper, bool lower)
    {
        if (_upper != null) _upper.Visible = upper;
        if (_lower != null) _lower.Visible = lower;
    }

    public override void _Process(double delta)
    {
        _carry += delta * 1000;
        int ms = (int)_carry;
        _carry -= ms;
        Tick(ms);
    }

    /// <summary>
    /// 006f4d10: the image fills the window unless the width and height scale factors differ by more than 5 % of
    /// their sum; then it is fitted inside and centred.
    /// </summary>
    static void Fit(UiNode? window, TextureRect rect, Texture2D tex)
    {
        if (window == null) return;
        var r = Fitted(window.Window.Rect.W, window.Window.Rect.H, tex.GetWidth(), tex.GetHeight());
        float ww = Math.Max(1, window.Window.Rect.W), wh = Math.Max(1, window.Window.Rect.H);
        rect.AnchorLeft = r.X / ww;
        rect.AnchorTop = r.Y / wh;
        rect.AnchorRight = (r.X + r.W) / ww;
        rect.AnchorBottom = (r.Y + r.H) / wh;
        rect.OffsetLeft = rect.OffsetTop = rect.OffsetRight = rect.OffsetBottom = 0;
    }

    /// <summary>006f4d10's placement of a texW x texH image in a winW x winH window (window-local x, y, w, h).</summary>
    public static (float X, float Y, float W, float H) Fitted(float winW, float winH, float texW, float texH)
    {
        if (texW <= 0 || texH <= 0) return (0, 0, winW, winH);
        float sx = winW / texW, sy = winH / texH;
        if (!(Math.Abs(sy + sx) * 0.05f < Math.Abs(sx - sy))) return (0, 0, winW, winH);
        float w = sy <= sx ? MathF.Round(texW * sy) : winW;
        float h = sx <= sy ? MathF.Round(texH * sx) : winH;
        float x = sy <= sx ? MathF.Round(Math.Abs(w - winW) * 0.5f) : 0;
        float y = sx <= sy ? MathF.Round(Math.Abs(h - winH) * 0.5f) : 0;
        return (x, y, w, h);
    }
}
