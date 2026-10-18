using System.Text.Json;
using System.Text.Json.Serialization;

namespace H5.Scripting.Lua;

/// <summary>
/// Complete, deterministic image of a <see cref="LuaState"/>: every reachable table (with its
/// exact node layout, so traversal order survives), closure, thread (stack and frames) and the
/// source of every compiled chunk. Like the original engine (which serialises CLuaThread and
/// the Lua state into save games), a restored state resumes sleeping threads exactly.
/// Host functions are stored by name and re-linked to the restoring state's registrations.
/// </summary>
public sealed class LuaSnapshot
{
    public sealed class Value
    {
        public LuaType T { get; set; }
        public double N { get; set; }
        public string? S { get; set; }
        /// <summary>Heap object id (tables, closures) or host function name in <see cref="S"/>.</summary>
        public int R { get; set; }
    }

    public sealed class Table
    {
        public int Id { get; set; }
        public Value[] Keys { get; set; } = Array.Empty<Value>();
        public Value[] Vals { get; set; } = Array.Empty<Value>();
        public int[] Next { get; set; } = Array.Empty<int>();
        public int FirstFree { get; set; }
    }

    public sealed class Closure
    {
        public int Id { get; set; }
        public string Chunk { get; set; } = "";
        public int Proto { get; set; }
        public Value[] Upvalues { get; set; } = Array.Empty<Value>();
    }

    public sealed class Frame
    {
        public int Closure { get; set; }
        public int Base { get; set; }
        public int Pc { get; set; }
        public int FuncSlot { get; set; }
        public int Want { get; set; }
    }

