using System.Text.Json.Nodes;
using H5.Adventure.Engine;
using H5.Net;
using H5.Save;

namespace H5.AdvNet.Tests;

/// <summary>Host-side saving and reloading of network adventure games (CMultiplayerHostLoadLogic, CAnnounceSaveGameOrderedCmd).</summary>
[Collection("netsave")]
public class NetSaveTests
{
    static JsonObject Save(string name) => new() { ["$t"] = "save", ["name"] = name };

    /// <summary>Plays end-turns until the day changes (every side ends; computers move on the host).</summary>
    static void PassTurn(Harness h)
    {
        int day = h.Session.Game.S.Day;
        for (int guard = 0; guard < 40 && h.Session.Game.S.Day == day; guard++)
            for (int s = 0; s < h.Clients.Length; s++)
                if (h.Clients[s].IsMyTurn) h.Send(s, AdvNetCommands.Adv(new EndTurnCmd()));
    }

    static List<SaveArchive> Capture(out Func<SaveArchive, string?> sink)
    {
        var list = new List<SaveArchive>();
        sink = a => { list.Add(a); return null; };
        return list;
    }

    [Fact]
    public void SaveIsOrderedThenWrittenWhenTheTurnPasses()
    {
        var saves = Capture(out var sink);
        AdventureNetSession.SaveSink = sink;
        try
        {
            var h = new Harness(2);
            Assert.True(h.Send(1, Save("Net 1")), string.Join("; ", h.Rejections));
            // Everybody hears about the order at once; nothing is written yet.
            Assert.All(h.Received, r => Assert.Contains("\"ordered\"", r[^1].Events.ToJsonString()));
            Assert.Empty(saves);
            Assert.False(h.Send(0, Save("Net 2"))); // one order at a time
            PassTurn(h);
            var archive = Assert.Single(saves);
            Assert.Equal(SaveKind.Multiplayer, archive.Header.Kind);
            Assert.Equal("Net 1", archive.Header.Name);
            Assert.Equal(h.Session.Game.S.ComputeHash(), archive.Header.StateHash);
            Assert.Contains(h.Received[0], u => u.Events.ToJsonString().Contains("\"saved\""));
            var net = NetSave.FromArchive(SaveArchive.FromBytes(archive.ToBytes()));
            Assert.Equal(new[] { "P0", "P1" }, net.Humans.Select(x => x.Name));
        }
        finally { AdventureNetSession.SaveSink = null; }
    }

    [Fact]
    public void WithoutAStoreTheOrderIsRefused()
    {
        AdventureNetSession.SaveSink = null;
        var h = new Harness(2);
        Assert.False(h.Send(0, Save("x")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReloadResumesTheGameWithPlayersInTheirSavedSlots(bool simultaneous)
    {
        var saves = Capture(out var sink);
        AdventureNetSession.SaveSink = sink;
        try
        {
            var h = new Harness(2, s => s.SimultaneousTurns = simultaneous);
            Assert.True(h.Send(0, Save("Resume")));
            PassTurn(h);
            var archive = Assert.Single(saves);
            var net = NetSave.FromArchive(archive);
            int p0 = h.Session.MapPlayerOf(0), p1 = h.Session.MapPlayerOf(1);

            var id = NetSaveLoads.Register(net);
            var settings = NetSaveLoads.RoomSettings(net, id);
            Assert.Equal(2, AdvNetSettings.From(settings).LoadSlots!.Count);
            var session = new AdventureNetSession(Env.Maps, Env.GhostData, () => 0);
            // The players rejoin in the other order without choosing a slot: each gets the slot saved under its name.
            var roster = new[] { Env.Player(0, "P1"), Env.Player(1, "P0") };
            var setups = session.Start(roster, settings, 99);
            Assert.Equal(p1, session.MapPlayerOf(0));
            Assert.Equal(p0, session.MapPlayerOf(1));
            Assert.Equal(archive.Header.StateHash, session.Game.S.ComputeHash());
            Assert.Equal(simultaneous, session.Simultaneous != null);
            if (simultaneous) Assert.Equal(net.Simul!["active"]!.GetValue<bool>(), session.Simultaneous!.Active);
            // The clients' replicas build from the setups.
            var replicas = setups.Select((s, i) => new AdventureNetReplica(Env.Maps, new GameStarted(i, s))).ToArray();
            Assert.All(replicas, r => Assert.False(r.Desynced));

            // A missing player: the start is refused.
            var lone = new AdventureNetSession(Env.Maps, Env.GhostData, () => 0);
            Assert.Throws<InvalidOperationException>(() => lone.Start(new[] { Env.Player(0, "P0") }, settings, 1));
            NetSaveLoads.Forget(id);
        }
        finally { AdventureNetSession.SaveSink = null; }
    }
}

[CollectionDefinition("netsave", DisableParallelization = true)]
public sealed class NetSaveCollection { }
