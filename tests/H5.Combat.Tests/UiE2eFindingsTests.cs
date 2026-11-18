namespace H5.Combat.Tests;

/// <summary>Engine findings of the combat UI end-to-end check (docs/rules/audit/ui_e2e_check.md).</summary>
[Collection("game")]
public class UiE2eFindingsTests(GameDataFixture f)
{
    [Fact]
    public void CreatureAbilityEntriesAreNotCastableSpells()   // FUN_00a34610: school SPECIAL entries are abilities
    {
        // The Royal Griffin's spell list holds SPELL_ABILITY_BATTLE_DIVE; it is the Battle Dive ability, not a spell
        // that puts an effect on a target.
        var b = f.Duel("CREATURE_ROYAL_GRIFFIN", 5, "CREATURE_PEASANT", 100, 1, new Cell(1, 4), new Cell(10, 5));
        b.Begin();
        var g = b.State.Units[0];
        Assert.Contains(g.Spells, k => k.Spell == "SPELL_ABILITY_BATTLE_DIVE");
        b.State.Active = g.Id;
        var la = b.GetLegalActions()!;
        Assert.DoesNotContain(la.Spells, s => s.Spell.StartsWith("SPELL_ABILITY_"));
        Assert.Contains(la.Abilities, a => a.Ability == "ABILITY_BATTLE_DIVE");
        var r = b.Apply(new CastSpellCmd(g.Id, "SPELL_ABILITY_BATTLE_DIVE", b.State.Units[1].Id) { Side = 0 });
        Assert.False(r.Ok);
    }
}
