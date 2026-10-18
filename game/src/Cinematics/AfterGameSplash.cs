using Godot;
using H5Reborn.Ui;

namespace H5Reborn.Cinematics;

/// <summary>
/// UI/AfterGameSplash (UIGameRoot ID_SPLASH_SCREEN, opened by the show_splash_screen
/// command; profiles/editor*.cfg set disable_splash_screen): the screen's pic1, pic2, …
/// windows (the exe collects "pic%d" children in order, 006b02b0) shown one after another
/// before the game closes. Any key or click advances.
/// </summary>
public partial class AfterGameSplash : UiScreen
{
    readonly List<UiNode> _pictures = new();
    readonly Action _onFinished;
    int _index = -1;
    double _shown;

    /// <summary>Seconds per picture. Not found in the data; kept from the earlier boot splash.</summary>
    public const double HoldTime = 3.5;

    public AfterGameSplash(Action onFinished) : base("UI/AfterGameSplash.(WindowScreen).xdb")
    {
        _onFinished = onFinished;
        for (int i = 1; Find($"pic{i}") is { } pic; i++)
        {
            _pictures.Add(pic);
            pic.Visible = false;
        }
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>Shows the splash (unless disabled with `--no-splash`) and then quits.</summary>
    public static void ShowAndQuit(SceneTree tree)
    {
        if (StartupOptions.DisableSplash) { tree.Quit(); return; }
        var layer = new CanvasLayer { Layer = 100, Name = "AfterGameSplash" };
        tree.Root.AddChild(layer);
        layer.AddChild(new AfterGameSplash(() => tree.Quit()));
    }

    public override void _Ready()
    {
        base._Ready();
        Next();
    }

    void Next()
    {
        if (_index >= 0 && _index < _pictures.Count) _pictures[_index].Visible = false;
        if (++_index >= _pictures.Count) { _onFinished(); return; }
        _pictures[_index].Visible = true;
        _shown = 0;
    }

    public override void _Process(double delta)
    {
        _shown += delta;
        if (_shown >= HoldTime) Next();
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true }) Next();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true }) Next();
    }
}

/// <summary>
/// Command-line switches for startup/exit presentation (after `--`), read directly from
/// the command line. Dev launches that jump to a screen (`--view-*`, `--combat-*`,
/// `--adventure=`, `--goto=` …) skip the intro automatically; `--play-intro` forces it.
/// </summary>
public static class StartupOptions
{
    static readonly string[] Neutral = { "--screenshot", "--play-intro", "--no-splash", "--position", "--resolution" };

    static string[] Args => OS.GetCmdlineUserArgs();

    /// <summary>`--skip-intro`, or any dev arg that opens another screen.</summary>
    public static bool SkipIntro =>
        !Args.Contains("--play-intro") &&
        (Args.Contains("--skip-intro") || Args.Any(a => !Neutral.Any(n => a.StartsWith(n, StringComparison.Ordinal))));

    /// <summary>`--no-splash`: quit without the after-game splash (the stock disable_splash_screen setvar).</summary>
    public static bool DisableSplash => Args.Contains("--no-splash");
}
