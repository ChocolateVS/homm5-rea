using System.Runtime.CompilerServices;
using Godot;
using H5Reborn.AdvNet;

namespace H5Reborn.Adventure;

/// <summary>
/// Connects the standard multiplayer and hot-seat setup screens (game/src/AdvNet) to the adventure
/// screen: a network game opens on a remote <see cref="AdventureSession"/> (the host simulates, this
/// client shows its redacted view and sends commands); a hot-seat game starts a local session with
/// several human players (stock, user or generated map via <see cref="AdvNetGame.Maps"/>).
/// </summary>
static class AdvNetHooks
{
    [ModuleInitializer]
    internal static void Init()
    {
        AdvNetScreens.OpenNetworkGame = net =>
        {
            var session = AdventureSession.FromRemote(new RemoteAdventure(net));
            void StartBattle(H5.AdvNet.BattleReplica b) => Callable.From(() => NetBattleLauncher.Start(net, b, node =>
            {
                if (node is not AdventureScreen screen) return;
                foreach (var h in screen.Session.S.Heroes) screen.World.SyncHero(h);
                screen.World.RefreshShroud();
                screen.Hud.Refresh();
            })).CallDeferred();
            net.BattleStarted += StartBattle;
            var screen = AdventureScreen.Open(session);
            net.SaveNotice += n => Callable.From(() => Save.SaveManager.ShowNetSaveNotice(screen, n)).CallDeferred();
            if (net.Replica.Settings.GhostMode) screen.AddChild(new GhostController(net, screen));
            // A battle already running when the game opens (e.g. after a reconnect) starts at once.
            if (net.Replica.Battle is { } running) StartBattle(running);
            return true;
        };
        AdvNetScreens.OpenHotSeatGame = plan =>
        {
            var lm = AdvNetGame.Maps.Load(plan.Settings);
            var seed = BitConverter.ToUInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
            var session = AdventureSession.StartLocal(lm, plan.Humans.Keys, seed, plan.Settings.Difficulty, plan.Settings.Rmg);
            AdventureScreen.Open(session);
            return true;
        };
    }
}
