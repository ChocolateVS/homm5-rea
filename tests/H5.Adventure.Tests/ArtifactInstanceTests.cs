using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>
/// The hero's book rebuild FUN_00b53b10 on the adventure side and the per-object artifact data of H5_Game.exe's artifact
/// set-up FUN_00ca7e40 (WAND_OF_X case 3 with its charge object, SCROLL_OF_SPELL_X case 0x34, ARTIFACT_RUNE_OF_FLAME
/// case 0x56), recharge FUN_00b4db10 (from the guild visit FUN_00cced80).
/// </summary>
[Collection("adv")]
public class ArtifactInstanceTests(Fixture f)
{
    Engine.Adventure Game(params string[] spells)
    {
        var map = f.Loader.Load(MapCatalog.Find(f.Fs, f.Install, "A2M1")!);
        map.SpellIds.Clear();
        map.SpellIds.AddRange(spells);
        map.Objects.RemoveAll(o => o.Kind == ObjectKind.Shrine);
        return Engine.Adventure.NewGame(f.Db, map, GameSetup.AllPlayers(map, 0, 7)).Game;
    }

    HeroState Hero(Engine.Adventure g)
    {
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.Spells.Clear();
        h.SpellSources.Clear();
        h.Perks.Clear();
        h.Artifacts.Clear();
        h.Backpack.Clear();
        h.ArtifactInstances.Clear();
        h.ArtifactsOn = new();
        h.Class = "HERO_CLASS_WIZARD";
        return h;
    }

    [Fact]
    public void PuttingOnATomeRebuildsTheBookWithItsMassVersions()
    {
        // 00b5a410 → 00b541f0 then 00b53b10: the tome's Bless entry has an artifact, passes vf 0x158 (00b526f0) into
        // 00b4bb40 → SPELL_MASS_BLESS with its Master perk as the source (vf 0xec). Off again (00b53730 → 00b53b10): the
        // mass entry has no artifact and its base is neither known nor in the book, so the perk leaves its sources and it goes.
        var g = Game("SPELL_BLESS");
        var h = Hero(g);
        h.Perks.Add("HERO_SKILL_MASTER_OF_BLESSING");
        h.Artifacts.Add("ARTIFACT_TOME_OF_LIGHT_MAGIC");
        g.SyncArtifactInstances(h);
        Assert.Equal(new[] { "SPELL_MASS_BLESS", "SPELL_BLESS" }, g.BookSpells(h));
        Assert.Equal(new[] { "HERO_SKILL_MASTER_OF_BLESSING" }, h.SpellSources["SPELL_MASS_BLESS"]);
        Assert.False(Engine.Adventure.KnowsSpell(h, "SPELL_BLESS"));
        h.Artifacts.Clear();
        g.SyncArtifactInstances(h);
        Assert.Empty(g.BookSpells(h));
    }

    [Fact]
    public void AMassVersionOfAKnownBaseStaysWhenTheArtifactGoes()
    {
        // 00b53b10: a version without artifact stays while its base (00a34110) is in the known list (+0x204).
        var g = Game("SPELL_BLESS");
        var h = Hero(g);
        h.Perks.Add("HERO_SKILL_MASTER_OF_BLESSING");
        g.ScriptLearnSpell(h, "SPELL_BLESS");
        h.Artifacts.Add("ARTIFACT_TOME_OF_LIGHT_MAGIC");
        g.SyncArtifactInstances(h);
        h.Artifacts.Clear();
        g.SyncArtifactInstances(h);
        Assert.Equal(new[] { "SPELL_BLESS", "SPELL_MASS_BLESS" }, g.BookSpells(h));
    }

    [Fact]
    public void WandsKeepTheMapSpellAndGetTheirTableCharges()
    {
        // 00ca7e40 case 3: spell from the map (00ca8300) or the table 00e93128; charge object full with FUN_00ca6360's
        // count (table 00e93150: Fireball 3, Disrupting Ray 10), 6 for a spell outside the table.
        var g = Game();
        var w = g.NewArtifactInstance(Engine.Adventure.WandArtifact, worn: true, "SPELL_FIREBALL");
        Assert.Equal(("SPELL_FIREBALL", 3, 3), (w.Spell, w.Charges, w.MaxCharges));
        Assert.Equal(10, Engine.Adventure.WandCharges("SPELL_DISRUPTING_RAY"));
        Assert.Equal(6, Engine.Adventure.WandCharges("SPELL_METEOR_SHOWER"));
        var rolled = g.NewArtifactInstance(Engine.Adventure.WandArtifact, worn: true);
        Assert.Contains(rolled.Spell, new[] { "SPELL_MAGIC_ARROW", "SPELL_FIREBALL", "SPELL_ICE_BOLT", "SPELL_SLOW", "SPELL_WEAKNESS",
            "SPELL_DISRUPTING_RAY", "SPELL_HASTE", "SPELL_STONESKIN", "SPELL_DEFLECT_ARROWS", "SPELL_PHANTOM" });
        Assert.Equal(Engine.Adventure.WandCharges(rolled.Spell), rolled.Charges);
        // Case 0x56: the Rune of Flame always draws from 00e933b8 (00ca8300 clears +0x20 for it).
        var rune = g.NewArtifactInstance(Engine.Adventure.RuneOfFlameArtifact, worn: true, "SPELL_FIREBALL");
        Assert.Contains(rune.Spell, new[] { "SPELL_RUNE_OF_CHARGE", "SPELL_RUNE_OF_BERSERKING", "SPELL_RUNE_OF_MAGIC_CONTROL", "SPELL_RUNE_OF_EXORCISM" });
        Assert.Equal(0, rune.MaxCharges);
    }

    [Fact]
    public void AScrollIsAWornBookEntryNotALearnedSpell()
    {
        // 00b541f0 → 00ca6930: the scroll's own spell (+0x20) is a book entry while worn; nothing joins the known list.
        var g = Game("SPELL_LIGHTNING_BOLT");
        var h = Hero(g);
        h.Artifacts.Add(Engine.Adventure.ScrollArtifact);
        h.ArtifactInstances.Add(g.NewArtifactInstance(Engine.Adventure.ScrollArtifact, worn: true, "SPELL_LIGHTNING_BOLT"));
        g.SyncArtifactInstances(h);
        Assert.Contains("SPELL_LIGHTNING_BOLT", g.BookSpells(h));
        Assert.Empty(h.Spells);
        // Moved to the backpack: no entry.
        h.Artifacts.Clear();
        h.Backpack.Add(Engine.Adventure.ScrollArtifact);
        h.ArtifactInstances[0].Worn = false;
        Assert.DoesNotContain("SPELL_LIGHTNING_BOLT", g.BookSpells(h));
    }

    [Fact]
    public void WandEntriesAreCastForChargesAndRechargedAtTheGuild()
    {
        // The combat hand-off carries the worn wands (00bc0030 / 00bbf610 use the charge object); FUN_00b4db10 refills
        // every carried wand (FUN_00ca6310: current = max).
        var g = Game();
        var h = Hero(g);
        h.Artifacts.Add(Engine.Adventure.WandArtifact);
        var w = g.NewArtifactInstance(Engine.Adventure.WandArtifact, worn: true, "SPELL_SLOW");
        h.ArtifactInstances.Add(w);
        Assert.Equal(new[] { w }, g.WandEntries(h));
        Assert.Contains("SPELL_SLOW", g.BookSpells(h));
        Assert.DoesNotContain("SPELL_SLOW", g.ManaBookSpells(h));
        w.Charges = 1;
        g.RechargeWands(h);
        Assert.Equal(5, w.Charges);
    }
}
