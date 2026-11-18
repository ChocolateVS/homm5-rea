using System.Reflection;
using H5.Core.Data;

namespace H5.Combat;

/// <summary>
/// Direct deep copy of a <see cref="BattleState"/> (what <see cref="BattleState.Clone"/> returns): the same result as the
/// JSON round trip it replaces, without the text. Every object is shallow-copied (MemberwiseClone, so every value field
/// comes along), then each reference field gets its own copy: lists, arrays and dictionaries are rebuilt in the same
/// order (a dictionary in its enumeration order with the default comparer, as the deserialiser builds it), nulls stay
/// null. Strings and <see cref="SkillEntry"/> records are immutable and shared. State types are plain public-field
/// data; a new reference field must be added here (BattleStateCopyTests checks every field against the JSON round trip
/// and that no mutable object is shared).
/// </summary>
public static class BattleStateCopy
{
    static readonly Func<object, object> Shallow = (Func<object, object>)typeof(object)
        .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate(typeof(Func<object, object>));

    static T M<T>(T o) where T : class => (T)Shallow(o);

    public static BattleState Of(BattleState s)
    {
        var c = M(s);
        c.Obstacles = L(s.Obstacles);
        c.ObstaclePlacements = Map(s.ObstaclePlacements, Placement);
        c.Units = Map(s.Units, Unit);
        c.Siege = s.Siege == null ? null : Siege(s.Siege);
        c.Heroes = Arr(s.Heroes, Hero);
        c.SideBonuses = Arr(s.SideBonuses, M);
        c.Fields = Map(s.Fields, Field);
        c.Rng = s.Rng == null ? null! : M(s.Rng);
        c.TacticsDone = A(s.TacticsDone);
        c.RetreatDenied = A(s.RetreatDenied);
        c.NoTavernTown = A(s.NoTavernTown);
        c.PileOfSkulls = A(s.PileOfSkulls);
        c.AiControlled = A(s.AiControlled);
        c.AiRetreat = Arr(s.AiRetreat, M);
        return c;
    }

    public static Unit Unit(Unit u)
    {
        var c = M(u);
        c.Abilities = L(u.Abilities);
        c.Spells = Map(u.Spells, M);
        c.Effects = Map(u.Effects, M);
        c.MicroArtifact = u.MicroArtifact == null ? null : Micro(u.MicroArtifact);
        c.AbilityUses = D(u.AbilityUses);
        return c;
    }

    static MicroArtifact Micro(MicroArtifact a)
    {
        var c = M(a);
        c.Effects = Map(a.Effects, M);
        return c;
    }

    static HeroState Hero(HeroState h)
    {
        var c = M(h);
        c.Resources = A(h.Resources);
        c.ResourcesSpent = A(h.ResourcesSpent);
        c.Skills = L(h.Skills);
        c.Perks = L(h.Perks);
        c.Spells = L(h.Spells);
        c.Wands = Map(h.Wands, M);
        c.Book = Map(h.Book, Book);
        c.Artifacts = L(h.Artifacts);
        c.FavoriteEnemies = L(h.FavoriteEnemies);
        c.SetPieces = D(h.SetPieces);
        c.SpecFamily = L(h.SpecFamily);
        c.AbilityUses = D(h.AbilityUses);
        c.Links = D(h.Links);
        c.BattleBonus = A(h.BattleBonus);
        return c;
    }

    static BookEntry Book(BookEntry e)
    {
        var c = M(e);
        c.Sources = L(e.Sources);
        return c;
    }

    static FieldObject Field(FieldObject f)
    {
        var c = M(f);
        c.Cells = L(f.Cells);
        return c;
    }

    static ObstaclePlacement Placement(ObstaclePlacement p)
    {
        var c = M(p);
        c.Cells = L(p.Cells);
        return c;
    }

    static SiegeState Siege(SiegeState g)
    {
        var c = M(g);
        c.Buildings = Map(g.Buildings, Building);
        c.Moat = L(g.Moat);
        c.SpentMines = L(g.SpentMines);
        c.FortLine = A(g.FortLine);
        return c;
    }

    static SiegeBuilding Building(SiegeBuilding b)
    {
        var c = M(b);
        c.Blocked = L(b.Blocked);
        c.BlockedDestroyed = L(b.BlockedDestroyed);
        return c;
    }

    // ---- collections (elements of value or immutable type are shared; null stays null)

    static List<T> L<T>(List<T> l) => l == null ? null! : new List<T>(l);

    static T[] A<T>(T[] a) => a == null ? null! : (T[])a.Clone();

    static Dictionary<K, V> D<K, V>(Dictionary<K, V> d) where K : notnull => d == null ? null! : new Dictionary<K, V>(d);

    static List<T> Map<T>(List<T> l, Func<T, T> f) where T : class
    {
        if (l == null) return null!;
        var r = new List<T>(l.Count);
        foreach (var x in l) r.Add(x == null ? null! : f(x));
        return r;
    }

    static T[] Arr<T>(T[] a, Func<T, T> f) where T : class
    {
        if (a == null) return null!;
        var r = new T[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = a[i] == null ? null! : f(a[i]);
        return r;
    }
}
