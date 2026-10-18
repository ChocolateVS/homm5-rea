using System.Text.RegularExpressions;
using H5.Adventure.Map;
using H5.Core.IO;
using H5.Core.Xdb;

namespace H5.Adventure.Scripting;

/// <summary>
/// Catalogues the original Lua scripts (global scripts/** and every map's MapScript.lua) and the
/// engine functions they call. Functions called but never defined by any script form the native
/// scripting API the adventure engine must provide (GetHeroLevel, SetObjectiveState, Trigger…).
/// This is an inventory only; executing scripts needs a Lua VM binding (not implemented yet).
/// </summary>
public sealed class LuaCatalog
{
    public sealed record ScriptFile(string Path, string Source);

    public List<ScriptFile> Scripts { get; } = new();
    /// <summary>Function name → number of call sites across all scripts.</summary>
    public Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Defined { get; } = new(StringComparer.Ordinal);
    /// <summary>Called but not defined in Lua: provided by the engine (or Lua's standard library).</summary>
    public IEnumerable<KeyValuePair<string, int>> NativeApi =>
        Calls.Where(kv => !Defined.Contains(kv.Key) && !LuaBuiltins.Contains(kv.Key)).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key);

    static readonly HashSet<string> LuaKeywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local",
        "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    };

    static readonly HashSet<string> LuaBuiltins = new(StringComparer.Ordinal)
    {
        "print", "type", "tostring", "tonumber", "pairs", "ipairs", "next", "unpack", "error", "assert",
        "pcall", "select", "setmetatable", "getmetatable", "rawget", "rawset", "require", "dofile",
        "getn", "tinsert", "tremove", "format", "mod", "floor", "random", "sqrt", "abs", "min", "max",
    };

    static readonly Regex Def = new(@"\bfunction\s+([A-Za-z_][A-Za-z0-9_.:]*)\s*\(|\b([A-Za-z_][A-Za-z0-9_]*)\s*=\s*function\s*\(", RegexOptions.Compiled);
    static readonly Regex Call = new(@"(?<![\w.:])([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);
    static readonly Regex Comments = new(@"--\[\[.*?\]\]|--[^\n]*", RegexOptions.Compiled | RegexOptions.Singleline);
    static readonly Regex Strings = new("\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)*'", RegexOptions.Compiled);

    public static LuaCatalog Build(PakFileSystem fs, IEnumerable<AdvMap> maps)
    {
        var cat = new LuaCatalog();
        foreach (var p in fs.Enumerate("scripts/").Where(p => p.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            cat.Add(p, TextDecoding.Decode(fs.ReadAllBytes(p)));
        foreach (var m in maps)
            if (m.ScriptSource != null) cat.Add(m.ScriptPath ?? m.Id, m.ScriptSource);
        return cat;
    }

    public void Add(string path, string source)
    {
        Scripts.Add(new ScriptFile(path, source));
        var code = Strings.Replace(Comments.Replace(source, ""), "\"\"");
        foreach (Match m in Def.Matches(code))
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            Defined.Add(name.Contains('.') || name.Contains(':') ? name.Split('.', ':')[^1] : name);
        }
        foreach (Match m in Call.Matches(code))
        {
            var name = m.Groups[1].Value;
            if (LuaKeywords.Contains(name)) continue;
            Calls[name] = Calls.GetValueOrDefault(name) + 1;
        }
    }
}
