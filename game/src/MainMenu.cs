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
    /// <summary>Screen to open when the front end loads next (e.g. back to the duel room after a battle).</summary>
    public static string? ReturnScreen;

    public override void _Ready()
    {
        // The original lays its UI out in real screen pixels (no scaling), re-anchoring
        // panels to the corners at higher resolutions.
        H5Reborn.Settings.VideoSettings.ApplyScaling();
        Audio.MenuAudio.Attach(this, "MAIN_MENU_MUSIC_ID");

        AddChild(new ColorRect { Name = "Black", Color = Colors.Black, AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        var container = new SubViewportContainer
        {
            Name = "Background3DContainer", AnchorRight = 1, AnchorBottom = 1, Stretch = true,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        // Opaque: sky areas not covered by the scene's cards show the scene's fog/clear colour,
        // as the original's 3D view does, not the black backing.
        var background = new SubViewport { Name = "Background3D", TransparentBg = false, HandleInputLocally = false };
        // The stock 3D backdrop (Arenas/Interface/MainMenu) behind the menu UI.
        background.AddChild(new Arena.MainMenuBackground());
        container.AddChild(background);
        AddChild(container);

        var layer = new Control { Name = "UiLayer", AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(layer);
        UiNavigator.Attach(layer);
        UiNavigator.Register("mainmenu", () => new MainMenuScreen());
        UiNavigator.Register("multiplayer", () => new MainMenuScreen("MultiPlayer"));
        UiNavigator.Register("lobby", () => new DuelLobbyScreen());
        UiNavigator.Register("wait", () => new DuelWaitScreen());
        UiNavigator.Register("options", () => new OptionsScreen());
        UiNavigator.Register("credits", () => new FrontEnd.CreditsScreen());
        UiNavigator.Register("hotseatduel", () => new Combat.HotseatDuelScreen());

        DevArgs.Parse();
        // `--goto=xdb:UI/CombatScreen-Heavy.(WindowScreen).xdb` shows any original screen as-is.
        if (DevArgs.Goto is { } path && path.StartsWith("xdb:"))
            UiNavigator.Register(path, () => new UiScreen(path[4..]));
        var start = ReturnScreen ?? (DevArgs.Goto is { } g && UiNavigator.Has(g) ? g : "mainmenu");
        ReturnScreen = null;
        UiNavigator.Goto(start);
        if (DevArgs.Goto == "create") UiNavigator.Goto("lobby");
        foreach (var (pos, at) in DevArgs.Clicks)
            GetTree().CreateTimer(at).Timeout += () => Click(pos);
    }

    static void Click(Vector2 pos)
    {
        // Buttons fire only while hovered; move there first (the window may be off-screen in tests).
        Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
        foreach (var pressed in new[] { true, false })
            Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = pressed });
    }
}
