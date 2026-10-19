using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Combat effects of the week of the month, confirmed against H5_Game.exe (docs/rules/audit/combat_heroes_skills_h.md).</summary>
[Collection("game")]
public class AuditWeekTests(GameDataFixture f)
{
    (Battle b, BattleState s) Setup(string week, params (string id, int count)[] theirs)
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = { new ArmySlot("CREATURE_FOOTMAN", 10) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = theirs.Select(a => new ArmySlot(a.id, a.count)).ToList() },
            7, skipTactics: true, arena: "Grass_Big_01", week: week);
        foreach (var h in s.Heroes) { h.Perks.Clear(); h.Skills.Clear(); h.Artifacts.Clear(); h.Specialization = ""; }
        return (new Battle(f.Db, s), s);
    }

    static Unit Of(BattleState s, string id) => s.Units.First(u => u.CreatureId == id);

    // exe 004bbfb0/004bc020 → 00bcdf10 (Might/Might and Magic: x + x/2 on the record's damage), 004bc470 → 00bcdf30
    // (Toughness: hp + hp/5), 004bb2a0 → 00bcdd80 flag 1 (Feebleness group 1 / Infirmity group 2: trunc(def·0.8 + 0.5)),
    // 004bc190 → 00bcdd40 (Idleness group 1 / Calm group 2: initiative ×0.8). Group by town, exe FUN_0045a0e0.
    [Fact]
    public void WeekChangesTheCreatureBaseStats()
    {
        var c = f.Db.Creature("CREATURE_FOOTMAN");
        Unit Foot(string week) => Of(Setup(week, ("CREATURE_IMP", 10)).s, "CREATURE_FOOTMAN");
        var n = Foot(MoonWeek.Nothing);   // hero bonuses are added on top of the week-adjusted record value
        Assert.Equal(n.BaseMinDamage + c.MinDamage / 2, Foot(MoonWeek.Might).BaseMinDamage);
        Assert.Equal(n.BaseMaxDamage + c.MaxDamage / 2, Foot(MoonWeek.MightNMagic).BaseMaxDamage);
        Assert.Equal(n.BaseHealth + c.Health / 5, Foot(MoonWeek.Toughness).BaseHealth);
        Assert.Equal(n.BaseDefence - c.Defence + (int)(c.Defence * 0.8f + 0.5f), Foot(MoonWeek.Feebleness).BaseDefence);   // Haven: group 1
        Assert.Equal(n.BaseDefence, Foot(MoonWeek.Infirmity).BaseDefence);                          // group 2 only
        Assert.Equal(n.BaseInitiative - c.Initiative + c.Initiative * 0.8f, Foot(MoonWeek.Idleness).BaseInitiative, 4);
        Assert.Equal(n.BaseInitiative, Foot(MoonWeek.Calm).BaseInitiative, 4);
        var imp = f.Db.Creature("CREATURE_IMP");
        Assert.Equal((int)(imp.Defence * 0.8f + 0.5f), Of(Setup(MoonWeek.Infirmity, ("CREATURE_IMP", 10)).s, "CREATURE_IMP").BaseDefence);
        Assert.Equal(n.BaseAttack, Foot(MoonWeek.Feebleness).BaseAttack);   // 004bb170 passes flag 0: attack never changes
    }

    // exe 00bcdc90 via 004ba970 (luck) / 004ba9d0 (morale): Sorrow −2 group 1, Light −2 group 2, Hope +1, Balance → 0;
    // under Balance 00a6acd0 / 00a6abf0 also skip the morale/luck effects (00989fd0 / 00989c40).
    [Fact]
    public void WeekChangesMoraleAndLuck()
    {
        int Morale(string week, string id) { var (b, s) = Setup(week, ("CREATURE_IMP", 10)); return b.MoraleOf(Of(s, id)); }
        int Luck(string week, string id) { var (b, s) = Setup(week, ("CREATURE_IMP", 10)); return b.LuckOf(Of(s, id)); }
        Assert.Equal(Morale(MoonWeek.Nothing, "CREATURE_FOOTMAN") - 2, Morale(MoonWeek.Sorrow, "CREATURE_FOOTMAN"));
        Assert.Equal(Morale(MoonWeek.Nothing, "CREATURE_IMP"), Morale(MoonWeek.Sorrow, "CREATURE_IMP"));
        Assert.Equal(Luck(MoonWeek.Nothing, "CREATURE_IMP") - 2, Luck(MoonWeek.Light, "CREATURE_IMP"));
        Assert.Equal(Luck(MoonWeek.Nothing, "CREATURE_FOOTMAN") + 1, Luck(MoonWeek.Hope, "CREATURE_FOOTMAN"));
        var (bb, sb) = Setup(MoonWeek.Balance, ("CREATURE_IMP", 10));
        sb.Heroes[0].Morale = 3; sb.Heroes[0].Luck = 2;
        sb.Heroes[1].Perks.Add("HERO_SKILL_DEAD_LUCK");   // a luck effect (00989c40): skipped under Balance
        Assert.Equal(0, bb.MoraleOf(Of(sb, "CREATURE_FOOTMAN")));
        Assert.Equal(0, bb.LuckOf(Of(sb, "CREATURE_FOOTMAN")));
    }

    // exe 00a34740 → 00bcddf0 (Magic / Might and Magic: cost/2, Ether: ×2, before the discounts), 00a6a700 → 00bcde10
    // (Evocation/Abjuration/Alteration/Conjuration: mastery > none → Expert), 00976b90 → 00bcde80 (element weeks
    // x + x/2 for that element, Chaos x + x/2, Fire and Ice ×2 for every spell).
    [Fact]
    public void WeekChangesSpellCostMasteryAndDamage()
    {
        var bolt = f.Db.Spell("SPELL_LIGHTNING_BOLT")!;
        var fireball = f.Db.Spell("SPELL_FIREBALL")!;
        var (b0, s0) = Setup(MoonWeek.Nothing, ("CREATURE_IMP", 10));
        var h0 = s0.Heroes[0];
        Assert.Equal(bolt.ManaCost / 2, Setup(MoonWeek.Magic, ("CREATURE_IMP", 10)).b.HeroManaCost(h0, bolt));
        Assert.Equal(bolt.ManaCost * 2, Setup(MoonWeek.Ether, ("CREATURE_IMP", 10)).b.HeroManaCost(h0, bolt));

        h0.Skills.Add(new SkillEntry("HERO_SKILL_DESTRUCTIVE_MAGIC", Mastery.Basic));
        var (be, se) = Setup(MoonWeek.Evocation, ("CREATURE_IMP", 10));
        Assert.Equal(Mastery.Expert, be.HeroMastery(h0, bolt));
        var none = se.Heroes[0];   // no Destructive Magic: stays at none
        Assert.Equal(Mastery.None, be.HeroMastery(none, bolt));
        Assert.Equal(Mastery.Basic, Setup(MoonWeek.Abjuration, ("CREATURE_IMP", 10)).b.HeroMastery(h0, bolt));

        int Base(string week, SpellDef sp) => (int)Setup(week, ("CREATURE_IMP", 10)).b.SpellBaseDamage(sp, sp, Mastery.Basic, 10);
        int boltBase = Base(MoonWeek.Nothing, bolt), fireBase = Base(MoonWeek.Nothing, fireball);
        Assert.Equal(boltBase + boltBase / 2, Base(MoonWeek.Air, bolt));
        Assert.Equal(boltBase, Base(MoonWeek.Fire, bolt));
        Assert.Equal(boltBase + boltBase / 2, Base(MoonWeek.Chaos, bolt));
        Assert.Equal(boltBase * 2, Base(MoonWeek.FireNIce, bolt));   // no element test in the exe
        Assert.Equal(fireBase * 2, Base(MoonWeek.FireNIce, fireball));
    }

    // exe 00bc1b90 @00bc1c78: the hero strike's base damage gets the Might weeks' x + x/2.
    [Fact]
    public void MightWeekRaisesTheHeroStrike()
    {
        var (b0, s0) = Setup(MoonWeek.Nothing, ("CREATURE_DEVIL", 10));
        var (b1, s1) = Setup(MoonWeek.Might, ("CREATURE_DEVIL", 10));
        int plain = b0.HeroStrikeDamage(s0.Heroes[0], Of(s0, "CREATURE_DEVIL"), 0);
        Assert.Equal(plain + plain / 2, b1.HeroStrikeDamage(s1.Heroes[0], Of(s1, "CREATURE_DEVIL"), 0));
    }
}
