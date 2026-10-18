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
            net.BattleStarted += _ =>
                GD.PushWarning("[H5] network battle started: interactive network battles are not wired into the combat screen yet");
            AdventureScreen.Open(session);
            return true;
        };
        AdvNetScreens.OpenHotSeatGame = plan =>
        {
            var lm = AdvNetGame.Maps.Load(plan.Settings);
            var seed = BitConverter.ToUInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
            var session = AdventureSession.StartLocal(lm, plan.Humans.Keys, seed, plan.Settings.Difficulty);
            AdventureScreen.Open(session);
            return true;
        };
    }
}
