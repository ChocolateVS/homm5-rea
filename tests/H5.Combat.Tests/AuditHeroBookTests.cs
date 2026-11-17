using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>The combat hero casts the adventure hero's book entries (H5_Game.exe 00bc1fa0 → 00b5aa20, +0x1e0).</summary>
[Collection("game")]
public class AuditHeroBookTests(GameDataFixture f)
{
    Battle With(HeroInstance hi) => new(f.Db, BattleSetup.Create(f.Db,
        new ArmySetup { Hero = hi, Army = { new ArmySlot("CREATURE_FOOTMAN", 20) } },
        new ArmySetup { Army = { new ArmySlot("CREATURE_PEASANT", 20) } }, 3, obstacles: Array.Empty<Cell>(), skipTactics: true));

    // exe 00b56cd0: the set-up rebuilds the book (00b53b10 at 00b56de9) before the worn artifacts' entries go in
    // (00b541f0 at 00b56e53), so a starting artifact's spell has no empowered version in the book; the combat hero reads
    // the book as it is (vf 0x248 = 00bc1fa0 → CHero vf 0xcc = 00b5aa20). Without an adventure book the versions are derived.
    [Fact]
    public void TheAdventureBookIsCastAsItIs()
    {
        var hero = f.Db.DuelPresets[0].Hero;
        HeroInstance Hi(List<BookEntry>? book) => new()
        {
            Shared = hero.Shared, Level = hero.Level, Perks = { "HERO_SKILL_EMPOWERED_SPELLS" }, Spells = { "SPELL_MAGIC_ARROW" }, Book = book,
        };
        Assert.NotNull(f.Db.Spell("SPELL_EMPOWERED_MAGIC_ARROW"));
        var derived = With(Hi(null));
        Assert.Contains("SPELL_EMPOWERED_MAGIC_ARROW", derived.HeroSpells(derived.State.Heroes[0]));
        var asSetUp = With(Hi([new BookEntry { Spell = "SPELL_MAGIC_ARROW", Artifact = "SOME_ARTIFACT" }]));
        var spells = asSetUp.HeroSpells(asSetUp.State.Heroes[0]).ToList();
        Assert.Contains("SPELL_MAGIC_ARROW", spells);
        Assert.DoesNotContain("SPELL_EMPOWERED_MAGIC_ARROW", spells);
        var rebuilt = With(Hi([new BookEntry { Spell = "SPELL_MAGIC_ARROW", Artifact = "SOME_ARTIFACT" },
            new BookEntry { Spell = "SPELL_EMPOWERED_MAGIC_ARROW", Sources = { "HERO_SKILL_EMPOWERED_SPELLS" } }]));
        Assert.Contains("SPELL_EMPOWERED_MAGIC_ARROW", rebuilt.HeroSpells(rebuilt.State.Heroes[0]));
    }
}
