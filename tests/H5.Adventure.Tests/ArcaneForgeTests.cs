using H5.Adventure.Engine;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>Academy Arcane Forge and creature artifacts per H5_Game.exe (docs/rules/AUDIT_ADVENTURE.md, Arcane Forge rows).</summary>
[Collection("adv")]
public class ArcaneForgeTests(Fixture f)
{
    static (Engine.Adventure G, TownState T, HeroState H) AcademyWithWizard(Fixture f, Mastery artificer = Mastery.Expert, int knowledge = 20)
    {
        var g = f.NewGame();
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        var t = g.S.Towns.First(x => x.Owner == h.Owner);
        t.Type = "TOWN_ACADEMY";
        t.Built[Engine.Adventure.ArcaneForgeBuilding] = 1;
        h.Town = "TOWN_ACADEMY";
        h.Knowledge = knowledge;
        h.Skills.RemoveAll(s => s.SkillId == Engine.Adventure.ArtificerSkill);
        h.Skills.Add(new SkillEntry(Engine.Adventure.ArtificerSkill, artificer));
        h.Pos = g.EntranceOf(t);
        h.InTown = t.Object;
        Array.Fill(h.Army, null);
        h.Army[0] = new Stack("CREATURE_GREMLIN", 20);
        h.Army[1] = new Stack("CREATURE_PEASANT", 10);
        h.Army[2] = new Stack("CREATURE_STONE_GARGOYLE", 5);
        g.Current.Purse = new Purse(new Resources(100, 100, 100, 100, 100, 100, 1000));
        return (g, t, h);
    }

    // FUN_00c0a2f0 (jump table 00c0a40c).
    [Fact]
    public void EffectValuesFollowTheKnowledgeFormulas()
    {
        Assert.Equal(5, Engine.Adventure.MicroEffectLevel(MicroEffect.Health, 20));
        Assert.Equal(6, Engine.Adventure.MicroEffectLevel(MicroEffect.Piercing, 20));
        Assert.Equal(6, Engine.Adventure.MicroEffectLevel(MicroEffect.Defence, 20));
        Assert.Equal(2, Engine.Adventure.MicroEffectLevel(MicroEffect.ArmorCrushing, 20));
        Assert.Equal(2, Engine.Adventure.MicroEffectLevel(MicroEffect.Speed, 20));
        Assert.Equal(3, Engine.Adventure.MicroEffectLevel(MicroEffect.Luck, 20));
        Assert.Equal(3, Engine.Adventure.MicroEffectLevel(MicroEffect.Morale, 20));
        Assert.Equal(20, Engine.Adventure.MicroEffectLevel(MicroEffect.Haste, 20));
        Assert.Equal(1, Engine.Adventure.MicroEffectLevel(MicroEffect.Haste, 0));   // Knowledge below 1 counts as 1
        // Magic protection with the exe's table sine (values checked against the 00e11de8 table for Knowledge 1..999):
        // the table interpolates just below 1 at π/2, so the maximum is 84, not 85.
        Assert.Equal(6, Engine.Adventure.MicroEffectLevel(MicroEffect.MagicProtection, 1));
        Assert.Equal(47, Engine.Adventure.MicroEffectLevel(MicroEffect.MagicProtection, 10));
        Assert.Equal(66, Engine.Adventure.MicroEffectLevel(MicroEffect.MagicProtection, 20));
        Assert.Equal(84, Engine.Adventure.MicroEffectLevel(MicroEffect.MagicProtection, 50));
        Assert.Equal(84, Engine.Adventure.MicroEffectLevel(MicroEffect.MagicProtection, 200));
        Assert.Equal(-1, Engine.Adventure.MicroEffectLevel(MicroEffect.None, 20));
    }

