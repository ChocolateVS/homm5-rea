namespace H5.Scripting.Lua;

/// <summary>An activation record of a Lua function (the original's explicit CallInfo).</summary>
public sealed class CallFrame
{
    public LuaClosure Closure = null!;
    /// <summary>Absolute stack index of local 0.</summary>
    public int Base;
    public int Pc;
    /// <summary>Absolute stack index of the called function (results are moved here).</summary>
    public int FuncSlot;
    /// <summary>Results the caller wants (-1 = all).</summary>
    public int Want;
}

/// <summary>
/// A script thread (CLuaThread in the original). Each owns a value stack and a frame list, so
/// it can be suspended at any instruction boundary — the original made Lua 4.0 stackless
/// for this — and resumed by the per-segment scheduler.
/// </summary>
public sealed class LuaThread : LuaObject
{
    public LuaValue[] Stack = new LuaValue[64];
    public int Top;
    public readonly List<CallFrame> Frames = new();
    /// <summary>Segments left to sleep (the field set by sleep()).</summary>
    public int Sleep;
    /// <summary>Set when a runtime error aborted the thread.</summary>
    public bool Failed;
    /// <summary>Diagnostic label: what started this thread (chunk, function name, callback text).</summary>
    public string Label = "";

    public bool Finished => Frames.Count == 0;

    public void EnsureStack(int needed)
    {
        if (needed <= Stack.Length) return;
        int n = Stack.Length;
        while (n < needed) n *= 2;
        Array.Resize(ref Stack, n);
    }

    public void Push(in LuaValue v)
    {
        if (Top == Stack.Length) Array.Resize(ref Stack, Stack.Length * 2);
        Stack[Top++] = v;
    }

    public override string ToString() => $"thread#{Id} {Label} frames={Frames.Count} sleep={Sleep}";
}

/// <summary>Arguments and results of a host (C) function call, with lua_to* style accessors.</summary>
public sealed class HostContext
{
    public LuaState L { get; }
    public LuaThread Thread { get; }
    public readonly LuaValue[] Args;
    public readonly List<LuaValue> Results = new();
    public string FunctionName { get; }

    internal HostContext(LuaState l, LuaThread thread, LuaValue[] args, string name)
    {
        L = l;
        Thread = thread;
        Args = args;
        FunctionName = name;
    }

    public int Count => Args.Length;
    public LuaValue Arg(int i) => i < Args.Length ? Args[i] : LuaValue.Nil;
    public bool IsNil(int i) => Arg(i).IsNil;
    public bool Has(int i) => i < Args.Length && !Args[i].IsNil;

    /// <summary>lua_tonumber: a number or numeric string, else 0.</summary>
    public double Number(int i) => Arg(i).TryToNumber(out var d) ? d : 0;

    /// <summary>Number truncated like the engine's (int) casts of lua_tonumber.</summary>
    public int Int(int i)
    {
        var d = Number(i);
        if (double.IsNaN(d)) return int.MinValue;
        if (d >= 2147483648.0 || d <= -2147483649.0) return int.MinValue;
        return (int)d;
    }

    public int Int(int i, int defaultValue) => Has(i) ? Int(i) : defaultValue;

    /// <summary>lua_tostring: a string, a number formatted with %.16g, else null.</summary>
    public string? String(int i) => Arg(i).TryToStr(out var s) ? s : null;

    /// <summary>String argument where nil/absent gives "".</summary>
    public string Str(int i) => String(i) ?? "";

    public bool Bool(int i) => Arg(i).IsTruthy;

    public void Return(LuaValue v) => Results.Add(v);
    public void Return(params LuaValue[] vs) => Results.AddRange(vs);
    public void ReturnBool(bool b) => Results.Add(LuaValue.Bool(b));

    public LuaRuntimeException Error(string msg) => new(msg);
}
