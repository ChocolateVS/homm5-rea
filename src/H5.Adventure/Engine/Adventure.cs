using H5.Adventure.Data;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Engine;

/// <summary>Per-player options when starting a game.</summary>
public sealed class PlayerSetup
{
    public int MapPlayer;
    public bool Human = true;
    /// <summary>Race override for players whose map race is random (TOWN_RANDOM_TYPE / TOWN_NO_TYPE).</summary>
    public string? Race;
}

public sealed class GameSetup
{
    public List<PlayerSetup> Players = new();
    /// <summary>0 = Easy, 1 = Normal, 2 = Hard, 3 = Heroic (DefaultStats index).</summary>
    public int Difficulty = 1;
    public bool Campaign;
    public ulong Seed = 1;

    /// <summary>Every active map player, humans for those that allow it.</summary>
    public static GameSetup AllPlayers(AdvMap map, int humans = 1, ulong seed = 1)
    {
        var s = new GameSetup { Seed = seed };
        foreach (var p in map.Players.Where(p => p.Active))
            s.Players.Add(new PlayerSetup { MapPlayer = p.Index, Human = p.CanBeHuman && humans-- > 0 });
        return s;
    }
}

/// <summary>
/// The adventure-map rules engine. Deterministic: given the same map, state and command
/// sequence it produces identical events and state hashes (for lockstep networking).
/// </summary>
public sealed partial class Adventure
{
    public AdventureDb Db { get; }
    public GameDatabase Core => Db.Core;
    public AdvMap Map { get; }
    public AdvState S { get; }
    List<AdvEvent> _ev = new();

    public Adventure(AdventureDb db, AdvMap map, AdvState state)
    {
        Db = db;
        Map = map;
        S = state;
    }

    void Emit(AdvEvent e) => _ev.Add(e);

    public PlayerState Current => S.Players[S.CurrentPlayer];

    public AdvResult Apply(AdvCommand cmd)
    {
        if (S.Finished) return AdvResult.Reject("game is over");
        _ev = new List<AdvEvent>();
        if (cmd.Player != Current.Index) return AdvResult.Reject("not your turn");
        string? err;
        if (S.Battle != null && cmd is not ResolveBattleCmd) err = "a battle must be resolved first";
        else if (S.Choice != null && cmd is not AnswerChoiceCmd) err = "a choice must be answered first";
        else err = cmd switch
        {
            MoveHeroCmd c => DoMove(c),
            EndTurnCmd => DoEndTurn(),
            BuildCmd c => DoBuild(c),
            RecruitCmd c => DoRecruit(c),
            HireHeroCmd c => DoHire(c),
            ChooseLevelUpCmd c => DoChooseLevelUp(c),
            AnswerChoiceCmd c => DoAnswer(c),
            ResolveBattleCmd c => DoResolveBattle(c),
            DismissHeroCmd c => DoDismiss(c),
            BuyWarMachineCmd c => DoBuyWarMachine(c),
            TransferStackCmd c => DoTransfer(c),
            SwapStacksCmd c => DoSwap(c),
            GarrisonHeroCmd c => DoGarrison(c),
            MoveArtifactCmd c => DoMoveArtifact(c),
            CastAdventureSpellCmd c => DoCastAdventureSpell(c),
            BuildShipCmd c => DoBuildShip(c),
            ExchangeResourcesCmd c => DoExchange(c),
            SellArtifactCmd c => DoSellArtifact(c),
            UpgradeCreatureCmd c => DoUpgradeCreature(c),
            BuyFactoryWarMachineCmd c => DoBuyFactoryWarMachine(c),
            BuyCartographerMapCmd c => DoBuyCartographerMap(c),
            UseTreeOfKnowledgeCmd c => DoUseTreeOfKnowledge(c),
            _ => "unknown command",
        };
        var result = err != null ? AdvResult.Reject(err) : new AdvResult { Ok = true, Events = _ev };
        Observer?.Invoke(cmd, result);
        return result;
    }

    /// <summary>Optional hook for logging/tools: called after every command with its result.</summary>
    public Action<AdvCommand, AdvResult>? Observer { get; set; }

    // ------------------------------------------------------------------ helpers

    MapObject O(int index) => Map.Objects[index];

    PlayerState? PlayerByIndex(int idx) => S.Players.Find(p => p.Index == idx);

    bool Allied(int a, int b) => a >= 0 && b >= 0 && PlayerByIndex(a) is { } pa && PlayerByIndex(b) is { } pb && pa.Team == pb.Team;

    void Give(PlayerState p, Resources r, int mul = 1)
    {
        p.Purse.Add(r, mul);
        var d = new Purse(r);
        if (mul != 1) for (int i = 0; i < 7; i++) d[i] *= mul;
        Emit(new ResourcesChanged(p.Index, d.R, (int[])p.Purse.R.Clone()));
    }

    void GiveOne(PlayerState p, int kind, int amount)
    {
        var d = new int[7];
        d[kind] = amount;
        p.Purse[kind] += amount;
        Emit(new ResourcesChanged(p.Index, d, (int[])p.Purse.R.Clone()));
    }

    static Resources Res(int kind, int amount)
    {
        var a = new int[7];
        a[kind] = amount;
        return new Resources(a[0], a[1], a[2], a[3], a[4], a[5], a[6]);
    }

    /// <summary>Nival rounding used throughout the original: ceil(x) − 1 (fan manual p.300, p.321).</summary>
    public static int NivalFloor(double x) => (int)Math.Ceiling(x) - 1;
}
