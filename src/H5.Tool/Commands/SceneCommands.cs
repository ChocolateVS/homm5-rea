using System.Xml.Linq;
using H5.Assets.Terrain;
using H5.Core.IO;
using H5.Core.Ui;
using H5.Core.Ui.Imaging;
using H5.Core.Xdb;

namespace H5.Tool.Commands;

static class SceneUtil
{
    /// <summary>Loads the DDS behind a (Texture) xdb path, or a .dds path directly.</summary>
    public static RgbaImage? LoadTexture(PakFileSystem fs, XdbLoader xdb, string? texturePath)
    {
        if (string.IsNullOrEmpty(texturePath)) return null;
        try
        {
            if (texturePath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                return fs.TryReadAllBytes(texturePath, out var raw) ? DdsImage.Decode(raw) : null;
            var tex = xdb.Load(texturePath).Root!;
            var dest = XdbRef.Parse((string?)tex.Element("DestName")?.Attribute("href"), texturePath);
            return !dest.IsEmpty && fs.TryReadAllBytes(dest.Path, out var bytes) ? DdsImage.Decode(bytes) : null;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or FileNotFoundException)
        {
            Console.Error.WriteLine($"texture {texturePath}: {e.Message}");
            return null;
        }
    }

    public static string ArenaTerrainPath(PakFileSystem fs, string arena)
    {
        if (arena.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) return arena;
        var dir = arena.Contains('/') ? arena.TrimEnd('/') : "Maps/CombatArenas/" + arena;
        var map = dir + "/map.xdb";
        if (fs.Exists(map))
        {
            var href = (string?)XDocument.Parse(System.Text.Encoding.UTF8.GetString(fs.ReadAllBytes(map)))
                .Root!.Element("GroundTerrainFileName")?.Attribute("href");
            if (!string.IsNullOrEmpty(href)) return XdbRef.Parse(href, map).Path;
        }
        return dir + "/GroundTerrain.bin";
    }
}

sealed class TerrainDumpCommand : ICommand
{
    public string Name => "terrain-dump";
    public string Help => "terrain-dump <arena|mapdir|file.bin> <outDir> [pxPerTile=8] [tilesPerTexture=4] - render terrain grids to PNG";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 2) { Console.WriteLine(Help); return 1; }
        var path = SceneUtil.ArenaTerrainPath(ctx.Fs, args[0]);
        int ppt = args.Length > 2 ? int.Parse(args[2]) : 8;
        float tilesPerTex = args.Length > 3 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 4;
        var outDir = args[1];
        var t = TerrainReader.Read(ctx.Fs.ReadAllBytes(path));
        Console.WriteLine($"{path}: {t.TilesX}x{t.TilesY} tiles, {t.Layers.Count} layers, version {t.Version}, flags14=0x{t.Flags14:X2}");
        foreach (var l in t.Layers) Console.WriteLine($"  layer {l.TilePath}");

        float min = t.Heights.Data.Min(), max = t.Heights.Data.Max();
        Console.WriteLine($"  heights {min:F2}..{max:F2}");
        SaveGray(t.Heights.Width, t.Heights.Height, i => (t.Heights.Data[i] - min) / Math.Max(1e-6f, max - min), Path.Combine(outDir, "heights.png"));
        void Bytes(Grid<byte>? g, string name)
        {
            if (g == null) return;
            var hist = g.Data.GroupBy(b => b).OrderByDescending(x => x.Count()).Take(8).Select(x => $"{x.Key}:{x.Count()}");
            Console.WriteLine($"  {name} {g.Width}x{g.Height} values {string.Join(" ", hist)}");
            byte bmax = Math.Max((byte)1, g.Data.Max());
            SaveGray(g.Width, g.Height, i => g.Data[i] / (float)bmax, Path.Combine(outDir, name + ".png"));
        }
        Bytes(t.VertexFlags, "chunk07");
        Bytes(t.VertexMap8, "chunk08");
        Bytes(t.HalfTileMap, "chunk10");
        Bytes(t.VertexMap15, "chunk15");
        if (t.Heights2 != null)
        {
            float a = t.Heights2.Data.Min(), b = t.Heights2.Data.Max();
            Console.WriteLine($"  chunk13 floats {a:F2}..{b:F2}");
            SaveGray(t.Heights2.Width, t.Heights2.Height, i => (t.Heights2.Data[i] - a) / Math.Max(1e-6f, b - a), Path.Combine(outDir, "chunk13.png"));
        }
        for (int i = 0; i < t.Layers.Count; i++)
            Bytes(t.Layers[i].Weights, $"layer{i}");

