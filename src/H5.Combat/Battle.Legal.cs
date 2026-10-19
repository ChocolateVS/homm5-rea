using H5.Core.Data;

namespace H5.Combat;

public sealed class SpellOption
{
    public string Spell = "";
    public int ManaCost;
    public Mastery Mastery;
    public Battle.SpellTargeting Targeting;
    /// <summary>Valid unit targets (for unit-targeted spells).</summary>
    public List<int> Targets = new();
}

public sealed class AbilityOption
{
    public string Ability = "";
    public Battle.AbilityTargeting Targeting;
    public List<int> Targets = new();
}

/// <summary>Everything the active actor may do, for UI highlighting and AI move generation.</summary>
public sealed class LegalActions
{
    public int Actor;
    public int Side;
    public bool IsHero;
    public List<Cell> Moves = new();
    /// <summary>Melee target id → cells the attacker may strike from (its current cell included).</summary>
    public Dictionary<int, List<Cell>> Melee = new();
    public List<int> Shoot = new();
    public List<SpellOption> Spells = new();
    public List<AbilityOption> Abilities = new();
    public List<int> HeroAttack = new();
    public bool CanWait = true;
    public bool CanDefend = true;
}

public sealed partial class Battle
{
    public LegalActions? GetLegalActions()
    {
        if (S.Phase != BattlePhase.Combat || S.Active is not int a) return null;
        var la = new LegalActions { Actor = a, IsHero = Actor.IsHero(a) };
        if (Actor.IsHero(a))
        {
            int side = Actor.HeroSide(a);
            var h = S.Heroes[side];
            la.Side = side;
            la.HeroAttack = S.Living.Where(u => Controller(u) != side && !u.HasEffect(H5.Combat.Fx.Invisible) && u.CreatureId != "BLADE_BARRIER").Select(u => u.Id).ToList();
            foreach (var id in HeroSpells(h))
            {
                var sp = Db.Spell(id);
                if (sp == null || !CanCastWarcry(h, sp)) continue;
                int cost = HeroManaCost(h, sp);
                if (h.Mana < cost) continue;
                var m = HeroMastery(h, sp);
                var opt = new SpellOption { Spell = id, ManaCost = cost, Mastery = m, Targeting = TargetingOf(sp) };
                if (opt.Targeting is SpellTargeting.Unit or SpellTargeting.UnitAndCell)
                {
                    foreach (var u in S.Units)
                        if (ValidateSpellTarget(a, side, sp, m, u.Id, opt.Targeting == SpellTargeting.UnitAndCell ? u.Pos : null) == null
                            && (sp.School != "MAGIC_SCHOOL_RUNIC" || RuneError(h, sp, u.Id) == null))
                            opt.Targets.Add(u.Id);
                    if (opt.Targets.Count == 0 && opt.Targeting == SpellTargeting.Unit) continue;
                }
                la.Spells.Add(opt);
            }
            foreach (var ab in HeroAbilities(h))
                la.Abilities.Add(new AbilityOption { Ability = ab.Id, Targeting = ab.Targeting, Targets = AbilityTargets(ab, side, null) });
            return la;
        }
        var unit = S.Unit(a)!;
        la.Side = Controller(unit);
        if (CanMove(unit)) la.Moves = Reachable(unit).Keys.OrderBy(c => c.X).ThenBy(c => c.Y).ToList();
        if (!unit.IsWarMachine)
            foreach (var t in S.Living)
            {
                if (Controller(t) == la.Side || t.HasEffect(H5.Combat.Fx.Invisible) || IsSiegeTower(t)) continue;
                var pos = CanMove(unit) ? AttackPositions(unit, t) : (AreAdjacent(unit, t) ? new List<Cell> { unit.Pos } : new List<Cell>());
                if (pos.Count > 0) la.Melee[t.Id] = pos;
            }
        if (CanShoot(unit))
            la.Shoot = S.Living.Where(t => Controller(t) != la.Side && !t.HasEffect(H5.Combat.Fx.Invisible) && !IsSiegeTower(t)
                && !EldritchProtected(unit, t, ranged: true)).Select(t => t.Id).ToList();
        foreach (var ks in unit.Spells)
        {
            var sp = Db.Spell(ks.Spell);
            if (sp == null || unit.Mana < CreatureManaCost(unit, sp)) continue;
            var opt = new SpellOption { Spell = sp.Id, ManaCost = CreatureManaCost(unit, sp), Mastery = MoonWeek.SpellMastery(ks.Mastery, sp.School, S.Week), Targeting = TargetingOf(sp) };
            if (opt.Targeting is SpellTargeting.Unit)
            {
                foreach (var u in S.Units)
                    if (ValidateSpellTarget(a, la.Side, sp, MoonWeek.SpellMastery(ks.Mastery, sp.School, S.Week), u.Id, null) == null) opt.Targets.Add(u.Id);
                if (opt.Targets.Count == 0) continue;
            }
            la.Spells.Add(opt);
        }
        foreach (var ab in UnitAbilities(unit))
            la.Abilities.Add(new AbilityOption { Ability = ab.Id, Targeting = ab.Targeting, Targets = AbilityTargets(ab, la.Side, unit) });
        if (unit.IsWarMachine) { la.CanDefend = false; }
        return la;
    }

    List<int> AbilityTargets(AbilityInfo ab, int side, Unit? user) => ab.Targeting switch
    {
        AbilityTargeting.Friend => S.Living.Where(u => Controller(u) == side && !u.IsObject && (user == null || !ab.NeedsAdjacency || AreAdjacent(user, u)) && u.Id != user?.Id).Select(u => u.Id).ToList(),
        AbilityTargeting.Enemy => S.Living.Where(u => Controller(u) != side && !u.IsObject && !u.HasEffect(H5.Combat.Fx.Invisible) && (user == null || !ab.NeedsAdjacency || AreAdjacent(user, u))).Select(u => u.Id).ToList(),
        AbilityTargeting.Any => S.Living.Where(u => !u.IsObject && u.Id != user?.Id).Select(u => u.Id).ToList(),
        AbilityTargeting.DeadFriend => S.Units.Where(u => !u.Alive && !u.Removed && u.Kind == UnitKind.Creature && (ab.Id == "CONSUME_CORPSE" || u.Side == side)).Select(u => u.Id).ToList(),
        _ => new List<int>(),
    };

    /// <summary>Cells a unit may be deployed to during tactics.</summary>
    public List<Cell> DeployCells(Unit u)
    {
        var (minX, maxX) = DeployZone(u.Side);
        var list = new List<Cell>();
        for (int x = minX; x <= maxX - u.Size + 1; x++)
        for (int y = 0; y <= S.Height - u.Size; y++)
        {
            var p = new Cell(x, y);
            if (u.CellsAt(p).All(c => !S.Obstacles.Contains(c))) list.Add(p);
        }
        return list;
    }
}
