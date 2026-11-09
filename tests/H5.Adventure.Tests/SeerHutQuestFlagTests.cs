using H5.Adventure.Engine;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>
/// Seer Hut AllowMultipleActivations / AllowMultipleCompletions (common objective 00afbc20 +0x3c / +0x3d, state listener
/// 00afb410, bannable condition 00afb060 / 00afb170) and the OBTAIN_ARTIFACT location (00b45210 / 00b422c0).
/// </summary>
[Collection("adv")]
public class SeerHutQuestFlagTests(Fixture f)
{
    sealed record Setup(Engine.Adventure G, Map.MapObject Hut, HeroState A, HeroState B, string Artifact);

    Setup Make()
    {
        var g = f.NewGame("A2S4");
        var hut = g.Map.Objects.First(o => o.Kind == Map.ObjectKind.SeerHut && o.Quest?.Objective.Kind == "OBJECTIVE_KIND_OBTAIN_ARTIFACT");
        var art = g.QuestRequirement(hut.Quest!).Id!;
        var at = hut.ActiveTiles.FirstOrDefault(hut.Pos);
        var a = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        var other = g.S.Players.First(x => x.Index != a.Owner);
        var def = f.Db.Core.Heroes.Values.First(d => d.Id != a.DefId && g.S.Heroes.All(x => x.DefId != d.Id));
        var b = g.ScriptDeployHero(def, other.Index, at);
        a.Pos = b.Pos = at;
        a.InTown = b.InTown = -1;
        a.Artifacts.Add(art);
        b.Artifacts.Add(art);
        return new Setup(g, hut, a, b, art);
    }

    static bool As(Engine.Adventure g, HeroState h, AdvCommand c)
    {
        g.S.CurrentPlayer = g.S.Players.FindIndex(p => p.Index == h.Owner);
        return g.Apply(c with { Player = h.Owner }).Ok;
    }

    [Fact]
    public void EveryPlayerCompletesOnceWhenMultipleCompletionsAreAllowed()
    {
        var (g, hut, a, b, _) = Make();
        var q = hut.Quest!;
        bool ama = q.AllowMultipleActivations, amc = q.AllowMultipleCompletions;
        try
        {
            q.AllowMultipleActivations = q.AllowMultipleCompletions = true;   // as every shipped Seer Hut
            Assert.True(As(g, a, new AcceptSeerQuestCmd(a.Id, hut.Index)));
            Assert.True(As(g, b, new AcceptSeerQuestCmd(b.Id, hut.Index)));
            long expA = a.Experience;
            Assert.True(As(g, a, new CompleteSeerQuestCmd(a.Id, hut.Index)));
            Assert.False(As(g, a, new CompleteSeerQuestCmd(a.Id, hut.Index)));
            Assert.False(As(g, a, new AcceptSeerQuestCmd(a.Id, hut.Index)));
            // The second player still completes his own (was: one completion per hut).
            Assert.True(g.SeerQuestMet(b, hut.Index));
            Assert.False(g.SeerQuestFailed(b.Owner, hut.Index));
            Assert.True(As(g, b, new CompleteSeerQuestCmd(b.Id, hut.Index)));
            Assert.Equal(-1, g.S.Obj(hut.Index).QuestBanPlayer);
        }
        finally { q.AllowMultipleActivations = ama; q.AllowMultipleCompletions = amc; }
    }

    [Fact]
    public void ACompletionBansTheOthersWithoutMultipleCompletions()
    {
        var (g, hut, a, b, art) = Make();
        var q = hut.Quest!;
        bool ama = q.AllowMultipleActivations, amc = q.AllowMultipleCompletions;
        try
        {
            q.AllowMultipleActivations = true;
            q.AllowMultipleCompletions = false;
            Assert.True(As(g, a, new AcceptSeerQuestCmd(a.Id, hut.Index)));
            Assert.True(As(g, b, new AcceptSeerQuestCmd(b.Id, hut.Index)));
            Assert.True(As(g, a, new CompleteSeerQuestCmd(a.Id, hut.Index)));
            Assert.Equal(a.Owner, g.S.Obj(hut.Index).QuestBanPlayer);
            // B's active quest is failed (state 4): the box counts it as fulfilled (004f25f0), Complete gives nothing.
            Assert.True(g.SeerQuestFailed(b.Owner, hut.Index));
            b.Artifacts.Remove(art);
            Assert.True(g.SeerQuestMet(b, hut.Index));
            var before = (b.Experience, b.Artifacts.Count, g.S.Players.First(p => p.Index == b.Owner).Purse[Purse.Gold]);
            Assert.True(As(g, b, new CompleteSeerQuestCmd(b.Id, hut.Index)));
            Assert.Equal(before, (b.Experience, b.Artifacts.Count, g.S.Players.First(p => p.Index == b.Owner).Purse[Purse.Gold]));
            Assert.Contains(b.Owner, g.S.Obj(hut.Index).QuestDoneBy);
        }
        finally { q.AllowMultipleActivations = ama; q.AllowMultipleCompletions = amc; }
    }

    [Fact]
    public void TheLastTakerWinsWithoutMultipleActivations()
    {
        var (g, hut, a, b, _) = Make();
        var q = hut.Quest!;
        bool ama = q.AllowMultipleActivations, amc = q.AllowMultipleCompletions;
        try
        {
            q.AllowMultipleActivations = false;
            q.AllowMultipleCompletions = true;
            Assert.True(As(g, a, new AcceptSeerQuestCmd(a.Id, hut.Index)));
            Assert.Equal(a.Owner, g.S.Obj(hut.Index).QuestBanPlayer);
            // B's take records B (00afb410 overwrites +0x16c) and fails A's active quest.
            Assert.True(As(g, b, new AcceptSeerQuestCmd(b.Id, hut.Index)));
            Assert.Equal(b.Owner, g.S.Obj(hut.Index).QuestBanPlayer);
            Assert.True(g.SeerQuestFailed(a.Owner, hut.Index));
            Assert.False(g.SeerQuestFailed(b.Owner, hut.Index));
        }
        finally { q.AllowMultipleActivations = ama; q.AllowMultipleCompletions = amc; }
    }

    [Fact]
    public void ArtifactLocationNamesARegion()
    {
        var (g, hut, a, _, art) = Make();
        var q = hut.Quest!;
        var saved = q.Objective.Parameters.ToList();
        try
        {
            q.Objective.Parameters = new List<string> { saved[0], "test_region" };
            var t = a.Pos;
            // A location naming no region is ignored (00b422c0: no region object → met).
            Assert.True(g.SeerQuestMet(a, hut.Index));
            g.Map.Regions["test_region"] = new Map.MapRect(t.Floor, t.X + 5, t.Y + 5, t.X + 6, t.Y + 6);
            Assert.False(g.SeerQuestMet(a, hut.Index));
            g.Map.Regions["test_region"] = new Map.MapRect(t.Floor, t.X, t.Y, t.X, t.Y);
            Assert.True(g.SeerQuestMet(a, hut.Index));
            a.Artifacts.Remove(art);
            Assert.False(g.SeerQuestMet(a, hut.Index));
        }
        finally { q.Objective.Parameters = saved; g.Map.Regions.Remove("test_region"); }
    }

    [Fact]
    public void RegionsAreReadFromTheMap()
    {
        var g = f.NewGame("A2M4");
        Assert.True(g.Map.Regions.ContainsKey("haven"));
        Assert.True(g.Map.Regions.ContainsKey("necro"));
    }
}