    public sealed class Thread
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public int Sleep { get; set; }
        public bool Failed { get; set; }
        public Value[] Stack { get; set; } = Array.Empty<Value>();
        public Frame[] Frames { get; set; } = Array.Empty<Frame>();
    }

    public int NextId { get; set; }
    public long Segment { get; set; }
    public int NoYield { get; set; }
    public int Globals { get; set; }
    public Dictionary<string, string> Chunks { get; set; } = new();
    public List<Table> Tables { get; set; } = new();
    public List<Closure> Closures { get; set; } = new();
    public List<Thread> Threads { get; set; } = new();
    public Value? ErrorHook { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static LuaSnapshot FromJson(string json) => JsonSerializer.Deserialize<LuaSnapshot>(json, Json)!;

    // ---- capture ---------------------------------------------------------------------------

    public static LuaSnapshot Capture(LuaState L)
    {
        var snap = new LuaSnapshot { NextId = L.PeekNextId, Segment = L.Segment, NoYield = L.NoYield, Globals = L.Globals.Id };
        foreach (var (key, (source, _)) in L.Chunks) snap.Chunks[key] = source;
        var seen = new HashSet<int>();
        var pending = new Stack<LuaObject>();

        Value V(LuaValue v)
        {
            switch (v.Type)
            {
                case LuaType.Nil: return new Value { T = LuaType.Nil };
                case LuaType.Number: return new Value { T = LuaType.Number, N = v.N };
                case LuaType.String: return new Value { T = LuaType.String, S = v.Str };
                case LuaType.Table:
                case LuaType.Userdata when v.O is LuaObject:
                    pending.Push((LuaObject)v.O!);
                    return new Value { T = v.Type, R = ((LuaObject)v.O!).Id };
                case LuaType.Function:
                    if (v.O is HostFunction h) return new Value { T = LuaType.Function, S = h.Name };
                    pending.Push((LuaClosure)v.O!);
                    return new Value { T = LuaType.Function, R = ((LuaClosure)v.O!).Id };
                default:
                    return new Value { T = LuaType.Nil };
            }
        }

        void Drain()
        {
            while (pending.Count > 0)
            {
                var o = pending.Pop();
                if (!seen.Add(o.Id)) continue;
                switch (o)
                {
                    case LuaTable t:
                        {
                            var (keys, vals, next, ff) = t.RawNodes;
                            snap.Tables.Add(new Table
                            {
                                Id = t.Id,
                                Keys = keys.Select(V).ToArray(),
                                Vals = vals.Select(V).ToArray(),
                                Next = next.ToArray(),
                                FirstFree = ff,
                            });
                            break;
                        }
                    case LuaClosure c:
                        snap.Closures.Add(new Closure
                        {
                            Id = c.Id,
                            Chunk = c.Proto.ChunkKey,
                            Proto = c.Proto.Index,
                            Upvalues = c.Upvalues.Select(V).ToArray(),
                        });
                        break;
                }
            }
        }

        V(LuaValue.Table(L.Globals));
        if (!L.ErrorHookFunction.IsNil) snap.ErrorHook = V(L.ErrorHookFunction);
        foreach (var t in L.Threads)
        {
            snap.Threads.Add(new Thread
            {
                Id = t.Id,
                Label = t.Label,
                Sleep = t.Sleep,
                Failed = t.Failed,
                Stack = t.Stack.Take(t.Top).Select(V).ToArray(),
                Frames = t.Frames.Select(f =>
                {
                    pending.Push(f.Closure);
                    return new Frame { Closure = f.Closure.Id, Base = f.Base, Pc = f.Pc, FuncSlot = f.FuncSlot, Want = f.Want };
                }).ToArray(),
            });
        }
        Drain();
        // Deterministic output order regardless of discovery order.
        snap.Tables.Sort((a, b) => a.Id.CompareTo(b.Id));
        snap.Closures.Sort((a, b) => a.Id.CompareTo(b.Id));
        return snap;
    }

    // ---- restore -----------------------------------------------------------------------------

    /// <summary>
    /// Restores into a state whose host functions are already registered (same names as when
    /// captured). Globals, threads and the chunk registry are replaced.
    /// </summary>
    public void Restore(LuaState L)
    {
        // Recompile chunks and index their prototypes by pre-order index.
        var protos = new Dictionary<string, Dictionary<int, Proto>>(StringComparer.Ordinal);
        L.Chunks.Clear();
        foreach (var (key, source) in Chunks)
        {
            var main = Compiler.Compile(source, key.Contains('#') ? key[..key.IndexOf('#')] : key);
            var map = new Dictionary<int, Proto>();
            void Walk(Proto p) { p.ChunkKey = key; map[p.Index] = p; foreach (var c in p.Protos) Walk(c); }
            Walk(main);
            protos[key] = map;
            L.Chunks[key] = (source, main);
        }
        L.RestoreSourceIndex();

        var tables = Tables.ToDictionary(t => t.Id, t => new LuaTable(0) { Id = t.Id });
        var closures = new Dictionary<int, LuaClosure>();
        foreach (var c in Closures)
        {
            var p = protos[c.Chunk][c.Proto];
            closures[c.Id] = new LuaClosure(p, new LuaValue[c.Upvalues.Length]) { Id = c.Id };
        }

        LuaValue V(Value v) => v.T switch
        {
            LuaType.Number => LuaValue.Number(v.N),
            LuaType.String => LuaValue.String(v.S ?? ""),
            LuaType.Table => LuaValue.Table(tables[v.R]),
            LuaType.Function when v.S != null => L.HostFunctions.TryGetValue(v.S, out var h)
                ? LuaValue.Function(h)
                : throw new InvalidOperationException($"host function `{v.S}' is not registered"),
            LuaType.Function => LuaValue.Function(closures[v.R]),
            _ => LuaValue.Nil,
        };

        foreach (var c in Closures)
        {
            var target = closures[c.Id];
            for (int i = 0; i < c.Upvalues.Length; i++) target.Upvalues[i] = V(c.Upvalues[i]);
        }
        foreach (var t in Tables)
            tables[t.Id].RestoreNodes(t.Keys.Select(V).ToArray(), t.Vals.Select(V).ToArray(), t.Next.ToArray(), t.FirstFree);

        L.ReplaceGlobals(tables[Globals]);
        L.ErrorHookFunction = ErrorHook != null ? V(ErrorHook) : LuaValue.Nil;
        L.Threads.Clear();
        foreach (var t in Threads)
        {
            var th = new LuaThread { Id = t.Id, Label = t.Label, Sleep = t.Sleep, Failed = t.Failed };
            th.EnsureStack(t.Stack.Length + 8);
            for (int i = 0; i < t.Stack.Length; i++) th.Stack[i] = V(t.Stack[i]);
            th.Top = t.Stack.Length;
            foreach (var f in t.Frames)
                th.Frames.Add(new CallFrame { Closure = closures[f.Closure], Base = f.Base, Pc = f.Pc, FuncSlot = f.FuncSlot, Want = f.Want });
            L.Threads.Add(th);
        }
        L.SetNextId(NextId);
        L.Segment = Segment;
        L.NoYield = NoYield;
    }
}
