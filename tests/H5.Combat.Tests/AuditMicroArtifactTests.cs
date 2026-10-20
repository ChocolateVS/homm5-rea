using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Creature artifact (micro-artifact) effects in combat per H5_Game.exe (docs/rules/audit/combat_micro_artifacts.md).</summary>
[Collection("game")]
public class AuditMicroArtifactTests(GameDataFixture f)
{
    static MicroArtifact Art(params (MicroEffect e, int v)[] fx) =>
        new() { Effects = fx.Select(x => new MicroArtifactEntry(x.e, x.v)).ToList() };

    BattleState Setup(MicroArtifact? mine, string creature = "CREATURE_GREMLIN", string enemy = "CREATURE_PEASANT", int enemyCount = 50)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Army = { new ArmySlot(creature, 20) { MicroArtifact = mine } } },
            new ArmySetup { Army = { new ArmySlot(enemy, enemyCount) } },
            3, obstacles: Array.Empty<Cell>(), skipTactics: true);
        return s;
    }

    // exe 004bb170 / 004bb2a0 / 004bc470 / 004bc0b0: the artifact's piercing, defence, health and speed are added to the
    // stack's base values (army getters read the summary through FUN_00c0a2c0, local bytes +6 / +7 / +4 / +12).
    [Fact]
    public void PiercingDefenceHealthAndSpeedRaiseTheBaseValues()
    {
        var plain = Setup(null).Units[0];
        var u = Setup(Art((MicroEffect.Piercing, 6), (MicroEffect.Defence, 4), (MicroEffect.Health, 5))).Units[0];
        Assert.Equal(plain.BaseAttack + 6, u.BaseAttack);
        Assert.Equal(plain.BaseDefence + 4, u.BaseDefence);
        Assert.Equal(plain.BaseHealth + 5, u.BaseHealth);
        Assert.Equal(plain.BaseHealth + 5, u.MaxHealthCache);
        var fast = Setup(Art((MicroEffect.Speed, 2))).Units[0];
        Assert.Equal(plain.BaseSpeed + 2, fast.BaseSpeed);
        Assert.Equal(1, fast.ArtifactEffects);   // Melt Artifact counts the effects
    }

    // exe 004bc190 tail: initiative × (min(haste, 50) · 0.01 + 1).
    [Fact]
    public void HasteMultipliesInitiativeCappedAtFiftyPercent()
    {
        var plain = Setup(null).Units[0];
        var u = Setup(Art((MicroEffect.Haste, 20))).Units[0];
        Assert.Equal((float)(plain.BaseInitiative * 1.2), (float)u.BaseInitiative, 4);
        var capped = Setup(Art((MicroEffect.Haste, 70))).Units[0];
        Assert.Equal((float)(plain.BaseInitiative * 1.5), (float)capped.BaseInitiative, 4);
    }

    // exe 004ba970 / 004ba9d0: MAE_LUCK / MAE_MORALE are added to the week-adjusted luck / morale.
    [Fact]
    public void LuckAndMoraleAreAdded()
    {
        var s0 = Setup(null);
        var s1 = Setup(Art((MicroEffect.Luck, 2), (MicroEffect.Morale, 3)));
        var b0 = new Battle(f.Db, s0); var b1 = new Battle(f.Db, s1);
        Assert.Equal(b0.LuckOf(s0.Units[0]) + 2, b1.LuckOf(s1.Units[0]));
        Assert.Equal(b0.MoraleOf(s0.Units[0]) + 3, b1.MoraleOf(s1.Units[0]));
    }

    // exe 0097b328: the target stack's MAE_MAGIC_PROTECTION scales the spell damage factor by (1 − value · 0.01).
    [Fact]
    public void MagicProtectionReducesSpellDamage()
    {
        var s = Setup(Art((MicroEffect.MagicProtection, 30)));
        var b = new Battle(f.Db, s);
        var fireball = f.Db.Spell("SPELL_FIREBALL")!;
        Assert.Equal(1 - 30 * (double)0.01f, b.SpellDamageMultiplier(s.Units[0], fireball, 1), 6);
        Assert.Equal(1.0, b.SpellDamageMultiplier(s.Units[1], fireball, 0), 6);
    }

    // exe 00a62cbc..00a62d08: each strike adds SPELL_EFFECT_ARMOR_CRUSHING (cumulative, 004e3990/004e4a80) to the target;
    // 00989790 case 0xb3 lowers its defence by the value unless it is ARMORED (ability 0x55).
    [Fact]
    public void ArmorCrushingAccumulatesPerStrikeAndSparesArmored()
    {
        var s = Setup(Art((MicroEffect.ArmorCrushing, 2)), enemy: "CREATURE_PEASANT", enemyCount: 500);
        var b = new Battle(f.Db, s);
        b.Begin();
        var g = s.Units[0]; var p = s.Units[1];
        g.Pos = g.StartPos = new Cell(5, 5); p.Pos = p.StartPos = new Cell(6, 5);
        int def0 = b.DefenceOf(p);
        s.Active = g.Id;
        var r = b.Apply(new AttackCmd(g.Id, p.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        int strikes = r.Events.OfType<AttackStarted>().Count(a => a.Attacker == g.Id);
        Assert.Equal(2.0 * strikes, p.GetEffect(MicroArtifacts.ArmorCrushingEffect)!.Value);
        Assert.Equal(Math.Max(0, def0 - 2 * strikes), b.DefenceOf(p));
        p.Abilities.Add(Ab.Armored);
        Assert.Equal(def0, b.DefenceOf(p));
        Assert.Null(g.GetEffect(MicroArtifacts.ArmorCrushingEffect));   // the peasants carry none
    }
}
