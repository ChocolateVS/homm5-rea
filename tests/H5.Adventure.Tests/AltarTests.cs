using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>Sacrificial Altar (exe 00b00e30 / 00b01120) and the experience modifier rule of the sacrifice buildings.</summary>
[Collection("adv")]
public class AltarTests(Fixture f)
{
    [Fact]
    public void AltarTurnsHitPointsTimesThreeAndArtifactGoldIntoModifiedExperience()
    {
        var g = f.NewGame("A2M13");
        var altar = g.Map.Objects.First(o => o.Shared.Type == Engine.Adventure.SacrificialAltar);
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.Perks.Clear();
        h.Skills.Clear();
        h.Artifacts.Clear();
        h.Backpack.Clear();
        h.Specialization = "";
        Array.Fill(h.Army, null);
        h.Army[0] = new Stack("CREATURE_PEASANT", 10);
        h.Army[1] = new Stack("CREATURE_ARCHER", 4);
        int hp = f.Core.Creatures["CREATURE_PEASANT"].Health;
        Assert.Equal(hp * 6 * 3, g.AltarCreatureExperience(h, "CREATURE_PEASANT", 6));
        // Basic Learning: +5 % through hero +0x19c (FUN_00b4bbb0), unlike the Inferno pit.
        h.Skills.Add(new SkillEntry("HERO_SKILL_LEARNING", Mastery.Basic));
        long before = h.Experience;
        var r = g.Apply(new SacrificeCreatureOnAltarCmd(h.Id, altar.Index, 0, 6) { Player = g.Current.Index });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(4, h.Army[0]!.Count);
        int exp = hp * 6 * 3;
        Assert.Equal(before + exp * 5 / 100 + exp, h.Experience);
        Assert.False(g.Apply(new SacrificeCreatureOnAltarCmd(h.Id, altar.Index, 1, 5) { Player = g.Current.Index }).Ok);
        // The whole stack goes; even the last one (no army check in 00b00e30).
        Assert.True(g.Apply(new SacrificeCreatureOnAltarCmd(h.Id, altar.Index, 1, 4) { Player = g.Current.Index }).Ok);
        Assert.True(g.Apply(new SacrificeCreatureOnAltarCmd(h.Id, altar.Index, 0, 4) { Player = g.Current.Index }).Ok);
        Assert.Equal(0, h.Army.StackCount());
        // Artifact: trunc(gold cost × 0.5).
        var art = f.Core.Artifacts.Values.First(a => a.CostOfGold > 1000);
        h.Backpack.Add(art.Id);
        Assert.Equal((int)(art.CostOfGold * 0.5), g.AltarArtifactExperience(art.Id));
        Assert.True(g.Apply(new SacrificeArtifactOnAltarCmd(h.Id, altar.Index, art.Id, true) { Player = g.Current.Index }).Ok);
        Assert.Empty(h.Backpack);
        // Only at an altar.
        var other = g.Map.Objects.First(o => o.Shared.Type != Engine.Adventure.SacrificialAltar);
        h.Army[0] = new Stack("CREATURE_PEASANT", 1);
        Assert.False(g.Apply(new SacrificeCreatureOnAltarCmd(h.Id, other.Index, 0, 1) { Player = g.Current.Index }).Ok);
    }
}
