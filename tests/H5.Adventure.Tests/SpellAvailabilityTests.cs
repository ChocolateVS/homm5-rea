using H5.Adventure.Engine;
using H5.Adventure.Map;
using H5.Adventure.State;
using H5.Core.Data;

namespace H5.Adventure.Tests;

/// <summary>
/// The map's spell manager (CMagicSystem, vtable 00e6eb00) and its callers in H5_Game.exe: availability slot 0x18 =
/// FUN_00a34af0 (Walker's Hut FUN_00ccf7b0), slot 4 = FUN_00a34d60 and the deck slot 0xc = FUN_00a35c60 (Shrine of
/// Magic set-up FUN_00a80990), the scroll set-up FUN_00ca7e40 case 0x34 and the perk spells of 00b54770 (cases 0x77,
/// 0x89, 0x92). The map list is SAdvMapDesc spellIDs at +0x1e0 (reader 00a0b4b0, 00a0be2e).
/// </summary>
[Collection("adv")]
public class SpellAvailabilityTests(Fixture f)
{
    Engine.Adventure Game(params string[] spells)
    {
        var map = f.Loader.Load(MapCatalog.Find(f.Fs, f.Install, "A2M1")!);
        map.SpellIds.Clear();
        map.SpellIds.AddRange(spells);
        // The map's own shrines draw from the deck at set-up; leave it untouched for the tests.
        map.Objects.RemoveAll(o => o.Kind == ObjectKind.Shrine);
        return Engine.Adventure.NewGame(f.Db, map, GameSetup.AllPlayers(map, 0, 7)).Game;
    }

    SpellDef Def(string id) => f.Db.Core.Spell(id)!;

    [Fact]
    public void AnEmptyMapListMakesEveryRegisteredSpellAvailable()
    {
        var g = Game();
        // 00a34f50 registers the table through 00a34e20; 00a34af0 reads +0x24 / +0x3c / +0x60 by circle.
        Assert.True(g.SpellAvailableOnMap("SPELL_MAGIC_ARROW"));
        Assert.True(g.SpellAvailableOnMap("SPELL_SUMMON_BOAT"));
        Assert.Contains(f.Db.Core.Spells.Values, s => s.Stock && s.School == "MAGIC_SCHOOL_RUNIC" && g.SpellAvailableOnMap(s.Id));
        Assert.Contains(f.Db.Core.Spells.Values, s => s.Stock && s.School == "MAGIC_SCHOOL_WARCRIES" && g.SpellAvailableOnMap(s.Id));
        // Skipped by 00a34e20: Dark Ritual (00a349f0), mass (00a33e20) and empowered (00a33f70) spells.
        Assert.False(g.SpellAvailableOnMap("SPELL_ABILITY_DARK_RITUAL"));
        Assert.False(g.SpellAvailableOnMap("SPELL_MASS_CURSE"));
        Assert.False(g.SpellAvailableOnMap("SPELL_EMPOWERED_MAGIC_ARROW"));
        Assert.False(g.SpellAvailableOnMap("SPELL_NO_SUCH_SPELL"));
    }

    [Fact]
    public void AMapListMakesOnlyItsSpellsAvailable()
    {
        var g = Game("SPELL_MAGIC_ARROW", "SPELL_SUMMON_BOAT");
        Assert.True(g.SpellAvailableOnMap("SPELL_MAGIC_ARROW"));
        Assert.True(g.SpellAvailableOnMap("SPELL_SUMMON_BOAT"));
        Assert.False(g.SpellAvailableOnMap("SPELL_FIREBALL"));
        // A campaign map (not under Maps/Multiplayer, 00c9d9e0) gets no extra runes or warcries.
        Assert.DoesNotContain(f.Db.Core.Spells.Values, s => s.Stock && s.School is "MAGIC_SCHOOL_RUNIC" or "MAGIC_SCHOOL_WARCRIES" && g.SpellAvailableOnMap(s.Id));
    }

