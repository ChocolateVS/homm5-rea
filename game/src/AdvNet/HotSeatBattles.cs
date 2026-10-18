using Godot;
using H5.Adventure.State;

namespace H5Reborn.AdvNet;

/// <summary>
/// The hot-seat hand-over between two human players: the map is covered (the stock HotSeatBlackBg
/// window has no texture of its own, the engine paints it black) and the stock box announces the
/// next player ("Next player's turn" / "It is &lt;playername&gt; turn").
/// </summary>
public static class HotSeatHandOver
{
    public static string Header => Adventure.AdvTexts.Get("UI/AdventureScreen-heavy/hotseat_player_turn_header.txt");

    public static string Text(PlayerState p)
    {
        // The stock colour text files are named crosswise: blue_player.txt says "Teal player" and
        // dark_blue_player.txt says "Blue player" (brown is "Tan").
        var colour = Adventure.AdvColors.TextName(p.Colour) switch { "blue" => "dark_blue", "teal" => "blue", var c => c };
        var name = Adventure.AdvTexts.Get($"Text/Game/Player/Colors/{colour}_player.txt");
        if (string.IsNullOrWhiteSpace(name)) name = $"Player {p.Index + 1}";
        var text = Adventure.AdvTexts.Get("UI/AdventureScreen-heavy/hotseat_player_turn_text.txt", ("playername", name));
        // The message box shows its body as a plain value, so drop the colour tags (the body is white anyway).
        text = System.Text.RegularExpressions.Regex.Replace(text, "<color=[^>]*>", "").Replace("  ", " ").Trim();
        return string.IsNullOrWhiteSpace(text) ? name : text;
    }

    /// <summary>A full-screen black cover that also swallows input until removed.</summary>
    public static Control Cover() => new ColorRect
    {
        Name = "HotSeatCover",
        Color = Colors.Black,
        AnchorRight = 1,
        AnchorBottom = 1,
        MouseFilter = Control.MouseFilterEnum.Stop,
    };
}

/// <summary>
/// Which battle sides humans command in a local (single-player or hot-seat) adventure battle.
/// The attacker (side 0) is the human whose turn it is; in hot-seat the defender (side 1) is also
/// played at this PC when it belongs to another human: a defending hero, or a town (with or
/// without a garrison hero) owned by a human player.
/// </summary>
public static class HotSeatBattles
{
    public static int[] HumanSides(AdvState s, PendingBattle pb)
    {
        bool Human(int owner) => owner >= 0 && s.Players.Any(p => p.Index == owner && p.Human);
        int defenderOwner = -1;
        if (pb.DefenderHero >= 0)
            defenderOwner = s.Heroes.FirstOrDefault(h => h.Id == pb.DefenderHero)?.Owner ?? -1;
        else if (pb.DefenderObject >= 0)
            defenderOwner = s.Towns.FirstOrDefault(t => t.Object == pb.DefenderObject)?.Owner ?? -1;
        return Human(defenderOwner) ? new[] { 0, 1 } : new[] { 0 };
    }
}
