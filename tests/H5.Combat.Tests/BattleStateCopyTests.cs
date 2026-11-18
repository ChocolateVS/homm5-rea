using System.Collections;
using System.Reflection;
using H5.Core.Data;

namespace H5.Combat.Tests;

/// <summary>
/// <see cref="BattleState.Clone"/> (<see cref="BattleStateCopy"/>) must give exactly what the JSON round trip gave: every
/// field bit for bit (doubles by their bits, lists and dictionaries in the same order), no mutable object shared with the
/// original, and a copy that can be played on without touching the original.
/// </summary>
[Collection("game")]
public class BattleStateCopyTests(GameDataFixture f)
{
    static BattleState JsonRoundTrip(BattleState s) => BattleState.Deserialize(s.Serialize());

    /// <summary>Records the battle state (as a JSON copy) at every decision of an AI-vs-AI duel.</summary>
    sealed class Recorder(IBattleAI inner, List<BattleState> states) : IBattleAI
    {
        public BattleCommand Decide(Battle b, int side)
        {
            states.Add(JsonRoundTrip(b.State));
            return inner.Decide(b, side);
        }
    }

    List<BattleState> DuelStates()
    {
        var states = new List<BattleState>();
        int n = f.Db.DuelPresets.Count;
        var rng = new Random(5);
        for (int g = 0; g < 8; g++)
        {
            int a = rng.Next(n), b = (a + 1 + rng.Next(n - 1)) % n;
            var s = BattleSetup.CreateDuel(f.Db, a, b, (ulong)(100 + g));
            var rec = new Recorder(new GreedyBattleAI { MaxCandidates = 8 }, states);
            BattleRunner.RunToEnd(new Battle(f.Db, s), rec, rec, maxCommands: 60);
            states.Add(JsonRoundTrip(s));
        }
        return states;
    }

    [Fact]
    public void CopyEqualsJsonRoundTripOnDuelStates()
    {
        var states = DuelStates();
        Assert.True(states.Count > 100);
        foreach (var s in states) CheckCopy(s);
    }

    [Fact]
    public void CopyEqualsJsonRoundTripOnFuzzedStates()
    {
        var states = DuelStates();
        var rng = new Random(11);
        int checkedStates = 0;
        for (int i = 0; i < 400; i++)
        {
            var s = JsonRoundTrip(states[rng.Next(states.Count)]);
            Fuzz(s, rng, 0);
            // Some combinations cannot be serialised at all (a computed property over a null list): no round trip to match.
            try { s.Serialize(); } catch (Exception) { continue; }
            CheckCopy(s);
            checkedStates++;
        }
        Assert.True(checkedStates > 200, $"only {checkedStates} fuzzed states serialisable");
    }

    [Fact]
    public void PlayingOnTheCopyLeavesTheOriginal()
    {
        var states = DuelStates().Where(s => s.Phase != BattlePhase.Finished).ToList();
        var ai = new GreedyBattleAI { MaxCandidates = 4 };
        foreach (var s in states.Where((_, i) => i % 7 == 0))
        {
            string before = s.Serialize();
            var copy = s.Clone();
            var b = new Battle(f.Db, copy);
            BattleRunner.RunToEnd(b, ai, ai, maxCommands: 10);
            Fuzz(copy, new Random(3), 0);
            Assert.Equal(before, s.Serialize());
        }
    }

