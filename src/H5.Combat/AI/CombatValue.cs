namespace H5.Combat.AI;

/// <summary>
/// One side's half of the original combat AI's position value (exe: 0x48-byte block filled by
/// FUN_00bfbe50 / per-unit FUN_00bfba50, printed by FUN_00bfa810 as
/// "Damage, AttackLevel, Bonus. TroopsHealth, TroopsDefence. Health, Defence").
/// Field comments give the dword index in that block.
/// </summary>
public sealed class SideValue
{
    /// <summary>[0] flat extras (ability objects). Not modelled yet: always 0.</summary>
    public double Extra;
    /// <summary>[1] hero strike potential, split by the enemy's walker damage share in <see cref="DamageMeasure"/>.</summary>
    public double HeroPotential;
    /// <summary>[2] spell potential (FUN_00bfa360). Not modelled yet: always 0.</summary>
    public double SpellPotential;
    /// <summary>[3] expected damage per round of all stacks.</summary>
    public double Damage;
    /// <summary>[4] damage-weighted mean of (Attack + 1).</summary>
    public double AttackLevel;
    /// <summary>[5] damage-weighted luck/morale multiplier (starts at 1).</summary>
    public double Bonus = 1;
    /// <summary>[6] hit points of everything on the side (creatures and war machines).</summary>
    public double Health;
    /// <summary>[7] HP-weighted mean of (Defence + 1) over <see cref="Health"/>.</summary>
    public double Defence;
    /// <summary>[8] hit points of creature stacks only.</summary>
    public double TroopsHealth;
    /// <summary>[9] HP-weighted mean of (Defence + 1) over <see cref="TroopsHealth"/>.</summary>
    public double TroopsDefence;
    /// <summary>[10]/[11] shooters' damage·bonus and their damage-weighted (Attack+1).</summary>
    public double ShooterDamage, ShooterAttack;
    /// <summary>[12]/[13] flyers' damage·bonus and attack.</summary>
    public double FlyerDamage, FlyerAttack;
    /// <summary>[14]/[15] walkers' damage·bonus and attack.</summary>
    public double WalkerDamage, WalkerAttack;
    /// <summary>[16] per-stack additive term (FUN_00bfa610). Not modelled yet: always 0.</summary>
    public double Additive;

    /// <summary>The engine's attack/defence damage factor (exe FUN_00a58d30): 1+0.05(A−D) or 1/(1+0.05(D−A)).</summary>
    public static double Factor(double a, double d) => a > d ? 1 + 0.05 * (a - d) : 1 / (1 + 0.05 * (d - a));

    static int Round(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

    /// <summary>Shooter power vs another side (exe FUN_00bf9cc0).</summary>
    public int ShooterPower(SideValue vs) => Round(Factor(ShooterAttack, Round(vs.TroopsDefence)) * ShooterDamage * Bonus);

    /// <summary>Damage measure vs the other side (exe FUN_00bfc4a0).</summary>
    public int DamageMeasure(SideValue other)
    {
        int dmg = Round(Factor(AttackLevel, 1.0) * Damage * Bonus);
        int hero = 0;
        double total = other.ShooterDamage + other.WalkerDamage + other.FlyerDamage;
        if (ShooterPower(other) < other.ShooterPower(this) && total > 0.1)
            hero = Round(other.WalkerDamage / total * HeroPotential);
        double sum = Extra + SpellPotential + hero + dmg;
        if (sum < 0 || sum > 0x70000000) return 0;
        return (int)(Additive + sum);
    }

    /// <summary>Effective creature HP (exe FUN_00bf9a50): TroopsHealth · max(0.1, 1/factor(1, TroopsDefence)).</summary>
    public int TroopsMeasure() => Round(TroopsHealth * Math.Max(0.1, 1 / Factor(1.0, TroopsDefence)));

    /// <summary>Effective war-machine HP (exe FUN_00bf9ab0).</summary>
    public int WarMachinesMeasure() => Round((Health - TroopsHealth) * Math.Max(0.1, 1 / Factor(1.0, TroopsDefence)));

    /// <summary>Overall power vs the other side, used for the mode ratio (exe FUN_00bf9b80).</summary>
    public int Power(SideValue other)
    {
        double k = Math.Max(0.1, 1 / Factor(Round(other.AttackLevel), Defence));
        double g = Math.Max(1.0, Factor(AttackLevel, Round(other.TroopsDefence)) * Bonus);
        return Round(Health * k + Damage * g + (SpellPotential + Extra));
    }
}

/// <summary>Both halves of a position value, from one side's point of view.</summary>
public sealed class CombatValue
{
    public required SideValue My { get; init; }
    public required SideValue Enemy { get; init; }

