using Godot;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5Reborn;

/// <summary>
/// Autoload singleton: owns the mounted original game archives and caches
/// Godot resources created from them. Everything visual is sourced from the
/// user's install through here.
/// </summary>
public partial class Game : Node
{
    public static Game Instance { get; private set; } = null!;

    public GameInstall Install { get; private set; } = null!;
    public PakFileSystem Fs { get; private set; } = null!;
    public XdbLoader Xdb { get; private set; } = null!;
    public string? StartupError { get; private set; }

    H5.Core.Data.GameDatabase? _db;
    /// <summary>Typed rules data (creatures, spells, presets…), loaded on first use.</summary>
    public H5.Core.Data.GameDatabase Db => _db ??= H5.Core.Data.GameDatabase.Load(Fs);

    readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);

    public override void _EnterTree()
    {
        Instance = this;
        try
        {
            var explicitRoot = OS.GetEnvironment("H5_GAME_DIR");
            Install = GameInstall.Locate(string.IsNullOrEmpty(explicitRoot) ? null : explicitRoot);
            Fs = PakFileSystem.OpenInstall(Install);
            Xdb = new XdbLoader(Fs);
            GD.Print($"[H5] Mounted {Fs.Paths.Count} entries from {Install.Root}");
        }
        catch (Exception e)
        {
            StartupError = e.Message;
            GD.PushError($"[H5] {e.Message}");
        }
    }

    public override void _ExitTree() => Fs?.Dispose();

    // Dev/test hook: `-- --screenshot=out.png --screenshot-after=3.0` saves the
    // viewport after the given seconds and quits. Used for automated visual checks.
    string? _screenshotPath;
    double _screenshotAfter = 3.0, _elapsed;

    public override void _Ready()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--screenshot=")) _screenshotPath = arg["--screenshot=".Length..];
            else if (arg.StartsWith("--screenshot-after="))
                _screenshotAfter = double.Parse(arg["--screenshot-after=".Length..], System.Globalization.CultureInfo.InvariantCulture);
            else if (arg.StartsWith("--view-model="))
            {
                Dev.ModelViewer.ModelPath = arg["--view-model=".Length..];
                CallDeferred(MethodName.OpenModelViewer);
            }
            else if (arg.StartsWith("--view-character="))
            {
                Dev.ModelViewer.CharacterPath = arg["--view-character=".Length..];
                CallDeferred(MethodName.OpenModelViewer);
            }
            else if (Arena.ArenaViewer.HandleArg(arg)) { }
            else if (Combat.CombatLauncher.HandleArg(arg)) { }
            else if (arg.StartsWith("--anim=")) Dev.ModelViewer.Anim = arg["--anim=".Length..];
            else if (arg.StartsWith("--anim-time="))
                Dev.ModelViewer.AnimTime = float.Parse(arg["--anim-time=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        }
        SetProcess(_screenshotPath != null);
    }

    void OpenModelViewer()
    {
        var tree = GetTree();
        tree.CurrentScene?.QueueFree();
        var viewer = new Dev.ModelViewer();
        tree.Root.AddChild(viewer);
        tree.CurrentScene = viewer;
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (_screenshotPath == null || _elapsed < _screenshotAfter) return;
        GetViewport().GetTexture().GetImage().SavePng(_screenshotPath);
        GD.Print($"[H5] Screenshot saved to {_screenshotPath}");
        _screenshotPath = null;
        GetTree().Quit();
    }

    /// <summary>Loads a texture from the paks. Accepts a .dds path or a (Texture) .xdb path/href.</summary>
    public Texture2D? LoadTexture(string pathOrHref)
    {
        var path = ResolveTexturePath(pathOrHref);
        if (path == null) return null;
        if (_textures.TryGetValue(path, out var cached)) return cached;
        if (!Fs.TryReadAllBytes(path, out var bytes)) return null;
        var image = LoadDds(bytes, path);
        if (image == null) return null;
        var tex = ImageTexture.CreateFromImage(image);
        _textures[path] = tex;
        return tex;
    }

    /// <summary>
    /// The stock exporter often omits the smallest mip levels (4x4 and below) while
    /// the header still counts them, and Godot requires the full chain. Pad the
    /// missing levels by repeating the last present level's first block so the
    /// texture stays GPU-compressed; fall back to the software decoder for
    /// anything Godot still can't read.
    /// </summary>
    static Image? LoadDds(byte[] bytes, string path)
    {
        var image = new Image();
        if (bytes.Length >= 128 && PadMipChain(ref bytes) && image.LoadDdsFromBuffer(bytes) == Error.Ok)
            return image;
        try
        {
            var rgba = H5.Assets.Textures.DdsDecoder.Decode(bytes);
            var img = Image.CreateFromData(rgba.Width, rgba.Height, false, Image.Format.Rgba8, rgba.Pixels);
            img.GenerateMipmaps();
            return img;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[H5] DDS decode failed: {path}: {e.Message}");
            return null;
        }
    }

    static bool PadMipChain(ref byte[] dds)
    {
        if (BitConverter.ToUInt32(dds, 0) != 0x20534444) return false; // "DDS "
        int height = BitConverter.ToInt32(dds, 12), width = BitConverter.ToInt32(dds, 16);
        int mips = Math.Max(1, BitConverter.ToInt32(dds, 28));
        uint pfFlags = BitConverter.ToUInt32(dds, 80), fourCC = BitConverter.ToUInt32(dds, 84);
        int bpp = BitConverter.ToInt32(dds, 88);
        int blockBytes = fourCC switch
        {
            0x31545844 => 8,                              // DXT1
            0x33545844 or 0x34545844 or 0x35545844 => 16, // DXT3/4/5
            _ => 0,
        };
        if ((pfFlags & 0x4) != 0 && blockBytes == 0) return true; // other FourCC: leave to Godot
        int unit = blockBytes > 0 ? blockBytes : Math.Max(1, bpp / 8);
        if (mips > 1)
        {
            // Godot expects the complete chain down to 1x1 whenever mipmaps are present.
            int full = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
            if (full != mips) { mips = full; BitConverter.TryWriteBytes(dds.AsSpan(28, 4), mips); }
        }

        long expected = 128, lastLevelOffset = 128;
        for (int level = 0, w = width, h = height; level < mips; level++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
        {
            long size = blockBytes > 0 ? (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes : (long)w * h * unit;
            if (expected + size <= dds.Length) lastLevelOffset = expected;
            expected += size;
        }
        if (dds.Length >= expected) return true;
        if (lastLevelOffset + unit > dds.Length) return false;

        var padded = new byte[expected];
        Buffer.BlockCopy(dds, 0, padded, 0, dds.Length);
        for (long p = dds.Length; p < expected; p += unit)
            Buffer.BlockCopy(dds, (int)lastLevelOffset, padded, (int)p, (int)Math.Min(unit, expected - p));
        dds = padded;
        return true;
    }

    string? ResolveTexturePath(string pathOrHref)
    {
        var r = XdbRef.Parse(pathOrHref);
        if (r.IsEmpty) return null;
        if (r.Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) return r.Path;
        // A (Texture) xdb names its image in <DestName href="..."/>, relative to itself.
        if (!Fs.Exists(r.Path)) return null;
        var root = Xdb.Load(r.Path).Root;
        var dest = (string?)root?.Element("DestName")?.Attribute("href");
        if (string.IsNullOrEmpty(dest)) return null;
        return XdbRef.Parse(dest, r.Path).Path;
    }
}
