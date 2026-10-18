using Godot;
using H5Reborn.Adventure;
using H5Reborn.Options;

namespace H5Reborn.Save;

/// <summary>
/// Adventure-map save keys from the stock bindings (profiles\default_profile\input_a2.cfg:
/// quicksave F5, save F6, load F7, quickload F8) and the autosave: at the start of each
/// human player's turn, when the "autosave" option is on (H5_Game.exe FUN_00607d50 saves
/// "autosavename" after the AI has moved and a human player is to act). The turn a game
/// starts or is loaded on is not autosaved.
/// </summary>
public partial class SaveHotkeys : Node
{
    readonly AdventureScreen _screen;
    (int Day, int Player) _turn;

    SaveHotkeys(AdventureScreen screen)
    {
        _screen = screen;
        Name = "SaveHotkeys";
        _turn = (screen.Session.S.Day, screen.Session.S.CurrentPlayer);
    }

    public static SaveHotkeys Attach(AdventureScreen screen)
    {
        if (screen.GetNodeOrNull<SaveHotkeys>("SaveHotkeys") is { } existing) return existing;
        var keys = new SaveHotkeys(screen);
        screen.AddChild(keys);
        return keys;
    }

    bool Busy => _screen.DialogLayer.GetChildCount() > 0 || _screen.Session.PendingEvents > 0;

    // Dev/test: `--save-dev=quicksave,save|load|esc|endturn[,quickload]` acts a few seconds after the map opens.
    double _devTimer = -1;
    string[] _devSteps = Array.Empty<string>();
    static bool _devUsed;

    public override void _Ready()
    {
        if (!_devUsed && OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--save-dev=")) is { } arg)
        {
            _devUsed = true;
            _devSteps = arg["--save-dev=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
            _devTimer = 4;
        }
    }

    void RunDevStep(string step)
    {
        GD.Print($"[H5] save-dev: {step}");
        switch (step)
        {
            case "quicksave": SaveManager.QuickSave(_screen); break;
            case "quickload": if (SaveManager.QuickLoad() is { } e) GD.PushWarning($"[H5] quickload: {e}"); break;
            case "save": SaveLoadScreen.ShowInGame(_screen, SaveLoadScreen.Mode.Save); break;
            case "load": SaveLoadScreen.ShowInGame(_screen, SaveLoadScreen.Mode.Load); break;
            case "esc": _screen.ShowEscMenu(); break;
            case "endturn": _screen.EndTurn(); break;
        }
    }

    public override void _Process(double delta)
    {
        if (_devTimer >= 0 && (_devTimer -= delta) < 0 && _devSteps.Length > 0)
        {
            RunDevStep(_devSteps[0]);
            _devSteps = _devSteps[1..];
            if (_devSteps.Length > 0) _devTimer = 2;
        }
        var s = _screen.Session;
        var turn = (s.S.Day, s.S.CurrentPlayer);
        if (turn == _turn || !s.IsHumanTurn || s.PendingEvents > 0) return;
        _turn = turn;
        if (OptionsStore.GetBool("autosave", true)) SaveManager.AutoSave(_screen);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k || Busy) return;
        switch (k.Keycode)
        {
            case Key.F5:
                SaveManager.QuickSave(_screen);
                break;
            case Key.F8:
                if (SaveManager.QuickLoad() is { } error) _screen.Message(SaveManager.Text("UI/SaveLoad/LoadSpecific/load.txt", "Load"), error);
                break;
            case Key.F6:
                SaveLoadScreen.ShowInGame(_screen, SaveLoadScreen.Mode.Save);
                break;
            case Key.F7:
                SaveLoadScreen.ShowInGame(_screen, SaveLoadScreen.Mode.Load);
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }
}
