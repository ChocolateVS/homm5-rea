using H5.Adventure.AI;
using H5.Adventure.State;

namespace H5.Adventure.Tests;

/// <summary>Adventure AI part 5 (docs/rules/AUDIT_AI.md): the adventure facts of the combat flee decision.</summary>
[Collection("adv")]
public class AiPart5Tests(Fixture f)
{
    [Fact]
    public void Flee_facts_sum_the_artifacts_and_read_experience_and_hire_price()
    {
        // FUN_00c96ea0: Σ max(004ea2f0, 00ca6410) over backpack and worn slots; hero vf 0x1a4; 00b4ced0 without a town.
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        var arts = g.Core.Artifacts.Keys.OrderBy(k => k).Take(3).ToList();
        h.Artifacts.Clear(); h.Backpack.Clear();
        h.Artifacts.Add(arts[0]); h.Backpack.Add(arts[1]); h.Backpack.Add(arts[2]);
        h.Experience = 12345;
        var st = AdventureAiStats.Load(g.Core);
        long expect = arts.Sum(a => Math.Max(OriginalAdventureAI.ArtifactItemValue(g, st, a), g.Core.Artifacts[a].CostOfGold));
        var info = g.AiRetreatInfoFor(h);
        Assert.Equal(expect, info.ArtifactValue);
        Assert.Equal(12345, info.Experience);
        Assert.Equal(g.HireCost(g.Current), info.HireCost);
    }
}
