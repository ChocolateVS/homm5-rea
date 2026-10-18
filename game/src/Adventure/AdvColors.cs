using Godot;

namespace H5Reborn.Adventure;

/// <summary>
/// Player colours. Character xdbs list player-coloured model variants in the order
/// White, Red, Blue, Green, Yellow, Orange, Teal, Purple, Tan (Characters/Heroes/*_LOD
/// ColourModels); maps name players PCOLOR_*. The RGB values are used for the minimap
/// and list frames — TODO(exe): take the exact values from the original instead of these.
/// </summary>
public static class AdvColors
{
    static readonly string[] Order = { "WHITE", "RED", "BLUE", "GREEN", "YELLOW", "ORANGE", "TEAL", "PURPLE", "TAN" };

    /// <summary>Index into a Character's ColourModels for a PCOLOR_* value (0 = neutral/white).</summary>
    public static int ColourModelIndex(string pcolor)
    {
        var name = pcolor.Replace("PCOLOR_", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        if (name == "BROWN") name = "TAN";
        int i = Array.IndexOf(Order, name);
        return i < 0 ? 0 : i;
    }

    public static Color Rgb(string pcolor) => ColourModelIndex(pcolor) switch
    {
        1 => new Color(0.86f, 0.10f, 0.10f),
        2 => new Color(0.15f, 0.35f, 0.95f),
        3 => new Color(0.10f, 0.70f, 0.15f),
        4 => new Color(0.95f, 0.85f, 0.15f),
        5 => new Color(0.95f, 0.50f, 0.10f),
        6 => new Color(0.10f, 0.75f, 0.75f),
        7 => new Color(0.60f, 0.20f, 0.80f),
        8 => new Color(0.65f, 0.50f, 0.30f),
        _ => new Color(0.85f, 0.85f, 0.85f),
    };
}
