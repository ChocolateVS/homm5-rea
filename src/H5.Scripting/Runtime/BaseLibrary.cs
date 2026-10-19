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
        L.Register("consoleCmd", ConsoleCmd);
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

    /// <summary>
    /// FUN_00cabb60 → FUN_009abd30 (chunk "parse"): the compiled chunk is returned as a function.
    /// A syntax error is a lua_error inside the protected parser: it is reported (_ERRORMESSAGE)
    /// and sets the CALLING thread's stop flag; nothing is returned. The load runs the collector
    /// check first (FUN_009abcb0 → FUN_00464a00).
    /// </summary>
    static void Parse(HostContext ctx)
    {
        var src = ctx.Str(0);
        ctx.L.GcCheck();
        Proto p;
        try { p = ctx.L.Compile(src, "parse"); }
        catch (LuaSyntaxException e) { throw ctx.Error(e.Message); }
        ctx.Return(LuaValue.Function(ctx.L.NewClosure(p, Array.Empty<LuaValue>())));
    }

    /// <summary>
    /// FUN_00cabda0: the file is read whole (FUN_00b0f720 — a missing file logs "Couldn't open
    /// file PATH", an empty one "File PATH is empty.", and nothing runs), then handed to the
    /// engine's DoString (FUN_00cab4a0 → FUN_009ac310): compiled and run at
    /// once in a NEW thread appended to the list; the caller continues when it finishes or sleeps.
    /// A syntax or runtime error stops only that new thread. (The exe names the chunk "DoString
    /// script"; the file path is kept here as the chunk name because saves re-link closures by it.)
    /// </summary>
    static void DoFile(HostContext ctx, FileLoader load)
    {
        var path = ctx.Str(0);
        var src = load(path);
        if (src == null) { ctx.L.Output?.Invoke("Couldn't open file " + path); return; }
        if (src.Length == 0) { ctx.L.Output?.Invoke("File " + path + " is empty."); return; }
        ctx.L.DoString(src, path);
    }

    /// <summary>FUN_00cabf00: float arguments; y == 0 (not NaN) is an error; x - floor(x / y) * y in doubles.</summary>
    static void Mod(HostContext ctx)
    {
        float x = F(ctx, 0), y = F(ctx, 1);
        if (y == 0) throw ctx.Error("Divisor can`t be zero");
        ctx.Return((double)x - Math.Floor((double)x / y) * (double)y);
    }

    /// <summary>FUN_00cac090: x &lt; 0 is an error (NaN passes through FSQRT).</summary>
    static void Sqrt(HostContext ctx)
    {
        float x = F(ctx, 0);
        if (x < 0) throw ctx.Error("Value under root can`t be negative");
        ctx.Return(Math.Sqrt((double)x));
    }

    // ---- console ------------------------------------------------------------------------------

    /// <summary>
    /// Console variables changed through consoleCmd("setvar NAME = VALUE"). Like the exe's console
    /// they are process-wide and not saved. Defaults of the variables the scripts touch:
    /// ai_time_limit (0x00f440d0, ms) = 180000 — the AI turn's wall-clock limit (0x0086de76
    /// compares GetTickCount deltas with it); dev_console_password = "" (FUN_00669f50 opens the
    /// console only when it equals "schwinge-des-todes").
    /// </summary>
    public static readonly Dictionary<string, string> ConsoleVars = new(StringComparer.Ordinal)
    {
        ["ai_time_limit"] = "180000",
        ["dev_console_password"] = "",
    };

    /// <summary>
    /// FUN_00cabca0 → FUN_00bcf150(text, 3, -1, 0): the line goes to the console's command
    /// handlers at once. Only setvar changes state the game reads; the other commands the scripts
    /// issue (bind/bindsection/unbind: key bindings, game_writelog, console_size) are UI/logging.
    /// </summary>
    static void ConsoleCmd(HostContext ctx)
    {
        var line = ctx.Str(0);
        ExecuteConsoleCommand(line);
        ctx.L.ConsoleCommand?.Invoke(line);
    }

    /// <summary>setvar NAME [=] VALUE: the variable takes the first value token (FUN_00c11fa0).</summary>
    public static void ExecuteConsoleCommand(string line)
    {
        var tok = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length < 2 || !tok[0].Equals("setvar", StringComparison.OrdinalIgnoreCase)) return;
        int v = tok.Length > 2 && tok[2] == "=" ? 3 : 2;
        if (v >= tok.Length) return;
        lock (ConsoleVars) ConsoleVars[tok[1]] = tok[v];
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

    /// <summary>
    /// print (FUN_00cac850 → FUN_00cab590): one log line, the concatenation of every argument
    /// formatted by FUN_0087a180(value, name = null, quote = false, level = 0).
    /// </summary>
    public static string PrintText(HostContext ctx)
    {
        var sb = new StringBuilder();
        foreach (var a in ctx.Args) Format(sb, a, null, false, 0, 0);
        return sb.ToString();
    }

    /// <summary>
    /// FUN_0087a180: an indent of 2·level spaces, "name = " when named, then: userdata "[userdata]",
    /// C function "[C-function]", function "[function]", nil "nil", anything lua_isnumber accepts
    /// (numeric strings too) "%.16g", a string (in quotes when <paramref name="quote"/>), or a table:
    /// "\n" indent "{\n", the list part t[1..] when t[1] and t[2] are both non-nil (items separated by
    /// "\n"), then every pair from next() — numeric keys as "[%.16g]", list keys skipped — each followed
    /// by ",\n" (", ;\n" once before the first such pair after a list; ",\n" after a list with no other
    /// pairs), indent "}". Nested values are formatted at level 0 with quoting on, and a value at level
    /// 0 with quoting on ends with "\n" (all as the exe does).
    /// </summary>
    static void Format(StringBuilder sb, LuaValue v, string? name, bool quote, int level, int nesting)
    {
        sb.Append(' ', Math.Max(0, level * 2));
        if (name != null) sb.Append(name).Append(" = ");
        if (v.Type == LuaType.Userdata) sb.Append("[userdata]");
        else if (v.Type == LuaType.Function) sb.Append(v.O is HostFunction ? "[C-function]" : "[function]");
        else if (v.IsNil) sb.Append("nil");
        else if (v.TryToNumber(out var n)) sb.Append(LuaNumber.Format(n));
        else if (v.Type == LuaType.String)
        {
            if (quote) sb.Append('"').Append(v.Str).Append('"');
            else sb.Append(v.Str);
        }
        else if (v.Type == LuaType.Table)
        {
            var t = v.TableValue;
            bool inner = level != -1;
            sb.Append('\n').Append(' ', Math.Max(0, level * 2)).Append("{\n");
            if (nesting > 64) sb.Append("...\n"); // the exe recurses without limit (a cycle overflows its stack)
            else
            {
                int next = 1;
                if (!t.Get(1).IsNil && !t.Get(2).IsNil)
                    for (bool first = true; !t.Get(next).IsNil; next++, first = false)
                    {
                        if (!first) sb.Append('\n');
                        Format(sb, t.Get(next), null, inner, 0, nesting + 1);
                    }
                bool list = next > 1, separated = false;
                foreach (var (k, val) in t.Pairs())
                {
                    string key;
                    if (k.TryToNumber(out var kn))
                    {
                        if (list)
                        {
                            double tr = Math.Truncate(kn);
                            if (kn == tr && tr >= 1 && tr < next) continue;
                        }
                        key = "[" + LuaNumber.Format(kn) + "]";
                    }
                    else key = k.TryToStr(out var ks) ? ks : "";
                    if (list && !val.IsNil && !separated) { sb.Append(", ;\n"); separated = true; }
                    Format(sb, val, key, inner, 0, nesting + 1);
                    sb.Append(",\n");
                }
                if (list && !separated) sb.Append(",\n");
            }
            sb.Append(' ', Math.Max(0, level * 2)).Append('}');
        }
        if (level == 0 && quote) sb.Append('\n');
    }
}
