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

/// <summary>Line modes of the exe's markup state (tag handlers 00cc2b60..): alignment and wrapping share one field.</summary>
public enum UiMlMode { Left = 1, Right = 2, Center = 3, NoWrap = 4, Justify = 5, WrapLeft = 6, WrapRight = 7, ForceWrap = 8, WrapDots = 9 }

/// <summary>Font size units of &lt;font size=N..&gt; (00cc3a80): pt (no unit) scales with the UI, px and fx do not.</summary>
public enum UiMlSizeUnit { Pt, Px, Fx }

/// <summary>
/// The exe's markup state (CMLLayout state, initialised in 00cc0530): mode, vertical alignment within the line
/// (0 top, 1 bottom, 2 middle), font size/face, colour, outline. Defaults: left, middle, System 16pt, white
/// (ml_text_default_font_name = "System", 00cc41e0).
/// </summary>
public sealed record UiMlState
{
    public UiMlMode Mode { get; init; } = UiMlMode.Left;
    public int VAlign { get; init; } = 2;
    public int Size { get; init; } = 16;
    public UiMlSizeUnit SizeUnit { get; init; } = UiMlSizeUnit.Pt;
    public string Face { get; init; } = "System";
    public uint Color { get; init; } = 0xFFFFFFFF;
    public int OutlineSize { get; init; }
    public uint OutlineColor { get; init; }
    public bool ForceFontSize { get; init; }
    public int MinFontSize { get; init; }
}

public enum UiMlKind { Word, Space, Break, Tab }

public readonly record struct UiMlItem(UiMlKind Kind, string Text, UiMlState State);

public sealed class UiMlStream
{
    public readonly List<UiMlItem> Items = new();
    /// <summary>Text appended where a &lt;wrapdots&gt; line is cut (set by "wrapdots=...").</summary>
    public string Dots = "";
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

    /// <summary>
    /// Key text of each binding of an input action (one string per binding, keys already
    /// joined with T_HOTKEY_CONTROLS_DELIMITER), for &lt;hotkey=action&gt;. Set by the client.
    /// </summary>
    public static Func<string, IReadOnlyList<string>?>? HotkeyResolver;

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
                        var inserted = values != null && vname != null && values.TryGetValue(vname, out var v) ? v : "";
                        // Inserted values are markup too: stock texts insert formatted strings (building
                        // descriptions with <color_default>/<br> into <value=desc>, colour tags into
                        // <value=who>, message bodies), and the original parses them in place. Style
                        // changes inside a value carry on after it, as with plain concatenation; the
                        // stock templates reset the style after values where they need to.
                        if (inserted.IndexOf('<') >= 0) Expand(inserted, depth + 1);
                        else runs.Add(new UiTextRun(inserted, style, false, vname));
                        break;
                    case "hotkey":
                        // <hotkey=action> (exe tag handler FUN_0066db50): when the action has bindings,
                        // "<hotkeys_prefix>" + per binding "<hotkey_prefix>" keys "<hotkey_postfix>"
                        // joined by "<hotkeys_delimiter>" + "<hotkeys_postfix>"; nothing otherwise.
                        if (attrs.TryGetValue("", out var hk) && HotkeyResolver?.Invoke(hk) is { Count: > 0 } binds)
                        {
                            var hs = new StringBuilder("<hotkeys_prefix>");
                            for (int b = 0; b < binds.Count; b++)
                            {
                                if (b > 0) hs.Append("<hotkeys_delimiter>");
                                hs.Append("<hotkey_prefix>").Append(binds[b]).Append("<hotkey_postfix>");
                            }
                            hs.Append("<hotkeys_postfix>");
                            Expand(hs.ToString(), depth + 1);
                        }
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

