namespace H5.Scripting.Lua;

/// <summary>
/// A Lua 4.0 universe as embedded by the original engine (CLuaScriptEngine): one global table
/// shared by all script threads, a creation-ordered thread list, and a per-segment scheduler.
///
/// Behaviour reproduced from H5_Game.exe:
/// - DoString / callbacks (FUN_009ac310): compile, append a new thread, run it at once until it
///   finishes or sleeps; a sleeping thread stays in the list.
/// - startThread (FUN_004bfd40): append a thread with the call prepared but NOT run; the
///   scheduler starts it (later in the same pass if appended during one).
/// - sleep (FUN_004bfe80): sets the current thread's segment counter (no argument = 1, else the
///   last argument rounded to nearest; non-numbers give 0); the VM loop (FUN_004c8cb0) stops at
///   the next instruction boundary while the counter is positive.
/// - Scheduler (FUN_004c0720): walk the list in order; a sleeping thread decrements its counter
///   and resumes when it reaches zero; finished or failed threads are removed.
/// - Runtime errors call the global _ERRORMESSAGE(msg) and abort the thread.
/// </summary>
public sealed partial class LuaState
{
    public LuaTable Globals { get; private set; }
    public readonly List<LuaThread> Threads = new();
    public LuaThread? Current { get; private set; }
    /// <summary>While positive, sleep() is ignored (the original's no-yield counter, +0x50).</summary>
    public int NoYield;
    /// <summary>Segments elapsed since creation (diagnostic).</summary>
    public long Segment;
    /// <summary>Instruction budget per resume before a thread is declared hung (the original would
    /// freeze the game forever; we abort the thread instead and report it).</summary>
    public long InstructionBudget = 50_000_000;

    /// <summary>Console output of print() and error reports.</summary>
    public Action<string>? Output;
    /// <summary>Called for every script error (message, thread label).</summary>
    public Action<string, string>? ErrorReported;
    /// <summary>consoleCmd(text): forwarded to the host console.</summary>
    public Action<string>? ConsoleCommand;
    /// <summary>Function installed by errorHook (nil = none); called with the message on errors.</summary>
    public LuaValue ErrorHookFunction = LuaValue.Nil;

    /// <summary>Compiled chunks by name, kept so closures can be re-linked after loading a save.</summary>
    public readonly Dictionary<string, (string Source, Proto Main)> Chunks = new(StringComparer.Ordinal);

    int _nextId = 1;

    public LuaState()
    {
        Globals = NewTable();
    }

    internal int NextId() => _nextId++;
    internal int PeekNextId => _nextId;
    internal void SetNextId(int id) => _nextId = id;

    public LuaTable NewTable(int sizeHint = 0)
    {
        var t = new LuaTable(sizeHint) { Id = _nextId++, Owner = this };
        return t;
    }

    // ---- collector ------------------------------------------------------------------------
    // The engine's luaC_checkGC (FUN_00464a00) counts calls instead of bytes: while the lock
    // (+0x48) is 0 every check increments +0x44 and the 1001st runs a full collection
    // (FUN_00464990) and resets the count. Checks happen at OP_CREATETABLE (before creating),
    // OP_CLOSURE and OP_CONCAT (after), every function return / C-call completion
    // (FUN_004c80a0 → FUN_009ac070), and chunk loads (FUN_009abcb0 / FUN_009abb60). The only
    // observable effect of a collection is that the mark pass (FUN_00464640) erases every
    // nil-valued node of every reachable table, so `for k,v in t` stops yielding them.

    /// <summary>Collector check count (+0x44).</summary>
    public int GcCount;
    readonly HashSet<LuaTable> _nilValued = new(ReferenceEqualityComparer.Instance);

    internal void NoteNilValue(LuaTable t) => _nilValued.Add(t);

    /// <summary>luaC_checkGC (FUN_00464a00).</summary>
    internal void GcCheck()
    {
        if (++GcCount > 1000)
        {
            Collect();
            GcCount = 0;
        }
    }

    /// <summary>A full collection's effect on live tables: nil-valued nodes are erased.</summary>
    public void Collect()
    {
        foreach (var t in _nilValued) t.CollectNilValues();
        _nilValued.Clear();
    }

    public LuaClosure NewClosure(Proto p, LuaValue[] upvalues) => new(p, upvalues) { Id = _nextId++ };

    public LuaThread NewThread(string label) => new() { Id = _nextId++, Label = label };

    /// <summary>Argument formats applied to natives registered from now on (exe parser FUN_0087ad40).</summary>
    public IReadOnlyDictionary<string, string>? ArgFormats;

    public void Register(string name, HostCall call)
    {
        var f = new HostFunction(name, call) { Id = 0 };
        if (ArgFormats != null && ArgFormats.TryGetValue(name, out var fmt)) f.Format = fmt;
        HostFunctions[name] = f;
        Globals.Set(name, LuaValue.Function(f));
    }

    public LuaValue GetGlobal(string name) => Globals.Get(name);
    public void SetGlobal(string name, LuaValue v) => Globals.Set(name, v);

    // ---- loading and running ------------------------------------------------------------