    /// <summary>The copier keeps fields JSON would drop only if state types have non-public fields; they must not.</summary>
    [Fact]
    public void StateTypesHaveOnlyPublicDataFields()
    {
        foreach (var t in StateTypes())
            foreach (var fi in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                Assert.True(fi.Name.EndsWith(">k__BackingField") || t.IsValueType, $"{t.Name}.{fi.Name} is not public data");
    }

    void CheckCopy(BattleState s)
    {
        var json = JsonRoundTrip(s);
        var copy = s.Clone();
        AssertSame(json, copy, "state");
        Assert.Equal(s.Serialize(), copy.Serialize());
        AssertDisjoint(s, copy, "state", new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    // ---------------------------------------------------------------- reflection helpers

    static bool Immutable(Type t) => t == typeof(string) || t == typeof(SkillEntry) || t.IsPrimitive || t.IsEnum || t.IsValueType;

    static IEnumerable<FieldInfo> Fields(Type t) => t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    static IEnumerable<Type> StateTypes()
    {
        var seen = new HashSet<Type>();
        var todo = new Stack<Type>(new[] { typeof(BattleState) });
        while (todo.Count > 0)
        {
            var t = todo.Pop();
            if (t == typeof(string) || t.IsPrimitive || t.IsEnum || !seen.Add(t)) continue;
            foreach (var a in t.IsArray ? new[] { t.GetElementType()! } : t.GetGenericArguments()) todo.Push(a);
            if (t.IsGenericType && t.Namespace == "System.Collections.Generic" || t.IsArray || t.Namespace == "System") continue;
            foreach (var fi in Fields(t)) todo.Push(fi.FieldType);
        }
        return seen.Where(t => !t.IsArray && !(t.IsGenericType && t.Namespace == "System.Collections.Generic") && t.Namespace != "System");
    }

    static void AssertSame(object? a, object? b, string path)
    {
        if (a == null || b == null) { Assert.True(a == null && b == null, $"{path}: null mismatch"); return; }
        var t = a.GetType();
        Assert.True(t == b.GetType(), $"{path}: type {t} vs {b.GetType()}");
        if (a is double da) { Assert.True(BitConverter.DoubleToInt64Bits(da) == BitConverter.DoubleToInt64Bits((double)b), $"{path}: {da} vs {b}"); return; }
        if (a is float fa) { Assert.True(BitConverter.SingleToInt32Bits(fa) == BitConverter.SingleToInt32Bits((float)b), $"{path}: {fa} vs {b}"); return; }
        if (t.IsPrimitive || t.IsEnum || a is string) { Assert.True(a.Equals(b), $"{path}: {a} vs {b}"); return; }
        if (a is IDictionary ad)
        {
            var bd = (IDictionary)b;
            var ka = ad.Keys.Cast<object>().ToList(); var kb = bd.Keys.Cast<object>().ToList();
            Assert.True(ka.SequenceEqual(kb), $"{path}: dictionary keys/order differ");
            foreach (var k in ka) AssertSame(ad[k], bd[k], $"{path}[{k}]");
            var ca = t.GetProperty("Comparer")!.GetValue(a); var cb = t.GetProperty("Comparer")!.GetValue(b);
            Assert.True(Equals(ca, cb), $"{path}: comparer differs");
            return;
        }
        if (a is IList al)
        {
            var bl = (IList)b;
            Assert.True(al.Count == bl.Count, $"{path}: count {al.Count} vs {bl.Count}");
            for (int i = 0; i < al.Count; i++) AssertSame(al[i], bl[i], $"{path}[{i}]");
            return;
        }
        foreach (var fi in Fields(t)) AssertSame(fi.GetValue(a), fi.GetValue(b), $"{path}.{fi.Name}");
    }

    static void AssertDisjoint(object? a, object? b, string path, HashSet<object> seen)
    {
        if (a == null || b == null) return;
        var t = a.GetType();
        if (Immutable(t) && !(t.IsValueType && Fields(t).Any(fi => !Immutable(fi.FieldType)))) return;
        if (!t.IsValueType)
        {
            Assert.False(ReferenceEquals(a, b) && !(a is Array { Length: 0 }), $"{path}: shared with the original");
            if (!seen.Add(a)) return;
        }
        if (a is IDictionary ad) { foreach (var k in ad.Keys) AssertDisjoint(ad[k], ((IDictionary)b)[k], $"{path}[{k}]", seen); return; }
        if (a is IList al) { var bl = (IList)b; for (int i = 0; i < al.Count; i++) AssertDisjoint(al[i], bl[i], $"{path}[{i}]", seen); return; }
        foreach (var fi in Fields(t)) AssertDisjoint(fi.GetValue(a), fi.GetValue(b), $"{path}.{fi.Name}", seen);
    }

    // ---------------------------------------------------------------- fuzzing

    static readonly double[] Doubles = { 0, -0.0, 1, 0.1, 1.0 / 3, double.PositiveInfinity, double.NegativeInfinity, double.NaN, 1e300, double.Epsilon };

    /// <summary>Gives every field of <paramref name="o"/> (recursively) a random value, nulls and empty collections included.</summary>
    static void Fuzz(object o, Random r, int depth)
    {
        var t = o.GetType();
        foreach (var fi in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (fi.IsInitOnly) continue;
            var cur = fi.GetValue(o);
            if (cur != null && !Immutable(fi.FieldType) && r.Next(3) == 0) { FuzzInside(cur, r, depth); continue; }
            fi.SetValue(o, Value(fi.FieldType, r, depth));
        }
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            if (p.CanWrite && p.GetIndexParameters().Length == 0) p.SetValue(o, Value(p.PropertyType, r, depth));
    }

    static void FuzzInside(object o, Random r, int depth)
    {
        if (o is IList l) { for (int i = 0; i < l.Count; i++) if (l[i] is { } x && !Immutable(x.GetType())) Fuzz(x, r, depth + 1); return; }
        if (o is IDictionary) return;
        Fuzz(o, r, depth + 1);
    }

    static object? Value(Type t, Random r, int depth)
    {
        var nt = Nullable.GetUnderlyingType(t);
        if (nt != null) return r.Next(3) == 0 ? null : Value(nt, r, depth);
        if (t == typeof(int)) return r.Next(5) == 0 ? r.Next(int.MinValue, int.MaxValue) : r.Next(-3, 40);
        if (t == typeof(long)) return (long)r.Next() * r.Next(-3, 3);
        if (t == typeof(ulong)) return (ulong)r.NextInt64() * 3;
        if (t == typeof(double)) return r.Next(2) == 0 ? Doubles[r.Next(Doubles.Length)] : r.NextDouble() * 1000 - 500;
        if (t == typeof(float)) return (float)(r.NextDouble() * 10);
        if (t == typeof(bool)) return r.Next(2) == 0;
        if (t.IsEnum) { var v = Enum.GetValues(t); return v.GetValue(r.Next(v.Length)); }
        if (t == typeof(string)) return r.Next(8) == 0 ? null : r.Next(6) == 0 ? "" : "s" + r.Next(50) + (r.Next(4) == 0 ? "é\"\\" : "");
        if (t == typeof(Cell)) return new Cell(r.Next(-2, 16), r.Next(-2, 16));
        if (t == typeof(SkillEntry)) return new SkillEntry("SK" + r.Next(9), (Mastery)r.Next(4));
        bool nullable = r.Next(10) == 0 || depth > 3;
        if (t.IsArray)
        {
            if (nullable) return null;
            var e = t.GetElementType()!;
            var a = Array.CreateInstance(e, r.Next(0, 4));
            for (int i = 0; i < a.Length; i++) a.SetValue(Value(e, r, depth + 1), i);
            return a;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            if (nullable) return null;
            var e = t.GetGenericArguments()[0];
            var l = (IList)Activator.CreateInstance(t)!;
            int n = r.Next(0, 4);
            for (int i = 0; i < n; i++) l.Add(Value(e, r, depth + 1));
            return l;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            if (nullable) return null;
            var d = (IDictionary)Activator.CreateInstance(t)!;
            // Insertions with removals in between, so the enumeration order is not the insertion order.
            for (int i = 0; i < 6; i++) d["k" + r.Next(20)] = r.Next(100);
            foreach (var k in d.Keys.Cast<object>().Where((_, i) => i % 2 == 0).ToList()) d.Remove(k);
            for (int i = 0; i < 3; i++) d["n" + r.Next(20)] = r.Next(100);
            return d;
        }
        if (t.IsClass)
        {
            if (nullable) return null;
            var o = Activator.CreateInstance(t)!;
            Fuzz(o, r, depth + 1);
            return o;
        }
        throw new InvalidOperationException("no fuzz value for " + t);
    }
}
