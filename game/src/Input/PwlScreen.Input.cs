using Godot;

namespace H5Reborn.Campaign;

/// <summary>
/// UI/PWLScreen bonus buttons send select_bonus on click and select_bonus_and_start on double click
/// (commandsOnLDblKlick): a double click picks the bonus and starts the mission, like pressing Start.
/// </summary>
public partial class PwlScreen
{
    public override void _EnterTree()
    {
        Bus.On("select_bonus_and_start", (_, node) =>
        {
            Bus.Dispatch(new H5.Core.Ui.UiCommand("RunReaction", "select_bonus"), node);
            Bus.Send("start");
        });
    }
}