    // FUN_00c0a700: no Wounding, at most min(mastery, 3) effects, no repeats.
    [Fact]
    public void EffectChoiceFollowsTheArtificerMastery()
    {
        var a = new MicroArtifact();
        Assert.False(Engine.Adventure.CanAddMicroEffect(a, MicroEffect.Wounding, 3));
        Assert.False(Engine.Adventure.CanAddMicroEffect(a, MicroEffect.Piercing, 0));
        Assert.True(Engine.Adventure.CanAddMicroEffect(a, MicroEffect.Piercing, 1));
        a.Effects.Add(new MicroArtifactEntry(MicroEffect.Piercing, 6));
        Assert.False(Engine.Adventure.CanAddMicroEffect(a, MicroEffect.Defence, 1));
        Assert.True(Engine.Adventure.CanAddMicroEffect(a, MicroEffect.Defence, 2));
        Assert.False(Engine.Adventure.CanAddMicroEffect(a, MicroEffect.Piercing, 3));
        a.Effects.Add(new MicroArtifactEntry(MicroEffect.Defence, 6));
        a.Effects.Add(new MicroArtifactEntry(MicroEffect.Luck, 3));
        Assert.False(Engine.Adventure.CanAddMicroEffect(a, MicroEffect.Haste, 4));
    }

    // FUN_00c0a630 / FUN_0063c180: effect i costs table price × (i + 1); dismantling refunds the price (FUN_00644600).
    [Fact]
    public void CraftingPaysEachEffectTimesItsPlaceAndDismantlingRefundsIt()
    {
        var (g, _, h) = AcademyWithWizard(f);
        var before = (int[])g.Current.Purse.R.Clone();
        var r = g.Apply(new CreateMicroArtifactCmd(h.Id, new[] { MicroEffect.Piercing, MicroEffect.Health }) { Player = h.Owner });
        Assert.True(r.Ok, r.Error);
        // Piercing 5 ore + 5 crystal (× 1), Health 5 wood + 5 gems (× 2).
        var paid = before.Zip(g.Current.Purse.R, (x, y) => x - y).ToArray();
        Assert.Equal(new[] { 10, 5, 0, 5, 0, 10, 0 }, paid);
        var a = h.MicroBag.SelectMany(x => x).Single(x => x != null)!;
        Assert.Equal(new[] { (MicroEffect.Piercing, 6), (MicroEffect.Health, 5) }, a.Effects.Select(e => (e.Effect, e.Level)).ToArray());
        Assert.False(a.HalfCost);
        int row = h.MicroBag.FindIndex(x => x.Contains(a)), idx = h.MicroBag[row].IndexOf(a);
        Assert.True(g.Apply(new DismantleMicroArtifactCmd(h.Id, row, idx) { Player = h.Owner }).Ok);
        Assert.Equal(before, g.Current.Purse.R);
        Assert.All(h.MicroBag, x => Assert.Equal(5, x.Count));
        Assert.All(h.MicroBag.SelectMany(x => x), x => Assert.Null(x));
        // Refused choices: Wounding, more effects than the mastery, no effect.
        Assert.False(g.Apply(new CreateMicroArtifactCmd(h.Id, new[] { MicroEffect.Wounding }) { Player = h.Owner }).Ok);
        Assert.False(g.Apply(new CreateMicroArtifactCmd(h.Id, Array.Empty<MicroEffect>()) { Player = h.Owner }).Ok);
    }

    // FUN_00c0a8f0 / FUN_00c0a630: an Ultimate Artificier's artifact is half price, rounded up per resource.
    [Fact]
    public void UltimateArtificerHalvesThePrice()
    {
        var (g, _, h) = AcademyWithWizard(f, Mastery.Ultimate);
        var a = Engine.Adventure.BuildMicroArtifact(h, new[] { MicroEffect.Luck, MicroEffect.Speed, MicroEffect.Haste })!;
        Assert.True(a.HalfCost);
        // Luck 5 wood 5 crystal ×1 → 3/3; Speed 5 ore 5 mercury ×2 → 5/5; Haste 5 mercury 5 sulfur ×3 → 8/8.
        Assert.Equal(new Resources(3, 5, 13, 3, 8, 0, 0), g.MicroArtifactCost(a));
    }

