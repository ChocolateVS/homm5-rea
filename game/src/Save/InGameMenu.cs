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
        // The stock confirmations: UI/EscMenu/RelatedTexts T_RESTART_MISSION_* and T_EXIT_TO_MAIN_MENU_* (00758460), and
        // UI/ExitGameQuestion T_HEADER / T_QUESTION for quitting, as in the combat Esc menu.
        menu.Bus.On("restart_mission", () => Ask(menu, EscTexts, "T_RESTART_MISSION_TITLE", "T_RESTART_MISSION_QUESTION", () => SaveManager.Restart(screen)));
        menu.Bus.On("main_menu", () => Ask(menu, EscTexts, "T_EXIT_TO_MAIN_MENU_TITLE", "T_EXIT_TO_MAIN_MENU_QUESTION", () => { Close(); returnToMainMenu(); }));
        menu.Bus.On("exit_to_windows", () => Ask(menu, ExitTexts, "T_HEADER", "T_QUESTION", () => Cinematics.AfterGameSplash.ShowAndQuit(screen.GetTree())));
        layer.AddChild(menu);
    }

    const string EscTexts = "UI/EscMenu/RelatedTexts.(WindowRelatedTexts).xdb";
    const string ExitTexts = "UI/ExitGameQuestion/Texts.(WindowRelatedTexts).xdb";

    static void Ask(Control menu, string texts, string title, string question, Action yes) =>
        MessageBoxes.YesNo(menu, UiGameRoot.RelatedTextAt(texts, title) ?? "", UiGameRoot.RelatedTextAt(texts, question) ?? "", ok => { if (ok) yes(); });
}
