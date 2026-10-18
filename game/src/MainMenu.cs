using Godot;
using H5Reborn.Ui;
using H5Reborn.Ui.Screens;

namespace H5Reborn;

/// <summary>
/// Front-end host: a "Background3D" viewport for the original 3D menu backdrop
/// (filled by the arena workstream) under the 2D UI layer, plus screen registration.
/// Dev args: `--goto=mainmenu|multiplayer|lobby|wait|create`, `--name=Alice`,
/// `--host`, `--connect=IP[:port]`, `--create-room=NAME`, `--join-first`,
/// `--preset=N`, `--ready`, `--autostart`, `--click=x,y@seconds`.
/// </summary>
public partial class MainMenu : Control
{
    public override void _Ready()
    {
        // The original lays its UI out in real screen pixels (no scaling), re-anchoring
        // panels to the corners at higher resolutions.
        GetTree().Root.ContentScaleMode = Window.ContentScaleModeEnum.Disabled;

        AddChild(new ColorRect { Name = "Black", Color = Colors.Black, AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        var container = new SubViewportContainer
        {
            Name = "Background3DContainer", AnchorRight = 1, AnchorBottom = 1, Stretch = true,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        container.AddChild(new SubViewport { Name = "Background3D", TransparentBg = true, HandleInputLocally = false });
        AddChild(container);

        var layer = new Control { Name = "UiLayer", AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(layer);
        UiNavigator.Attach(layer);
        UiNavigator.Register("mainmenu", () => new MainMenuScreen());
        UiNavigator.Register("multiplayer", () => new MainMenuScreen("MultiPlayer"));
        UiNavigator.Register("lobby", () => new DuelLobbyScreen());
        UiNavigator.Register("wait", () => new DuelWaitScreen());

        DevArgs.Parse();
        // `--goto=xdb:UI/CombatScreen-Heavy.(WindowScreen).xdb` shows any original screen as-is.
        if (DevArgs.Goto is { } path && path.StartsWith("xdb:"))
            UiNavigator.Register(path, () => new UiScreen(path[4..]));
        UiNavigator.Goto(DevArgs.Goto is { } g && UiNavigator.Has(g) ? g : "mainmenu");
        if (DevArgs.Goto == "create") UiNavigator.Goto("lobby");
        foreach (var (pos, at) in DevArgs.Clicks)
            GetTree().CreateTimer(at).Timeout += () => Click(pos);
    }

    static void Click(Vector2 pos)
    {
        foreach (var pressed in new[] { true, false })
            Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = pressed });
    }
}
