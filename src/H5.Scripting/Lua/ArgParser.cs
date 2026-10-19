using System.Globalization;

namespace H5.Scripting.Lua;

/// <summary>
/// The engine's native argument parser (FUN_0087ad40, reached through FUN_00b0f690 →
/// FUN_00cab470), applied before a native's body. For format letter i (1-based argument i):
/// <list type="bullet">
/// <item>argument absent (beyond lua_gettop): the "[default]" text is used — n: atof, s: the text,
/// b: "false" = false, anything else true; without a default: lua_error "Not enough arguments when
/// calling function NAME.\n" (FUN_00879fb0).</item>
/// <item>n: lua_isnumber (FUN_004bf5c0, numeric strings are converted in place); s: lua_isstring
/// (string or number, FUN_004bf690); b: nil or a number (value = not nil); t: table; u: userdata;
/// any mismatch is lua_error "Wrong type of argument K, when calling function NAME.\n"
/// (FUN_0087a030; K is the single character '0'+i).</item>
/// <item>'.' stops checking (the rest is read by the native itself). Extra arguments are ignored.</item>
/// </list>
/// An explicit nil counts as present (Lua 4.0 lua_gettop), so nil for an `n` or `s` parameter is a
/// type error, not a default.
/// </summary>
public static class ArgParser
{
    public static LuaValue[] Apply(string format, string name, LuaValue[] args)
    {
        LuaValue[]? result = null;
        int i = 0; // argument index (0-based)
        for (int p = 0; p < format.Length;)
        {
            char kind = format[p++];
            string? def = null;
            if (p < format.Length && format[p] == '[')
            {
                int end = format.IndexOf(']', p + 1);
                if (end < 0) end = format.Length;
                def = format[(p + 1)..end];
                p = Math.Min(format.Length, end + 1);
            }
            if (kind == '.') break;
            if (i >= args.Length)
            {
                if (def == null) throw new LuaRuntimeException($"Not enough arguments when calling function {name}.\n");
                result = Grow(result ?? args, format);
                result[i] = kind switch
                {
                    'n' => LuaValue.Number((float)Atof(def)),
                    'b' => LuaValue.Bool(def != "false"),
                    's' => LuaValue.String(def),
                    _ => LuaValue.Nil,
                };
                i++;
                continue;
            }
            var a = args[i];
            bool ok = kind switch
            {
                'n' => a.TryToNumber(out _),
                's' => a.Type is LuaType.String or LuaType.Number,
                'b' => a.IsNil || a.TryToNumber(out _),
                't' => a.Type == LuaType.Table,
                'u' => a.Type == LuaType.Userdata,
                _ => true,
            };
            if (!ok) throw new LuaRuntimeException($"Wrong type of argument {(char)('0' + i + 1)}, when calling function {name}.\n");
            if (kind == 'n' && a.Type == LuaType.String && a.TryToNumber(out var d))
            {
                result ??= (LuaValue[])args.Clone();
                result[i] = LuaValue.Number(d);
            }
            i++;
        }
        return result ?? args;
    }

    static LuaValue[] Grow(LuaValue[] args, string format)
    {
        int letters = 0;
        for (int p = 0; p < format.Length; p++)
        {
            if (format[p] == '[') { int e = format.IndexOf(']', p); p = e < 0 ? format.Length : e; continue; }
            if (format[p] == '.') break;
            letters++;
        }
        if (args.Length >= letters) return args;
        var r = new LuaValue[letters];
        for (int k = 0; k < r.Length; k++) r[k] = k < args.Length ? args[k] : LuaValue.Nil;
        return r;
    }

    /// <summary>CRT atof: leading number prefix, 0 when none.</summary>
    static double Atof(string s)
    {
        s = s.TrimStart();
        int n = 0;
        while (n < s.Length && (char.IsDigit(s[n]) || s[n] is '.' or '-' or '+' or 'e' or 'E')) n++;
        for (; n > 0; n--)
            if (double.TryParse(s[..n], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        return 0;
    }
}
