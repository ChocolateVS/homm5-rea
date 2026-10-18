using System.Globalization;
using System.Text;
using H5.Core.Xdb;

namespace H5.Core.Ui;

public enum UiTextAlign { Left, Center, Right, Justify }

/// <summary>Vertical placement tags &lt;top&gt; &lt;middle&gt; &lt;bottom&gt; (Default = use the text area's own alignment).</summary>
public enum UiTextVAlign { Default, Top, Middle, Bottom }

public sealed record UiTextStyle
{
    public string Face { get; init; } = "Default";
    public int Size { get; init; } = 18;
    public uint Color { get; init; } = 0xFFC5BEA1; // color_default
    public UiTextAlign Align { get; init; } = UiTextAlign.Left;
    public UiTextVAlign VAlign { get; init; } = UiTextVAlign.Default;
    public bool Outline { get; init; }
    public uint OutlineColor { get; init; } = 0xFF000000;
    public int OutlineSize { get; init; } = 1;
}

/// <summary>A styled piece of text. <see cref="LineBreak"/> runs end a line.</summary>
public sealed record UiTextRun(string Text, UiTextStyle Style, bool LineBreak = false, string? ValueName = null);

/// <summary>
/// Parser for the H5 UI text markup. Built-in tags: <c>&lt;font face= size= outlinecolor=
/// outlinesize= forcefontsize=&gt;</c>, <c>&lt;color=AARRGGBB&gt;</c>, <c>&lt;left|center|right|justify&gt;</c>,
/// <c>&lt;top|middle|bottom&gt;</c> (vertical), <c>&lt;br&gt;</c>, <c>&lt;push&gt;/&lt;pop&gt;</c> (style stack), <c>&lt;value=name&gt;</c> (runtime
/// substitution), <c>&lt;tab&gt;</c>, wrap hints (<c>nowrap, forcewrap, wrapdots=, wrapright</c>).
/// Every other tag is a macro: the text of
/// <c>UI/Consts/mlTags[/Fonts|/Colors|/Styles|/Special]/&lt;tag&gt;.txt</c>, expanded recursively
/// (e.g. menu → &lt;h2&gt;&lt;color_menu&gt; → &lt;font face=Header size=28&gt;…).
/// Note: <c>UI/Consts/Tags/*.txt</c> are *format strings* referenced by TextFormat xdbs
/// (Tags/menu.txt is literally "&lt;menu&gt;"), not macro definitions.
/// </summary>
public sealed class UiMarkup
{
    static readonly string[] MacroDirs =
    {
        "UI/Consts/mlTags/", "UI/Consts/mlTags/Fonts/",
        "UI/Consts/mlTags/Colors/", "UI/Consts/mlTags/Styles/", "UI/Consts/mlTags/Special/",
    };

    readonly XdbLoader _xdb;
    readonly Dictionary<string, string?> _macros = new(StringComparer.OrdinalIgnoreCase);

    public UiMarkup(XdbLoader xdb) => _xdb = xdb;

    public string? Macro(string tag)
    {
        if (_macros.TryGetValue(tag, out var m)) return m;
        foreach (var d in MacroDirs)
        {
            var p = d + tag + ".txt";
            if (_xdb.FileSystem.Exists(p)) { m = _xdb.ReadText("/" + p); break; }
        }
        return _macros[tag] = m?.TrimEnd('\r', '\n');
    }

