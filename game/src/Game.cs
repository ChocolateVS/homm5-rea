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
        }
        SetProcess(_screenshotPath != null);
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
        var image = new Image();
        var err = image.LoadDdsFromBuffer(bytes);
        if (err != Error.Ok) { GD.PushWarning($"[H5] DDS load failed ({err}): {path}"); return null; }
        var tex = ImageTexture.CreateFromImage(image);
        _textures[path] = tex;
        return tex;
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