    [Fact]
    public void WalkersHutSplitsTheTalismanSpellsByMapAvailability()
    {
        var g = Game("SPELL_MAGIC_ARROW");
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        var t = g.S.Towns.First(x => x.Owner == h.Owner);
        t.Type = "TOWN_STRONGHOLD";
        t.Built[Engine.Adventure.WalkersHut] = 1;
        h.Class = "HERO_CLASS_BARBARIAN";
        h.Level = 40;
        h.TalismanLevel = 0;
        h.Spells.Clear();
        int n = g.TalismanMaxLevel;
        var q = g.TalismanQuoteFor(t, h, 1, n);
        // FUN_00ccf7b0: vf 0x18 true → list 1, else list 2; none of the talisman spells is on this map's list.
        Assert.Empty(q.Allowed);
        Assert.Equal(g.TalismanLevels.Select(l => l.Spell), q.Forbidden);
        // 00b56b50 removes an unavailable spell instead of granting it.
        h.TalismanLevel = n;
        h.Spells.Add(g.TalismanLevels[0].Spell);
        g.GrantTalismanSpells(h);
        Assert.DoesNotContain(g.TalismanLevels[0].Spell, h.Spells);
    }

    [Fact]
    public void ShrineDrawsAreFromTheMapPoolAndDoNotRepeatUntilTheCircleRunsOut()
    {
        var g = Game();
        var shrine = new MapObject { Shared = new SharedDef { ShrineLevel = 1 } };
        var pool = f.Db.Core.Spells.Values.Where(s => s.Stock && s.Level == 1 && g.SpellAvailableOnMap(s.Id)
            && s.School is not ("MAGIC_SCHOOL_ADVENTURE" or "MAGIC_SCHOOL_RUNIC" or "MAGIC_SCHOOL_WARCRIES")).Select(s => s.Id).ToHashSet();
        var drawn = Enumerable.Range(0, pool.Count).Select(_ => g.ShrineSpell(shrine)!).ToList();
        Assert.Equal(pool.Count, drawn.Distinct().Count());
        Assert.True(drawn.ToHashSet().SetEquals(pool));
        // The deck refills once the circle is exhausted (00a35c60).
        Assert.Contains(g.ShrineSpell(shrine)!, pool);

        // A map list limits the shrine to it.
        var g2 = Game("SPELL_MAGIC_ARROW", "SPELL_FIREBALL");
        Assert.Equal("SPELL_MAGIC_ARROW", g2.ShrineSpell(shrine));
        Assert.Equal("SPELL_MAGIC_ARROW", g2.ShrineSpell(shrine));
        Assert.Null(g2.ShrineSpell(new MapObject { Shared = new SharedDef { ShrineLevel = 2 } }));
        Assert.Null(g2.ShrineSpell(new MapObject { Shared = new SharedDef { ShrineLevel = 0 } }));
    }

    [Fact]
    public void RunicShrinesRollARuneBetweenLevelAndMaxRunicMagicLevel()
    {
        var g = Game();
        var shrine = new MapObject { Shared = new SharedDef { RunicMagic = true, ShrineLevel = 2, MaxRunicMagicLevel = 3 } };
        for (int i = 0; i < 20; i++)
        {
            var d = Def(g.ShrineSpell(shrine)!);
            Assert.Equal("MAGIC_SCHOOL_RUNIC", d.School);
            Assert.InRange(d.Level, 2, 3);
        }
        // 00a34d60: min > max gives none.
        Assert.Null(g.ShrineSpell(new MapObject { Shared = new SharedDef { RunicMagic = true, ShrineLevel = 3, MaxRunicMagicLevel = 1 } }));
    }

    [Fact]
    public void AMapShrineSpellIsKeptWhenValid()
    {
        var g = Game("SPELL_MAGIC_ARROW");
        // FUN_00a80990: the map spell is not tested against the manager, only by 00a349f0.
        Assert.Equal("SPELL_FIREBALL", g.ShrineSpell(new MapObject { SpellId = "SPELL_FIREBALL", Shared = new SharedDef { ShrineLevel = 1 } }));
        Assert.Null(g.ShrineSpell(new MapObject { SpellId = "SPELL_ABILITY_DARK_RITUAL", Shared = new SharedDef { ShrineLevel = 1 } }));
    }

