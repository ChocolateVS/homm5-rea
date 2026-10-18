using Godot;

namespace H5Reborn.Interaction;

/// <summary>
/// The original keyboard bindings, read at runtime from the install's
/// profiles/default_profile/input_a2.cfg (the file the stock game itself loads).
/// Each <c>bindsection</c> (adventure_screen, town_screen, hero_screen, …) maps a key
/// chord to one or more action names; screens dispatch those names to the same
/// handlers their UI buttons use, so keys and buttons can never disagree.
/// Lines before the first <c>bindsection</c> form the global section.
/// Continuous camera bindings (+camera_*) are handled by the camera code, not here.
/// </summary>
public static class KeyBindings
{
    public readonly record struct Chord(Key Key, bool Ctrl, bool Shift, bool Alt);

    static Dictionary<string, List<(Chord Chord, string Action)>>? _sections;

    public const string Global = "";

    static Dictionary<string, List<(Chord, string)>> Sections => _sections ??= Load();

    /// <summary>Actions bound to this key event in a section, in file order (empty when none).</summary>
    public static List<string> Actions(string section, InputEventKey k)
    {
        var result = new List<string>();
        if (!Sections.TryGetValue(section, out var list)) return result;
        var key = k.Keycode == Key.None ? k.PhysicalKeycode : k.Keycode;
        foreach (var (c, action) in list)
            if (c.Key == key && c.Ctrl == k.CtrlPressed && c.Shift == k.ShiftPressed && c.Alt == k.AltPressed)
                result.Add(action);
        return result;
    }

    /// <summary>All chords bound to an action in a section (for tooltips/tests).</summary>
    public static IEnumerable<Chord> ChordsFor(string section, string action) =>
        Sections.TryGetValue(section, out var l) ? l.Where(e => e.Item2 == action).Select(e => e.Item1) : Enumerable.Empty<Chord>();

    static Dictionary<string, List<(Chord, string)>> Load()
    {
        var map = new Dictionary<string, List<(Chord, string)>>(StringComparer.OrdinalIgnoreCase) { [Global] = new() };
        string? path = null;
        try
        {
            var root = Game.Instance.Install.Root;
            // A profile's own copy wins when present (the stock game writes one per profile).
            var profile = Profiles.ProfileManager.CurrentName;
            var candidates = new[]
            {
                System.IO.Path.Combine(root, "profiles", profile ?? "", "input_a2.cfg"),
                System.IO.Path.Combine(root, "profiles", "default_profile", "input_a2.cfg"),
            };
            path = candidates.FirstOrDefault(System.IO.File.Exists);
        }
        catch { /* no install: no bindings */ }
        if (path == null) return map;
        Parse(System.IO.File.ReadAllLines(path), map);
        return map;
    }

    /// <summary>Parses input_a2.cfg text (exposed for tests).</summary>
    public static void Parse(IEnumerable<string> lines, Dictionary<string, List<(Chord, string)>> map)
    {
        var section = Global;
        foreach (var raw in lines)
        {
            var line = raw;
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("bindsection", StringComparison.OrdinalIgnoreCase))
            {
                section = line["bindsection".Length..].Trim();
                if (!map.ContainsKey(section)) map[section] = new();
                continue;
            }
            if (!line.StartsWith("bind ", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("bind\t", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line[4..].Trim();
            int q = rest.IndexOf('\'');
            if (q <= 0) continue;
            var action = rest[..q].Trim();
            // Continuous (+/-) bindings drive the camera code and mouse axes; skip them here.
            if (action.StartsWith('+') || action.StartsWith('-') || action == "none") continue;
            var keys = new List<string>();
            int i = q;
            while (i < rest.Length)
            {
                int a = rest.IndexOf('\'', i);
                if (a < 0) break;
                int b = rest.IndexOf('\'', a + 1);
                if (b < 0) break;
                keys.Add(rest[(a + 1)..b]);
                i = b + 1;
            }
            bool ctrl = false, shift = false, alt = false;
            Key key = Key.None;
            foreach (var name in keys)
            {
                switch (name.ToUpperInvariant())
                {
                    case "CTRL" or "LCTRL" or "RCTRL": ctrl = true; break;
                    case "SHIFT": shift = true; break;
                    case "ALT": alt = true; break;
                    default: key = KeyOf(name); break;
                }
            }
            if (key == Key.None) continue;
            map[section].Add((new Chord(key, ctrl, shift, alt), action));
        }
    }

    static Key KeyOf(string name)
    {
        var n = name.ToUpperInvariant();
        if (n.Length == 1 && n[0] >= 'A' && n[0] <= 'Z') return Key.A + (n[0] - 'A');
        if (n.Length == 1 && n[0] >= '0' && n[0] <= '9') return Key.Key0 + (n[0] - '0');
        if (n.Length >= 2 && n[0] == 'F' && int.TryParse(n[1..], out var f) && f is >= 1 and <= 12) return Key.F1 + (f - 1);
        return n switch
        {
            "SPACE" => Key.Space, "TAB" => Key.Tab, "ENTER" => Key.Enter, "NUM_ENTER" => Key.KpEnter,
            "ESC" => Key.Escape, "BACKSPACE" => Key.Backspace, "DELETE" => Key.Delete, "INSERT" => Key.Insert,
            "HOME" => Key.Home, "END" => Key.End, "PG_UP" => Key.Pageup, "PG_DOWN" => Key.Pagedown,
            "UP" => Key.Up, "DOWN" => Key.Down, "LEFT" => Key.Left, "RIGHT" => Key.Right,
            "PAUSE" => Key.Pause, "SYSRQ" => Key.Print,
            _ => Key.None,
        };
    }
}
