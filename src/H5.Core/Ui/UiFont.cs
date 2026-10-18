using H5.Core.IO;
using H5.Core.Ui.Binary;
using H5.Core.Xdb;

namespace H5.Core.Ui;

/// <summary>
/// A glyph in a font atlas. The atlas rect (X1,Y1)-(X2,Y2) is drawn with its left edge at
/// pen + <see cref="A"/>; the pen then moves by A + B. The rect is wider than B for italic
/// faces (overhang). Verified visually against the stock Header/Default atlases.
/// </summary>
public readonly record struct UiGlyph(char Char, int X1, int Y1, int X2, int Y2, int A, int B, int RectWidth)
{
    public int Width => X2 - X1;
    public int Height => Y2 - Y1;
    public int Advance => A + B;
}

/// <summary>
/// Pre-rendered bitmap font. Each UI/Fonts/*.(Font).xdb names a source Windows face
/// (e.g. "Monotype Corsiva" for Default), a pixel height and an atlas texture; the
/// glyph table lives in <c>bin/fonts/&lt;uid&gt;</c> (localised, shipped in texts.pak).
/// </summary>
public sealed class UiFont
{
    public string Name = "";          // logical face used by markup: Default, Header, System, Button
    public string FaceName = "";      // Windows face the atlas was rendered from
    public int Height;                // nominal size from the xdb
    public string? TexturePath, DdsPath;
    public string Uid = "";
    /// <summary>Header fields from the glyph blob (ids 10..17). See docs/formats/ui.md.</summary>
    public int CellHeight, Ascent, Descent, LineHeight, DefaultChar;
    /// <summary>Distance between consecutive line tops (field 13, never less than the cell).</summary>
    public int LineAdvance => Math.Max(LineHeight, CellHeight);
    public Dictionary<int, int> HeaderFields = new();
    public Dictionary<char, UiGlyph> Glyphs = new();

    public UiGlyph? Glyph(char c) =>
        Glyphs.TryGetValue(c, out var g) ? g : Glyphs.TryGetValue((char)DefaultChar, out var d) ? d : null;

    public int MeasureWidth(string s) => s.Sum(c => Glyph(c)?.Advance ?? 0);

    public static UiFont Load(XdbLoader xdb, string fontXdbPath)
    {
        var fs = xdb.FileSystem;
        var e = xdb.Load(fontXdbPath).Root!;
        var f = new UiFont
        {
            Name = (string?)e.Element("Name") ?? "",
            FaceName = (string?)e.Element("FaceName") ?? "",
            Height = (int?)e.Element("Height") ?? 0,
            Uid = (string?)e.Element("uid") ?? "",
        };
        var tex = XdbRef.Parse((string?)e.Element("Texture")?.Attribute("href"), fontXdbPath);
        if (!tex.IsEmpty)
        {
            f.TexturePath = tex.Path;
            if (fs.Exists(tex.Path))
            {
                var dest = XdbRef.Parse((string?)xdb.Load(tex.Path).Root!.Element("DestName")?.Attribute("href"), tex.Path);
                f.DdsPath = dest.IsEmpty ? null : dest.Path;
            }
        }
        if (fs.TryReadAllBytes("bin/fonts/" + f.Uid, out var blob)) ParseGlyphs(f, blob);
        return f;
    }

    /// <summary>
    /// Glyph blob layout (inside the standard envelope's payload):
    /// ids 10..17 = header ints, id 1 = hash_map&lt;u16 char, glyph&gt; (id3 = count, id4 = buckets,
    /// then all id1 keys followed by all id2 values; value = 7×i32: x1,y1,x2,y2,A,B,rectWidth),
    /// id 2 = kerning hash_map (empty in stock fonts).
    /// </summary>
    static void ParseGlyphs(UiFont f, byte[] blob)
    {
        var payload = NivalObject.Payload(blob);
        foreach (var c in payload.Children())
        {
            if (c.Id >= 10 && c.Data.Length <= 4) f.HeaderFields[c.Id] = c.Data.AsInt32();
            if (c.Id != 1) continue;
            // Keys are stored first (all id-1 chunks), then the values in the same order (id-2).
            var keys = new List<int>();
            var values = new List<byte[]>();
            foreach (var kv in c.Data.Children())
            {
                if (kv.Id == 1 && kv.Data.Length == 2) keys.Add(kv.Data.AsInt32() & 0xFFFF);
                else if (kv.Id == 2 && kv.Data.Length == 28) values.Add(kv.Data.Bytes.ToArray());
            }
            for (int i = 0; i < Math.Min(keys.Count, values.Count); i++)
            {
                var b = values[i];
                int I(int k) => BitConverter.ToInt32(b, k * 4);
                f.Glyphs[(char)keys[i]] = new UiGlyph((char)keys[i], I(0), I(1), I(2), I(3), I(4), I(5), I(6));
            }
        }
        f.HeaderFields.TryGetValue(10, out f.CellHeight);
        f.HeaderFields.TryGetValue(12, out f.Descent);
        f.HeaderFields.TryGetValue(13, out f.LineHeight);
        f.HeaderFields.TryGetValue(15, out f.DefaultChar);
        // Glyph rects are exactly CellHeight tall (the visual line box); field 13 is the
        // line-to-line advance (Button_16: cell 19, advance 37) and field 17 the ascent
        // (baseline offset from the cell top), used to align mixed fonts on one line.
        f.Ascent = f.HeaderFields.TryGetValue(17, out var asc) && asc > 0 ? asc : f.CellHeight - f.Descent;
    }
}

/// <summary>All fonts from UI/Fonts/FontsTable.(FontsTable).xdb with markup face/size lookup.</summary>
public sealed class UiFontTable
{
    public const string TablePath = "UI/Fonts/FontsTable.(FontsTable).xdb";
    public readonly List<UiFont> Fonts = new();

    public static UiFontTable Load(XdbLoader xdb)
    {
        var t = new UiFontTable();
        foreach (var item in xdb.Load(TablePath).Root!.Element("fonts")!.Elements("Item"))
        {
            var r = XdbRef.Parse((string?)item.Attribute("href"), TablePath);
            if (!r.IsEmpty && xdb.FileSystem.Exists(r.Path)) t.Fonts.Add(UiFont.Load(xdb, r.Path));
        }
        return t;
    }

    /// <summary>
    /// Picks the font for <c>&lt;font face=X size=N&gt;</c>: same logical face, nearest
    /// height (ties go to the smaller). Markup asks for sizes with no atlas (h2 = Header 28),
    /// so the original must also choose a neighbour — verify against the exe if text
    /// metrics look off.
    /// </summary>
    public UiFont? Find(string face, int size)
    {
        var cands = Fonts.Where(f => string.Equals(f.Name, face, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cands.Count == 0) cands = Fonts.Where(f => f.Name == "Default").ToList();
        return cands.OrderBy(f => Math.Abs(f.Height - size)).ThenBy(f => f.Height).FirstOrDefault();
    }
}
