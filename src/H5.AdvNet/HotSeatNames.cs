namespace H5.AdvNet;

/// <summary>
/// Player names of the UI/HotSeatWait rows (CHotSeatLogic, vtable 00e3edc0, over CHSWaitItem rows). The item's default
/// name is item vf 0x20 = 00713c80: its name table entry for the row's colour (+0x44, set by item vf 4 = 00713b30 from the
/// slot's colour); the table holds the related texts player_red, player_blue, player_green, player_yellow, player_orange,
/// player_teal, player_purple, player_brown under colours 1..8 (00715cd0).
/// </summary>
public static class HotSeatNames
{
    /// <summary>The colour keys of the name table in colour order 1..8 (00715cd0).</summary>
    public static readonly string[] ColourKeys = { "red", "blue", "green", "yellow", "orange", "teal", "purple", "brown" };

    /// <summary>The name-table key of a slot colour ("PCOLOR_RED" → "red").</summary>
    public static string ColourKey(string colour) => colour.Replace("PCOLOR_", "").ToLowerInvariant();

    /// <summary>006666f0 → 00666950: the text without leading and trailing " \t\n\r" (the 006664c0 type-0 checks are not traced).</summary>
    public static string Trim(string n) => n.Trim(' ', '\t', '\n', '\r');

    /// <summary>0077c5c0 ends with item vf 0x28 (00713a00 = vf 0x1c(vf 0x20())) on every row: each name starts as its default.</summary>
    public static void Init(IEnumerable<SetupSlot> slots, Func<string, string> colourName)
    {
        foreach (var s in slots) s.Name = colourName(ColourKey(s.Colour));
    }

    /// <summary>0077bb50: valid when the trimmed text is not empty, or the text is the row's default name.</summary>
    public static bool Valid(SetupSlot s, Func<string, string> colourName) =>
        Trim(s.Name).Length > 0 || s.Name == colourName(ColourKey(s.Colour));

    /// <summary>
    /// 0077be40 (logic vf 0x1c), every row: the trimmed text, or the row's default when that is empty or the text is one of
    /// the table's colour names (item vf 0x24 = 00713d40) — so a row still named after a colour follows its colour.
    /// Run after every row command (0077bd40) and by Start (0077c470) unless every row is <see cref="Valid"/> (vf 0x18 = 0077bbe0).
    /// </summary>
    public static void Fix(IEnumerable<SetupSlot> slots, Func<string, string> colourName)
    {
        var table = ColourKeys.Select(colourName).ToHashSet(StringComparer.Ordinal);
        foreach (var s in slots)
        {
            var t = Trim(s.Name);
            s.Name = t.Length == 0 || table.Contains(s.Name) ? colourName(ColourKey(s.Colour)) : t;
        }
    }
}
