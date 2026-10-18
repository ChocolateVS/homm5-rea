using System.Text;
using H5.Scripting.Lua;

namespace H5.Scripting.Runtime;

/// <summary>
/// The engine's base script functions, registered in every script context (registration table
/// at 0x00f7bcb8 in H5_Game.exe). There is no standard Lua library in the game: these 16
/// functions are all the "built-ins" scripts have besides each context's engine API.
/// Math functions read their arguments as 32-bit floats, like the original's argument parser.
/// </summary>
public static class BaseLibrary
{
    /// <summary>Loads a script file by pak path; null if missing.</summary>
    public delegate string? FileLoader(string path);

    public static void Register(LuaState L, FileLoader load, Func<uint> rawRandom)
    {
        L.Register("sleep", Sleep);
        L.Register("startThread", StartThread);
        L.Register("errorHook", ErrorHook);
        L.Register("isEqual", IsEqual);
        L.Register("_ERRORMESSAGE", ctx => ctx.L.Output?.Invoke("(Script) ERROR: " + ctx.Str(0)));
        L.Register("parse", Parse);
        L.Register("print", ctx => ctx.L.Output?.Invoke(PrintText(ctx)));
        L.Register("consoleCmd", ctx => ctx.L.ConsoleCommand?.Invoke(ctx.Str(0)));
        L.Register("doFile", ctx => DoFile(ctx, load));
        L.Register("mod", Mod);
        L.Register("sqrt", Sqrt);
        L.Register("floor", ctx => ctx.Return(Math.Floor((double)F(ctx, 0))));
        L.Register("ceil", ctx => ctx.Return(Math.Ceiling((double)F(ctx, 0))));
        L.Register("intg", ctx =>
        {
            float x = F(ctx, 0);
            ctx.Return(x < 0 ? Math.Ceiling((double)x) : Math.Floor((double)x));
        });
        L.Register("frac", ctx =>
        {
            float x = F(ctx, 0);
            double a = Math.Abs((double)x);
            ctx.Return((a - Math.Floor(a)) * (x < 0 ? -1.0 : 1.0));
        });
        L.Register("round", ctx => ctx.Return(Math.Floor((double)(F(ctx, 0) + 0.5f))));
        L.Register("random", ctx => Random(ctx, rawRandom));
    }

    static float F(HostContext ctx, int i) => (float)ctx.Number(i);

    /// <summary>
    /// FUN_004bfe80: no argument = 1 segment; otherwise the LAST argument rounded to nearest
    /// (x87 FISTP, ties to even); a non-number gives 0 (does not yield). Ignored when yielding is
    /// not allowed or the thread already failed.
    /// </summary>
    static void Sleep(HostContext ctx)
    {
        var L = ctx.L;
        var t = L.Current;
        if (L.NoYield >= 1 || t == null || t.Failed) return;
        if (ctx.Count == 0) { t.Sleep = 1; return; }
        var last = ctx.Arg(ctx.Count - 1);
        double d = last.TryToNumber(out var n) ? n : 0.0;
        t.Sleep = (int)Math.Round(d, MidpointRounding.ToEven);
    }

    /// <summary>FUN_004bfe20: first argument must be a function; extra arguments are passed to it.</summary>
    static void StartThread(HostContext ctx)
    {
        if (ctx.Count == 0) throw ctx.Error("Must be at least one parameter for StartThread");
        var f = ctx.Arg(0);
        if (!f.IsFunction) throw ctx.Error("StartThread first parameter must be a function");
        var label = f.O is LuaClosure c ? c.Proto.Name : ((HostFunction)f.O!).Name;
        ctx.L.StartThread(f, ctx.Args.Skip(1).ToArray(), "thread " + label);
    }

    static void ErrorHook(HostContext ctx)
    {
        if (ctx.Count != 1) throw ctx.Error("errorHook: not enough arguments, must be one exactly");
        var f = ctx.Arg(0);
        if (!f.IsFunction && !f.IsNil) throw ctx.Error("errorHook: arg type must be function or nil");
        ctx.L.ErrorHookFunction = f;
    }

    static void IsEqual(HostContext ctx) => ctx.ReturnBool(ctx.Arg(0).Equals(ctx.Arg(1)));

    /// <summary>parse(text): compiles a string and returns it as a function (nil on syntax error).</summary>
    static void Parse(HostContext ctx)
    {
        var src = ctx.Str(0);
        try
        {
            var p = ctx.L.Compile(src, "parse");
            ctx.Return(LuaValue.Function(ctx.L.NewClosure(p, Array.Empty<LuaValue>())));
        }
        catch (LuaSyntaxException) { ctx.Return(LuaValue.Nil); }
    }

    /// <summary>doFile(path): runs a script file from the game data, like DoString on its text.</summary>
    static void DoFile(HostContext ctx, FileLoader load)
    {
        var path = ctx.Str(0);
        var src = load(path);
        if (src == null)
        {
            ctx.L.Output?.Invoke("Can`t find script file: " + path);
            return;
        }
        ctx.L.DoString(src, path);
    }

    static void Mod(HostContext ctx)
    {
        float x = F(ctx, 0), y = F(ctx, 1);
        if (y == 0) throw ctx.Error("mod: division by zero");
        ctx.Return((double)x - Math.Floor((double)x / y) * (double)y);
    }

    static void Sqrt(HostContext ctx)
    {
        float x = F(ctx, 0);
        if (x < 0) throw ctx.Error("sqrt: negative argument");
        ctx.Return(Math.Sqrt((double)x));
    }

    /// <summary>
    /// FUN_004c0da0: random() = raw 32-bit value; random(a, b) = a + r % (b - a + 1) for the
    /// first two arguments; random(n) = r % n (the documented 0..n-1).
    /// </summary>
    static void Random(HostContext ctx, Func<uint> raw)
    {
        if (ctx.Count == 0) { ctx.Return((double)raw()); return; }
        if (ctx.Count == 1)
        {
            int n = ctx.Int(0);
            ctx.Return(n <= 0 ? 0 : (double)(raw() % (uint)n));
            return;
        }
        int lo = ctx.Int(0), hi = ctx.Int(1);
        uint span = (uint)(hi - lo) + 1u;
        ctx.Return(span == 0 ? lo : (double)(int)(raw() % span + (uint)lo));
    }

    /// <summary>print: the textual form of each argument, concatenated (strings and numbers literally).</summary>
    public static string PrintText(HostContext ctx)
    {
        var sb = new StringBuilder();
        foreach (var a in ctx.Args) Append(sb, a, 0);
        return sb.ToString();
    }

    static void Append(StringBuilder sb, LuaValue v, int depth)
    {
        switch (v.Type)
        {
            case LuaType.Nil: sb.Append("nil"); break;
            case LuaType.Number: sb.Append(LuaNumber.Format(v.N)); break;
            case LuaType.String: sb.Append(v.Str); break;
            case LuaType.Function: sb.Append(v.O is HostFunction ? "<C-function>" : "<function>"); break;
            case LuaType.Table:
                if (depth > 2) { sb.Append("{...}"); break; }
                sb.Append('{');
                bool first = true;
                foreach (var (k, val) in v.TableValue.Pairs())
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    Append(sb, k, depth + 1);
                    sb.Append('=');
                    Append(sb, val, depth + 1);
                }
                sb.Append('}');
                break;
            default: sb.Append("<userdata>"); break;
        }
    }
}
