using H5.Core.Data;

namespace H5.Combat.Tests;

// The combat hero's spell list is the adventure hero's book (H5_Game.exe vf 0x248 = 00bc1fa0 → CHero vf 0xcc = 00b5aa20,
// hero +0x1e0), worn-artifact entries included (docs/rules/audit/combat_book_spells.md).
[Collection("game")]
public class AuditBookSpellTests(GameDataFixture f)
{
    static Battle Started(GameDataFixture f, ulong seed)
    {
        var s = BattleSetup.CreateDuel(f.Db, 12, 3, seed, skipTactics: true, arena: "Grass_Big_01");
        var battle = new Battle(f.Db, s);
        battle.Begin();
        return battle;
    }

    /// <summary>
    /// Ring of Unsummoning (00ca7e40 case 0x5d) puts SPELL_ABILITY_UNSUMMON (0x42) in the book (00b541f0); 0097c320 case
    /// 0x42 offers it while the opposing army has a summoned stack, 00984a00 case 0x42 deals Banish's damage. It is a hero
    /// ability (school SPECIAL, FUN_00a34610), not a spell of the spell book.
    /// </summary>
    [StockFact]
    public void RingOfUnsummoningEntryGivesBanishWithoutThePerk()
    {
        var b = Started(f, 2);
        var h = b.State.Heroes[0];
        h.Perks.Remove(Sk.Banish);
        var summoned = BattleSetup.AddCreature(f.Db, b.State, 1, "CREATURE_PEASANT", 500, new Cell(11, 9), summoned: true);
        Assert.DoesNotContain(b.HeroAbilities(h), a => a.Id == "BANISH");
        h.Spells.Add("SPELL_ABILITY_UNSUMMON");
        Assert.Contains(b.HeroAbilities(h), a => a.Id == "BANISH");
        Assert.DoesNotContain("SPELL_ABILITY_UNSUMMON", b.HeroSpells(h));
        int hp = summoned.TotalHealth;
        b.State.Active = Actor.Hero(0);
        var r = b.Apply(new UseAbilityCmd(Actor.Hero(0), "BANISH") { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(hp - (10 * h.Level + 10 * (int)h.SkillLevel(Sk.Summoning)), summoned.TotalHealth);
    }

    /// <summary>
    /// A worn artifact's spell is re-derived like any book entry: 00b5a410 runs 00b53b10 after 00b541f0, and every entry with
    /// an artifact passes vf 0x158 (00b526f0) into 00b4bb40 — the mass version with its Master perk (00a33cf0 / 00a33eb0).
    /// </summary>
    [StockFact]
    public void ArtifactBookSpellGetsItsMassVersion()
    {
        var b = Started(f, 3);
        var h = b.State.Heroes[0];
        h.Spells = new() { "SPELL_BLESS" };
        h.Perks.Remove(Sk.MasterBlessing);
        Assert.DoesNotContain("SPELL_MASS_BLESS", b.HeroSpells(h));
        h.Perks.Add(Sk.MasterBlessing);
        Assert.Contains("SPELL_MASS_BLESS", b.HeroSpells(h));
    }
}
