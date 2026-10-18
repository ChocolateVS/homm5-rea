using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Refusal reasons the original shows for an invalid spell target (exe FUN_00982010).</summary>
[Collection("game")]
public class SpellRefusalTests(GameDataFixture f)
{
    [Fact]
    public void FriendOnlySpellOnEnemyIsRefusedWithFriendlyText()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 30, posA: new Cell(1, 4), posB: new Cell(10, 4));
        var enemy = b.State.Units[1];
        Assert.Equal("COMBAT_CANT_CAST_ONLY_FOR_FRIENDLY", b.SpellTargetRefusalKey(Actor.Hero(0), 0, "SPELL_BLESS", Mastery.Basic, enemy.Id));
        Assert.Null(b.SpellTargetRefusalKey(Actor.Hero(0), 0, "SPELL_BLESS", Mastery.Basic, b.State.Units[0].Id));
    }

    [Fact]
    public void HostileSpellOnOwnStackIsRefusedWithHostileText()
    {
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 30, posA: new Cell(1, 4), posB: new Cell(10, 4));
        Assert.Equal("COMBAT_CANT_CAST_ONLY_FOR_HOSTILE", b.SpellTargetRefusalKey(Actor.Hero(0), 0, "SPELL_LIGHTNING_BOLT", Mastery.Basic, b.State.Units[0].Id));
        Assert.Null(b.SpellTargetRefusalKey(Actor.Hero(0), 0, "SPELL_LIGHTNING_BOLT", Mastery.Basic, b.State.Units[1].Id));
    }

    [Fact]
    public void ImmuneTargetIsRefusedWithImmunityText()
    {
        // Air elementals are immune to air (lightning) damage.
        var b = f.Duel("CREATURE_FOOTMAN", 10, "CREATURE_AIR_ELEMENTAL", 5, posA: new Cell(1, 4), posB: new Cell(10, 4));
        Assert.Equal("COMBAT_CANT_CAST_SPELL_IMMUNITY", b.SpellTargetRefusalKey(Actor.Hero(0), 0, "SPELL_LIGHTNING_BOLT", Mastery.Basic, b.State.Units[1].Id));
    }
}