    [Fact]
    public void RandomScrollsHoldACircle3Or4NonAdventureSpell()
    {
        var g = Game();
        for (int i = 0; i < 20; i++)
        {
            var d = Def(g.RandomScrollSpell()!);
            Assert.InRange(d.Level, 3, 4);
            Assert.DoesNotContain(d.School, new[] { "MAGIC_SCHOOL_ADVENTURE", "MAGIC_SCHOOL_RUNIC", "MAGIC_SCHOOL_WARCRIES" });
        }
        Assert.Null(Game("SPELL_MAGIC_ARROW").RandomScrollSpell());
    }

    HeroState Hero(Engine.Adventure g)
    {
        var h = g.S.Heroes.First(x => x.Alive && x.Owner == g.Current.Index);
        h.Spells.Clear();
        h.Perks.Clear();
        return h;
    }

    [Fact]
    public void SecretsOfDestructionTeachesAnUnknownDestructiveSpellOfCircles1To3()
    {
        var g = Game();
        var h = Hero(g);
        g.ScriptLearnPerk(h, "HERO_SKILL_SECRETS_OF_DESTRUCTION");
        var d = Def(Assert.Single(h.Spells));
        Assert.Equal("MAGIC_SCHOOL_DESTRUCTIVE", d.School);
        Assert.InRange(d.Level, 1, 3);
        // None on the map: nothing learned.
        var g2 = Game("SPELL_BLESS");
        var h2 = Hero(g2);
        g2.ScriptLearnPerk(h2, "HERO_SKILL_SECRETS_OF_DESTRUCTION");
        Assert.Empty(h2.Spells);
    }

    [Fact]
    public void InsightsTeachesALearnableSpellOfTheLowestCircleThatHasOne()
    {
        var g = Game("SPELL_MAGIC_ARROW", "SPELL_FIREBALL");
        var h = Hero(g);
        g.ScriptLearnPerk(h, "HERO_SKILL_INSIGHTS");
        Assert.Equal(new[] { "SPELL_MAGIC_ARROW" }, h.Spells);
    }

    [Fact]
    public void AbsoluteWizardryTeachesEveryMapSpell()
    {
        var g = Game("SPELL_MAGIC_ARROW", "SPELL_FIREBALL", "SPELL_SUMMON_BOAT");
        var h = Hero(g);
        g.ScriptLearnPerk(h, "HERO_SKILL_ABSOLUTE_WIZARDY");
        Assert.Equal(new[] { "SPELL_FIREBALL", "SPELL_MAGIC_ARROW", "SPELL_SUMMON_BOAT" }, h.Spells.OrderBy(s => s, StringComparer.Ordinal));
    }
    [Fact]
    public void MasterPerksGiveTheMassVersionsOfKnownSpells()
    {
        // 00b54770 cases 0x2e..0x33 (00b5527a): the known list +0x204, FUN_00a344c0 / FUN_00a33eb0, learned with the perk as source.
        var g = Game();
        var h = Hero(g);
        h.Spells.AddRange(new[] { "SPELL_CURSE", "SPELL_SLOW", "SPELL_WEAKNESS" });
        g.ScriptLearnPerk(h, "HERO_SKILL_MASTER_OF_CURSES");
        Assert.Equal(new[] { "SPELL_CURSE", "SPELL_SLOW", "SPELL_WEAKNESS", "SPELL_MASS_CURSE", "SPELL_MASS_WEAKNESS" }, h.Spells);
        Assert.Equal(new[] { "HERO_SKILL_MASTER_OF_CURSES" }, h.SpellSources["SPELL_MASS_CURSE"]);
        Assert.False(Engine.Adventure.KnowsSpell(h, "SPELL_MASS_CURSE"));
        Assert.True(Engine.Adventure.KnowsSpell(h, "SPELL_CURSE"));
    }