    readonly Dictionary<string, string> _keyBySource = new(StringComparer.Ordinal);
    /// <summary>Host functions by name, for re-linking function values after loading a snapshot.</summary>
    public readonly Dictionary<string, HostFunction> HostFunctions = new(StringComparer.Ordinal);

    /// <summary>
    /// Compiles a chunk and remembers its source under a key unique to that source text, so
    /// closures can be re-linked to their prototypes after a save is loaded.
    /// </summary>
    public Proto Compile(string source, string chunk)
    {
        var p = Compiler.Compile(source, chunk);
        if (!_keyBySource.TryGetValue(source, out var key))
        {
            key = chunk;
            for (int n = 2; Chunks.ContainsKey(key); n++) key = $"{chunk}#{n}";
            _keyBySource[source] = key;
            Chunks[key] = (source, p);
        }
        SetChunkKey(p, key);
        return p;
    }

    internal void ReplaceGlobals(LuaTable g) => Globals = g;

    internal void RestoreSourceIndex()
    {
        _keyBySource.Clear();
        foreach (var (key, (source, _)) in Chunks) _keyBySource.TryAdd(source, key);
    }

    static void SetChunkKey(Proto p, string key)
    {
        p.ChunkKey = key;
        foreach (var c in p.Protos) SetChunkKey(c, key);
    }

    /// <summary>
    /// lua_dobuffer as used by the engine: compile and run in a new thread immediately.
    /// Returns false on a compile error (reported through _ERRORMESSAGE like the original).
    /// The thread is left in the scheduler list if it went to sleep.
    /// </summary>
    public bool DoString(string source, string chunk)
    {
        Proto p;
        GcCheck();
        try { p = Compile(source, chunk); }
        catch (LuaSyntaxException e)
        {
            ReportError(e.Message, chunk);
            return false;
        }
        var t = NewThread(chunk);
        Threads.Add(t);
        t.Push(LuaValue.Function(NewClosure(p, Array.Empty<LuaValue>())));
        PrepareCall(t, 0, 0);
        Run(t);
        if (t.Finished || t.Failed) Threads.Remove(t);
        return true;
    }

    /// <summary>Calls a global function by name in a new thread, run at once (engine callbacks).</summary>
    public bool CallGlobal(string function, params LuaValue[] args)
    {
        var f = Globals.Get(function);
        if (!f.IsFunction)
        {
            ReportError($"attempt to call global `{function}' (a {f.TypeName()} value)", function);
            return false;
        }
        return CallFunction(f, function, args);
    }

    public bool CallFunction(LuaValue f, string label, params LuaValue[] args)
    {
        var t = NewThread(label);
        Threads.Add(t);
        t.Push(f);
        foreach (var a in args) t.Push(a);
        if (!PrepareCall(t, 0, 0)) { Threads.Remove(t); return !t.Failed; }
        Run(t);
        bool ok = !t.Failed;
        if (t.Finished || t.Failed) Threads.Remove(t);
        return ok;
    }

    /// <summary>startThread: queue a thread that will run when the scheduler reaches it.</summary>
    public LuaThread StartThread(LuaValue f, LuaValue[] args, string label)
    {
        var t = NewThread(label);
        Threads.Add(t);
        t.Push(f);
        foreach (var a in args) t.Push(a);
        // A host function is called immediately by the original's precall and leaves nothing to run.
        PrepareCall(t, 0, 0);
        return t;
    }

    /// <summary>One script segment: the original scheduler pass (FUN_004c0720).</summary>
    public void Step()
    {
        Segment++;
        for (int i = 0; i < Threads.Count;)
        {
            var t = Threads[i];
            if (t.Failed || t.Finished) { Threads.RemoveAt(i); continue; }
            if (t.Sleep > 0 && --t.Sleep > 0) { i++; continue; }
            Run(t);
            if (t.Failed || t.Finished)
            {
                // The thread may have been moved if callbacks appended/removed threads; find it again.
                int at = Threads.IndexOf(t);
                if (at >= 0) { Threads.RemoveAt(at); i = at; continue; }
            }
            i++;
        }
    }

    /// <summary>Runs segments until no thread is sleeping or <paramref name="maxSegments"/> pass.</summary>
    public int RunUntilIdle(int maxSegments)
    {
        int n = 0;
        while (Threads.Count > 0 && n < maxSegments) { Step(); n++; }
        return n;
    }

    // ---- errors ---------------------------------------------------------------------------

    internal void ReportError(string message, string label)
    {
        ErrorReported?.Invoke(message, label);
        var handler = Globals.Get("_ERRORMESSAGE");
        if (handler.IsFunction)
        {
            // The handler runs without the ability to sleep, on a scratch thread.
            NoYield++;
            try
            {
                var t = NewThread("_ERRORMESSAGE");
                t.Push(handler);
                t.Push(LuaValue.String(message));
                if (PrepareCall(t, 0, 0)) RunNoErrors(t);
            }
            finally { NoYield--; }
        }
        else Output?.Invoke("Script error: " + message);
    }

    void RunNoErrors(LuaThread t)
    {
        try { Execute(t); }
        catch (Exception e) when (e is LuaRuntimeException or InvalidOperationException)
        {
            Output?.Invoke("Script error: " + e.Message);
        }
        t.Frames.Clear();
    }
}
