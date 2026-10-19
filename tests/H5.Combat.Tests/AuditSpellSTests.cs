using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>School spells and spell immunities confirmed against H5_Game.exe (docs/rules/audit/combat_spells_s.md).</summary>
[Collection("game")]
public class AuditSpellSTests(GameDataFixture f)
{
    Battle Make((string id, int count)[] mine, (string id, int count)[] theirs)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = mine.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = theirs.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            7, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        return new Battle(f.Db, s);
    }

    static Unit Theirs(Battle b, string id) => b.State.Units.Single(x => x.Side == 1 && x.CreatureId == id);
    SpellDef Sp(string id) => f.Db.Spell(id)!;

    // exe FUN_008a34c0 → FUN_00b61950 / FUN_00b618a0: Immunity to Blind and to Mind Control are derived from the stack's
    // own Undead / Mechanical / Elemental / Immunity to Magic abilities; Slow and the element immunities from Immunity to
    // Magic; Shawl of the Great Lich on the opposing hero cancels Mind Control immunity, the derived one too; Vampirism's
    // dynamic Undead does not count.
    [Fact]
    public void DerivedImmunitiesFollowTheCreatureFlags()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_SKELETON", 10), ("CREATURE_PEASANT", 10), ("CREATURE_BLACK_DRAGON", 1) });
        var sk = Theirs(b, "CREATURE_SKELETON");
        var pe = Theirs(b, "CREATURE_PEASANT");
        var bd = Theirs(b, "CREATURE_BLACK_DRAGON");
        Assert.True(b.HasAbilityExe(sk, Ab.ImmuneBlind));
        Assert.True(b.HasAbilityExe(sk, Ab.ImmuneMind));
        Assert.False(b.HasAbilityExe(sk, Ab.ImmuneSlow));
        Assert.True(b.HasAbilityExe(bd, Ab.ImmuneSlow));
        Assert.True(b.HasAbilityExe(bd, Ab.ImmuneFire));
        pe.Effects.Add(new Effect { Id = "SPELL_VAMPIRISM", Remaining = 3 });
        Assert.True(pe.Has(Ab.Undead));
        Assert.False(b.HasAbilityExe(pe, Ab.ImmuneMind));
        Assert.False(b.ExeSpellImmune(pe, Sp("SPELL_FORGETFULNESS"), "SPELL_FORGETFULNESS", null));
        Assert.True(b.ExeSpellImmune(pe, Sp("SPELL_PLAGUE"), "SPELL_PLAGUE", null));          // Plague uses the dynamic Undead
        b.State.Heroes[0].Artifacts.Add("ARTIFACT_SHAWL_OF_GREAT_LICH");
        Assert.False(b.HasAbilityExe(sk, Ab.ImmuneMind));
        Assert.False(b.MindImmune(sk));
        Assert.True(b.HasAbilityExe(sk, Ab.ImmuneBlind));
        Assert.False(b.ExeSpellImmune(sk, Sp("SPELL_FORGETFULNESS"), "SPELL_FORGETFULNESS", null));
        Assert.True(b.ExeSpellImmune(sk, Sp("SPELL_BLIND"), "SPELL_BLIND", null));
    }

    // exe FUN_0097c9f0 per-spell table: Vampirism refuses Mechanical and Elemental only (undead are valid); Enchanted
    // Obsidian refuses every non-damage spell (FUN_004e5cb0); Armored refuses Disrupting Ray unless the caster is a
    // Disrupter (FUN_004e5e60); a Rune of Elemental Immunity covers every spell of its element (Disrupting Ray is Air).
    [Fact]
    public void SpellTableImmunities()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_SKELETON", 10), ("CREATURE_IRON_GOLEM", 5), ("CREATURE_PEASANT", 10), ("CREATURE_DEFENDER", 10) });
        var vamp = Sp("SPELL_VAMPIRISM");
        Assert.False(b.ExeSpellImmune(Theirs(b, "CREATURE_SKELETON"), vamp, vamp.Id, null));
        Assert.True(b.ExeSpellImmune(Theirs(b, "CREATURE_IRON_GOLEM"), vamp, vamp.Id, null));
        var pe = Theirs(b, "CREATURE_PEASANT");
        pe.Abilities.Add(Ab.EnchantedObsidian);
        Assert.True(b.ExeSpellImmune(pe, Sp("SPELL_BLESS"), "SPELL_BLESS", null));
        Assert.False(b.ExeSpellImmune(pe, Sp("SPELL_LIGHTNING_BOLT"), "SPELL_LIGHTNING_BOLT", null));
        pe.Abilities.Remove(Ab.EnchantedObsidian);
        var ray = Sp("SPELL_DISRUPTING_RAY");
        var def = Theirs(b, "CREATURE_DEFENDER");
        Assert.True(b.ExeSpellImmune(def, ray, ray.Id, b.State.Heroes[0]));
        b.State.Heroes[0].Specialization = "HERO_SPEC_DISRUPTER";
        Assert.False(b.ExeSpellImmune(def, ray, ray.Id, b.State.Heroes[0]));
        Assert.False(b.ExeSpellImmune(pe, ray, ray.Id, null));
        pe.Effects.Add(new Effect { Id = Fx.RuneElementalImmunity, Value = 1, Value2 = 4, Remaining = double.PositiveInfinity });
        Assert.True(b.ExeSpellImmune(pe, ray, ray.Id, null));
        Assert.True(b.ExeSpellImmune(pe, Sp("SPELL_PLAGUE"), "SPELL_PLAGUE", null));          // earth
    }

    // exe FUN_0097c9f0 (targets with a war machine / spawn cast): only spells 1..10, Explosion, Call Lightning, Firewall,
    // Stone Spikes and the boss spells reach them — Wasp Swarm, Deep Freeze and Divine Vengeance do not.
    [Fact]
    public void WarMachinesTakeOnlyTheListedSpells()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_PEASANT", 10) });
        var pe = Theirs(b, "CREATURE_PEASANT");
        pe.Kind = UnitKind.WarMachine;
        Assert.False(b.ExeSpellImmune(pe, Sp("SPELL_FIREBALL"), "SPELL_FIREBALL", null));
        Assert.False(b.ExeSpellImmune(pe, Sp("SPELL_STONE_SPIKES"), "SPELL_STONE_SPIKES", null));
        Assert.True(b.ExeSpellImmune(pe, Sp("SPELL_WASP_SWARM"), "SPELL_WASP_SWARM", null));
        Assert.True(b.ExeSpellImmune(pe, Sp("SPELL_DEEP_FREEZE"), "SPELL_DEEP_FREEZE", null));
        Assert.True(b.ExeSpellImmune(pe, Sp("SPELL_DIVINE_VENGEANCE"), "SPELL_DIVINE_VENGEANCE", null));
    }

    // exe FUN_0097d2a0: Irresistible Magic only reduces the protection of damage spells (FUN_004e5cb0) — a non-damage spell
    // on a magic-immune stack stays refused with it; a damage spell gets through.
    [Fact]
    public void IrresistibleMagicOnlyHelpsDamageSpells()
    {
        var b = Make(new[] { ("CREATURE_FOOTMAN", 10) }, new[] { ("CREATURE_BLACK_DRAGON", 1) });
        var bd = Theirs(b, "CREATURE_BLACK_DRAGON");
        var h = b.State.Heroes[0];
        h.Skills.RemoveAll(s => s.SkillId == Sk.Invocation);
        h.Skills.Add(new SkillEntry(Sk.Invocation, Mastery.Basic));
        Assert.Equal("COMBAT_CANT_CAST_SPELL_IMMUNITY", b.SpellTargetRefusalKey(Actor.Hero(0), 0, "SPELL_SLOW", Mastery.Basic, bd.Id));
        Assert.Null(b.SpellTargetRefusalKey(Actor.Hero(0), 0, "SPELL_LIGHTNING_BOLT", Mastery.Basic, bd.Id));
    }
}
