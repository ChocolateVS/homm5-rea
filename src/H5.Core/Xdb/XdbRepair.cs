using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace H5.Core.Xdb;

/// <summary>
/// Last-resort reader for the few malformed .xdb files in mod data that the original engine still loads
/// (MMH55 5.5: "GameMechanics/Shot/Creatures/Orcs/Kujin.xdb" lacks the '>' closing its XML declaration;
/// "UI/AdventureScreen-Heavy.(WindowScreenShared).xdb" disables a child with "&lt;-- !Item …/&gt;" instead of a
/// comment). Only used after a strict parse failed. The engine's own parser was not traced; dropping the
/// broken "&lt;-- …&gt;" tag is what the author meant (the item is commented out) — see docs/mmh55/INVENTORY.md.
/// </summary>
public static partial class XdbRepair
{
    [GeneratedRegex(@"^(﻿?\s*<\?xml[^>]*?\?)(?!>)")]
    private static partial Regex UnclosedDeclaration();

    [GeneratedRegex(@"<--(?!>)[^>]*>")]
    private static partial Regex BrokenComment();

    public static string Repair(string xml)
    {
        xml = UnclosedDeclaration().Replace(xml, "$1>", 1);
        return BrokenComment().Replace(xml, "");
    }

    public static XDocument Load(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        return XDocument.Parse(Repair(text));
    }
}
