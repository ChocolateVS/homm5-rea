using Godot;
using H5Reborn.Interaction;

namespace H5Reborn.Ui;

/// <summary>
/// Keyboard confirm/cancel for the stock modal windows that have no code of their own for it.
/// The original UI data carries no hotkeys (button xdbs only have DefaultSubState); the keys come
/// from input_a2.cfg sections — global close_window/confirm_action (Esc / Enter), "saveload"
/// (Esc close, Enter save_load_enter) and "mpcreate" (Esc back). Each listed window maps the
/// section's action to the message its own Cancel/OK button sends, so keys and buttons agree.
/// Screens that override _UnhandledInput themselves keep their own handling.
/// </summary>
public partial class UiScreen
{
    /// <summary>(input section, Esc message, Enter message) per window name.</summary>
    static readonly Dictionary<string, (string Section, string? Esc, string? Enter)> DialogKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GameOptions.(WindowScreen)"] = ("", "back", null),
        ["EscMenu"] = ("", "return_to_game", null),
        ["Save.(WindowScreen)"] = ("saveload", "close", "save"),
        ["Load.(WindowScreen)"] = ("saveload", "close", "load"),
        ["CustomGameCreate.(WindowScreen)"] = ("mpcreate", "back", null),
        ["HallOfFame.(WindowScreen)"] = ("", "close", null),
    };

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k || !IsVisibleInTree()) return;
        if (!DialogKeys.TryGetValue(Name, out var keys)) return;
        var actions = KeyBindings.Actions(keys.Section, k);
        if (keys.Section.Length > 0) actions.AddRange(KeyBindings.Actions(KeyBindings.Global, k));
        foreach (var a in actions)
        {
            string? msg = a switch
            {
                "close_window" or "esc_pressed" or "close" or "back" or "save_load_esc" => keys.Esc,
                "confirm_action" or "enter_pressed" or "save_load_enter" or "enter" => keys.Enter,
                _ => null,
            };
            if (msg == null) continue;
            GetViewport().SetInputAsHandled();
            Bus.Send(msg);
            return;
        }
    }
}
