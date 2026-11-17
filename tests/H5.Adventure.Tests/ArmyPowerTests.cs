using H5.Adventure.State;
using MoonWeek = H5.Combat.MoonWeek;
using H5.Core.Numerics;

namespace H5.Adventure.Tests;

/// <summary>
/// Army power of H5_Game.exe: stack value 004e9b90, army sum 004eaaa0, hero / army ratio 004eabb0 and the join decision's
/// ratio (008e1d75, Courtier 008e1da7). Expected values follow the exe's x87 steps (24-bit chop, ftol) on stock data
/// (Peasant: Power 41, Skeleton: Power 54; AI/Adventure CreatureOverHealthCoef 0.1, OverDamageCoef 0.5, Good luck / morale
/// 0.0173, BadMoraleCoef 0.0833).
/// </summary>
[Collection("adv")]
public class ArmyPowerTests(Fixture f)
{
    static HeroState Hero() => new() { Level = 1, InTown = -1 };

    [StockFact]
    public void A_stack_without_bonuses_is_power_times_100_times_count()
    {
        var g = f.NewGame();
        g.S.MoonWeek = MoonWeek.Nothing;
        // 004e9bdc: ftol(max(41, 1) · 100.0) = 4100; every delta term is 0, every factor 1.0; × count 100.
        Assert.Equal(410000, g.StackPower(new Stack("CREATURE_PEASANT", 100), null));
        // 004e9bb6: no creature record → 1, not multiplied by the count.
        Assert.Equal(1, g.StackPower(new Stack("CREATURE_NO_SUCH", 100), null));
        // 004eaaa0: the sum skips empty stacks.
        Assert.Equal(410000 + 1, g.ArmyPowerSum(new Stack?[] { new("CREATURE_PEASANT", 100), null, new("CREATURE_NO_SUCH", 3), new("CREATURE_PEASANT", 0) }, null));
    }

    [StockFact]
    public void The_week_changes_luck_and_morale_but_undead_keep_morale_zero()
    {
        var g = f.NewGame();
        g.S.MoonWeek = MoonWeek.Hope;   // 00bcdc90: luck and morale +1
        // 004e9e1d: ftol(4100 · chop(1 + chop(0.0173f))) = 4170; 004e9ecd: ftol(4170 · 1.0173) = 4242; × 100.
        Assert.Equal(424200, g.StackPower(new Stack("CREATURE_PEASANT", 100), null));
        // 004ba9d0: ABILITY_UNDEAD → morale 0; luck +1: ftol(5400 · 1.0173) = 5493; × 30.
        Assert.Equal(164790, g.StackPower(new Stack("CREATURE_SKELETON", 30), null));
    }

    [StockFact]
    public void Hero_bonuses_enter_through_the_hit_point_and_damage_getters_and_the_tavern_morale_cancels()
    {
        var g = f.NewGame();
        g.S.MoonWeek = MoonWeek.Nothing;
        var h = Hero();
        h.Perks.Add("HERO_SKILL_TOUGHNESS");   // 004bc470: +2 hit points → ftol(4100 + chop(2 · 0.1f)) = 4100
        h.Perks.Add("HERO_SKILL_FRENZY");      // 004bbfb0 / 004bc020: +1 min and max → ftol(4100 + 2 · 0.5f) = 4101
        Assert.Equal(410100, g.StackPower(new Stack("CREATURE_PEASANT", 100), h));
        // The hero's own morale / luck do not count (004ba9d0 / 004ba970 read the stack and the week only).
        h.Perks.Clear();
        h.NextBattleMorale = 3;
        Assert.Equal(410000, g.StackPower(new Stack("CREATURE_PEASANT", 100), h));
        // 004e9dcb: the hero's town (vf 0x54) has a tavern (vf 0x34(4)) → morale −1, but the army's morale base (+0x28,
        // 0045b7b0) has + 1 for the same tavern: they cancel (−1 alone would give ftol(4100 · chop(1 − chop(0.0833f))) = 3758).
        var t = g.S.Towns.First();
        t.Built["TB_TAVERN"] = 1;
        h.InTown = t.Object;
        Assert.Equal(410000, g.StackPower(new Stack("CREATURE_PEASANT", 100), h));
    }

    [StockFact]
    public void The_join_ratio_divides_the_sums_and_a_courtier_scales_it()
    {
        var g = f.NewGame();
        g.S.MoonWeek = MoonWeek.Nothing;
        var h = Hero();
        h.Army = Army.Empty();
        h.Army[0] = new Stack("CREATURE_PEASANT", 100);
        var guards = new List<Stack> { new("CREATURE_SKELETON", 30) };
        // 004eabb0: FILD 410000 / FILD 162000 (FDIVR), chopped.
        Assert.Equal(X87.Div(410000, 162000), g.ArmyPowerRatio(h, guards));
        Assert.Equal(2.530864f, g.StrengthRatio(h, guards));
        // 008e1da7: × chop(chop(5 · Courtier_DiplomacyBonusPerLevel 0.1f) + 1.0f).
        h.Specialization = "HERO_SPEC_COURTIER";
        h.Level = 5;
        Assert.Equal(3.7962959f, g.StrengthRatio(h, guards));
    }
}
