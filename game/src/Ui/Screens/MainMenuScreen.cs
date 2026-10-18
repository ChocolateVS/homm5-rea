namespace H5Reborn.Ui.Screens;

/// <summary>
/// UI/MainMenu2: the stock main menu. Its alternative full-screen panels are all
/// present in the data and toggled by the menu's messages, as the original
/// screen code does.
/// </summary>
public partial class MainMenuScreen : UiScreen
{
    static readonly string[] Panels =
    {
        "Main", "SinglePlayer", "MultiPlayer", "SelectCampaign", "SelectMission", "HotSeatSubMenu", "LoadReplay",
    };

    public MainMenuScreen(string panel = "Main") : base("UI/MainMenu2.(WindowScreen).xdb")
    {
        ShowOnly(panel, Panels);
        SetVisible("SinglePlayer/ChangeProfile", false);
        Find("Version")?.SetValue("version", "3.1");

        Bus.On("single", () => ShowOnly("SinglePlayer", Panels));
        Bus.On("multi", () => ShowOnly("MultiPlayer", Panels));
        Bus.On("back", () => ShowOnly("Main", Panels));
        Bus.On("lan", () => UiNavigator.Goto("lobby"));
        Bus.On("ubi_com", () => UiNavigator.Goto("lobby"));
        Bus.On("main_menu_exit", () => GetTree().Quit());
        Bus.On("hot_seat", () => ShowOnly("HotSeatSubMenu", Panels));
        // Hot-seat duel: preset choice on the stock duel options window, then a local battle.
        Bus.On("duel_mode_hotseat", () =>
        {
            if (!UiNavigator.Has("hotseatduel")) UiNavigator.Register("hotseatduel", () => new Combat.HotseatDuelScreen());
            UiNavigator.Goto("hotseatduel");
        });
        // Custom Game opens the stock map list (UI/CustomGameCreate) like the original; a duel
        // against the AI stays reachable through Hot-Seat → Duel Mode and `--combat-vs-ai`.
        Bus.On("custom_game", () =>
        {
            if (!UiNavigator.Has("customgame")) UiNavigator.Register("customgame", () => new Adventure.CustomGameScreen());
            UiNavigator.Goto("customgame");
        });
        Bus.On("change_profile", () => SetVisible("SinglePlayer/ChangeProfile", true));
        Bus.On("profile_select", () => SetVisible("SinglePlayer/ChangeProfile", false));
    }
}
