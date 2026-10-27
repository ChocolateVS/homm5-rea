using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Sweep 10 of the combat audit: GAP rows closed against H5_Game.exe (docs/rules/audit/combat_sweep_10.md).</summary>
[Collection("game")]
public class AuditSweep10Tests(GameDataFixture f)
{
    static Unit Add(Battle b, int side, string id, int n, Cell at) => BattleSetup.AddCreature(b.Db, b.State, side, id, n, at, summoned: false);

    static ApplyResult Use(Battle b, Unit u, string ability, Unit? t = null, Cell? cell = null)
    {
        b.State.Active = u.Id;
        return b.Apply(new UseAbilityCmd(u.Id, ability) { Side = u.Side, TargetId = t?.Id, Cell = cell });
    }

    (Battle b, Unit a, Unit t) Make(string mine, int countA, string theirs, int countB, Cell posA, Cell posB)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new() { new ArmySlot(mine, countA) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new() { new ArmySlot(theirs, countB) } },
            7, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); }
        foreach (var u in s.Units.Where(u => u.IsWarMachine).ToList()) s.Units.Remove(u);
        var a = s.Units.First(u => u.Side == 0 && u.CreatureId == mine);
        var t = s.Units.First(u => u.Side == 1 && u.CreatureId == theirs);
        a.Pos = a.StartPos = posA;
        t.Pos = t.StartPos = posB;
        return (new Battle(f.Db, s), a, t);
    }

    // exe FUN_009789b0 case 0xf1 @ 00978be8..00978bf2: FUN_00a34ca0 is called with ECX = 0x30 (SPELL_RESURRECT) and
    // EDX = 3 (Expert) — Resurrect's damage table at Expert with the hero's level as power (Base 240, PerPower 30), not
    // SPELL_EFFECT_GUARDIAN_ANGEL's own 1 + 1·level table (sweep 9 read the wrong record).
    [Fact]
    public void GuardianAngelUsesResurrectsExpertTable()
    {
        var (b, _, _) = Make("CREATURE_PEASANT", 10, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        var h = b.State.Heroes[0];
        h.Level = 12;
        var res = f.Db.Spell("SPELL_RESURRECT")!;
        Assert.Equal((int)Math.Round(res.DamageAt(Mastery.Expert).Eval(12)), b.GuardianAngelAmount(h));
        Assert.Equal(240 + 30 * 12, b.GuardianAngelAmount(h));
    }

    // exe FUN_00860660 → FUN_008605a0, FUN_00861d30 (c56750 with creature 0x1a at the chosen cell): the Balors appear at
    // the corpse — for a small corpse the 2-cell placement nearest to it (FUN_00b3e1e0: the corpse's own anchor when it
    // fits, else the smallest squared distance). The engine put them next to the army's edge of the field.
    [Fact]
    public void BalorsAppearAtTheCorpse()
    {
        var b = f.Duel("CREATURE_ARCHDEVIL", 3, "CREATURE_PEASANT", 50, 1, new Cell(5, 5), new Cell(14, 1));
        var imps = Add(b, 0, "CREATURE_IMP", 200, new Cell(9, 7));
        b.Begin();
        imps.Count = 0;
        var u = b.State.Units[0];
        var r = Use(b, u, Ab.BalorSummoning, imps);
        Assert.True(r.Ok, r.Error);
        var balor = b.State.Units.Single(x => x.CreatureId == "CREATURE_BALOR");
        Assert.Equal(new Cell(9, 7), balor.Pos);
    }

    // exe FUN_00b3e1e0: when the wanted anchor is taken, the free anchors at the smallest squared distance, first in
    // row-major order (y outer) without an RNG.
    [Fact]
    public void ExePlacementTakesTheNearestFreeAnchorRowMajor()
    {
        var b = f.Duel("CREATURE_PEASANT", 3, "CREATURE_PEASANT", 50, 1, new Cell(5, 5), new Cell(14, 1));
        b.Begin();
        var probe = new Unit { Id = -1, Size = 1 };
        Assert.Equal(new Cell(5, 4), b.ExePlacement(probe, new Cell(5, 5), null));   // (5,4), (4,5), (6,5), (5,6) tie: row 4 first
    }

    // exe FUN_0085b6d0: round(MoatMaxDamage · town factor) with FPU rounding (half to even): 170 · 0.75 = 127.5 → 128
    // (the engine truncated to 127).
    [Fact]
    public void MoatDamageIsRoundedNotTruncated()
    {
        var attacker = new ArmySetup { Army = { new ArmySlot("CREATURE_ARCHER", 30) } };
        var defender = new ArmySetup { Army = { new ArmySlot("CREATURE_ARCHER", 30) } };
        var s = BattleSetup.Create(f.Db, attacker, defender, 3, skipTactics: true, siege: new SiegeSetup
        {
            Town = "TOWN_PRESERVE", FortLevel = 2, TownLevel = 3, TownMaxLevel = 4,
        });
        Assert.Equal(170, f.Db.Moats["TOWN_PRESERVE"].MaxDamage);
        Assert.Equal(128, s.Siege!.MoatDamage);
    }

    // exe FUN_00984a00 common tail 00984cb6 after case 0x34: the hero's vf 0x1cc (FUN_00412b40, constant 0 on the combat
    // hero vtable 00e8499c) is false, so its ATB is set to 0 (vf 0x184 → FUN_00a2d6a0(0)) — the engine's 0.
    [Fact]
    public void EncourageLeavesTheHeroAtZeroAtb()
    {
        var (b, u, _) = Make("CREATURE_PEASANT", 10, "CREATURE_PEASANT", 10, new Cell(1, 1), new Cell(14, 10));
        var h = b.State.Heroes[0];
        h.Perks.Add(Sk.DivineGuidance);
        b.Begin();
        b.State.Active = Actor.Hero(0);
        h.Atb = 1.1;
        var r = b.Apply(new UseAbilityCmd(Actor.Hero(0), "DIVINE_GUIDANCE") { Side = 0, TargetId = u.Id });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(0, h.Atb, 6);
    }
}
