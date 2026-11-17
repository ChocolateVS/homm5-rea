using H5.Adventure.State;

namespace H5.Adventure.Engine;

/// <summary>
/// Game statistics for the mission results window (UI/MissionResults, rows of 0079fcf0). H5_Game.exe keeps them in
/// CMissionResultsLogger (world +0x170, ctor 00877f70), an IGameEventLogger fed through the world's event multiplexer
/// (world vf 0x180), and builds the window's 0xa0-byte summary with its IMissionResults vf 0 = 00877dd0 for the
/// human player. Counters whose event sources are not traced (creatures and resources gained) are not recorded:
/// their values are null.
/// </summary>
public sealed record MissionSummary(
    int Days, int Weeks, int Months,
    int[] ResourcesAtEnd, int[]? ResourcesDuring,
    int? BattlesFought, int? BattlesWon, int? BattlesLost,
    int? HeroesKilled, int? HeroesLost,
    int TownsOccupied, int TownsLost,
    int CreaturesAtEnd, int? CreaturesDuring,
    int EnemyCreaturesAtEnd, int? EnemyCreaturesDuring,
    int ArtifactsAtEnd, int ArtifactsDuring);

public sealed partial class Adventure
{
    /// <summary>
    /// 00877dd0 for <paramref name="player"/>: the record, the purse (player vf 0xd8), creatures (00877bf0) and artifacts
    /// (00877500) now, and the creatures of every player not allied with it (00a85030); the mission time of 0079fcf0
    /// from the day counter (world vf 0xa8 = <see cref="AdvState.Day"/> − 1): days = d % 7 + 1, weeks = (d / 7) % 4 + 1,
    /// months = d / 28 + 1.
    /// </summary>
    public MissionSummary Summary(int player)
    {
        var p = PlayerByIndex(player);
        var rec = p?.Mission ?? new MissionRecord();
        int d = S.Day - 1;
        int enemies = S.Players.Where(o => o.Index != player && !Allied(player, o.Index)).Sum(o => MissionCreatures(o.Index));
        return new MissionSummary(d % 7 + 1, d / 7 % 4 + 1, d / 28 + 1,
            p?.Purse.R.ToArray() ?? new int[7], null,
            rec.BattlesFought, rec.BattlesWon, rec.BattlesLost, rec.HeroesKilled, rec.HeroesLost,
            rec.TownsOccupied, rec.TownsLost,
            MissionCreatures(player), null, enemies, null,
            MissionArtifacts(player), rec.Artifacts);
    }

    /// <summary>
    /// 00877bf0: the creatures of the player's heroes' armies (player vf 0xc0) plus the army (00877b40: the stack counts
    /// of the object's army) of every map object the player owns — towns' garrisons and flagged army-holding
    /// buildings here.
    /// </summary>
    int MissionCreatures(int player)
    {
        int n = S.Heroes.Where(h => h.Alive && h.Owner == player).Sum(h => h.Army.Live().Sum(s => s.Count));
        n += S.Towns.Where(t => t.Owner == player).Sum(t => t.Garrison.Live().Sum(s => s.Count));
        n += S.Objects.Where(kv => kv.Value.Owner == player && !S.Towns.Any(t => t.Object == kv.Key)).Sum(kv => kv.Value.Army.Live().Sum(s => s.Count));
        return n;
    }

    /// <summary>00877500: per hero of the player (vf 0xc0) the worn (vf 0x74, 00ca66d0) and backpack (vf 0x78, 00ca6760) artifacts.</summary>
    int MissionArtifacts(int player) =>
        S.Heroes.Where(h => h.Alive && h.Owner == player).Sum(h => h.Artifacts.Count + h.Backpack.Count);

    /// <summary>CLogMapStartedCmd (vtable 00e0e954, execute 00428ad0) → logger vf 0x8 (00877d40): every player's artifact count.</summary>
    void LogMapStarted()
    {
        foreach (var p in S.Players) p.Mission.Artifacts = MissionArtifacts(p.Index);
    }

    /// <summary>Hero gains an artifact (00b4b610) → logger vf 0x38 (00877810): +1 for the hero's owner.</summary>
    void LogArtifactGained(HeroState h)
    {
        if (PlayerByIndex(h.Owner) is { } p) p.Mission.Artifacts++;
    }

    /// <summary>
    /// The combat end 00af6680 (when the combat's vf 0x110 is 0) → logger vf 0x18 = 00877570(…, winner side, loser
    /// side, …): each side's owner (side vf 4 → player vf 0x2c, players 1..8 only) counts a battle (+0x24), the winner's a
    /// win (+0x28), the loser's a loss (+0x2c). A neutral side has no player and counts nothing.
    /// </summary>
    void LogBattle(int winner, int loser)
    {
        if (PlayerByIndex(winner) is { } w) { w.Mission.BattlesFought++; w.Mission.BattlesWon++; }
        if (PlayerByIndex(loser) is { } l) { l.Mission.BattlesFought++; l.Mission.BattlesLost++; }
    }

    /// <summary>
    /// A hero beaten in battle: the death 004fe5b0 (player vf 0x148), flight 004fe770 (vf 0x140) and surrender 004fe890
    /// (vf 0x144) each call logger vf 0x28 = 00877680(killer's player, hero) when world vf 0x138 (+0x84) is clear and the
    /// killer (argument 3) and its owner (vf 0) are live → +0x34 of the killer's player. The flight and the surrender then
    /// run hero vf 0x1d8 / 0x1dc → 00b52cd0 → logger vf 0x2c = 00877700(the hero's owner) → +0x30; so does a death of a
    /// hero in the pool's return set (pool vf 0x48), which 004fe5b0 hands to vf 0x140.
    /// </summary>
    void LogHeroBeaten(HeroState h, HeroState? killer, bool toTavern)
    {
        if (killer != null && PlayerByIndex(killer.Owner) is { } k) k.Mission.HeroesKilled++;
        if ((toTavern || S.TavernReturnAfterDeath.Contains(h.DefId)) && PlayerByIndex(h.Owner) is { } o) o.Mission.HeroesLost++;
    }

    /// <summary>Town owner change (00cd5400) → logger vf 0x34 (00877780): the old owner lost, the new owner took a town.</summary>
    void LogTownOwner(int previous, int now)
    {
        if (previous == now) return;
        if (PlayerByIndex(previous) is { } a) a.Mission.TownsLost++;
        if (PlayerByIndex(now) is { } b) b.Mission.TownsOccupied++;
    }
}