        RenderBlend(ctx, t, ppt, tilesPerTex, Path.Combine(outDir, "blend.png"));
        Console.WriteLine($"wrote PNGs to {Path.GetFullPath(outDir)}");
        return 0;
    }

    static void SaveGray(int w, int h, Func<int, float> v, string path)
    {
        var img = new RgbaImage(w, h);
        for (int i = 0; i < w * h; i++)
        {
            byte g = (byte)Math.Clamp(v(i) * 255, 0, 255);
            img.Pixels[i * 4] = img.Pixels[i * 4 + 1] = img.Pixels[i * 4 + 2] = g; img.Pixels[i * 4 + 3] = 255;
        }
        PngWriter.Save(img, path);
    }

    /// <summary>Composites tile textures with the layer weights and simple N·L shading.</summary>
    static void RenderBlend(ToolContext ctx, TerrainData t, int ppt, float tilesPerTex, string path)
    {
        var xdb = new XdbLoader(ctx.Fs);
        var textures = t.Layers.Select(l =>
        {
            if (!ctx.Fs.Exists(l.TilePath)) return null;
            var tile = xdb.Load(l.TilePath).Root!;
            var r = XdbRef.Parse((string?)tile.Element("Texture")?.Attribute("href"), l.TilePath);
            return SceneUtil.LoadTexture(ctx.Fs, xdb, r.Path);
        }).ToList();
        var normals = t.ComputeNormals();
        var light = System.Numerics.Vector3.Normalize(new(-0.5f, -0.6f, 0.8f));
        int W = t.TilesX * ppt, H = t.TilesY * ppt;
        var img = new RgbaImage(W, H);
        for (int py = 0; py < H; py++)
        for (int px = 0; px < W; px++)
        {
            float tx = px / (float)ppt, ty = py / (float)ppt;
            float r = 0, g = 0, b = 0;
            for (int li = 0; li < t.Layers.Count; li++)
            {
                float w = Bilinear(t.Layers[li].Weights, tx, ty) / 255f;
                if (w <= 0) continue;
                var tex = textures[li];
                float cr = 255, cg = 0, cb = 255;
                if (tex != null)
                {
                    int sx = (int)((tx / tilesPerTex % 1 + 1) % 1 * tex.Width), sy = (int)((ty / tilesPerTex % 1 + 1) % 1 * tex.Height);
                    int si = (sy * tex.Width + sx) * 4;
                    cr = tex.Pixels[si]; cg = tex.Pixels[si + 1]; cb = tex.Pixels[si + 2];
                }
                r = r * (1 - w) + cr * w; g = g * (1 - w) + cg * w; b = b * (1 - w) + cb * w;
            }
            var n = normals[Math.Min(t.Heights.Height - 1, (int)ty) * t.Heights.Width + Math.Min(t.Heights.Width - 1, (int)tx)];
            float shade = 0.45f + 0.55f * Math.Max(0, System.Numerics.Vector3.Dot(n, light));
            int di = (py * W + px) * 4;
            img.Pixels[di] = (byte)Math.Min(255, r * shade); img.Pixels[di + 1] = (byte)Math.Min(255, g * shade);
            img.Pixels[di + 2] = (byte)Math.Min(255, b * shade); img.Pixels[di + 3] = 255;
        }
        PngWriter.Save(img, path);
    }

    static float Bilinear(Grid<byte> g, float x, float y)
    {
        int x0 = Math.Clamp((int)x, 0, g.Width - 1), y0 = Math.Clamp((int)y, 0, g.Height - 1);
        int x1 = Math.Min(x0 + 1, g.Width - 1), y1 = Math.Min(y0 + 1, g.Height - 1);
        float fx = x - x0, fy = y - y0;
        return (g[x0, y0] * (1 - fx) + g[x1, y0] * fx) * (1 - fy) + (g[x0, y1] * (1 - fx) + g[x1, y1] * fx) * fy;
    }
}

sealed class TerrainStatsCommand : ICommand
{
    public string Name => "terrain-stats";
    public string Help => "terrain-stats - decode every *Terrain.bin in the archives and report failures";

    public int Run(ToolContext ctx, string[] args)
    {
        int ok = 0, fail = 0;
        foreach (var p in ctx.Fs.Paths.Where(p => p.EndsWith("Terrain.bin", StringComparison.OrdinalIgnoreCase)).Order())
        {
            try
            {
                var t = TerrainReader.Read(ctx.Fs.ReadAllBytes(p));
                if (t.Heights.Width != t.TilesX + 1 || t.Layers.Any(l => l.Weights.Width != t.TilesX + 1))
                    throw new InvalidDataException("unexpected grid size");
                ok++;
                if (args.Contains("-v")) Console.WriteLine($"{p}: {t.TilesX}x{t.TilesY} layers={t.Layers.Count} h={t.Heights.Data.Min():F1}..{t.Heights.Data.Max():F1}");
            }
            catch (Exception e) { fail++; Console.WriteLine($"FAIL {p}: {e.Message}"); }
        }
        Console.WriteLine($"decoded {ok}, failed {fail}");
        return fail == 0 ? 0 : 1;
    }
}

