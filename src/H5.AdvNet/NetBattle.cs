using System.Text.Json.Nodes;
using H5.Combat;
using H5.Core.Data;

namespace H5.AdvNet;

/// <summary>
/// A battle started on the adventure map that humans fight interactively (attacker = battle
/// side 0, defender = side 1). The host runs it authoritatively; each participating human keeps
/// a lockstep replica (like duels — both participants see both armies, so nothing is hidden)
/// and checks the state hash after every command. AI-controlled sides (computer players,
/// neutrals, dropped humans) are played by the host's battle AI.
/// </summary>
public sealed class NetBattle
{
    public Battle Battle { get; }
    /// <summary>Room side controlling each battle side, or -1 for the AI.</summary>
    public int[] Controller { get; }
    public int AttackerMapPlayer { get; }
    readonly IBattleAI _ai;
    readonly JsonNode _initialState;
    public long TurnStartedMs { get; private set; }
    int _lastActor = int.MinValue;

    public NetBattle(GameDatabase core, BattleState initial, int[] controller, int attackerMapPlayer, IBattleAI ai, long nowMs)
    {
        _initialState = JsonNode.Parse(initial.Serialize())!;
        Battle = new Battle(core, initial);
        Controller = controller;
        AttackerMapPlayer = attackerMapPlayer;
        _ai = ai;
        TurnStartedMs = nowMs;
    }

    public bool Finished => Battle.State.Phase == BattlePhase.Finished;

    /// <summary>Begins the battle; returns the "start" record for each participant (keyed by room side).</summary>
    public Dictionary<int, JsonObject> Start()
    {
        var opening = JsonNode.Parse(MessageJson.Serialize(Battle.Begin()))!;
        var starts = new Dictionary<int, JsonObject>();
        for (int bs = 0; bs < 2; bs++)
            if (Controller[bs] >= 0)
                starts[Controller[bs]] = new JsonObject
                {
                    ["start"] = new JsonObject
                    {
                        ["state"] = _initialState.DeepClone(),
                        ["mySide"] = bs,
                        ["opening"] = opening.DeepClone(),
                    },
                };
        return starts;
    }

    public IEnumerable<int> Participants => Controller.Where(c => c >= 0).Distinct();

    public int? BattleSideOf(int roomSide)
    {
        for (int bs = 0; bs < 2; bs++) if (Controller[bs] == roomSide) return bs;
        return null;
    }

    /// <summary>Applies a human command (side taken from the connection). Null on success, else the reason.</summary>
    public string? ApplyHuman(int roomSide, BattleCommand cmd, List<JsonObject> updates, long nowMs)
    {
        if (BattleSideOf(roomSide) is not { } bs) return "you are not in this battle";
        var r = Apply(cmd with { Side = bs }, updates, nowMs);
        if (r != null) return r;
        RunAi(updates, nowMs);
        return null;
    }

    string? Apply(BattleCommand cmd, List<JsonObject> updates, long nowMs)
    {
        var r = Battle.Apply(cmd);
        if (!r.Ok) return r.Error ?? "rejected";
        updates.Add(new JsonObject
        {
            ["command"] = JsonNode.Parse(MessageJson.Serialize(cmd)),
            ["events"] = JsonNode.Parse(MessageJson.Serialize(r.Events)),
            ["hash"] = Battle.State.ComputeHash(),
        });
        TurnStartedMs = nowMs;
        return null;
    }

    /// <summary>Lets the AI act for every AI-controlled side until a human must act or the battle ends.</summary>
    public void RunAi(List<JsonObject> updates, long nowMs)
    {
        for (int guard = 0; guard < 10000 && !Finished; guard++)
        {
            var s = Battle.State;
            if (s.Phase == BattlePhase.Tactics)
            {
                bool acted = false;
                for (int bs = 0; bs < 2; bs++)
                    if (Controller[bs] < 0 && !s.TacticsDone[bs])
                    {
                        Apply(new EndTacticsCmd { Side = bs }, updates, nowMs);
                        acted = true;
                    }
                if (!acted) return;
                continue;
            }
            if (Battle.SideToAct is not { } side || Controller[side] >= 0) return;
            var cmd = _ai.Decide(Battle, side) with { Side = side };
            if (Apply(cmd, updates, nowMs) != null && Apply(new DefendCmd(Battle.State.ActiveUnitOrHero()) { Side = side }, updates, nowMs) != null)
                return;
        }
    }

    /// <summary>
    /// Dynamic battles: a human who runs out of time defends (assumption — the original's
    /// timeout action was not traced). Limit = creature TimeToCommand (hero: DynamicBattle_HeroTimeToCommand)
    /// × the speed coefficient (2 / 1.5 / 1 for slow / normal / fast).
    /// </summary>
    public bool TimeoutIfDue(Func<BattleState, int, double> timeToCommand, int dynamicSpeed, List<JsonObject> updates, long nowMs)
    {
        if (dynamicSpeed <= 0 || Finished || Battle.State.Phase != BattlePhase.Combat) return false;
        if (Battle.SideToAct is not { } side || Controller[side] < 0) return false;
        var actor = Battle.State.ActiveUnitOrHero();
        if (actor != _lastActor) { _lastActor = actor; TurnStartedMs = nowMs; }
        double coef = dynamicSpeed switch { 1 => 2.0, 2 => 1.5, _ => 1.0 };
        double seconds = timeToCommand(Battle.State, actor) * coef;
        if (nowMs - TurnStartedMs < seconds * 1000) return false;
        if (Apply(new DefendCmd(actor) { Side = side }, updates, nowMs) != null)
            Apply(new WaitCmd(actor) { Side = side }, updates, nowMs);
        RunAi(updates, nowMs);
        return true;
    }

    /// <summary>Hands control of a dropped human's battle side to the AI.</summary>
    public void ReleaseSide(int roomSide, List<JsonObject> updates, long nowMs)
    {
        for (int bs = 0; bs < 2; bs++) if (Controller[bs] == roomSide) Controller[bs] = -1;
        RunAi(updates, nowMs);
    }

    public List<ArmySlot> Survivors(int side) => Battle.State.Units
        .Where(u => u.Side == side && u.Alive && !u.Summoned && !u.IsWarMachine)
        .GroupBy(u => u.CreatureId).Select(g => new ArmySlot(g.Key, g.Sum(u => u.Count))).ToList();

    public bool AttackerFled => Battle.State.EndReason?.Contains("fled", StringComparison.OrdinalIgnoreCase) == true
        || Battle.State.EndReason?.Contains("surrender", StringComparison.OrdinalIgnoreCase) == true;
}

static class BattleStateExtensions
{
    /// <summary>The unit (or hero actor id) whose turn it is.</summary>
    public static int ActiveUnitOrHero(this BattleState s) => s.Active ?? Actor.Hero(0);
}
