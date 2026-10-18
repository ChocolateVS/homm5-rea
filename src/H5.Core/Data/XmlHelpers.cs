using System.Globalization;
using System.Xml.Linq;

namespace H5.Core.Data;

/// <summary>Small typed accessors for .xdb elements. Missing values fall back to defaults.</summary>
public static class XmlHelpers
{
    public static string Str(this XElement? e, string name, string def = "") =>
        e?.Element(name)?.Value.Trim() ?? def;

    public static int Int(this XElement? e, string name, int def = 0) =>
        int.TryParse(e?.Element(name)?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

    public static double Dbl(this XElement? e, string name, double def = 0) =>
        double.TryParse(e?.Element(name)?.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    public static bool Bool(this XElement? e, string name, bool def = false)
    {
        var s = e?.Element(name)?.Value.Trim();
        return s == null ? def : s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
    }

    public static string? Href(this XElement? e, string name)
    {
        var h = (string?)e?.Element(name)?.Attribute("href");
        return string.IsNullOrEmpty(h) ? null : h;
    }

    public static IEnumerable<XElement> Items(this XElement? e, string name) =>
        e?.Element(name)?.Elements("Item") ?? Enumerable.Empty<XElement>();

    public static List<string> StrItems(this XElement? e, string name) =>
        e.Items(name).Select(i => i.Value.Trim()).Where(s => s.Length > 0).ToList();

    public static double ParseDouble(string? s, double def = 0) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
}

public enum Mastery { None = 0, Basic = 1, Advanced = 2, Expert = 3, Ultimate = 4 }

public static class MasteryParse
{
    public static Mastery Parse(string s) => s switch
    {
        "MASTERY_BASIC" => Mastery.Basic,
        "MASTERY_ADVANCED" => Mastery.Advanced,
        "MASTERY_EXPERT" => Mastery.Expert,
        "MASTERY_EXTRA_EXPERT" => Mastery.Ultimate,
        _ => Mastery.None,
    };
}

public sealed record Resources(int Wood, int Ore, int Mercury, int Crystal, int Sulfur, int Gem, int Gold)
{
    public static Resources Parse(XElement? e) => new(
        e.Int("Wood"), e.Int("Ore"), e.Int("Mercury"), e.Int("Crystal"), e.Int("Sulfur"), e.Int("Gem"), e.Int("Gold"));
}

/// <summary>A "Base + PerPower * SpellPower" pair as used throughout spell and RPG data.</summary>
public readonly record struct PowerFormula(double Base, double PerPower)
{
    public double Eval(double power) => Base + PerPower * power;
    public static PowerFormula Parse(XElement e) => new(e.Dbl("Base"), e.Dbl("PerPower"));
}
