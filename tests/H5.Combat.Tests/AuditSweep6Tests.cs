using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Might / Barbarian perks and effect leftovers confirmed against H5_Game.exe (docs/rules/audit/combat_sweep_6.md).</summary>
[Collection("game")]
public class AuditSweep6Tests(GameDataFixture f)
{
    Battle Make(string mine, int countA, string theirs, int countB, ulong seed = 7, Cell? posA = null, Cell? posB = null)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new() { new ArmySlot(mine, countA) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new() { new ArmySlot(theirs, countB) } },
            seed, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        var a = s.Units.First(u => u.Side == 0 && u.CreatureId == mine);
        var t = s.Units.First(u => u.Side == 1 && u.CreatureId == theirs);
        if (posA is Cell pa) a.Pos = a.StartPos = pa;
        if (posB is Cell pb) t.Pos = t.StartPos = pb;
        return new Battle(f.Db, s);
    }

    // exe FUN_008cf270: after the strike, on a living target, Suffering Strike (HasSkill 0x5f @ 008cf692 → SPELL_WEAKNESS,
    // FUN_00bbf160) is applied before the Lions mark and before Stunning Blow's ATB drop (008cf875: ATB − RPGStats +0x708).
    // The engine dropped the ATB first.
    [Fact]
    public void SufferingStrikePrecedesStunningBlow()
    {
        int seen = 0;
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var b = Make("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 1000, seed);
            var h = b.State.Heroes[0];
            h.Perks.Add(Sk.SufferingStrike);
            h.Perks.Add(Sk.StunningBlow);
            var pe = b.State.Units.First(u => u.Side == 1);
            b.Begin();
            b.State.Active = Actor.Hero(0);
            var r = b.Apply(new HeroAttackCmd(pe.Id) { Side = 0 });
            Assert.True(r.Ok, r.Error);
            int atb = r.Events.FindIndex(e => e is AtbChanged a && a.Actor == pe.Id);
            int weak = r.Events.FindIndex(e => e is EffectAdded x && x.UnitId == pe.Id && x.Effect == "SPELL_WEAKNESS");
            Assert.True(atb >= 0);
            if (weak < 0) continue;
            Assert.True(weak < atb, $"weakness {weak} atb {atb}");
            seen++;
        }
        Assert.True(seen > 0);
    }

    // exe 008cf875..008cf8a1: Stunning Blow sets ATB − StunningBlow_ATBDrop through the ATB setter (vf 0x18c →
    // FUN_00a2d6a0), which only caps at 1.25 (FUN_00465610); the engine capped the result at 1.
    [Fact]
    public void StunningBlowKeepsAnAtbAboveOne()
    {
        var b = Make("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 1000);
        b.State.Heroes[0].Perks.Add(Sk.StunningBlow);
        var pe = b.State.Units.First(u => u.Side == 1);
        b.Begin();
        pe.Atb = 1.2;
        b.State.Active = Actor.Hero(0);
        var r = b.Apply(new HeroAttackCmd(pe.Id) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(1.2 - f.Db.Stats.Get("combat/HeroSkills/Barbarian/StunningBlow_ATBDrop", 0.1), pe.Atb, 6);
    }

    // exe FUN_00a65260 (00a65630: GetEffect(0x131) on the attacker; 00a65ad3: hero strike FUN_008cfb50 with 0x131 after the
    // strike loop) is the melee attack routine only; the ranged attack FUN_00a63020 has no Powerful Blow follow-up.
    // The engine also followed up shots.
    [Fact]
    public void PowerfulBlowFollowsMeleeAttacksOnly()
    {
        bool HeroStruck(bool melee)
        {
            var b = melee ? Make("CREATURE_ARCHER", 200, "CREATURE_PEASANT", 1000, posA: new Cell(5, 5), posB: new Cell(6, 5))
                          : Make("CREATURE_ARCHER", 200, "CREATURE_PEASANT", 1000, posA: new Cell(1, 5), posB: new Cell(10, 5));
            var archer = b.State.Units.First(u => u.Side == 0);
            var pe = b.State.Units.First(u => u.Side == 1);
            b.Begin();
            b.State.Heroes[0].Links[Sk.PowerfulBlow] = archer.Id;
            b.State.Active = archer.Id;
            var r = b.Apply(melee ? new AttackCmd(archer.Id, pe.Id) { Side = 0 } : new ShootCmd(archer.Id, pe.Id) { Side = 0 });
            Assert.True(r.Ok, r.Error);
            return r.Events.Any(e => e is AttackStarted a && a.Kind == AttackKind.Hero);
        }
        Assert.True(HeroStruck(melee: true));
        Assert.False(HeroStruck(melee: false));
    }

    // exe FUN_008a4800 @ 008a4a94: with SPELL_ABILITY_BERSERKER_RAGE (0xf5) the attack adds FUN_008a22f0() — the stack's
    // defence computed at that moment; the defence getter FUN_008a32b0 returns 0 under the rage. The engine added a
    // snapshot of the defence taken when the rage started.
    [Fact]
    public void BerserkerRageAddsTheCurrentDefenceToAttack()
    {
        var b = Make("CREATURE_FOOTMAN", 10, "CREATURE_PEASANT", 100);
        var u = b.State.Units.First(x => x.Side == 0);
        u.Effects.Add(new Effect { Id = Fx.BerserkerRage, Value = 0, Value2 = 2, Positive = true, Dispellable = false });
        int a0 = b.AttackOf(u);
        Assert.Equal(0, b.DefenceOf(u));
        u.Effects.Add(new Effect { Id = "SPELL_STONESKIN", Value = 7, Positive = true });
        Assert.Equal(a0 + 7, b.AttackOf(u));
        Assert.Equal(0, b.DefenceOf(u));
    }
}
