using System.Text.Json.Nodes;
using H5.AdvNet;
using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Combat;
using H5.Core.Data;
using H5.Core.IO;
using H5.Net;

namespace H5.AdvNet.Tests;

static class Env
{
    static readonly Lazy<(PakFileSystem Fs, GameDatabase Db, GameInstall Install)> Data = new(() =>
    {
        var install = GameInstall.Locate();
        var fs = PakFileSystem.OpenInstall(install);
        return (fs, GameDatabase.Load(fs), install);
    });
    static readonly Lazy<AdvMapSource> MapSource = new(() => new AdvMapSource(Data.Value.Db, Data.Value.Fs, Data.Value.Install));
    static readonly Lazy<GhostData> Ghost = new(() => GhostData.Load(Data.Value.Fs, Data.Value.Db.Stats));

    public static AdvMapSource Maps => MapSource.Value;
    public static GhostData GhostData => Ghost.Value;
    public const string TestMap = "A2M1";

    public static JsonObject Settings(Action<AdvNetSettings>? edit = null)
    {
        var s = new AdvNetSettings { Map = TestMap };
        edit?.Invoke(s);
        return s.ToJson();
    }

    public static RoomPlayer Player(int side, string name, int mapPlayer = -1) =>
        new(side + 1, name, side, true, side == 0, new AdvPlayerOptions { MapPlayer = mapPlayer }.ToJson());
}

/// <summary>Runs a session the way LobbyServer does, without sockets: per-side updates go to replicas.</summary>
sealed class Harness
{
    public AdventureNetSession Session { get; }
    public AdventureNetReplica[] Clients { get; }
    public int Seq;
    public long Now;
    public readonly List<string> Rejections = new();
    public readonly List<GameUpdate>[] Received;

    public Harness(int players, Action<AdvNetSettings>? settings = null, ulong seed = 7)
    {
        Session = new AdventureNetSession(Env.Maps, Env.GhostData, () => Now);
        var roster = Enumerable.Range(0, players).Select(i => Env.Player(i, $"P{i}")).ToList();
        var setups = Session.Start(roster, Env.Settings(settings), seed);
        Clients = setups.Select((s, i) => new AdventureNetReplica(Env.Maps, new GameStarted(i, s))).ToArray();
        Received = Enumerable.Range(0, players).Select(_ => new List<GameUpdate>()).ToArray();
    }

    public bool Send(int side, JsonNode cmd)
    {
        var r = Session.Apply(side, cmd);
        if (r is SessionResult.Rejected rej) { Rejections.Add($"side {side}: {rej.Reason}"); return false; }
        Publish(r, side);
        return true;
    }

    public void Tick(long advanceMs)
    {
        Now += advanceMs;
        if (Session.Tick(Now) is { } r) Publish(r, -1);
    }

    void Publish(SessionResult r, int from)
    {
        var per = (SessionResult.AcceptedPerSide)r;
        Seq++;
        for (int s = 0; s < Clients.Length; s++)
        {
            var u = new GameUpdate(Seq, null, from, per.EventsBySide[s], per.HashBySide[s]);
            Received[s].Add(u);
            Clients[s].Apply(u);
        }
    }
}

/// <summary>A tiny client-side bot that only uses what its replica knows.</summary>
sealed class ClientBot(int seed)
{
    readonly Random _rnd = new(seed);
    readonly GreedyBattleAI _battleAi = new() { MaxCandidates = 8 };
    int _day = -1, _moves;

    public JsonNode? Decide(AdventureNetReplica r)
    {
        if (r.Battle is { } b)
            return b.IsMyTurn ? AdvNetCommands.Battle(_battleAi.Decide(b.Battle, b.MySide) with { Side = b.MySide }) : null;
        if (!r.IsMyTurn) return null;
        var v = r.View;
        if (v.Day != _day) { _day = v.Day; _moves = 0; }
        if (v.Choice != null) return AdvNetCommands.Adv(new AnswerChoiceCmd(true));
        foreach (var h in v.Heroes.Where(h => h.Owner == r.MyPlayer && h.Alive))
            if (h.PendingLevelUps.Count > 0) return AdvNetCommands.Adv(new ChooseLevelUpCmd(h.Id, 0));
        if (_moves < 6)
            foreach (var h in v.Heroes.Where(h => h.Owner == r.MyPlayer && h.Alive && h.MovePoints > 150 && !h.Garrisoned))
            {
                _moves++;
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    var t = new Tile(h.Pos.X + _rnd.Next(-6, 7), h.Pos.Y + _rnd.Next(-6, 7), h.Pos.Floor);
                    var f = r.Map.Map.Floor(t.Floor);
                    if (f == null || !f.InBounds(t.X, t.Y) || f.IsBlocked(t.X, t.Y) || t == h.Pos) continue;
                    if (r.Game.FindPath(h, t) is { Count: > 0 }) return AdvNetCommands.Adv(new MoveHeroCmd(h.Id, t));
                }
            }
        return AdvNetCommands.Adv(new EndTurnCmd());
    }
}