    [Fact]
    public void LearningASpellWithItsMasterPerkOrEmpoweredSpellsAddsTheirVersions()
    {
        // 00b545a0 → 00b4bb40: Empowered Spells (0x46) → FUN_00a34060, the spell's Master perk → FUN_00a33eb0.
        var g = Game();
        var h = Hero(g);
        h.Perks.Add("HERO_SKILL_MASTER_OF_WRATH");
        h.Perks.Add("HERO_SKILL_EMPOWERED_SPELLS");
        g.ScriptLearnSpell(h, "SPELL_HASTE");
        g.ScriptLearnSpell(h, "SPELL_MAGIC_ARROW");
        g.ScriptLearnSpell(h, "SPELL_CURSE");
        Assert.Equal(new[] { "SPELL_HASTE", "SPELL_MASS_HASTE", "SPELL_MAGIC_ARROW", "SPELL_EMPOWERED_MAGIC_ARROW", "SPELL_CURSE" }, h.Spells);
    }

    [Fact]
    public void EmpoweredSpellsGivesTheEmpoweredVersionsOfKnownSpells()
    {
        // 00b54770 case 0x46: FUN_00a34060 over the known list +0x204.
        var g = Game();
        var h = Hero(g);
        h.Spells.AddRange(new[] { "SPELL_MAGIC_ARROW", "SPELL_BLESS" });
        g.ScriptLearnPerk(h, "HERO_SKILL_EMPOWERED_SPELLS");
        Assert.Equal(new[] { "SPELL_MAGIC_ARROW", "SPELL_BLESS", "SPELL_EMPOWERED_MAGIC_ARROW" }, h.Spells);
    }

    [Fact]
    public void AbsoluteWizardrySpellsAreSourcedAndItsFallThroughGivesNoMassSpells()
    {
        // Case 0x89 learns with the perk as source (no 00b4bb40, not in +0x204); the fall-through into 00b5527a calls
        // FUN_00a344c0(0x89, spell), which never holds.
        var g = Game("SPELL_CURSE");
        var h = Hero(g);
        h.Perks.Add("HERO_SKILL_MASTER_OF_CURSES");
        g.ScriptLearnPerk(h, "HERO_SKILL_ABSOLUTE_WIZARDY");
        Assert.Equal(new[] { "SPELL_CURSE" }, h.Spells);
        Assert.False(Engine.Adventure.KnowsSpell(h, "SPELL_CURSE"));
        // A plain learn afterwards puts it in the known list and runs 00b4bb40.
        g.ScriptLearnSpell(h, "SPELL_CURSE");
        Assert.True(Engine.Adventure.KnowsSpell(h, "SPELL_CURSE"));
        Assert.Equal(new[] { "SPELL_CURSE", "SPELL_MASS_CURSE" }, h.Spells);
    }
    [Fact]
    public void TomesPutTheirSchoolOfTheMapPoolInTheBook()
    {
        // 00ca6930: tome 0x4c..0x4f → slot 0x10(circle 1..5, school 0 / 2 / 1 / 3); 00b541f0 book entries, not known.
        var g = Game("SPELL_MAGIC_ARROW", "SPELL_FIREBALL", "SPELL_BLESS", "SPELL_SUMMON_BOAT");
        var h = Hero(g);
        h.Class = "HERO_CLASS_WIZARD";
        h.Artifacts.Clear();
        h.Artifacts.Add("ARTIFACT_TOME_OF_DESTRUCTION");
        Assert.Equal(new[] { "SPELL_MAGIC_ARROW", "SPELL_FIREBALL" }, g.ArtifactSpells(h));
        Assert.Empty(h.Spells);
        Assert.Equal(new[] { "SPELL_BLESS" }, g.SpellsOfArtifact("ARTIFACT_TOME_OF_LIGHT_MAGIC"));
        Assert.Empty(g.SpellsOfArtifact("ARTIFACT_TOME_OF_DARK_MAGIC"));
        // 00b541f0: no artifact spells for class 8 (vf 0x258).
        h.Class = "HERO_CLASS_BARBARIAN";
        Assert.Empty(g.ArtifactSpells(h));
    }

