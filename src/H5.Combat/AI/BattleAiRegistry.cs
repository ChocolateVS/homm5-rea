namespace H5.Combat.AI;

/// <summary>
/// Chooses a combat AI by name so clients, servers and tools share one switch.
/// "original" (default) is the port of the stock game's AI; "greedy" is the earlier stand-in.
/// </summary>
public static class BattleAiRegistry
{
    public const string Original = "original", Greedy = "greedy";
    public static readonly string[] Names = { Original, Greedy };

    public static IBattleAI Create(string? name = null, AiDifficulty difficulty = AiDifficulty.Normal) =>
        (name ?? Original).ToLowerInvariant() switch
        {
            Greedy => new GreedyBattleAI(),
            Original => new OriginalBattleAI { Difficulty = difficulty },
            var other => throw new ArgumentException($"unknown combat AI '{other}' (expected {string.Join("/", Names)})"),
        };

    public static AiDifficulty ParseDifficulty(string? s) =>
        string.IsNullOrEmpty(s) ? AiDifficulty.Normal : Enum.Parse<AiDifficulty>(s, ignoreCase: true);
}
