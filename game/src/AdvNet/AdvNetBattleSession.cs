using Godot;
using H5.AdvNet;
using H5.Combat;
using H5Reborn.Combat;

namespace H5Reborn.AdvNet;

/// <summary>
/// A battle on a networked adventure map, fought on the stock combat screen. The host is
/// authoritative (it also plays neutral / computer / dropped sides with the battle AI); this
/// client keeps the <see cref="BattleReplica"/> in lockstep and, like duel replicas, applies each
/// host update only when the screen takes it for playback (<see cref="AdventureNetReplica.DeferBattleUpdates"/>).
/// </summary>
public sealed class AdvNetBattleSession : CombatSession
{
    readonly AdvNetGame _net;
    readonly BattleReplica _replica;

    public AdvNetBattleSession(AdvNetGame net, BattleReplica replica)
    {
        _net = net;
        _replica = replica;
        IsNetworked = true;
        // The opening is shown from the replica directly; the raw queue only holds later updates.
        Opening = replica.Opening;
        net.Rejected += OnRejected;
    }

    void OnRejected(string reason) => LastRejection = reason;

    public BattleReplica Replica => _replica;
    public override Battle Battle => _replica.Battle;
    public override IReadOnlyCollection<int> LocalSides => new[] { _replica.MySide };
    public override IReadOnlyList<int> Presets => Array.Empty<int>();
    public override ulong Seed => 0;
    public override bool Busy => _net.Busy || _net.Replica.PendingBattleUpdates > 0;
    public override bool Desynced => _replica.Desynced || _net.Desynced;

    /// <summary>The surrender box's gold check (00570cf0): my player's gold in the replicated adventure state.</summary>
    public override long Gold(int side) => side == _replica.MySide
        ? _net.Replica.View.Players.FirstOrDefault(p => p.Index == _net.Replica.MyPlayer)?.Purse[H5.Adventure.State.Purse.Gold] ?? 0 : 0;

    public override void Submit(BattleCommand cmd)
    {
        if (Busy || Ended) return;
        LastRejection = null;
        _net.Send(cmd with { Side = _replica.MySide });
    }

    public override bool TryDequeue(out IReadOnlyList<BattleEvent> events)
    {
        if (_net.Replica.TakeBattleUpdate(out var b, out events) && ReferenceEquals(b, _replica))
        {
            if (_replica.Ended && EndResult == null)
            {
                var st = _replica.Battle.State;
                EndResult = (_replica.Winner ?? st.Winner, string.IsNullOrEmpty(st.EndReason) ? "battle over" : st.EndReason);
            }
            return true;
        }
        events = Array.Empty<BattleEvent>();
        return false;
    }

    public override void Leave() => _net.Rejected -= OnRejected;
}

/// <summary>
/// Opens the combat screen for a networked adventure battle and resumes the adventure screen
/// afterwards. The adventure screen stays alive (detached) while the combat screen owns the scene,
/// exactly like local adventure battles; the host has already applied the battle's result to the
/// view by the time the player leaves the results screen.
/// </summary>
public static class NetBattleLauncher
{
    /// <summary>Called by the adventure screen hook when <see cref="AdvNetGame.BattleStarted"/> fires.</summary>
    public static void Start(AdvNetGame net, BattleReplica replica, Action<Node>? onReturn = null)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var adventure = tree.CurrentScene;
        var placeholder = new Node { Name = "AdventurePause" };
        tree.Root.AddChild(placeholder);
        if (adventure != null) tree.Root.RemoveChild(adventure);
        tree.CurrentScene = placeholder;

        var session = new AdvNetBattleSession(net, replica);
        CombatScreen.ReturnHandler = _ =>
        {
            tree.CurrentScene?.QueueFree();
            if (adventure != null)
            {
                tree.Root.AddChild(adventure);
                tree.CurrentScene = adventure;
                onReturn?.Invoke(adventure);
            }
        };
        GD.Print($"[advnet] battle started: this player is battle side {replica.MySide}");
        CombatScreen.Open(session);
    }
}
