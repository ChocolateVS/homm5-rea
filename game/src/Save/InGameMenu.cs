using Godot;
using H5Reborn.Adventure;
using H5Reborn.Options;
using H5Reborn.Ui;

namespace H5Reborn.Save;

/// <summary>
/// The stock in-game menu on the adventure map (UI/EscMenu): Save Game, Load Game,
/// Options, Restart Mission, Main Menu, Quit To Desktop, Return To Game. Save is
/// disabled while the game cannot be saved (battle in progress, game over).
/// </summary>
public static class InGameMenu
{
    public static void Show(AdventureScreen screen, Action returnToMainMenu)
    {
        var layer = screen.DialogLayer;
        var menu = new UiScreen("UI/EscMenu.(WindowScreen).xdb") { MouseFilter = Control.MouseFilterEnum.Stop, Name = "EscMenu" };
        if (menu.Find("SaveBtn") is { } save) save.Enabled = SaveManager.CannotSave(screen) == null;
        if (menu.Find("Surrender") is { } surrender) surrender.Visible = false;
        void Close() => menu.QueueFree();

        menu.Bus.On("return_to_game", Close);
        menu.Bus.On("save", () => { Close(); SaveLoadScreen.ShowInGame(screen, SaveLoadScreen.Mode.Save); });
        menu.Bus.On("load", () => { Close(); SaveLoadScreen.ShowInGame(screen, SaveLoadScreen.Mode.Load); });
        menu.Bus.On("options", () =>
        {
            Close();
            GameOptionsScreen? options = null;
            options = new GameOptionsScreen(() => options!.QueueFree()) { MouseFilter = Control.MouseFilterEnum.Stop };
            layer.AddChild(options);
        });
        menu.Bus.On("restart_mission", () => Ask(menu, "Restart Mission", "Restart the current mission? Progress since the start will be lost.", () => SaveManager.Restart(screen)));
        menu.Bus.On("main_menu", () => Ask(menu, "Main Menu", "Leave this game and return to the main menu?", () => { Close(); returnToMainMenu(); }));
        menu.Bus.On("exit_to_windows", () => Ask(menu, "Quit To Desktop", "Quit the game?", () => Cinematics.AfterGameSplash.ShowAndQuit(screen.GetTree())));
        layer.AddChild(menu);
    }

    static void Ask(Control menu, string title, string text, Action yes) =>
        MessageBoxes.YesNo(menu, title, text, ok => { if (ok) yes(); });
}
