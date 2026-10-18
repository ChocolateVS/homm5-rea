using Godot;
using H5.Core.Ui;

namespace H5Reborn.Ui;

/// <summary>
/// Shared UI services built on the original data: window loader, markup macro
/// expander, bitmap font table, and texture/text caches. Lazily created on first use.
/// </summary>
public sealed class UiResources
{
    static UiResources? _instance;
    public static UiResources Instance => _instance ??= new UiResources();

    public UiLoader Loader { get; }
    public UiMarkup Markup { get; }
    public UiFontTable Fonts { get; }
    readonly Dictionary<string, string?> _texts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, UiWindow> _windows = new(StringComparer.OrdinalIgnoreCase);

    UiResources()
    {
        var xdb = Game.Instance.Xdb;
        Loader = new UiLoader(xdb);
        Markup = new UiMarkup(xdb);
        Fonts = UiFontTable.Load(xdb);
        UiMarkup.HotkeyResolver ??= Interaction.KeyBindings.HotkeyTexts;
    }

    /// <summary>
    /// Loads a fresh window tree (trees are mutable per screen instance, so each
    /// call returns a new copy; the parsed xdb documents are cached by XdbLoader).
    /// </summary>
    public UiWindow LoadWindow(string path) => Loader.Load(path);

    public Texture2D? Texture(string? ddsOrXdbPath) =>
        string.IsNullOrEmpty(ddsOrXdbPath) ? null : Game.Instance.LoadTexture(ddsOrXdbPath);

    /// <summary>Reads a UTF-16 text file from the paks (cached). Accepts a pak path with or without leading '/'.</summary>
    public string? Text(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_texts.TryGetValue(path, out var t)) return t;
        t = Game.Instance.Xdb.ReadText(path.StartsWith('/') ? path : "/" + path);
        return _texts[path] = t?.TrimEnd('\r', '\n');
    }

    readonly Dictionary<string, bool> _additive = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True for textures exported with ConversionType CONVERT_TRANSPARENT_ADD (glows,
    /// highlights, selection frames): alpha is 0 and the colour is meant to be added.
    /// </summary>
    public bool IsAdditive(string? textureXdbPath)
    {
        if (string.IsNullOrEmpty(textureXdbPath) || !textureXdbPath.EndsWith(".xdb", StringComparison.OrdinalIgnoreCase)) return false;
        if (_additive.TryGetValue(textureXdbPath, out var add)) return add;
        add = Game.Instance.Fs.Exists(textureXdbPath) &&
              (string?)Game.Instance.Xdb.Load(textureXdbPath).Root?.Element("ConversionType") == "CONVERT_TRANSPARENT_ADD";
        return _additive[textureXdbPath] = add;
    }

    /// <summary>
    /// Adds the texture's RGB to the destination. Godot's built-in add mode scales by
    /// source alpha, which these textures leave at 0, so alpha is ignored here.
    /// </summary>
    public static readonly ShaderMaterial AdditiveMaterial = new()
    {
        Shader = new Shader
        {
            Code = """
                shader_type canvas_item;
                render_mode blend_add;
                void fragment() { COLOR = vec4(COLOR.rgb, 1.0); }
                """,
        },
    };

    public static Color ToColor(uint argb) =>
        Color.Color8((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

    /// <summary>Strips markup tags, for plain-text uses such as Godot tooltips' accessibility text.</summary>
    public static string StripMarkup(string s)
    {
        var sb = new System.Text.StringBuilder();
        bool inTag = false;
        foreach (var c in s)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