    public int MyPower => My.Power(Enemy);
    public int EnemyPower => Enemy.Power(My);

    /// <summary>
    /// True when <paramref name="x"/> is a better position than <paramref name="y"/>: exe FUN_00bfae20(this = y, arg = x),
    /// which answers "is the argument better" (the comparator FUN_008d0030 and the start-value exclusion call it so).
    /// The value's first half is the enemy (FUN_00bfc2a0 fills it with combat vf 0xe0(side)), the second (+0x48) our side.
    /// Outside kamikaze mode (value +0x90 → +0x18 ≠ 1) a position that has lost all OWN troops (+0x68, [8] of our half)
    /// is worse than one that has not (0x00bfaf33..0x00bfaf57). Otherwise the enemy-minus-own changes in damage, troop
    /// HP and war-machine HP are weighted and summed; x wins when that total exceeds threshold·total,
    /// threshold = clamp(CompareTreshold·0.01, 0, 1).
    /// </summary>
    public static bool IsBetter(CombatValue x, CombatValue y, CombatModeCoefs c, CombatMode mode)
    {
        if (mode != CombatMode.Kamikaze)
        {
            bool xLost = x.My.TroopsHealth <= 0, yLost = y.My.TroopsHealth <= 0;
            if (yLost && !xLost) return true;
            if (xLost && !yLost) return false;
        }
        double enemyDmg = y.Enemy.DamageMeasure(y.My) - x.Enemy.DamageMeasure(x.My);
        double myDmg = y.My.DamageMeasure(y.Enemy) - x.My.DamageMeasure(x.Enemy);
        double enemyHp = y.Enemy.TroopsMeasure() - x.Enemy.TroopsMeasure();
        double myHp = y.My.TroopsMeasure() - x.My.TroopsMeasure();
        double enemyWm = y.Enemy.WarMachinesMeasure() - x.Enemy.WarMachinesMeasure();
        double myWm = y.My.WarMachinesMeasure() - x.My.WarMachinesMeasure();
        double total = (enemyDmg * c.EnemyDamage - myDmg * c.MyDamage)
                     + (enemyHp * c.EnemyTroopsHealth - myHp * c.MyTroopsHealth)
                     + (enemyWm * c.EnemyWarMachinesHealth - myWm * c.MyWarMachinesHealth);
        double t = Math.Clamp(c.CompareThreshold * 0.01, 0, 1);
        return total > t * total;
    }

    /// <summary>A scalar for logging/tie inspection: weighted own-minus-enemy measures.</summary>
    public double Score(CombatModeCoefs c) =>
        My.DamageMeasure(Enemy) * c.MyDamage - Enemy.DamageMeasure(My) * c.EnemyDamage
        + My.TroopsMeasure() * c.MyTroopsHealth - Enemy.TroopsMeasure() * c.EnemyTroopsHealth
        + My.WarMachinesMeasure() * c.MyWarMachinesHealth - Enemy.WarMachinesMeasure() * c.EnemyWarMachinesHealth;

    /// <summary>Builds the value of the battle's current position from <paramref name="side"/>'s point of view.</summary>
    public static CombatValue Of(Battle b, int side) => new() { My = Fill(b, side), Enemy = Fill(b, 1 - side) };

