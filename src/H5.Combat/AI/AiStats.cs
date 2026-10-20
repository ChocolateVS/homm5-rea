using H5.Core.Data;

namespace H5.Combat.AI;

/// <summary>Game difficulty as the AI tunables index it (DefaultStats AI/*/Difficulty*).</summary>
public enum AiDifficulty { Easy, Normal, Hard, Heroic }

/// <summary>
/// One weighting row of AI/Combat (KamikazeMode, NormalMode, PowerSavingMode). In the exe these sit
/// at RPGStats+0xb90/0xbb0/0xbd0 (+4…+0x1c) and are read by the combat-value comparison FUN_00bfae20.
/// </summary>
public sealed record CombatModeCoefs(
    double EnemyDamage, double MyDamage,
    double EnemyTroopsHealth, double MyTroopsHealth,
    double EnemyWarMachinesHealth, double MyWarMachinesHealth,
    double CompareThreshold);

/// <summary>Per-difficulty combat AI switches (AI/Combat/Difficulty*, RPGStats+0xb54 + 12·difficulty).</summary>
public sealed record CombatDifficulty(
    int MaxSpellLevelToCast,
    bool CenterAreaSpellsOnCreatureOnly,
    bool UseDefendShootersMoves,
    bool UseBlockEnemyMoves,
    bool ConsiderOnlyShortestPathToEnemy);

/// <summary>Typed view of DefaultStats.xdb AI/Combat (exe: RPGStats+0xb50, reader FUN_0096a660).</summary>
public sealed class CombatAiStats
{
    public required CombatDifficulty[] Difficulty { get; init; }
    /// <summary>Our/enemy power ratio below which the AI fights recklessly (RPGStats+0xb84).</summary>
    public double KamikazeModeThreshold { get; init; }
    /// <summary>Our/enemy power ratio above which the AI preserves its troops (RPGStats+0xb88).</summary>
    public double PowerSavingThreshold { get; init; }
    /// <summary>Gold value ceiling for buying Fortress runes in combat (RPGStats+0xb8c, used by FUN_008d0960).</summary>
    public int MaxRuneMoneyEstimate { get; init; }
    public required CombatModeCoefs Kamikaze { get; init; }
    public required CombatModeCoefs Normal { get; init; }
    public required CombatModeCoefs PowerSaving { get; init; }
    public double EnemyLargeCreaturesMinRelativePower { get; init; }
    public double EnemyAreaAttackMinRelativePower { get; init; }
    public double OurShootersMinRelativePower { get; init; }
    public double OurShootersMinRelativeAdvantage { get; init; }

    /// <summary>
    /// The weights the position compare really uses (FUN_00bfae20 at 0x00bfaf5d..0x00bfaf8a): the mode switch falls through
    /// every case (three calls of the AI/Combat getter, a single +0x80), so every mode reads PowerSavingMode
    /// (RPGStats+0xbd0). The mode still decides the own-troops rule (skipped in kamikaze mode).
    /// </summary>
    public CombatModeCoefs CompareCoefs => PowerSaving;

    /// <summary>The weighting row of a mode as the data defines it (not what the compare reads, see <see cref="CompareCoefs"/>).</summary>
    public CombatModeCoefs Coefs(CombatMode m) => m switch
    {
        CombatMode.Kamikaze => Kamikaze,
        CombatMode.PowerSaving => PowerSaving,
        _ => Normal,
    };

    public static CombatAiStats Load(GameDatabase db)
    {
        var s = db.Stats;
        const string P = "AI/Combat/";
        CombatModeCoefs Mode(string n) => new(
            s.Get(P + n + "/EnemyDamageCoef", 1000), s.Get(P + n + "/MyDamageCoef", 1000),
            s.Get(P + n + "/EnemyTroopsHealthCoef", 400), s.Get(P + n + "/MyTroopsHealthCoef", 100),
            s.Get(P + n + "/EnemyWarMachinesHealthCoef", 1), s.Get(P + n + "/MyWarMachinesHealthCoef", 1),
            s.Get(P + n + "/CombatValueCompareTreshold", 0));
        CombatDifficulty Diff(string n) => new(
            (int)s.Get(P + n + "/MaxSpellLevelToCast", 5),
            s.Bool(P + n + "/CenterAreaSpellsOnCreatureOnly"),
            s.Bool(P + n + "/UseDefendShootersMoves", true),
            s.Bool(P + n + "/UseBlockEnemyMoves", true),
            s.Bool(P + n + "/ConsiderOnlyShortestPathToEnemy"));
        return new CombatAiStats
        {
            Difficulty = new[] { Diff("DifficultyEasy"), Diff("DifficultyNormal"), Diff("DifficultyHard"), Diff("DifficultyHeroic") },
            KamikazeModeThreshold = s.Get(P + "KamikazeModeTreshold", 0.2),
            PowerSavingThreshold = s.Get(P + "PowerSavingTreshold", 1.8),
            MaxRuneMoneyEstimate = (int)s.Get(P + "MaxRuneMoneyEstimate", 35000),
            Kamikaze = Mode("KamikazeMode"),
            Normal = Mode("NormalMode"),
            PowerSaving = Mode("PowerSavingMode"),
            EnemyLargeCreaturesMinRelativePower = s.Get(P + "Autoplacement/EnemyLargeCreaturesMinRelativePower", 0.55),
            EnemyAreaAttackMinRelativePower = s.Get(P + "Autoplacement/EnemyAreaAttackMinRelativePower", 0.35),
            OurShootersMinRelativePower = s.Get(P + "Autoplacement/OurShootersMinRelativePower", 0.2),
            OurShootersMinRelativeAdvantage = s.Get(P + "Autoplacement/OurShootersMinRelativeAdvantage", 1.3),
        };
    }
}

/// <summary>Combat AI behaviour mode chosen from the power ratio (exe FUN_00a2f4d0: 0 normal, 1 kamikaze, 2 power saving).</summary>
public enum CombatMode { Normal = 0, Kamikaze = 1, PowerSaving = 2 }
