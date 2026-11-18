using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using H5.Adventure.State;

namespace H5.Adventure.AI;

/// <summary>
/// Cheap deep copy of an <see cref="AdvState"/> for the AI's simulations (the exe simulates a visit on the live world
/// in "simulation mode", player vf 0x13c(1), and restores it; we run the visit on a private copy instead).
///
/// The copy has the same content as <see cref="AdvState.Clone"/> (its JSON round trip) — same members, same collection
/// order, no shared mutable object — so <c>copy.ComputeHash() == state.ComputeHash()</c> and nothing done on the copy can
/// reach the real game. It is built from per-type copiers compiled once (MemberwiseClone, then every reference field
/// copied), several times faster than the JSON round trip. Immutable types (strings, primitives, enums, structs made of
/// immutable fields such as <c>Tile</c>, classes whose fields are all readonly and immutable such as <c>SkillEntry</c>)
/// are shared. Determinism: the copy enumerates every collection in the original's order, and the copy's RNG is its own
/// object, so a simulation never consumes the game's random stream.
/// </summary>
public static class StateCopy
{
    public static AdvState Of(AdvState s) => (AdvState)Copy(s)!;

    /// <summary>
    /// A copy for a trial that changes no map object's state but <paramref name="deepObject"/>'s: like <see cref="Of"/>,
    /// except that <see cref="AdvState.Objects"/> is a new dictionary (same keys, same order) sharing every
    /// <see cref="ObjState"/> with the original but the one of <paramref name="deepObject"/>, which is copied. The object
    /// states are most of a full copy's cost. Only for code that writes no other object's state (the purchase trial:
    /// the market trade, its marketplace build, the recruit).
    /// </summary>
    public static AdvState OfSharingObjects(AdvState s, int deepObject)
    {
        var c = (AdvState)WithoutObjects(s);
        var objs = s.Objects;
        var d = new Dictionary<int, ObjState>(objs.Count, objs.Comparer);
        foreach (var (k, v) in objs) d.Add(k, k == deepObject ? (ObjState)Copy(v)! : v);
        c.Objects = d;
        return c;
    }

    static Func<object, object>? _withoutObjects;
    static Func<object, object> WithoutObjects => _withoutObjects ??= BuildObject(typeof(AdvState), nameof(AdvState.Objects));