    // 004283b0 / 00428550 / 0042a9e0: only Academy creatures (FUN_004ba910 == 5), the hero needs Artificier.
    [Fact]
    public void EquipUnequipAndSwapBetweenAcademyStacks()
    {
        var (g, _, h) = AcademyWithWizard(f);
        Assert.True(g.Apply(new CreateMicroArtifactCmd(h.Id, new[] { MicroEffect.Defence }) { Player = h.Owner }).Ok);
        int row = h.MicroBag.FindIndex(x => x.Any(y => y != null)), idx = h.MicroBag[row].FindIndex(y => y != null);
        Assert.False(g.Apply(new EquipMicroArtifactCmd(h.Id, 1, row, idx) { Player = h.Owner }).Ok);   // peasants
        Assert.True(g.Apply(new EquipMicroArtifactCmd(h.Id, 0, row, idx) { Player = h.Owner }).Ok);
        Assert.Equal(6, h.Army[0]!.Artifact!.Bonus(MicroEffect.Defence));
        Assert.All(h.MicroBag.SelectMany(x => x), x => Assert.Null(x));
        Assert.True(g.Apply(new MoveMicroArtifactBetweenCreaturesCmd(h.Id, 0, 2) { Player = h.Owner }).Ok);
        Assert.Null(h.Army[0]!.Artifact);
        Assert.NotNull(h.Army[2]!.Artifact);
        Assert.True(g.Apply(new UnequipMicroArtifactCmd(h.Id, 2, 1, 3) { Player = h.Owner }).Ok);
        Assert.NotNull(h.MicroBag[1][3]);
        Assert.Null(h.Army[2]!.Artifact);
        h.Skills.RemoveAll(s => s.SkillId == Engine.Adventure.ArtificerSkill);
        Assert.False(g.Apply(new EquipMicroArtifactCmd(h.Id, 0, 1, 3) { Player = h.Owner }).Ok);
    }

    // FUN_00cd2310 / FUN_00b4f200 / FUN_00c0a770: the forge raises the values to the hero's Knowledge, never lowers them.
    [Fact]
    public void ForgeUpgradesArtifactsToTheHerosKnowledge()
    {
        var (g, t, h) = AcademyWithWizard(f, knowledge: 4);
        Assert.True(g.Apply(new CreateMicroArtifactCmd(h.Id, new[] { MicroEffect.Haste }) { Player = h.Owner }).Ok);
        h.Army[0]!.Artifact = new MicroArtifact { Effects = { new MicroArtifactEntry(MicroEffect.Piercing, 2) } };
        h.Knowledge = 12;
        Assert.True(g.UpgradeMicroArtifacts(h, t));
        Assert.Equal(12, h.MicroBag.SelectMany(x => x).Single(x => x != null)!.Bonus(MicroEffect.Haste));
        Assert.Equal(4, h.Army[0]!.Artifact!.Bonus(MicroEffect.Piercing));
        h.Knowledge = 1;
        Assert.False(g.UpgradeMicroArtifacts(h, t));
        Assert.Equal(4, h.Army[0]!.Artifact!.Bonus(MicroEffect.Piercing));
    }

    // FUN_0063c560: shell by effect count, triangular prefix index of the first two effects.
    [Fact]
    public void NamePartsFollowTheForgeScreen()
    {
        var one = new MicroArtifact { Effects = { new(MicroEffect.Luck, 1) } };
        Assert.Equal((1, -1, MicroEffect.Luck), Engine.Adventure.MicroArtifactNameParts(one));
        var two = new MicroArtifact { Effects = { new(MicroEffect.Piercing, 1), new(MicroEffect.Wounding, 1) } };
        Assert.Equal((2, 0, MicroEffect.None), Engine.Adventure.MicroArtifactNameParts(two));   // (0,1) → Prefix_1
        var three = new MicroArtifact { Effects = { new(MicroEffect.ArmorCrushing, 1), new(MicroEffect.Piercing, 1), new(MicroEffect.Haste, 1) } };
        Assert.Equal((3, 9, MicroEffect.Haste), Engine.Adventure.MicroArtifactNameParts(three));   // (1,2) → Prefix_10
    }
}
