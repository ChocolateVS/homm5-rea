using H5.Adventure.Engine;
using H5.Adventure.State;

namespace H5.AdvNet;

/// <summary>What ends simultaneous turns. The seam between the original rules and fan-mod variants.</summary>
public interface ISimultaneousPolicy
{
    /// <summary>Whether two players' areas meeting switches the game to sequential turns.</summary>
    bool BreakOnIntersection { get; }
}

/// <summary>TotE 3.1: the first intersection ends simultaneous turns for the rest of the game.</summary>
public sealed class OriginalSimultaneousPolicy : ISimultaneousPolicy
{
    public bool BreakOnIntersection => true;
}

/// <summary>
/// H5_Game_PEST.exe (the 5.5 target, docs/rules/SIMULTANEOUS.md §7): every caller of the area check CWorld slot 7
/// (009c2660, itself patched to RET) skips the break — JZ→JMP at 0042e072 (ship built), 008659c5 (Dimension Door),
/// 00865ae3 (Summon Boat), 0098f909 (turns start), 009984f7 (humans' phase of a new day), 00b4b6d4 (wings/boots
/// equipped) — and the battle-end deferred break call at 00af3664 is NOPed. Areas are never computed. Everything
/// else (contact, battles, captures, timers) is the stock engine's behaviour.
/// </summary>
public sealed class PestSimultaneousPolicy : ISimultaneousPolicy
{
    public bool BreakOnIntersection => false;
}

/// <summary>One CEventSimultaneousConflict (ctor 009c2dc0) for a human: auto-approve request and/or the message box.</summary>
public sealed record SimultaneousNotice(int MapPlayer, bool Approve, bool Message);

/// <summary>
/// Host-side state of simultaneous turns (docs/rules/SIMULTANEOUS.md): the CWorld+0x90 flag, the
/// humans' phase (all humans current at once), ended players (state W) and approval flags
/// (CPlayer+0x21d). Deterministic; the session drives it after every command.
/// </summary>
public sealed class SimultaneousTurns
{
    public enum CheckKind { None, Immediate, Deferred }

    readonly H5.Adventure.Engine.Adventure _g;
    readonly Func<int, bool> _isHuman;
    public ISimultaneousPolicy Policy { get; }

    /// <summary>CWorld+0x90: still simultaneous. Never set again once cleared.</summary>
    public bool Active { get; private set; } = true;
    /// <summary>All alive humans are current (0098f7a0 with +0x90 set).</summary>
    public bool InPhase { get; private set; }
    public readonly SortedSet<int> Ended = new();
    public readonly SortedSet<int> Pending = new();
    public readonly List<SimultaneousNotice> Notices = new();
    public SimultaneousConflict? LastConflict { get; private set; }
    CheckKind _requested;

    public SimultaneousTurns(H5.Adventure.Engine.Adventure g, Func<int, bool> isHuman, ISimultaneousPolicy policy)
    {
        _g = g;
        _isHuman = isHuman;
        Policy = policy;
    }

    public IEnumerable<int> Humans => _g.S.Players.Where(p => p.Alive && _isHuman(p.Index)).Select(p => p.Index);

    public bool Playing(int mapPlayer) => InPhase && _isHuman(mapPlayer) && !Ended.Contains(mapPlayer)
        && _g.S.Players.Find(p => p.Index == mapPlayer) is { Alive: true };

    int IndexOf(int mapPlayer) => _g.S.Players.FindIndex(p => p.Index == mapPlayer);

    /// <summary>Records a check requested by a command (the trigger table in SIMULTANEOUS.md §4).</summary>
    public void OnCommand(AdvCommand cmd)
    {
        if (!Active) return;
        var kind = cmd switch
        {
            CastAdventureSpellCmd { Spell: "SPELL_SUMMON_BOAT" } => CheckKind.Immediate,     // 00865af2
            CastAdventureSpellCmd { Spell: "SPELL_DIMENSION_DOOR" } => CheckKind.Deferred,   // 008656e0 case 0x32
            BuildShipCmd => CheckKind.Immediate,                                            // 0042e083
            MoveArtifactCmd { ToBackpack: false, Artifact: "ANGEL_WINGS" or "BOOTS_OF_LEVITATION" } => CheckKind.Immediate, // 00b4b610
            ResolveBattleCmd => CheckKind.Deferred,                                         // 00af35f0 (condition GAP)
            _ => CheckKind.None,
        };
        if (kind > _requested) _requested = kind;
    }

