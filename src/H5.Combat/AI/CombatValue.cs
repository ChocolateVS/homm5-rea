namespace H5.Combat.AI;

/// <summary>
/// One side's half of the original combat AI's position value (exe: 0x48-byte block filled by
/// FUN_00bfbe50 / per-unit FUN_00bfba50, printed by FUN_00bfa810 as
/// "Damage, AttackLevel, Bonus. TroopsHealth, TroopsDefence. Health, Defence").
/// Field comments give the dword index in that block.
/// </summary>
public sealed class SideValue
{
    /// <summary>[0] flat extras (the unit vf 0x84 object; null for every unit class, so always 0).</summary>
    public double Extra;
    /// <summary>[1] hero strike potential, split by the enemy's walker damage share in <see cref="DamageMeasure"/>.</summary>
    public double HeroPotential;
    /// <summary>[2] spell potential (FUN_00bfa360 of the walkers, flyers and the hero; an int slot).</summary>
    public double SpellPotential;
    /// <summary>[3] expected damage per round of all stacks.</summary>
    public double Damage;
    /// <summary>[4] damage-weighted mean of (Attack + 1).</summary>
    public double AttackLevel;
    /// <summary>[5] damage-weighted bonus factor FUN_00bfb4b0 (starts at 1).</summary>
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
    /// <summary>[16] the other army's moat damage (FUN_00bfa610, <see cref="CombatValue.MoatShare"/>).</summary>
    public double Additive;

    /// <summary>The engine's attack/defence damage factor (exe FUN_00a58d30): 1+0.05(A−D) or 1/(1+0.05(D−A)).</summary>
    public static double Factor(double a, double d) => a > d ? 1 + 0.05 * (a - d) : 1 / (1 + 0.05 * (d - a));

    // The measures below are x87 code under chop: every float → int conversion truncates (FISTP with RC = 11), and the
    // float slots read as ints (TroopsDefence, AttackLevel) are truncated before use. The 0.1 floors are the float 0.1f (00e12f10).
    const double Floor01 = 0.1f;
    static int T(double v) => (int)v;

    /// <summary>Shooter power vs another side (exe FUN_00bf9cc0): trunc(factor([11], trunc(vs [9])) · [10] · [5]).</summary>
    public int ShooterPower(SideValue vs) => T(Factor(ShooterAttack, T(vs.TroopsDefence)) * ShooterDamage * Bonus);

    /// <summary>The same with the flat extras (exe FUN_00bf9d30): trunc(factor([11], trunc(vs [9])) · [10] · [5] + ([0] + [2])).</summary>
    public int ShooterPowerWithExtras(SideValue vs) => T(Factor(ShooterAttack, T(vs.TroopsDefence)) * ShooterDamage * Bonus + (int)(Extra + SpellPotential));

    /// <summary>FUN_00bf9eb0 on (enemy, own): true when our shooters out-shoot the enemy's (FUN_00bf9d30 both ways, own &gt; enemy).</summary>
    public static bool OutShoots(SideValue own, SideValue enemy) => own.ShooterPowerWithExtras(enemy) > enemy.ShooterPowerWithExtras(own);

    /// <summary>
    /// Damage measure vs the other side (exe FUN_00bfc4a0): dmg = trunc(factor([4], 1) · [3] · [5]); when THIS side
    /// out-shoots the other (FUN_00bf9cc0 this vs other &gt; other vs this) and the other's [12] + [14] + [10] &gt; 0.1,
    /// hero = trunc(other [14] / that total · [1]); sum = [0] + [2] + hero + dmg; 0 when sum &lt; 0 or &gt; 0x70000000,
    /// else [16] + sum.
    /// </summary>
    public int DamageMeasure(SideValue other)
    {
        int dmg = T(Factor(AttackLevel, 1.0) * Damage * Bonus);
        int hero = 0;
        float total = (float)(other.FlyerDamage + other.WalkerDamage + other.ShooterDamage);
        if (ShooterPower(other) > other.ShooterPower(this) && total > 0.1f)
            hero = T(other.WalkerDamage / total * HeroPotential);
        long sum = (long)Extra + (long)SpellPotential + hero + dmg;
        if (sum < 0 || sum > 0x70000000) return 0;
        return (int)((long)Additive + sum);
    }

