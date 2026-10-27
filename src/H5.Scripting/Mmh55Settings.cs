using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace H5.Scripting;

/// <summary>One player setting of MMH55's <c>scripts/H55-Settings.lua</c>: the global, its value in the file, its comment.</summary>
/// <param name="Comment">The text after "--" on the setting's line.</param>
/// <param name="Notes">Comment lines between this setting and the next one (guidelines, warnings), separators dropped.</param>
public sealed record Mmh55Setting(string Name, string Default, string Comment, string Notes);

/// <summary>
/// MMH55's player options are the script globals of <c>scripts/H55-Settings.lua</c> ("All settings in this document are
/// script variables"), read by H55-Core / H55-AdvMap after <c>dofile("/scripts/H55-Settings.lua")</c>. MMH55 changes them
/// by rewriting that file (MMH55-Settings.pak, written by MMH55_Utility). H5Reborn keeps the mounted file as the defaults
/// and appends the player's changed values to its text when a script loads it, so the scripts see them exactly as if
/// the file had been edited.
/// </summary>
public static class Mmh55Settings
{
    public const string ScriptPath = "scripts/H55-Settings.lua";

    static readonly Regex Line = new(@"^\s*(H55_[A-Za-z0-9_]+)\s*=\s*(-?[0-9]+(?:\.[0-9]+)?)\s*;?\s*(?:--(.*))?$", RegexOptions.Compiled);
    static readonly Regex NameOk = new(@"^H55_[A-Za-z0-9_]+$", RegexOptions.Compiled);

    public static List<Mmh55Setting> Parse(string text)
    {
        var list = new List<(string Name, string Value, string Comment, StringBuilder Notes)>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var m = Line.Match(raw);
            if (m.Success)
            {
                if (list.All(s => s.Name != m.Groups[1].Value))
                    list.Add((m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value.Trim(), new StringBuilder()));
                continue;
            }
            var t = raw.Trim();
            if (list.Count == 0 || !t.StartsWith("--")) continue;
            t = t.TrimStart('-').Trim();
            if (t.Length == 0) continue;
            var notes = list[^1].Notes;
            if (notes.Length > 0) notes.Append('\n');
            notes.Append(t);
        }
        return list.Select(s => new Mmh55Setting(s.Name, s.Value, s.Comment, s.Notes.ToString())).ToList();
    }

    public static bool IsScript(string path) =>
        string.Equals(path.Replace('\\', '/').TrimStart('/'), ScriptPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>A valid override: an H55_ global and a plain number (invariant culture).</summary>
    public static bool Valid(string name, string value) =>
        NameOk.IsMatch(name) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d);

    /// <summary>The file text with the overrides appended as assignments (invalid entries skipped).</summary>
    public static string Apply(string text, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides == null || overrides.Count == 0) return text;
        var sb = new StringBuilder(text);
        sb.Append("\n-- H5Reborn: player options (Options -> MMH 5.5)\n");
        foreach (var (k, v) in overrides.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (Valid(k, v))
                sb.Append(k).Append(" = ").Append(double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)).Append(";\n");
        return sb.ToString();
    }
}