    /// <summary>
    /// Advances the phase: begins it when the turn reaches the humans, runs requested checks and
    /// parks the engine's current player on a human still playing. Returns the human whose end of
    /// turn the session must send to the engine when every human has ended (else -1).
    /// </summary>
    public int Step()
    {
        if (Active && !InPhase && _g.S.Battle == null && !_g.S.Finished && _isHuman(_g.Current.Index))
        {
            InPhase = true;
            Ended.Clear();
            _requested = CheckKind.Immediate;   // 0098f7a0 / 00998060: check when the humans' turns start
        }
        if (_requested != CheckKind.None && _g.S.Battle == null) RunCheck();
        if (!InPhase || _g.S.Battle != null || _g.S.Choice != null) return -1;
        var playing = Humans.Where(Playing).ToList();
        if (playing.Count == 0)
        {
            // Everybody ended: the turn passes on from the last human in the original order.
            InPhase = false;
            Ended.Clear();
            int last = Humans.LastOrDefault(-1);
            if (last < 0) return -1;
            _g.S.CurrentPlayer = IndexOf(last);
            return last;
        }
        if (!playing.Contains(_g.Current.Index)) _g.S.CurrentPlayer = IndexOf(playing[0]);
        return -1;
    }

    /// <summary>A human of the phase ends its turn (00998760: only the end flag; the phase goes on).</summary>
    public void EndTurn(int mapPlayer) => Ended.Add(mapPlayer);

    /// <summary>Lets <paramref name="mapPlayer"/>'s command run on the engine.</summary>
    public void Enter(int mapPlayer) => _g.S.CurrentPlayer = IndexOf(mapPlayer);

    void RunCheck()
    {
        var kind = _requested;
        _requested = CheckKind.None;
        if (!Active || !Policy.BreakOnIntersection) return;
        var humans = Humans.ToHashSet();
        LastConflict = _g.FindSimultaneousConflict(humans);
        if (LastConflict == null) return;
        if (kind == CheckKind.Immediate) { Break(); return; }
        // Deferred (009c25c0): every human must acknowledge first.
        foreach (var h in humans) { Pending.Add(h); Notices.Add(new SimultaneousNotice(h, true, true)); }
    }

    /// <summary>CApproveSimultaneousConflict (004289a0). Returns an error or null.</summary>
    public string? Approve(int mapPlayer)
    {
        if (!Active) return null;
        if (!Pending.Remove(mapPlayer)) return "nothing to approve";
        if (Pending.Count == 0) Break(message: false);
        return null;
    }

    /// <summary>A human left (the computer takes over): drop its flags.</summary>
    public void Left(int mapPlayer)
    {
        Ended.Remove(mapPlayer);
        if (Pending.Remove(mapPlayer) && Pending.Count == 0 && Active) Break(message: false);
    }

    /// <summary>
    /// CWorld slot 8 (0098fa60) + FUN_009c22f0: sequential from now on; during the humans' phase turns
    /// restart with the first human (0098f7a0), the others waiting.
    /// </summary>
    void Break(bool message = true)
    {
        Active = false;
        Pending.Clear();
        if (InPhase)
        {
            InPhase = false;
            Ended.Clear();
            int first = _g.HumansFirstOrder(-1, _isHuman);
            if (first >= 0) _g.S.CurrentPlayer = first;
        }
        foreach (var h in Humans) Notices.Add(new SimultaneousNotice(h, false, message));
    }

    /// <summary>The current player shown in <paramref name="mapPlayer"/>'s view: itself while it plays the phase.</summary>
    public int? ViewCurrentFor(int mapPlayer)
    {
        if (!InPhase) return null;
        if (Playing(mapPlayer)) return IndexOf(mapPlayer);
        var other = Humans.Where(Playing).Select(IndexOf).DefaultIfEmpty(-1).First();
        return other >= 0 ? other : null;
    }
}