sealed class UiDumpCommand : ICommand
{
    public string Name => "ui-dump";
    public string Help => "ui-dump <window.xdb> [out.png] [width height] [--all] [--hide=A,B] - print a UI window tree and render it (--all shows hidden windows)";

    public int Run(ToolContext ctx, string[] args)
    {
        if (args.Length < 1) { Console.WriteLine(Help); return 1; }
        var xdb = new XdbLoader(ctx.Fs);
        var root = new UiLoader(xdb).Load(args[0]);
        var pos = args.Where(a => !a.StartsWith("--")).ToArray();
        int w = pos.Length > 3 ? int.Parse(pos[2]) : UiLayout.DesignWidth, h = pos.Length > 3 ? int.Parse(pos[3]) : UiLayout.DesignHeight;
        bool all = args.Contains("--all");
        UiLayout.Apply(root, w, h);
        Print(root, 0);
        if (pos.Length > 1 && pos[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            var r = new UiPreviewRenderer(ctx.Fs, xdb) { ShowHidden = all, DrawOutlines = args.Contains("--outlines") };
            foreach (var hide in args.Where(a => a.StartsWith("--hide=")).SelectMany(a => a[7..].Split(',')))
                r.Hidden.Add(hide);
            var img = r.Render(root, w, h);
            PngWriter.Save(img, pos[1]);
            Console.WriteLine($"rendered {pos[1]} ({r.Missing.Count} missing textures)");
            foreach (var m in r.Missing.Take(10)) Console.WriteLine($"  missing {m}");
        }
        return 0;
    }

    static void Print(UiWindow win, int depth)
    {
        var bg = win.Background?.DdsPath ?? win.Background?.TexturePath;
        var txt = win.Text?.Raw is { Length: > 0 } t ? $" text=\"{t.Replace("\n", "\n")[..Math.Min(40, t.Length)]}\"" : "";
        var cmds = string.Join(",", win.ButtonStates.SelectMany(s => s.OnEnter).Select(c => $"{c.Kind}:{c.Argument}"));
        Console.WriteLine($"{new string(' ', depth * 2)}{win.Type} '{win.Name}' {win.Rect}{(win.Visible ? "" : " hidden")}" +
                          $"{(bg != null ? $" bg={win.Background!.Kind}:{bg}" : "")}{txt}{(cmds.Length > 0 ? $" cmd={cmds}" : "")}");
        foreach (var c in win.Children) Print(c, depth + 1);
    }
}

sealed class FontDumpCommand : ICommand
{
    public string Name => "font-dump";
    public string Help => "font-dump [outDir] - list UI fonts and their glyph metrics; optionally render a sample sheet per font";

    public int Run(ToolContext ctx, string[] args)
    {
        var xdb = new XdbLoader(ctx.Fs);
        var table = UiFontTable.Load(xdb);
        foreach (var f in table.Fonts)
        {
            Console.WriteLine($"{f.Name,-8} h={f.Height,-3} face='{f.FaceName}' glyphs={f.Glyphs.Count} cell={f.CellHeight} line={f.LineHeight} desc={f.Descent} default={f.DefaultChar} " +
                              $"hdr=[{string.Join(",", f.HeaderFields.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"))}] dds={f.DdsPath}");
            if (args.Length == 0 || f.DdsPath == null) continue;
            var atlas = DdsImage.Decode(ctx.Fs.ReadAllBytes(f.DdsPath));
            const string sample = "Heroes of Might and Magic V - Tribes of the East 0123456789";
            var img = new RgbaImage(Math.Max(64, f.MeasureWidth(sample) + 8), f.CellHeight + 8);
            img.Fill(0xFF203040);
            int x = 4;
            foreach (var ch in sample)
            {
                if (f.Glyph(ch) is not { } g) continue;
                img.Draw(atlas, g.X1, g.Y1, g.Width, g.Height, x + g.A, 4, g.Width, g.Height, 0xFFF2C011);
                x += g.Advance;
            }
            PngWriter.Save(img, Path.Combine(args[0], $"{f.Name}_{f.Height}.png"));
        }
        return 0;
    }
}

sealed class GlyphDumpCommand : ICommand
{
    public string Name => "glyph-dump";
    public string Help => "glyph-dump <face> <size> <chars> - print raw glyph records";

    public int Run(ToolContext ctx, string[] args)
    {
        var f = UiFontTable.Load(new XdbLoader(ctx.Fs)).Find(args[0], int.Parse(args[1]))!;
        Console.WriteLine($"{f.Name} {f.Height}");
        foreach (var ch in args[2])
            if (f.Glyphs.TryGetValue(ch, out var g)) Console.WriteLine($"'{ch}' rect=({g.X1},{g.Y1})-({g.X2},{g.Y2}) w={g.Width} A={g.A} B={g.B} rectW={g.RectWidth}");
        return 0;
    }
}