    /// <summary>
    /// Damage vs the other side (exe FUN_00bfa1b0, the mana limit's "Our Damage"): trunc(factor([4], trunc(other [9]))
    /// · [3] · [5]) + the hero share when we out-shoot (no 0.1 floor) + [0] + [16].
    /// </summary>
    public int DamageVs(SideValue other)
    {
        int dmg = T(Factor(AttackLevel, T(other.TroopsDefence)) * Damage * Bonus);
        int hero = 0;
        if (other.ShooterPower(this) < ShooterPower(other))
            hero = T(other.WalkerDamage / (float)(other.FlyerDamage + other.ShooterDamage + other.WalkerDamage) * HeroPotential);
        return (int)Additive + (int)Extra + hero + dmg;
    }

    /// <summary>Effective creature HP (exe FUN_00bf9a50): trunc([8] · max(0.1, 1/factor(1, [9]))).</summary>
    public int TroopsMeasure() => T(TroopsHealth * Math.Max(Floor01, 1 / Factor(1.0, TroopsDefence)));

    /// <summary>Effective war-machine HP (exe FUN_00bf9ab0): trunc(([6] − [8]) · max(0.1, 1/factor(1, [9]))).</summary>
    public int WarMachinesMeasure() => T((Health - TroopsHealth) * Math.Max(Floor01, 1 / Factor(1.0, TroopsDefence)));

