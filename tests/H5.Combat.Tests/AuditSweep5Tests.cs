using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Racial and might perks confirmed against H5_Game.exe (docs/rules/audit/combat_sweep_5.md).</summary>
[Collection("game")]
public class AuditSweep5Tests(GameDataFixture f)
{
    Battle Make(int footmen, int imps, ulong seed = 7)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new() { new ArmySlot("CREATURE_FOOTMAN", footmen) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new() { new ArmySlot("CREATURE_IMP", imps) } },
            seed, skipTactics: true, arena: "Grass_Big_01");
        foreach (var h in s.Heroes) { h.Specialization = ""; h.Artifacts.Clear(); h.Perks.Clear(); h.SetPieces = new(); }
        return new Battle(f.Db, s);
    }

    static Unit Of(Battle b, string id) => b.State.Units.First(u => u.CreatureId == id);

    static void SideBySide(Battle b, Unit a, Unit t)
    {
        a.Pos = a.StartPos = new Cell(5, 5);
        t.Pos = t.StartPos = new Cell(6, 5);
    }

    // exe FUN_0085fe60 tail (0086048d..0086054f): n = floor(count·portion) + 1 when a battle-RNG float is below the
    // fraction; FUN_00861840 places nothing for n ≤ 0 (008619df) but still ends the caster's action. The engine always
    // gated at least one creature.
    [Fact]
    public void GatingRoundsRandomlyAndMayGateNothing()
    {
        int none = 0, one = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var b = Make(10, 1, seed);
            b.Begin();
            var h = b.State.Heroes[1];
            h.Skills = new() { new SkillEntry("HERO_SKILL_GATING", Mastery.Basic) };
            var imp = Of(b, "CREATURE_IMP");
            Assert.Equal(0.3, b.GatingPortion(imp, h), 6);
            b.State.Active = imp.Id;
            var r = b.Apply(new UseAbilityCmd(imp.Id, Ab.Gating) { Side = 1 });
            Assert.True(r.Ok, r.Error);
            int summoned = r.Events.OfType<UnitSummoned>().Sum(e => e.Count);
            Assert.InRange(summoned, 0, 1);
            if (summoned == 0) none++; else one++;
            Assert.Contains(r.Events, e => e is AtbChanged a && a.Actor == imp.Id && Math.Abs(a.Atb - 0.5) < 1e-6);   // the action ended either way
        }
        Assert.True(none > 15 && one > 3, $"none {none}, one {one}");
    }

    // exe FUN_00861840: the caster's ATB is clamp(Quick/Caller StartATB, 0, 0.999) (00861930 / 00861971); the gated stack
    // starts at 0, or at 0x3f7fffef ("Gating immedately", 00861b6a) with Urgash's Call (HERO_SKILL_ABSOLUTE_GATING 0x62).
    [Fact]
    public void GatingAtbFollowsQuickAndAbsoluteGating()
    {
        var b = Make(10, 10);
        var h = b.State.Heroes[1];
        Assert.Equal(0.5, b.GatingCallerAtb(h), 6);
        Assert.Equal(0.0, Battle.GatedStartAtb(h));
        h.Perks.Add(Sk.QuickGating);
        h.Perks.Add("HERO_SKILL_ABSOLUTE_GATING");
        Assert.Equal(0.75, b.GatingCallerAtb(h), 6);
        Assert.Equal(BitConverter.Int32BitsToSingle(0x3f7fffef), Battle.GatedStartAtb(h));
    }

    // exe FUN_00a5ed90: Fiery Wrath's 0.1·damage and Hellfire's Base + PerSP·SP are ONE fire hit through FUN_0097d740
    // (Fireball protections, rounded half up), Searing Fires ×1.5 (00e3a3ac) on the sum; Hellfire needs mana ≥ cost and an
    // ability roll of 30 (EDX = 0x1e, Range(0,100) < 30) and charges the cost. The engine dealt Hellfire separately with
    // a continuous 30 % chance, through the spell damage path.
    [Fact]
    public void HellfireIsOneFireHitWithFieryWrath()
    {
        var b = Make(10, 500);
        var foot = Of(b, "CREATURE_FOOTMAN"); var imp = Of(b, "CREATURE_IMP");
        var h = b.State.Heroes[0];
        h.Perks.AddRange(new[] { Sk.FieryWrath, Sk.Hellfire, Sk.SearingFires });
        h.SpellPower = 3;
        int procs = 0;
        int plain = b.PerkElementalAmount(100 * 0.1f, imp, "SPELL_FIREBALL", simulate: true);
        int full = b.PerkElementalAmount((100 * 0.1f + 10 + 10 * 3) * 1.5f, imp, "SPELL_FIREBALL", simulate: true);
        for (int i = 0; i < 400; i++)
        {
            h.Mana = 1000;
            int before = imp.TotalHealth;
            b.FieryWrathAndHellfire(foot, imp, h, 100, false);
            int dealt = before - imp.TotalHealth;
            if (h.Mana < 1000) { procs++; Assert.Equal(995, h.Mana); Assert.Equal(full, dealt); }
            else Assert.Equal(plain, dealt);
            imp.Count = imp.InitialCount; imp.TopHealth = imp.MaxHealthCache;
        }
        Assert.InRange(procs, 80, 160);   // 30/101 of 400 ≈ 119
        h.Mana = 4;
        for (int i = 0; i < 50; i++)
        {
            b.FieryWrathAndHellfire(foot, imp, h, 100, false);
            Assert.Equal(4, h.Mana);
        }
    }

    // exe FUN_00bc1b90 case 0x35 (00bc1dc9..00bc1e5f): base at level+3, Cavalry Commander trunc((p·level + 1)·dmg),
    // Unstoppable Charge ×3, and only then the week of Might (00bc1c78) and Offender. The engine multiplied after the week.
    [Fact]
    public void HolyChargeMultipliesBeforeTheWeekOfMight()
    {
        var b = Make(10, 500);
        var imp = Of(b, "CREATURE_IMP");
        var h = b.State.Heroes[0];
        h.Level = 10;
        h.Specialization = "HERO_SPEC_JOUSTING";
        h.Perks.Add(Sk.UnstoppableCharge);
        b.State.Week = MoonWeek.Might;
        int baseDmg = Math.Max(1, (int)Math.Floor(b.HeroKills(13, imp.Tier) * imp.MaxHealthCache));
        int joust = (int)(((double)0.01f * 10 + 1.0) * baseDmg);
        Assert.Equal(MoonWeek.MightDamage(joust * 3, MoonWeek.Might), b.HeroStrikeDamage(h, imp, 3, holyCharge: true));
    }

    // exe FUN_00a673c0 @ 00a68686 (and the Chain Shot exit 00a5a120): the Demon Lord strikes the marked stack AFTER each
    // of its strikes (special 0x9b excepted); the engine struck before the attack.
    [Fact]
    public void MarkOfTheDamnedStrikesAfterTheMarkedStacksBlow()
    {
        var b = Make(10, 500);
        b.Begin();
        var foot = Of(b, "CREATURE_FOOTMAN"); var imp = Of(b, "CREATURE_IMP");
        SideBySide(b, foot, imp);
        b.State.Heroes[1].Links[Sk.MarkOfDamned] = foot.Id;
        b.State.Active = foot.Id;
        var r = b.Apply(new AttackCmd(foot.Id, imp.Id, foot.Pos) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        var ev = r.Events;
        int firstBlow = ev.FindIndex(e => e is Damaged d && d.Source == foot.Id);
        int mark = ev.FindIndex(e => e is AbilityTriggered a && a.Ability == Sk.MarkOfDamned);
        Assert.True(firstBlow >= 0 && mark > firstBlow, $"blow {firstBlow}, mark {mark}");
    }

    // exe 008a65e0 (Defend, from 008a6be0): with Preparation the stack gets SPELL_EFFECT_PREPARATION (0x11e); FUN_00a64e90
    // answers the first strike before it and removes the effect (00a64fa0), so later attackers get no early answer.
    [Fact]
    public void PreparationIsSpentByTheFirstAttack()
    {
        var b = Make(10, 500);
        b.Begin();
        var foot = Of(b, "CREATURE_FOOTMAN"); var imp = Of(b, "CREATURE_IMP");
        SideBySide(b, foot, imp);
        b.State.Heroes[1].Perks.Add(Sk.Preparation);
        b.State.Active = imp.Id;
        Assert.True(b.Apply(new DefendCmd(imp.Id) { Side = 1 }).Ok);
        Assert.True(imp.HasEffect(Battle.PreparationEffect));
        b.State.Active = foot.Id;
        var r = b.Apply(new AttackCmd(foot.Id, imp.Id, foot.Pos) { Side = 0 });
        Assert.True(r.Ok, r.Error);
        var first = r.Events.OfType<Damaged>().First();
        Assert.Equal(imp.Id, first.Source);
        Assert.False(imp.HasEffect(Battle.PreparationEffect));
    }

    // exe FUN_00a5ae40: ability roll (no attacker) of 40, 50 with Cunning of the Woods; exe FUN_00978e10: 10 + 2·level for
    // creatures 0x51/0x52 only.
    [Fact]
    public void AvengerAndMatronSalvoUseIntegerChances()
    {
        var b = Make(10, 10);
        var h = b.State.Heroes[0];
        Assert.Equal(40, Battle.AvengerCritChance(h));
        h.Perks.Add("HERO_SKILL_CUNNING_OF_THE_WOODS");
        Assert.Equal(50, Battle.AvengerCritChance(h));
        h.Level = 12;
        Assert.Equal(34, Battle.MatronSalvoChance(h));
    }
}