    public List<UiTextRun> Parse(string text, UiTextStyle? initial = null, IReadOnlyDictionary<string, string>? values = null)
    {
        var runs = new List<UiTextRun>();
        var style = initial ?? new UiTextStyle();
        var stack = new Stack<UiTextStyle>();
        var sb = new StringBuilder();
        void Flush() { if (sb.Length > 0) { runs.Add(new UiTextRun(sb.ToString(), style)); sb.Clear(); } }
        Expand(text, 0);
        Flush();
        return runs;

        void Expand(string s, int depth)
        {
            if (depth > 32) return;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\r') continue;
                if (c == '\n') { Flush(); runs.Add(new UiTextRun("", style, true)); continue; }
                if (c != '<') { sb.Append(c); continue; }
                int end = s.IndexOf('>', i + 1);
                if (end < 0) { sb.Append(c); continue; }
                string tag = s[(i + 1)..end].Trim();
                i = end;
                var (name, attrs) = SplitTag(tag);
                switch (name.ToLowerInvariant())
                {
                    case "br": Flush(); runs.Add(new UiTextRun("", style, true)); break;
                    case "tab": sb.Append('\t'); break;
                    case "push": Flush(); stack.Push(style); break;
                    case "pop": Flush(); if (stack.Count > 0) style = stack.Pop(); break;
                    case "left": Flush(); style = style with { Align = UiTextAlign.Left }; break;
                    case "center": Flush(); style = style with { Align = UiTextAlign.Center }; break;
                    // Vertical alignment tags, registered by the exe's markup parser (H5_Game.exe FUN_00cc1190).
                    case "top": Flush(); style = style with { VAlign = UiTextVAlign.Top }; break;
                    case "middle": Flush(); style = style with { VAlign = UiTextVAlign.Middle }; break;
                    case "bottom": Flush(); style = style with { VAlign = UiTextVAlign.Bottom }; break;
                    case "right": Flush(); style = style with { Align = UiTextAlign.Right }; break;
                    case "justify": Flush(); style = style with { Align = UiTextAlign.Justify }; break;
                    case "color":
                        if (attrs.TryGetValue("", out var col) && TryArgb(col, out var argb)) { Flush(); style = style with { Color = argb }; }
                        break;
                    case "font":
                        Flush();
                        if (attrs.TryGetValue("face", out var face)) style = style with { Face = face };
                        if (attrs.TryGetValue("size", out var size) && int.TryParse(new string(size.TakeWhile(char.IsDigit).ToArray()), out var sz)) style = style with { Size = sz };
                        if (attrs.TryGetValue("outlinecolor", out var oc))
                            style = style with { Outline = true, OutlineColor = oc.Equals("black", StringComparison.OrdinalIgnoreCase) ? 0xFF000000 : TryArgb(oc, out var o) ? o : 0xFF000000 };
                        if (attrs.TryGetValue("outlinesize", out var os) && int.TryParse(os, out var osz)) style = style with { OutlineSize = osz };
                        break;
                    case "value":
                        Flush();
                        attrs.TryGetValue("", out var vname);
                        runs.Add(new UiTextRun(values != null && vname != null && values.TryGetValue(vname, out var v) ? v : "", style, false, vname));
                        break;
                    case "nowrap": case "forcewrap": case "wrapdots": case "wrapright": case "wrapleft": case "dots":
                    case "image": case "minfontsize": break;
                    default:
                        var macro = Macro(name);
                        if (macro != null) Expand(macro, depth + 1);
                        break;
                }
            }
        }
    }

    static (string name, Dictionary<string, string> attrs) SplitTag(string tag)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int eq = tag.IndexOf('='), sp = tag.IndexOf(' ');
        // <color=FF00FF00> / <value=x> : the value is attached to the tag name.
        if (eq > 0 && (sp < 0 || eq < sp))
        {
            var n = tag[..eq];
            var rest = tag[(eq + 1)..];
            int s2 = rest.IndexOf(' ');
            attrs[""] = s2 < 0 ? rest : rest[..s2];
            return (n, attrs);
        }
        var parts = tag.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts.Skip(1))
        {
            var kv = p.Split('=', 2);
            attrs[kv[0]] = kv.Length > 1 ? kv[1] : "";
        }
        return (parts.Length > 0 ? parts[0] : "", attrs);
    }

    /// <summary>Named colours of the markup parser (H5_Game.exe 00cc3610); anything else is hex ARGB.</summary>
    static readonly Dictionary<string, uint> NamedColors = new(StringComparer.Ordinal)
    {
        ["white"] = 0xFFFFFFFF, ["red"] = 0xFFFF0000, ["green"] = 0xFF00FF00, ["blue"] = 0xFF0000FF,
        ["yellow"] = 0xFFFFFF00, ["cyan"] = 0xFF00FFFF, ["orange"] = 0xFFFF8040, ["pink"] = 0xFFFF80FF,
        ["brown"] = 0xFF6E6E38, ["grey"] = 0xFFB4B4B4, ["black"] = 0xFF000000, ["darkyellow"] = 0xFFF2C011,
        ["beige"] = 0xFFD1B7A7, ["lightblue"] = 0xFF0065D5,
    };

    static bool TryArgb(string s, out uint argb) =>
        NamedColors.TryGetValue(s, out argb) || uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out argb);
}