    /// <summary>Per-unit accumulation (exe FUN_00bfba50) followed by the averaging pass (FUN_00bf9ee0).</summary>
    static SideValue Fill(Battle b, int side)
    {
        var s = b.State;
        var v = new SideValue();
        double accAtk = 0, accBonus = 0, accDef = 0, accTroopDef = 0, accShoot = 0, accFly = 0, accWalk = 0;
        var enemies = s.Living.Where(e => b.Controller(e) != side && !e.IsObject).ToList();
        int unitCount = s.Living.Count(u => !u.IsObject);
        foreach (var u in s.Living)
        {
            if (u.IsObject || b.Controller(u) != side) continue;
            var (min, max) = b.DamageRangeOf(u);
            double dmg = u.Count * (min + max) * 0.5;
            bool shooter = b.CanShoot(u);
            if (shooter) dmg = Math.Round((1 - ThreatShare(b, u, enemies, unitCount) + 1) * dmg);
            int atk = b.AttackOf(u);
            v.Damage += dmg;
            accAtk += (atk + 1) * dmg;
            double bonusDmg = dmg * LuckMoraleBonus(b, u);
            accBonus += bonusDmg;
            if (!u.IsWarMachine || shooter)
            {
                if (shooter) { v.ShooterDamage += bonusDmg; v.ShooterAttack += (atk + 1) * dmg; accShoot += dmg; }
                else if (u.Flying) { v.FlyerDamage += bonusDmg; v.FlyerAttack += (atk + 1) * dmg; accFly += dmg; }
                else { v.WalkerDamage += bonusDmg; v.WalkerAttack += (atk + 1) * dmg; accWalk += dmg; }
            }
            double hp = u.TotalHealth;
            int def = b.DefenceOf(u);
            v.Health += hp;
            accDef += (def + 1) * hp;
            if (!u.IsWarMachine)
            {
                v.TroopsHealth += hp;
                accTroopDef += (def + 1) * hp;
            }
        }
        if (v.Damage > 0) { v.AttackLevel = accAtk / v.Damage; v.Bonus = accBonus / v.Damage; }
        if (v.Health > 0) v.Defence = accDef / v.Health;
        if (v.TroopsHealth > 0) v.TroopsDefence = accTroopDef / v.TroopsHealth;
        if (accShoot > 0) v.ShooterAttack /= accShoot;
        if (accFly > 0) v.FlyerAttack /= accFly;
        if (accWalk > 0) v.WalkerAttack /= accWalk;
        v.HeroPotential = HeroStrikePotential(b, side, enemies);
        return v;
    }

    /// <summary>
    /// FUN_00bfb950, the shooter factor f of (2 − f)·damage: 1 for a war machine or with fewer than two units on the field;
    /// otherwise the share, weighted by expected damage (FUN_00bfb880), of the enemy stacks that cannot shoot and
    /// threaten the shooter (FUN_004b1660 on the matrix FUN_004b23b0 builds) among all enemy stacks that cannot shoot,
    /// 1 when there are none. An unthreatened shooter counts double. GAP-detail: the threat matrix is not decoded; ours
    /// counts a stack as threatening when it can come next to the shooter within its speed.
    /// </summary>
    static double ThreatShare(Battle b, Unit u, List<Unit> enemies, int unitCount)
    {
        if (u.IsWarMachine || unitCount < 2) return 1;
        double total = 0, threat = 0;
        foreach (var e in enemies)
        {
            if (b.CanShoot(e)) continue;
            var (min, max) = b.DamageRangeOf(e);
            double w = e.Count * (min + max) * 0.5;
            total += w;
            if (Battle.Distance(e, u) <= b.SpeedOf(e) + 1) threat += w;
        }
        return total != 0 ? threat / total : 1;
    }

    /// <summary>Expected multiplier from luck (×2 / ×0.5) and morale (extra / lost half turn), 10% per point, capped at 50%.</summary>
    static double LuckMoraleBonus(Battle b, Unit u)
    {
        int luck = b.LuckOf(u), morale = b.MoraleOf(u);
        double pl = Math.Min(0.5, 0.1 * Math.Abs(luck)), pm = Math.Min(0.5, 0.1 * Math.Abs(morale));
        double l = luck >= 0 ? 1 + pl : 1 - 0.5 * pl;
        double m = morale >= 0 ? 1 + 0.5 * pm : 1 - 0.5 * pm;
        return l * m;
    }

    static double HeroStrikePotential(Battle b, int side, List<Unit> enemies)
    {
        var h = b.State.Heroes[side];
        if (!h.Present) return 0;
        var targets = enemies.Where(e => !e.IsWarMachine).ToList();
        if (targets.Count == 0) return 0;
        return targets.Average(t => (double)b.HeroStrikeDamage(h, t, 0));
    }
}
