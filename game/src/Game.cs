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

    /// <summary>The game being run: stock 3.1 or MMH 5.5 (chosen at startup, see <see cref="FrontEnd.TargetPrompt"/>).</summary>
    public GameTarget Target { get; private set; }
    /// <summary>Both installs were found and nothing decided which to run: Boot asks before anything else loads.</summary>
    public GameTargets.Selection? PendingTargetPrompt { get; private set; }
    GameTargets.Selection? _selection;

    public override void _EnterTree()
    {
        Instance = this;
        try
        {
            _selection = FrontEnd.TargetPrompt.Compute();
            if (_selection.InitialRoot is not { } root) throw GameInstall.NotFound();
            Mount(_selection.Initial, root);
            if (_selection.NeedsPrompt) PendingTargetPrompt = _selection;
        }
        catch (Exception e)
        {
            StartupError = e.Message;
            GD.PushError($"[H5] {e.Message}");
        }
    }

    void Mount(GameTarget target, string root)
    {
        Install = GameInstall.At(root);
        Fs = PakFileSystem.OpenInstall(Install);
        Xdb = new XdbLoader(Fs);
        Target = target;
        GD.Print($"[H5] Mounted {Fs.Paths.Count} entries from {Install.Root} (target {GameTargets.Name(target)})");
    }

    /// <summary>
    /// The startup prompt's answer: when it differs from the install mounted for the prompt, drop everything loaded
    /// from that data and mount the chosen install. Only the prompt has used the data at this point.
    /// </summary>
    public void ChooseTarget(GameTarget target)
    {
        PendingTargetPrompt = null;
        if (_selection?.RootFor(target) is not { } root || target == Target) return;
        Fs?.Dispose();
        _textures.Clear();
        _db = null;
        Ui.UiResources.Reset();
        Options.OptionsData.Reset();
        Mount(target, root);
    }

    public override void _ExitTree() => Fs?.Dispose();

    // Dev/test hook: `-- --screenshot=out.png --screenshot-after=3.0` saves the
    // viewport after the given seconds and quits. Used for automated visual checks.
    string? _screenshotPath;
    double _screenshotAfter = 3.0, _elapsed;

    public override void _Ready()
    {
        Settings.VideoSettings.Apply();
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
            else if (Effects.EffectViewer.HandleArg(arg)) { }
            else if (Combat.CombatLauncher.HandleArg(arg)) { }
            else if (Adventure.AdventureLauncher.HandleArg(arg)) { }
            else if (Town.TownLauncher.HandleArg(arg)) { }
            else if (AdvNet.AdvNetDevDriver.HandleArg(arg)) { }
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
        // Headless (dummy renderer) has no framebuffer to read back: just quit on time.
        if (DisplayServer.GetName() != "headless" && GetViewport().GetTexture()?.GetImage() is { } shot && !shot.IsEmpty())
        {
            shot.SavePng(_screenshotPath);
            GD.Print($"[H5] Screenshot saved to {_screenshotPath}");
        }
        else GD.Print($"[H5] No framebuffer (headless?); screenshot skipped: {_screenshotPath}");
        _screenshotPath = null;
        GetTree().Quit();
    }

    /// <summary>
    /// Drops what the previous screens loaded and nothing uses any more (called when a new adventure map is opened, after
    /// the old screen is gone): textures no material or node uses any more (forgotten here, freed with their last C#
    /// holder; a later request loads them again)
    /// and the decoded skeleton / animation cache of the character builder. A long session (maps, battles, towns) would
    /// otherwise keep everything it ever showed. Returns the number of textures released.
    /// </summary>
    public int ReleaseUnused()
    {
        // Wrappers of freed materials / nodes still hold their textures until the GC collects them.
        GC.Collect();
        if (!OperatingSystem.IsBrowser()) GC.WaitForPendingFinalizers();
        GC.Collect();
        int released = 0;
        foreach (var (path, tex) in _textures.ToList())
        {
            if (!GodotObject.IsInstanceValid(tex) || tex.GetReferenceCount() > 1) continue;
            // Not disposed: a C# holder elsewhere (a UI cache) may share this wrapper; the texture goes with the last one.
            _textures.Remove(path);
            released++;
        }
        Render.CharacterBuilder.ReleaseDecoded();
        return released;
    }

    /// <summary>Loads a texture from the paks. Accepts a .dds path or a (Texture) .xdb path/href.</summary>
    public Texture2D? LoadTexture(string pathOrHref)
    {
        var path = ResolveTexturePath(pathOrHref);
        if (path == null) return null;
        if (_textures.TryGetValue(path, out var cached)) return cached;
        byte[] bytes;
        using (Dev.LoadTrace.Time("texture read")) if (!Fs.TryReadAllBytes(path, out bytes)) return null;
        Image? image;
        using (Dev.LoadTrace.Time("texture decode")) image = LoadDds(bytes, path);
        if (image == null) return null;
        ImageTexture tex;
        using (Dev.LoadTrace.Time("texture upload")) tex = ImageTexture.CreateFromImage(image);
        // The texture holds its own copy (on the GPU); free the decoded image now instead of whenever the GC
        // finalizes its small wrapper (the browser build's 2 GB wasm heap filled up with them).
        image.Dispose();
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
    internal static Image? LoadDds(byte[] bytes, string path)
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
