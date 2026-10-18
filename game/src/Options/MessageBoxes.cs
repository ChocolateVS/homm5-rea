using Godot;

namespace H5Reborn.Options;

/// <summary>Original-style message boxes (UI/CommonBoxes, via the adventure dialogs) for menu screens.</summary>
public static class MessageBoxes
{
    static Control Host(Control screen)
    {
        // Boxes go on a full-screen layer above the screen so they centre and block input.
        var layer = new Control { Name = "MessageBox", AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Stop };
        screen.AddChild(layer);
        layer.ChildExitingTree += _ => Callable.From(() => { if (GodotObject.IsInstanceValid(layer) && layer.GetChildCount() == 0) layer.QueueFree(); }).CallDeferred();
        return layer;
    }

    public static void YesNo(Control screen, string title, string text, Action<bool> answer) =>
        Adventure.AdvDialogs.YesNo(Host(screen), title, text, null, answer);

    public static void Message(Control screen, string title, string text, Action? onClose = null) =>
        Adventure.AdvDialogs.Message(Host(screen), title, text, null, onClose);
}