    static readonly ConcurrentDictionary<Type, Func<object, object>> Copiers = new();
    static readonly ConcurrentDictionary<Type, bool> ImmutableTypes = new();
    static readonly Func<object, object> Memberwise =
        (Func<object, object>)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<object, object>));

    public static object? Copy(object? o)
    {
        if (o == null) return null;
        var t = o.GetType();
        if (IsImmutable(t)) return o;
        return Copiers.GetOrAdd(t, Build)(o);
    }

    /// <summary>A field's copier, resolved on first use (fields of sealed types skip the type lookup).</summary>
    sealed class Slot(Type t)
    {
        Func<object, object>? _f;
        public object? Copy(object? o)
        {
            if (o == null) return null;
            if (o.GetType() != t) return StateCopy.Copy(o);
            return (_f ??= Copiers.GetOrAdd(t, Build))(o);
        }
    }

    static class Imm<T> { public static readonly bool V = IsImmutable(typeof(T)); }

    /// <summary>Element copier for collections: a sealed element type skips the per-element type lookup.</summary>
    static class Elem<T>
    {
        static readonly Slot S = new(typeof(T));
        public static T C(T x) => x == null ? x : (T)S.Copy(x)!;
    }

    [ThreadStatic] static HashSet<Type>? _visiting;

    static bool IsImmutable(Type t)
    {
        if (ImmutableTypes.TryGetValue(t, out bool r)) return r;
        var visiting = _visiting ??= new HashSet<Type>();
        if (!visiting.Add(t)) return false;   // a self-referencing type is not immutable
        try { r = ComputeImmutable(t); }
        finally { visiting.Remove(t); }
        ImmutableTypes.TryAdd(t, r);
        return r;
    }

    static bool ComputeImmutable(Type t)
    {
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)) return true;
        if (t.IsArray || t.IsInterface || t.IsAbstract || t == typeof(object)) return false;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>)) return IsImmutable(t.GetGenericArguments()[0]);
        if (!t.IsValueType && !t.IsSealed) return false;
        if (!t.IsValueType && t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true) return false;
        foreach (var f in Fields(t))
        {
            if (!t.IsValueType && !f.IsInitOnly) return false;
            if (!IsImmutable(f.FieldType)) return false;
        }
        return true;
    }

    static IEnumerable<FieldInfo> Fields(Type t)
    {
        for (var x = t; x != null && x != typeof(object); x = x.BaseType)
            foreach (var f in x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return f;
    }

    static Func<object, object> Build(Type t)
    {
        if (t.IsArray) return BuildArray(t);
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments();
            string? m = def == typeof(List<>) ? nameof(CopyList)
                      : def == typeof(HashSet<>) ? nameof(CopySet)
                      : def == typeof(Dictionary<,>) ? nameof(CopyDict) : null;
            if (m != null)
            {
                var mi = typeof(StateCopy).GetMethod(m, BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(args);
                return (Func<object, object>)mi.CreateDelegate(typeof(Func<object, object>));
            }
            if (def.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && !t.IsValueType)
                throw new NotSupportedException($"StateCopy: unsupported collection {t}");
        }
        if (t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && !t.IsValueType)
            throw new NotSupportedException($"StateCopy: unsupported type {t}");
        return BuildObject(t);
    }

    static Func<object, object> BuildArray(Type t)
    {
        if (t.GetArrayRank() != 1) throw new NotSupportedException($"StateCopy: multi-dimensional array {t}");
        var mi = typeof(StateCopy).GetMethod(nameof(CopyArray), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(t.GetElementType()!);
        return (Func<object, object>)mi.CreateDelegate(typeof(Func<object, object>));
    }

    /// <summary>The copier method (object → object) of a one-dimensional array, List, HashSet or Dictionary type; null otherwise.</summary>
    static MethodInfo? CollectionCopier(Type t)
    {
        string? m = null;
        Type[] args;
        if (t.IsArray && t.GetArrayRank() == 1) { m = nameof(CopyArray); args = new[] { t.GetElementType()! }; }
        else if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            args = t.GetGenericArguments();
            m = def == typeof(List<>) ? nameof(CopyList) : def == typeof(HashSet<>) ? nameof(CopySet) : def == typeof(Dictionary<,>) ? nameof(CopyDict) : null;
        }
        else return null;
        return m == null ? null : typeof(StateCopy).GetMethod(m, BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(args);
    }

    static object CopyArray<T>(object o)
    {
        // An empty array cannot change: shared.
        if (((T[])o).Length == 0) return o;
        var a = (T[])((T[])o).Clone();
        if (Imm<T>.V) return a;
        for (int i = 0; i < a.Length; i++) a[i] = Elem<T>.C(a[i]);
        return a;
    }

    static Func<object, object> BuildObject(Type t) => BuildObject(t, null);

    /// <summary>The copier of <paramref name="t"/>; the field named <paramref name="share"/> (if any) keeps the original's reference.</summary>
    static Func<object, object> BuildObject(Type t, string? share)
    {
        var mutable = Fields(t).Where(f => !IsImmutable(f.FieldType) && f.Name != share).ToList();
        if (mutable.Count == 0) return Memberwise;
        if (t.IsValueType)
        {
            // Boxed struct with reference fields: copy through reflection on the box.
            return o =>
            {
                var c = Memberwise(o);
                foreach (var f in mutable) f.SetValue(c, Copy(f.GetValue(o)));
                return c;
            };
        }
        var src = Expression.Parameter(typeof(object), "o");
        var dst = Expression.Parameter(typeof(object), "c");
        var s = Expression.Variable(t, "s");
        var d = Expression.Variable(t, "d");
        var slotCopy = typeof(Slot).GetMethod(nameof(Slot.Copy))!;
        var body = new List<Expression> { Expression.Assign(s, Expression.Convert(src, t)), Expression.Assign(d, Expression.Convert(dst, t)) };
        var slow = new List<FieldInfo>();
        var anyCopy = typeof(StateCopy).GetMethod(nameof(Copy))!;
        var vars = new List<ParameterExpression> { s, d };
        foreach (var f in mutable)
        {
            if (f.IsInitOnly) { slow.Add(f); continue; }
            if (CollectionCopier(f.FieldType) is { } direct)
            {
                // A collection field: its copier called directly when the value has the field's own type, else the
                // general lookup (same result, without the per-field delegate dispatch).
                var v = Expression.Variable(f.FieldType, "v");
                vars.Add(v);
                body.Add(Expression.Assign(v, Expression.Field(s, f)));
                body.Add(Expression.Assign(Expression.Field(d, f),
                    Expression.Condition(Expression.Equal(v, Expression.Constant(null, f.FieldType)), Expression.Constant(null, f.FieldType),
                        Expression.Convert(Expression.Condition(
                            Expression.Equal(Expression.Call(v, typeof(object).GetMethod(nameof(GetType))!), Expression.Constant(f.FieldType, typeof(Type))),
                            Expression.Call(direct, Expression.Convert(v, typeof(object))),
                            Expression.Call(anyCopy, Expression.Convert(v, typeof(object)))), f.FieldType))));
                continue;
            }
            body.Add(Expression.Assign(Expression.Field(d, f),
                Expression.Convert(Expression.Call(Expression.Constant(new Slot(f.FieldType)), slotCopy,
                    Expression.Convert(Expression.Field(s, f), typeof(object))), f.FieldType)));
        }
        body.Add(dst);
        var fill = Expression.Lambda<Func<object, object, object>>(Expression.Block(vars, body), src, dst).Compile();
        return o =>
        {
            var c = Memberwise(o);
            fill(o, c);
            foreach (var f in slow) f.SetValue(c, Copy(f.GetValue(o)));
            return c;
        };
    }

    static object CopyList<T>(object o)
    {
        var l = (List<T>)o;
        if (Imm<T>.V) return new List<T>(l);
        var c = new List<T>(l.Count);
        foreach (var x in l) c.Add(Elem<T>.C(x));
        return c;
    }

    static object CopySet<T>(object o)
    {
        var l = (HashSet<T>)o;
        if (l.Count == 0) return new HashSet<T>(l.Comparer);
        if (Imm<T>.V) return new HashSet<T>(l, l.Comparer);
        var c = new HashSet<T>(l.Comparer);
        foreach (var x in l) c.Add(Elem<T>.C(x));
        return c;
    }

    static object CopyDict<K, V>(object o) where K : notnull
    {
        var l = (Dictionary<K, V>)o;
        if (l.Count == 0) return new Dictionary<K, V>(l.Comparer);
        if (Imm<K>.V && Imm<V>.V) return new Dictionary<K, V>(l, l.Comparer);
        var c = new Dictionary<K, V>(l.Count, l.Comparer);
        foreach (var (k, v) in l) c.Add(Imm<K>.V ? k : Elem<K>.C(k), Imm<V>.V ? v : Elem<V>.C(v));
        return c;
    }
}
