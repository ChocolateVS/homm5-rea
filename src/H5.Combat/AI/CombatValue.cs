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
    /// GAP-details (not ported): [0] (the vf 0x84 object's vf 0xc), [2] (FUN_00bfa360, best castable damage spell),
    /// [16] (FUN_00bfa610), the bonus factor FUN_00bfb4b0 (ours: <see cref="LuckMoraleBonus"/>), the hero part [1]
    /// (0085b4a0 / turn, 0 after turn 2; ours: <see cref="HeroStrikePotential"/>) and its [5] multiplier, the
    /// think-record damage multiplier FUN_00a314c0 (1.0 unless set), the harpy rule (FUN_00a2f130 when FUN_00a2f8f0 holds
    /// for the enemy army), the category skip for the vf 0x7c type-2 object (ours: a war machine that cannot shoot),
    /// Celestial Shield / FUN_00a5b780 / Deflect Arrows health terms, the doubled defence under 0x13d, and the
    /// hypnotize / berserk side rules of FUN_00bfbe50 (ours: <see cref="Battle.Controller"/>).
    /// </summary>
    static SideValue Fill(Battle b, int side)
    {
        var s = b.State;
        var v = new SideValue();
        var m = Contact.Build(b, side);
        long a0 = 0, a2 = 0, a3 = 0;
        float a1 = 0;
        int a4 = 0, a5 = 0, a6 = 0;
        float shD = 0, shA = 0, flD = 0, flA = 0, waD = 0, waA = 0;
        int damage = 0, health = 0, troops = 0;
        var enemies = s.Living.Where(e => b.Controller(e) != side && !e.IsObject).ToList();
        foreach (var u in s.Living)
        {
            if (u.IsObject || b.Controller(u) != side) continue;
            int dmg = ExpectedDamage(b, u, m);
            bool shooter = b.CanShoot(u);
            if (shooter) dmg = (int)((1.0f - ShooterThreat(b, u, m) + 1.0f) * dmg);
            damage += dmg;
            int atk = b.AttackOf(u);
            a0 += (long)(atk + 1) * dmg;
            float bd = (float)(dmg * LuckMoraleBonus(b, u));
            a1 += bd;
            if (!u.IsWarMachine || shooter)
            {
                float ad = (float)((double)(atk + 1) * dmg);
                if (shooter) { shD += bd; shA = (float)((double)ad + shA); a4 += dmg; }
                else if (u.Flying) { flD += bd; flA = (float)((double)ad + flA); a5 += dmg; }
                else { waD += bd; waA = (float)((double)ad + waA); a6 += dmg; }
            }
            int hp = (int)Math.Min(int.MaxValue, u.TotalHealth);
            bool rune = u.HasEffect(H5.Combat.Fx.RuneEtherealness);
            if (u.Has(Ab.Incorporeal) || rune) hp = (int)(long)(hp * (rune ? 1.1 : 1.4));
            if (u.IsClone) hp = 1;
            int def = b.DefenceOf(u);
            health += hp;
            a2 += (long)(def + 1) * hp;
            if (!u.IsWarMachine)
            {
                troops += hp;
                a3 += (long)(def + 1) * hp;
            }
        }
        v.Damage = damage; v.Health = health; v.TroopsHealth = troops;
        v.ShooterDamage = shD; v.FlyerDamage = flD; v.WalkerDamage = waD;
        if (damage != 0) { v.AttackLevel = (float)((double)a0 / damage); v.Bonus = (float)((double)a1 / damage); }
        if (health != 0) v.Defence = (float)((double)a2 / health);
        if (troops != 0) v.TroopsDefence = (float)((double)a3 / troops);
        v.ShooterAttack = a4 != 0 ? shA / (float)a4 : shA;
        v.FlyerAttack = a5 != 0 ? flA / (float)a5 : flA;
        v.WalkerAttack = a6 != 0 ? waA / (float)a6 : waA;
        v.HeroPotential = HeroStrikePotential(b, side, enemies);
        return v;
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