    [Fact]
    public void FixedArtifactSpellsAndTheBloodRageFilter()
    {
        // 00ca7e40: Band of Conjurer 0x2b, Ring of Unsummoning 0x42; 00b4b810: a Blood Rage hero gets no non-warcry spell.
        var g = Game();
        var h = Hero(g);
        h.Class = "HERO_CLASS_WIZARD";
        h.Artifacts.Clear();
        h.Artifacts.AddRange(new[] { "ARTIFACT_BAND_OF_CONJURER", "ARTIFACT_RING_OF_UNSUMMONING" });
        Assert.Equal(new[] { "SPELL_SUMMON_ELEMENTALS", "SPELL_ABILITY_UNSUMMON" }, g.ArtifactSpells(h));
        Assert.Contains("SPELL_SUMMON_ELEMENTALS", g.BookSpells(h));
        h.Perks.Add("HERO_SKILL_DEMONIC_RAGE");
        // Unsummon is not valid for 00a349f0 (SPECIAL school), so 00b4b810 lets it through.
        Assert.Equal(new[] { "SPELL_ABILITY_UNSUMMON" }, g.ArtifactSpells(h));
    }

    [Fact]
    public void ResearchersLearnTheFirstUnknownRuneOfTheLowestCircle()
    {
        // 00b575d0: vf 0x190(0x40) = 00b4b5a0 (specialization), roll < RPGStats +0x930 (Researcher_ChanceToGetExtraRune),
        // circles 1..4 of slot 0x10(circle, 5): the first rune not known (vf 0xdc) is learned.
        var g = Game();
        var pool = f.Db.Core.Spells.Values
            .Where(s => s.Stock && s.School == "MAGIC_SCHOOL_RUNIC" && s.Level is >= 1 and <= 4 && g.SpellAvailableOnMap(s.Id)).ToList();
        var h = Hero(g);
        h.Specialization = "HERO_SPEC_RESEARCHER";
        h.Skills.RemoveAll(s => s.SkillId == "HERO_SKILL_DEMONIC_RAGE");
        h.Skills.Add(new SkillEntry("HERO_SKILL_RUNELORE", Mastery.Expert));
        g.ScriptGainExperience(h, f.Db.Core.Experience.For(40));
        var runes = h.Spells.Where(s => f.Db.Core.Spell(s)!.School == "MAGIC_SCHOOL_RUNIC").ToList();
        Assert.NotEmpty(runes);
        // Circle by circle: runes come in non-decreasing circles, and a lower circle is complete before the next starts.
        var circles = runes.Select(s => f.Db.Core.Spell(s)!.Level).ToList();
        Assert.Equal(circles.OrderBy(c => c), circles);
        Assert.All(pool.Where(s => s.Level < circles.Max()), s => Assert.Contains(s.Id, runes));
        // Anyone else: no rune.
        var g2 = Game();
        var h2 = Hero(g2);
        h2.Specialization = "HERO_SPEC_NONE";
        h2.Skills.Add(new SkillEntry("HERO_SKILL_RUNELORE", Mastery.Expert));
        g2.ScriptGainExperience(h2, f.Db.Core.Experience.For(40));
        Assert.DoesNotContain(h2.Spells, s => f.Db.Core.Spell(s)!.School == "MAGIC_SCHOOL_RUNIC");
    }

    [Fact]
    public void BankSpellsComeFromTheHighestCircleWithAnUnknownPoolSpell()
    {
        // 009cd2a0 (009cd6df..): per spell circles hi..lo of slot 0x10(circle, 8), unknown (vf 0xdc) only, vf 0xe8(spell, 0).
        var g = Game("SPELL_MAGIC_ARROW", "SPELL_FIREBALL", "SPELL_SUMMON_BOAT");
        var h = Hero(g);
        h.Class = "HERO_CLASS_WIZARD";
        g.BankSpells(h, 3, 3, 1, 3);
        Assert.Equal(new[] { "SPELL_FIREBALL", "SPELL_MAGIC_ARROW" }, h.Spells);
        Assert.True(Engine.Adventure.KnowsSpell(h, "SPELL_FIREBALL"));
        // Class 8 (vf 0x258): hi · n · 1000 experience instead (vf 0x19c → 0x198).
        var g2 = Game("SPELL_MAGIC_ARROW");
        var b = Hero(g2);
        b.Class = "HERO_CLASS_BARBARIAN";
        long xp = b.Experience;
        g2.BankSpells(b, 2, 2, 1, 7);
        Assert.Empty(b.Spells);
        Assert.True(b.Experience > xp);
    }

}
