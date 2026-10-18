using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>Hero strike rules from H5_Game.exe FUN_008cf270 / FUN_00bc1b90 (docs/rules/audit/combat_heroes.md).</summary>
[Collection("game")]
public class AuditHeroStrikeTests(GameDataFixture f)
{
    (Battle b, BattleState s) Setup()
    {
        var s = BattleSetup.Create(f.Db,
            new ArmySetup { Hero = f.Db.DuelPresets[0].Hero, Army = new() { new ArmySlot("CREATURE_FOOTMAN", 10) } },
            new ArmySetup { Hero = f.Db.DuelPresets[3].Hero, Army = new() { new ArmySlot("CREATURE_IMP", 50) } },
            7, skipTactics: true, arena: "Grass_Big_01");
        return (new Battle(f.Db, s), s);
    }

    // exe FUN_00bc1b90: Offender multiplies the strike by trunc(level x 0.05 + 1) (x87 truncation), applied after the
    // kills x health base and before the minimum of 1.
    [Fact]
    public void OffenderScalesTheStrikeByLevel()
    {
        var (b, s) = Setup();
        var h = s.Heroes[0];
        var imp = s.Units.First(u => u.CreatureId == "CREATURE_IMP");
        h.Specialization = "";
        int plain = b.HeroStrikeDamage(h, imp, 0);
        h.Specialization = "HERO_SPEC_OFFENDER";
        float per = (float)f.Db.Stats.Get("combat/HeroSpecializations/Barbarian/Offender_HeroAttackDamageBonusPerLevel", 0.05);
        Assert.Equal((int)(((float)h.Level * per + 1f) * plain), b.HeroStrikeDamage(h, imp, 0));
    }

    // exe 008cf406-008cf443: critical chance = CriticalStrike_ChanceToWork with Critical Strike, plus
    // PowerfullBlow_ChanceToCritical for Powerful Blow strikes, capped at 100. (The engine used 10% x 4 for Powerful Blow.)
    [Fact]
    public void CriticalChanceComposesCriticalStrikeAndPowerfulBlow()
    {
        var (b, s) = Setup();
        var h = s.Heroes[0];
        h.Perks.Remove("HERO_SKILL_CRITICAL_STRIKE");
        Assert.Equal(0, b.HeroCritChance(h, powerfulBlow: false));
        Assert.Equal(10, b.HeroCritChance(h, powerfulBlow: true));
        h.Perks.Add("HERO_SKILL_CRITICAL_STRIKE");
        Assert.Equal(30, b.HeroCritChance(h, powerfulBlow: false));
        Assert.Equal(40, b.HeroCritChance(h, powerfulBlow: true));
    }

    // exe FUN_00bc1b90: Deadeye Shot raises the PLAIN strike's level by 3 (HeroStrikeDamage with levelBonus 3).
    [Fact]
    public void DeadeyeShotAddsThreeLevelsToThePlainStrike()
    {
        var (b, s) = Setup();
        var h = s.Heroes[0];
        h.Perks.Remove("HERO_SKILL_CRITICAL_STRIKE");
        h.FavoriteEnemies.Clear();
        h.Perks.Add("HERO_SKILL_SNIPE_DEAD");
        var imp = s.Units.First(u => u.CreatureId == "CREATURE_IMP");
        int expected = b.HeroStrikeDamage(h, imp, 3);
        b.Begin();
        int before = imp.TotalHealth;
        // Advance to the hero's turn and strike.
        for (int i = 0; i < 50 && b.SideToAct is not null && !(b.State.Active == Actor.Hero(0)); i++)
        {
            var la = b.GetLegalActions();
            if (la == null) break;
            b.Apply(la.IsHero ? new WaitCmd(la.Actor) { Side = b.SideToAct!.Value } : new DefendCmd(la.Actor) { Side = b.SideToAct!.Value });
        }
        Assert.Equal(Actor.Hero(0), b.State.Active);
        before = imp.TotalHealth;
        Assert.True(b.Apply(new HeroAttackCmd(imp.Id) { Side = 0 }).Ok);
        Assert.Equal(expected, before - imp.TotalHealth);
    }
}
