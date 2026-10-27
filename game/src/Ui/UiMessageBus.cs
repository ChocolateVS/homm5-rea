using H5.Core.Ui;

namespace H5Reborn.Ui;

/// <summary>
/// Routes the UI data's named commands (ARSendGameMessage EventName, UISRunReaction
/// names such as "single", "back", "Combat_Defend") to code. The names are the
/// contract between the original UI files and game logic, so buttons are never
/// hard-wired. Screen buses fall back to their parent (ultimately <see cref="Global"/>).
/// </summary>
public sealed class UiMessageBus
{
    public static readonly UiMessageBus Global = new(null);

    readonly UiMessageBus? _parent;
    readonly Dictionary<string, Action<UiCommand, UiNode?>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised for commands nobody handles (useful while screens are incomplete).</summary>
    public static event Action<UiCommand>? Unhandled;

    public UiMessageBus(UiMessageBus? parent) => _parent = parent;

    public void On(string name, Action handler) => _handlers[name] = (_, _) => handler();
    public void On(string name, Action<UiCommand, UiNode?> handler) => _handlers[name] = handler;
    public void Off(string name) => _handlers.Remove(name);

    public bool Dispatch(UiCommand cmd, UiNode? source)
    {
        if (cmd.Kind == "PlaySound") { UiAudio.PlaySound?.Invoke(cmd.Argument); return true; }
        if (cmd.Kind == "ShowWindow")
        {
            foreach (var w in source?.Screen?.FindAll(cmd.Argument) ?? Enumerable.Empty<UiNode>()) w.Visible = cmd.IntParam != 0;
            return true;
        }
        for (var bus = this; bus != null; bus = bus._parent)
        {
            if (bus._handlers.TryGetValue(cmd.Argument, out var h)) { h(cmd, source); return true; }
        }
        Godot.GD.Print($"[UI] unhandled {cmd.Kind}:{cmd.Argument}");
        Unhandled?.Invoke(cmd);
        return false;
    }

    public void Send(string name) => Dispatch(new UiCommand("Code", name), null);
}

/// <summary>Audio hooks; the sound workstream plugs players in here.</summary>
public static class UiAudio
{
    public static Action<UiNode>? Hover;
    public static Action<UiNode>? Click;
    public static Action<string>? PlaySound;
    /// <summary>The slider sound (uiconsts ScrollBarSound, played by 0048fd60).</summary>
    public static Action? Slider;
}
