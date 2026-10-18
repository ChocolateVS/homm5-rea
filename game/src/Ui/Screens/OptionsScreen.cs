namespace H5Reborn.Ui.Screens;

/// <summary>
/// The main menu's Options screen: the data-driven UI/GameOptions screen
/// (game/src/Options/GameOptionsScreen), returning to <paramref name="returnTo"/> on Back.
/// </summary>
public partial class OptionsScreen(string returnTo = "mainmenu")
    : Options.GameOptionsScreen(() => UiNavigator.Goto(returnTo));