    /// <summary>
    /// Overall power vs the other side, used for the mode ratio (exe FUN_00bf9b80): k = max(0.1, 1/factor(trunc(other [4]), [7])),
    /// g = max(1, factor([4], trunc(other [9])) · [5]); trunc([3] · g + ([0] + [2]) + [6] · k).
    /// </summary>
    public int Power(SideValue other)
    {
        double k = Math.Max(Floor01, 1 / Factor(T(other.AttackLevel), Defence));
        double g = Math.Max(1.0, Factor(AttackLevel, T(other.TroopsDefence)) * Bonus);
        return T(Damage * g + (int)(Extra + SpellPotential) + Health * k);
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

    /// <summary>
    /// One side's half (exe FUN_00bfbe50): the contact matrix for the side (FUN_00bfb350 → FUN_004b23b0(army, 1)), the
    /// per-stack accumulation FUN_00bfba50 over the stacks the side controls, then the averaging pass FUN_00bf9ee0.
    /// Per stack: dmg = <see cref="ExpectedDamage"/>; a shooter's dmg = trunc((1 − f + 1) · dmg), f = <see cref="ShooterThreat"/>;
    /// [3] += dmg, a0 += (attack + 1)·dmg, a1 += dmg·bonus (float); shooters [10] += dmg·bonus, [11] += (attack + 1)·dmg,
    /// a4 += dmg; flyers (vf 0x190) [12], [13], a5; walkers [14], [15], a6; hp = the stack's health (×1.4 incorporeal,
    /// ×1.1 under Rune of Etherealness, FISTP chop; 1 for a Phantom copy), [6] += hp, a2 += (defence + 1)·hp; creatures
    /// also [8] += hp, a3 += (defence + 1)·hp. Averages: [4] = a0/[3], [5] = a1/[3], [7] = a2/[6], [9] = a3/[8],
    /// [11] /= a4, [13] /= a5, [15] /= a6.
    /// [0] is 0 for every unit: its source, the unit vf 0x84 object (+= its vf 0xc), is null on the creature (008ae290),
    /// hero (00bc2b50) and war-machine (00a76cf0) unit interfaces (all → FUN_00c98940).
    /// [2] += <see cref="Battle.AiSpellPotential"/> (FUN_00bfa360(10, 0)) for each walker and flyer and, in FUN_00bfbe50, for
    /// the army's hero. The bonus is <see cref="ExeBonus"/> (FUN_00bfb4b0). The harpy rule: with FUN_00a2f8f0(enemy army)
    /// (<see cref="NoRangedThreat"/>) a stack with Strike and Return + No Enemy Retaliation (FUN_00a2f130) is a shooter.
    /// Health terms (00bfbc07..00bfbe05): ×1.4 incorporeal (×1.1 under Rune of Etherealness, FISTP chop); 1 under Phantom
    /// (effect 0x28; ours: a Phantom copy); else Celestial Shield (effect 0x22) ×1.25 (FISTP chop); Deflect Arrows (effect
    /// 0x1d): trunc(((1 − s) + s / (1 − g)) · hp · hp), g = clamp(power, 0, 0.95) (FUN_00976b60), s = the enemy army's
    /// shooter share FUN_00bfa500 (<see cref="ShooterShare"/>) — the exe multiplies by hp twice; a value beyond the int
    /// range gives the FISTP integer indefinite. a2 uses the defence doubled under effect 0x13d (FISTP), a3 the plain one.
    /// The hero part [1] (00bfc00e..00bfc08b): n = trunc(elapsed combat time / ticks per turn + 1) (FUN_00af1c00 /
    /// FUN_00a2d700, FISTP chop; ours: <see cref="BattleState.Time"/>); n &gt; 2 → [1] stays 0; else [1] = X / max(1, n)
    /// (integer division).
    /// GAP-details (not ported): X = FUN_0085b4a0(combat vf 0x18, army) — the sum of vf 0x1c(0,0,0) over the objects of
    /// that list (+0x54) of type 0 / 3 / 5 with vf 0x18(0,0) set owned by the army — is not identified (ours:
    /// <see cref="HeroStrikePotential"/>); [16] is <see cref="MoatShare"/>; the [5] multiplier 1 + 0.05 · (hero unit vf 0x244(no spell) + hero
    /// vf 0x40 = FUN_00bc0730) unless the hero holds effect 0x41 (vf 0x40 not identified); the think-record damage
    /// multiplier FUN_00a314c0 (1.0 unless set); FUN_00a5b780's health addend (unit vf 0x70 / vf 0x2d0 / vf 0x1a8,
    /// RPGStats +0x4cc / +0x4c0); Celestial Shield's ×2 branch (effect +0x20 / 100000 &gt; 2, field not identified); the
    /// category skip for a unit whose vf 0x7c object has +0x1c = 2 (ours: a war machine that cannot shoot); the [8] / a3
    /// test (unit vf 0 = 1 and creature interface vf 0x58 = +0x48 false; ours: not a war machine); the hypnotize rule of
    /// FUN_00bfbe50 (effect 0x14: counted for the owner while its +0x1c ≤ the unit's vf 0x180, for the other army while
    /// ≥) and the berserk rule (effect 0x12: skipped when its FUN_00bbbbc0 target belongs to the valued army; an enemy
    /// berserker without a target is skipped) — ours: <see cref="Battle.Controller"/>; a unit counts when the valued
    /// army owns (vf 0xc) or controls (vf 0x18) it.
    /// </summary>
    static SideValue Fill(Battle b, int side)
    {
        var s = b.State;
        var v = new SideValue();
        var m = Contact.Build(b, side);
        long a0 = 0, a2 = 0, a3 = 0;
        float a1 = 0;
        int a4 = 0, a5 = 0, a6 = 0, spell = 0;
        float shD = 0, shA = 0, flD = 0, flA = 0, waD = 0, waA = 0;
        int damage = 0, health = 0, troops = 0;
        var enemies = s.Living.Where(e => b.Controller(e) != side && !e.IsObject).ToList();
        bool harpy = NoRangedThreat(b, 1 - side);
        float share = ShooterShare(b, 1 - side);
        foreach (var u in s.Living)
        {
            if (u.IsObject || b.Controller(u) != side) continue;
            int dmg = ExpectedDamage(b, u, m);
            bool shooter = b.CanShoot(u);
            if (shooter) dmg = (int)((1.0f - ShooterThreat(b, u, m) + 1.0f) * dmg);
            damage += dmg;
            int atk = b.AttackOf(u);
            a0 += (long)(atk + 1) * dmg;
            float bd = (float)(dmg * (double)ExeBonus(b, u));
            a1 += bd;
            if (!u.IsWarMachine || shooter)
            {
                float ad = (float)((double)(atk + 1) * dmg);
                if (shooter || harpy && u.Has(Ab.StrikeAndReturn) && u.Has(Ab.NoEnemyRetaliation)) { shD += bd; shA = (float)((double)ad + shA); a4 += dmg; }
                else if (u.Flying) { flD += bd; flA = (float)((double)ad + flA); a5 += dmg; spell += b.AiSpellPotential(u, null); }
                else { waD += bd; waA = (float)((double)ad + waA); a6 += dmg; spell += b.AiSpellPotential(u, null); }
            }
            int hp = (int)Math.Min(int.MaxValue, u.TotalHealth);
            bool rune = u.HasEffect(H5.Combat.Fx.RuneEtherealness);
            if (u.Has(Ab.Incorporeal) || rune) hp = (int)(long)(hp * (rune ? 1.1 : 1.4));
            if (u.IsClone) hp = 1;
            else if (u.HasEffect("SPELL_CELESTIAL_SHIELD")) hp = (int)(hp * 1.25f);
            if (u.GetEffect("SPELL_DEFLECT_ARROWS") is { } da)
            {
                float g = Math.Max(0f, Math.Min((float)da.Value, 0.95f));
                hp = Fistp((1.0 - share + 1.0 / (1.0 - g) * share) * (float)hp * (float)hp);
            }
            int def = b.DefenceOf(u);
            int def2 = u.HasEffect(H5.Combat.Fx.Invisible) ? Fistp(def + (double)def) : def;
            health += hp;
            a2 += (long)(def2 + 1) * hp;
            if (!u.IsWarMachine)
            {
                troops += hp;
                a3 += (long)(def + 1) * hp;
            }
        }
        var hero = s.Heroes[side];
        if (hero.Present) spell += b.AiSpellPotential(null, hero);
        v.SpellPotential = spell;
        v.Damage = damage; v.Health = health; v.TroopsHealth = troops;
        v.ShooterDamage = shD; v.FlyerDamage = flD; v.WalkerDamage = waD;
        if (damage != 0) { v.AttackLevel = (float)((double)a0 / damage); v.Bonus = (float)((double)a1 / damage); }
        if (health != 0) v.Defence = (float)((double)a2 / health);
        if (troops != 0) v.TroopsDefence = (float)((double)a3 / troops);
        v.ShooterAttack = a4 != 0 ? shA / (float)a4 : shA;
        v.FlyerAttack = a5 != 0 ? flA / (float)a5 : flA;
        v.WalkerAttack = a6 != 0 ? waA / (float)a6 : waA;
        int n = (int)((float)s.Time + 1.0f);
        if (n <= 2) v.HeroPotential = (int)HeroStrikePotential(b, side, enemies) / Math.Max(1, n);
        v.Additive = MoatShare(b, 1 - side);
        return v;
    }

    /// <summary>
    /// Value slot [16] of a half: FUN_00bfa610(combat vf 0xe0(army)) — over the OTHER army's units (army vf 0x6c),
    /// FUN_0085c2c0(unit): the combat objects at the unit's cell (FUN_0085bbf0 on combat vf 0x18 = +0x190) of type 5 add
    /// their vf 0x1c(unit, 1, 0); × 2 for a unit that shoots (vf 0x1e0). Type 5 is the moat (CArenaMoat: the same objects'
    /// cells are the moat cells of FUN_00b3e040); ours: a unit of the siege attacker standing on a moat cell counts the
    /// moat damage (<see cref="SiegeState.MoatDamage"/>), the only side our moat hurts. GAP-detail: vf 0x1c is read as
    /// that damage (not traced).
    /// </summary>
    internal static int MoatShare(Battle b, int side)
    {
        if (b.State.Siege is not { Moat.Count: > 0 } sg || side != 0) return 0;
        int sum = 0;
        foreach (var u in b.State.Living)
        {
            if (u.Side != side || u.IsObject || !u.CellsAt(u.Pos).Any(sg.Moat.Contains)) continue;
            sum += b.CanShoot(u) ? sg.MoatDamage * 2 : sg.MoatDamage;
        }
        return sum;
    }

    /// <summary>x87 FISTP of a double: the integer, or the integer indefinite 0x80000000 outside the int range.</summary>
    internal static int Fistp(double x) => x >= -2147483648.0 && x < 2147483648.0 ? (int)x : int.MinValue;

    /// <summary>
    /// FUN_00a2f8f0(army), the harpy rule's test: true when the army has no hero (vf 0xc) and none of its living stacks
    /// (army vf 0x6c) shoots (vf 0x1e0), has a spell potential (FUN_00bfa4f0 &gt; 0) or has Strike and Return + No Enemy
    /// Retaliation (FUN_00a2f130). Army vf 0x6c = 004da670 → vf 0x5c = 004da600: the creature interfaces (unit vf 0x6c ≠ 0, so no
    /// war machine or hero) of the army's units (+0x28) that pass FUN_00a6d490 (not vf 0x5c, visibility vf 0x4c ≠ 1: a hidden
    /// stack — ours: the Invisible effect — is left out).
    /// </summary>
    internal static bool NoRangedThreat(Battle b, int side)
    {
        if (b.State.Heroes[side].Present) return false;
        foreach (var u in b.State.Living)
        {
            if (u.Side != side || u.IsObject || u.IsWarMachine || u.HasEffect(Fx.Invisible)) continue;
            if (b.CanShoot(u) || b.AiSpellPotential(u, null) > 0 || u.Has(Ab.StrikeAndReturn) && u.Has(Ab.NoEnemyRetaliation)) return false;
        }
        return true;
    }

    /// <summary>
    /// FUN_00bfa500(army): over the army's stacks (army vf 0x64), r = count (vf 0x1d8) · (max + min) (vf 0x1ec / 0x1f0) · 0.5
    /// in single precision; the shooters' (vf 0x1e0) sum over the total, 0 when the total is not above 0. Army vf 0x64 =
    /// 004da560 → vf 0x60 = 004da500: every unit of the army (+0x28), war machines included (no creature-interface test), that
    /// passes FUN_00a6d490 (not vf 0x5c, vf 0x4c ≠ 1: hidden stacks — ours: Invisible — left out). GAP-detail: whether the
    /// hero's unit is in that list is not traced (left out here).
    /// </summary>
    internal static float ShooterShare(Battle b, int side)
    {
        float all = 0, shoot = 0;
        foreach (var u in b.State.Living)
        {
            if (u.Side != side || u.IsObject || u.HasEffect(Fx.Invisible)) continue;
            var (min, max) = b.DamageRangeOf(u);
            float r = (float)u.Count * (float)((float)max + (float)min) * 0.5f;
            all = r + all;
            if (b.CanShoot(u)) shoot = r + shoot;
        }
        return all > 0f ? shoot / all : 0f;
    }

    /// <summary>
    /// FUN_00bfb4b0(unit, x), the stack's bonus factor (single precision, clamped to [0, 13]), as ported:
    /// (1 + 0.1·vf 0x1b0)(1 + 0.1·vf 0x1ac) (morale, luck); × (1 − min(t, 5)/6) when the disabled time t (FUN_004e4920: the
    /// remaining duration of Blind 0x13, Frozen 0xc9, Sabotage 0x14a or Sleeping Strike 0x150, else of Seduce 0x148 via
    /// FUN_004e48a0, over the ticks per turn) is above 0.1; × max(2, min(initiative, 30)) · 0.1; × (1 + 0.05·(speed − 5))
    /// without ability 1 (shooter); a shooter (ability 1) that cannot shoot (vf 0x1e0) × 0.9 with ability 2 (no melee
    /// penalty), else × 0.5; ability 4 (range penalty) and able to shoot × 0.5; × (0.95 + 0.1·clamp(ATB, 0, 1)) when the unit
    /// is not x (always in the position value: x is an army there); × (1 + 0.1·mana/max) when max mana &gt; 0.
    /// GAP-details: the Gating term (effect 0x9f: × (1 − min(ceil(max(1 − ATB, 0) · vf 0x174) / ticks per turn, 3)/6)), the
    /// Forgetfulness term for a shooter (effect 0x11: × (2 − FUN_00976b30)/2, its power FUN_00a34c40 not identified), the
    /// creature's attack and defence terms (× (1 + 0.1·(creature interface +0x20)/record +0x78), × (1 + 0.1·(+0x24)/record
    /// +0x7c): fields not identified), the rune share FUN_00bfb3d0 (× (1 + 0.1·share); ours: no creature runes) and
    /// × 1.1 for vf 0x1e4 (FUN_008a10e0, field −0x80 not identified) without ability 0x22 are not applied; the shooter
    /// ability is read as having shots (war machines: no); Seduce is read on the unit itself (the exe reads it through vf 0x30).
    /// </summary>
    internal static float ExeBonus(Battle b, Unit u)
    {
        float k = (float)((b.MoraleOf(u) * (double)0.1f + 1.0) * (b.LuckOf(u) * (double)0.1f + 1.0));
        float t = DisabledTime(u);
        if (t > 0.1f) k = (float)((1.0 - Math.Min(t, 5f) * (double)(1f / 6f)) * k);
        float init = (float)b.InitiativeOf(u);
        float c = Math.Max(2f, Math.Min(init, 30f));
        k = (float)((double)c * k * 0.1f);
        bool shooterAbility = !u.IsWarMachine && u.MaxShots > 0;
        if (!shooterAbility) k = (float)(((b.SpeedOf(u) - 5) * (double)0.05f + 1.0) * k);
        bool shoots = b.CanShoot(u);
        if (shooterAbility && !shoots) k *= u.Has(Ab.NoMeleePenalty) ? 0.9f : 0.5f;
        if (u.Has(Ab.RangePenalty) && shoots) k *= 0.5f;
        float atb = Math.Max(0f, Math.Min((float)u.Atb, 1f));
        k = (float)((atb * (double)0.1f + 1.0 - 0.05f) * k);
        if (u.MaxMana > 0) k = (float)((u.Mana * (double)0.1f / u.MaxMana + 1.0) * k);
        return k < 0f ? 0f : k > 13f ? 13f : k;
    }

    /// <summary>FUN_004e4920: remaining turns of the first of Blind, Frozen, Sabotage, Sleeping Strike, else Seduce; 0 without.</summary>
    static float DisabledTime(Unit u)
    {
        foreach (var id in new[] { "SPELL_BLIND", H5.Combat.Fx.Frozen, H5.Combat.Fx.Sabotaged, H5.Combat.Fx.Sleep, H5.Combat.Fx.Seduced })
            if (u.GetEffect(id) is { } e) return (float)e.Remaining;
        return 0f;
    }

    /// <summary>
    /// FUN_00bfb880: trunc(F · trunc((max + min) · count · 0.5)), F = <see cref="ContactFactor"/> (FUN_00bfa280).
    /// </summary>
    internal static int ExpectedDamage(Battle b, Unit u, Contact m)
    {
        var (min, max) = b.DamageRangeOf(u);
        int raw = (int)(((double)(float)max + (float)min) * u.Count * 0.5);
        return (int)(ContactFactor(b, u, m) * raw);
    }

    /// <summary>
    /// FUN_00bfa280: 0.0 for a creature (vf 0 = 1) that neither flies (vf 0x190) nor shoots (vf 0x1e0) when the matrix has at
    /// least two stacks, some stack of another army exists (FUN_004b1810) and no stack of another army is in contact with it
    /// (FUN_004b1770: matrix [j][u] = 0 for every such j); otherwise 1.0.
    /// </summary>
    internal static double ContactFactor(Battle b, Unit u, Contact m)
    {
        if (u.Flying || b.CanShoot(u) || u.IsWarMachine || m.Count < 2) return 1.0;
        int idx = m.IndexOf(u);
        if (idx < 0) return 1.0;
        int others = 0, contacts = 0;
        for (int j = 0; j < m.Count; j++)
        {
            if (m.Units[j].Side == u.Side) continue;
            others++;
            if (m[j, idx]) contacts++;
        }
        return others != 0 && contacts == 0 ? 0.0 : 1.0;
    }

    /// <summary>
    /// FUN_00bfb950, the shooter factor f of (2 − f)·damage: 1 for a war machine or with fewer than two stacks in the
    /// matrix; otherwise, over the stacks of other armies that cannot shoot, the share of their expected damage
    /// (<see cref="ExpectedDamage"/>) held by those in contact with the shooter (FUN_004b1660(e, u) = matrix [u][e]);
    /// 1 when that damage is 0.
    /// </summary>
    internal static double ShooterThreat(Battle b, Unit u, Contact m)
    {
        if (u.IsWarMachine || m.Count < 2) return 1;
        int ui = m.IndexOf(u);
        int total = 0, threat = 0;
        for (int j = 0; j < m.Count; j++)
        {
            var e = m.Units[j];
            if (e.Side == u.Side || b.CanShoot(e)) continue;
            int w = ExpectedDamage(b, e, m);
            total += w;
            if (ui >= 0 && m[ui, j]) threat += w;
        }
        return total != 0 ? (double)threat / total : 1;
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

/// <summary>
/// The combat AI's contact matrix (exe FUN_004b23b0(army, 1), built by FUN_00bfb350 for the side being valued, kept at
/// value +0x44). Rows and columns are the stacks on the field; [v, u] = 1 when stack u can come into contact with v:
/// for each creature size s = 1, 2 (FUN_004b1b50 / FUN_004b1ff0 / FUN_004b1e20) a grid over the size-s positions
/// (indexed by the footprint's far corner, FUN_00b3f380 expands a cell c to c .. c + s − 1) is filled: obstacles and
/// non-creature objects blocked; the side's own stacks blocked and labelled over every position touching them; the
/// other stacks blocked and labelled on their cells (s = 1) or their far-corner position only (s = 2; the flag 1 of
/// FUN_004b23b0 makes them block). Free positions are flooded in 8 directions (FUN_004b1880 seeds in column order,
/// FUN_004b1c90 collects the labels bordering the region, the seed's own label excepted); in each region every stack
/// of size s gets [v, u] = 1 for every collected v. Last, two stacks next to each other get 1 (FUN_004b18f0 → FUN_00b3d3f0).
/// GAP-details: the unit vf 0xc4 cell flag (siege, army vf 0xac), the type-6 object rule of FUN_00b3d3f0 and whether
/// war machines are in the object list FUN_00af4450 (ours: they are matrix stacks only).
/// </summary>
public sealed class Contact
{
    public readonly List<Unit> Units;
    readonly bool[,] _m;
    Contact(List<Unit> units) { Units = units; _m = new bool[units.Count, units.Count]; }
    public int Count => Units.Count;
    public bool this[int v, int u] => _m[v, u];
    public int IndexOf(Unit u) => Units.FindIndex(x => x.Id == u.Id);

    static readonly (int, int)[] Dirs = { (0, -1), (0, 1), (-1, 0), (1, 0), (1, -1), (1, 1), (-1, -1), (-1, 1) };

    public static Contact Build(Battle b, int side)
    {
        var s = b.State;
        var c = new Contact(s.Living.Where(u => !u.IsObject).ToList());
        int n = c.Count, w = s.Width, h = s.Height;
        for (int size = 1; size <= 2; size++)
        {
            var idx = new short[w, h];
            var blocked = new bool[w, h];
            var visited = new bool[w, h];
            for (int x = 0; x < w; x++) for (int y = 0; y < h; y++) idx[x, y] = -1;
            void Mark(Cell cell, int label, int expand)
            {
                for (int dx = 0; dx < expand; dx++)
                for (int dy = 0; dy < expand; dy++)
                {
                    int x = cell.X + dx, y = cell.Y + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    blocked[x, y] = true;
                    idx[x, y] = (short)label;
                }
            }
            foreach (var o in s.Obstacles) Mark(o, -1, size);
            foreach (var o in s.Living.Where(u => u.IsObject)) foreach (var cell in o.CellsAt(o.Pos)) Mark(cell, -1, size);
            for (int i = 0; i < n; i++)
            {
                var u = c.Units[i];
                bool own = u.Side == side;
                if (own || size < 2) foreach (var cell in u.CellsAt(u.Pos)) Mark(cell, i, own ? size : 1);
                else Mark(new Cell(u.Pos.X + u.Size - 1, u.Pos.Y + u.Size - 1), i, 1);
            }
            for (int sx = 0; sx < w; sx++)
            for (int sy = 0; sy < h; sy++)
            {
                if (blocked[sx, sy] || visited[sx, sy]) continue;
                var region = new List<int>();
                var stack = new Stack<(int, int)>();
                visited[sx, sy] = true;
                stack.Push((sx, sy));
                while (stack.Count > 0)
                {
                    var (px, py) = stack.Pop();
                    foreach (var (dx, dy) in Dirs)
                    {
                        int x = px + dx, y = py + dy;
                        if (x < 0 || y < 0 || x >= w || y >= h || visited[x, y]) continue;
                        if (idx[x, y] >= 0 && !region.Contains(idx[x, y])) region.Add(idx[x, y]);
                        if (!blocked[x, y]) { visited[x, y] = true; stack.Push((x, y)); }
                    }
                }
                foreach (int u in region)
                    if (c.Units[u].Size == size)
                        foreach (int v in region) c._m[v, u] = true;
            }
        }
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            if (i != j && Battle.AreAdjacent(c.Units[i], c.Units[j])) c._m[j, i] = true;
        return c;
    }
}
