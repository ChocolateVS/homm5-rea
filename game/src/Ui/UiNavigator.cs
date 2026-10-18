using Godot;

namespace H5Reborn.Ui;

/// <summary>
/// Owns the visible UI stack: one current screen plus modal overlays, above the
/// "Background3D" viewport where 3D backdrops (main-menu arena, town, combat) render.
/// </summary>
public static class UiNavigator
{
    static Control? _layer;
    static UiScreen? _current;
    static readonly List<Control> _overlays = new();
    static readonly Dictionary<string, Func<UiScreen>> _screens = new(StringComparer.OrdinalIgnoreCase);

    public static UiScreen? Current => _current;

    public static void Attach(Control layer) { _layer = layer; _current = null; _overlays.Clear(); }

    public static void Register(string name, Func<UiScreen> factory) => _screens[name] = factory;

    public static bool Has(string name) => _screens.ContainsKey(name);

    public static UiScreen Goto(string name)
    {
        if (!_screens.TryGetValue(name, out var factory)) throw new ArgumentException($"Unknown screen {name}");
        while (_overlays.Count > 0) CloseOverlay(_overlays[^1]);
        _current?.QueueFree();
        _current = factory();
        _layer!.AddChild(_current);
        return _current;
    }

    /// <summary>Shows a modal overlay: a full-screen input blocker with the given content on top.</summary>
    public static T ShowOverlay<T>(T content) where T : Control
    {
        var blocker = new Control { Name = "Modal", AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Stop };
        blocker.AddChild(content);
        _layer!.AddChild(blocker);
        _overlays.Add(blocker);
        return content;
    }

    public static void CloseOverlay(Control content)
    {
        var blocker = content.GetParent() as Control ?? content;
        _overlays.Remove(blocker);
        blocker.QueueFree();
    }
}