    /// <summary>
    /// Tokenizes markup the way the exe's markup stream does (CMLStream, H5_Game.exe 00cc0820) into the element
    /// list its layout consumes (CMLLayout 00cc0c20, see <see cref="UiTextLayout"/>).
    /// Outside tags every character is a word character except ' ' (a space run), '\n' and the end (a line
    /// break), '\r', '&lt;' and '&gt;'. A token ends where the category changes, so a run of spaces is one space
    /// element and consecutive '\n' make ONE break; '\r' is dropped, and a '\r' directly after a tag (or at the
    /// start, or after another '\r') is dropped together with the character after it, so "&lt;br&gt;\r\n"
    /// breaks once. Tags take effect between tokens: a tag inside a word splits it. Macro tags, &lt;value=..&gt;
    /// and &lt;hotkey=..&gt; are spliced into the stream at the tag (00cbf0d0). The stream always ends with a break.
    /// </summary>
    public UiMlStream Tokenize(string text, UiMlState? initial = null, IReadOnlyDictionary<string, string>? values = null)
    {
        var result = new UiMlStream();
        var items = result.Items;
        var state = initial ?? new UiMlState();
        var stack = new Stack<UiMlState>();
        var sources = new Stack<(string s, int i)>();
        sources.Push((text, 0));
        var tok = new StringBuilder();
        var tag = new StringBuilder();
        int prev = 0, spliced = 0;
        bool inTag = false;

        bool Next(out char c)
        {
            while (sources.Count > 0)
            {
                var (s, i) = sources.Pop();
                if (i < s.Length) { sources.Push((s, i + 1)); c = s[i]; return true; }
            }
            c = '\0';
            return false;
        }
        // Splices are bounded so self-referencing macros cannot loop forever.
        void Splice(string s) { if (spliced++ < 4096 && s.Length > 0) sources.Push((s, 0)); }
        void Emit()
        {
            if (prev == 2) items.Add(new UiMlItem(UiMlKind.Word, tok.ToString(), state));
            else if (prev == 1) items.Add(new UiMlItem(UiMlKind.Space, tok.ToString(), state));
            else if (prev == 4) items.Add(new UiMlItem(UiMlKind.Break, "", state));
            tok.Clear();
        }

        while (true)
        {
            if (!Next(out char c))
            {
                // End of text (category 4): a pending word/space is emitted, then the closing break.
                if (!inTag && prev != 4) Emit();
                items.Add(new UiMlItem(UiMlKind.Break, "", state));
                break;
            }
            if (inTag)
            {
                if (c != '>') { tag.Append(c); continue; }
                inTag = false;
                prev = 6;
                HandleTag(tag.ToString().Trim());
                continue;
            }
            int cat = c switch { '<' => 7, '>' => 6, ' ' => 1, '\n' => 4, '\r' => 5, _ => 2 };
            if (cat == prev && cat is 1 or 2 or 4) { tok.Append(c); continue; }
            if (prev is 1 or 2 or 4) Emit();
            else if (cat == 5) { Next(out _); prev = 5; continue; }
            tok.Clear();
            prev = cat;
            if (cat == 7) { inTag = true; tag.Clear(); continue; }
            if (cat is 5 or 6) continue;
            tok.Append(c);
        }
        return result;

        void HandleTag(string t)
        {
            var (name, attrs) = SplitTag(t);
            int argc = t.Length == 0 ? 0 : 1 + attrs.Count * 2;
            switch (name.ToLowerInvariant())
            {
                case "br": items.Add(new UiMlItem(UiMlKind.Break, "", state)); break;
                case "left": state = state with { Mode = UiMlMode.Left }; break;
                case "right": state = state with { Mode = UiMlMode.Right }; break;
                case "center": state = state with { Mode = UiMlMode.Center }; break;
                case "nowrap": state = state with { Mode = UiMlMode.NoWrap }; break;
                case "justify": state = state with { Mode = UiMlMode.Justify }; break;
                case "wrapleft": state = state with { Mode = UiMlMode.WrapLeft }; break;
                case "wrapright": state = state with { Mode = UiMlMode.WrapRight }; break;
                case "forcewrap": state = state with { Mode = UiMlMode.ForceWrap }; break;
                case "wrapdots":
                    // CDotsHandler (00cbf580): mode 9; "=..." (one punctuation token) sets the dots text.
                    state = state with { Mode = UiMlMode.WrapDots };
                    if (attrs.TryGetValue("", out var dots)) result.Dots = dots;
                    break;
                case "top": state = state with { VAlign = 0 }; break;
                case "bottom": state = state with { VAlign = 1 }; break;
                case "middle": state = state with { VAlign = 2 }; break;
                case "tab": items.Add(new UiMlItem(UiMlKind.Tab, "", state)); break;
                case "push": stack.Push(state); break;
                case "pop": if (stack.Count > 0) state = stack.Pop(); break;
                case "color":
                    // CCOLORHandler (00cc3580) needs exactly "color = value".
                    if (attrs.Count == 1 && attrs.TryGetValue("", out var col) && TryArgb(col, out var argb)) state = state with { Color = argb };
                    break;
                case "font":
                    // CFONTHandler (00cc3a80): with any argument the outline is reset (size 0, colour = text colour)
                    // before the attributes apply. size=N[pt|px|fx]: no unit = pt (scaled with the UI).
                    if (argc > 1) state = state with { OutlineSize = 0, OutlineColor = state.Color };
                    if (attrs.TryGetValue("face", out var face)) state = state with { Face = face };
                    if (attrs.TryGetValue("size", out var size))
                    {
                        var digits = new string(size.TakeWhile(char.IsDigit).ToArray());
                        var unit = size[digits.Length..];
                        if (int.TryParse(digits, out var sz))
                            state = state with
                            {
                                Size = sz,
                                SizeUnit = unit switch { "fx" => UiMlSizeUnit.Fx, "px" => UiMlSizeUnit.Px, _ => UiMlSizeUnit.Pt },
                            };
                    }
                    if (attrs.TryGetValue("outlinesize", out var os) && int.TryParse(os, out var osz)) state = state with { OutlineSize = osz };
                    if (attrs.TryGetValue("outlinecolor", out var oc) && TryArgb(oc, out var ocv)) state = state with { OutlineColor = ocv };
                    if (attrs.ContainsKey("forcefontsize")) state = state with { ForceFontSize = true };
                    break;
                case "minfontsize":
                    if (argc > 1) state = state with { OutlineSize = 0, OutlineColor = state.Color };
                    if (attrs.TryGetValue("size", out var ms) && int.TryParse(ms, out var msz)) state = state with { MinFontSize = msz };
                    break;
                case "image":
                    break; // GAP: inline images (CIMAGEHandler 00cc4c00) are not drawn; no stock text uses them.
                case "value":
                    attrs.TryGetValue("", out var vname);
                    if (values != null && vname != null && values.TryGetValue(vname, out var v)) Splice(v);
                    break;
                case "hotkey":
                    if (attrs.TryGetValue("", out var hk) && HotkeyResolver?.Invoke(hk) is { Count: > 0 } binds)
                    {
                        var hs = new StringBuilder("<hotkeys_prefix>");
                        for (int b = 0; b < binds.Count; b++)
                        {
                            if (b > 0) hs.Append("<hotkeys_delimiter>");
                            hs.Append("<hotkey_prefix>").Append(binds[b]).Append("<hotkey_postfix>");
                        }
                        hs.Append("<hotkeys_postfix>");
                        Splice(hs.ToString());
                    }
                    break;
                default:
                    if (Macro(name) is { } macro) Splice(macro);
                    break;
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
