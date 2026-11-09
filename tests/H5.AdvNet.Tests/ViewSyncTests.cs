using System.Text;
using System.Text.Json.Nodes;
using H5.Net;
using Xunit;

namespace H5.AdvNet.Tests;

public class ViewSyncTests
{
    [Fact]
    public void SegmentPatchesRebuildTheSenderDocument()
    {
        var rnd = new Random(5);
        var sender = new ViewSender();
        var receiver = new ViewReceiver();
        var keys = Enumerable.Range(0, 40).Select(i => "F" + (i % 7) + "\u001f" + i).ToList();
        int resets = 0, incremental = 0;
        for (int round = 0; round < 400; round++)
        {
            // Mostly small edits of the previous list; sometimes inserts, deletes or a reorder.
            if (rnd.Next(10) == 0) keys = keys.OrderBy(_ => rnd.Next()).ToList();
            if (rnd.Next(3) == 0) keys.Insert(rnd.Next(keys.Count + 1), "F" + rnd.Next(7) + "\u001fn" + round);
            if (rnd.Next(3) == 0 && keys.Count > 1) keys.RemoveAt(rnd.Next(keys.Count));
            var segs = keys.Select(k => new ViewSegment(k, Encoding.UTF8.GetBytes(rnd.Next(8) == 0 ? "v" + rnd.Next(1000) : k.Length.ToString()))).ToList();
            var patch = sender.Update(segs);
            if (patch.ContainsKey("reset")) resets++; else incremental++;
            var wire = JsonNode.Parse(patch.ToJsonString())!.AsObject();
            receiver.Apply(wire);
            Assert.Equal(sender.Hash, receiver.Hash);
            Assert.Equal(string.Concat(segs.Select(s => Encoding.UTF8.GetString(s.Bytes.Span))), Encoding.UTF8.GetString(receiver.Document()));
        }
        Assert.True(resets > 0 && incremental > 0);
    }

    [Fact]
    public void ReplicaViewMatchesTheFullFilterAfterEveryUpdate()
    {
        var h = new Harness(2);
        var bots = new[] { new ClientBot(1), new ClientBot(2) };
        for (int step = 0; step < 120 && h.Session.Game.S.Day < 3; step++)
        {
            bool acted = false;
            for (int s = 0; s < 2 && !acted; s++)
                if (bots[s].Decide(h.Clients[s]) is { } cmd) { h.Send(s, cmd); acted = true; }
            if (!acted) h.Tick(1000);
            foreach (var c in h.Clients)
            {
                Assert.False(c.Desynced);
                Assert.Equal(JsonDiff.CanonicalHash(h.Session.ViewFor(c.MyPlayer)), JsonDiff.CanonicalHash(JsonNode.Parse(c.View.Serialize())));
            }
        }
        // Updates carry segments, not whole views.
        var setupBytes = h.Setups[0]["view"]!.ToJsonString().Length;
        Assert.All(h.Received[0], u => Assert.True(u.Events["view"]!["reset"] == null || u.Events["view"]!.ToJsonString().Length <= setupBytes));
    }

    [Fact]
    public void ATamperedSegmentIsDetected()
    {
        var h = new Harness(2);
        var bots = new[] { new ClientBot(1), new ClientBot(2) };
        for (int step = 0; step < 40; step++)
        {
            bool acted = false;
            for (int s = 0; s < 2 && !acted; s++)
                if (bots[s].Decide(h.Clients[s]) is { } cmd) { h.Send(s, cmd); acted = true; }
            if (!acted) h.Tick(1000);
        }
        var replay = new AdventureNetReplica(Env.Maps, new GameStarted(0, h.Setups[0]));
        bool tampered = false;
        foreach (var u in h.Received[0])
        {
            var events = u.Events.DeepClone();
            if (!tampered && events["view"]!["set"] is JsonArray set && set.Count > 0)
            {
                var text = (string)set[0]![1]!;
                int i = text.IndexOfAny("0123456789".ToCharArray());
                if (i >= 0)
                {
                    set[0]![1] = text[..i] + (text[i] == '9' ? '8' : (char)(text[i] + 1)) + text[(i + 1)..];
                    tampered = true;
                }
            }
            replay.Apply(u with { Events = events });
            if (tampered) break;
        }
        Assert.True(tampered);
        Assert.True(replay.Desynced);
    }
}
