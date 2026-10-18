using System.Globalization;

namespace H5.Scripting.Lua;

/// <summary>
/// Number ↔ string conversions exactly as the original: Lua 4.0 formats numbers with
/// sprintf("%.16g") (NUMBER_FMT in llimits.h) and parses with strtod followed by
/// trailing-space skipping (luaO_str2d). The game was built with MSVC 7.1, whose printf
/// always prints at least three exponent digits ("1e+020") and spells non-finite values
/// as "1.#INF" / "-1.#IND".
/// </summary>
public static class LuaNumber
{
    const int Precision = 16;

    public static string Format(double d)
    {
        if (double.IsNaN(d)) return "-1.#IND";
        if (double.IsPositiveInfinity(d)) return "1.#INF";
        if (double.IsNegativeInfinity(d)) return "-1.#INF";
        if (d == 0) return double.IsNegative(d) ? "-0" : "0";

        // %.*e with P-1 fraction digits gives the decimal exponent X after rounding.
        var e = d.ToString("E" + (Precision - 1), CultureInfo.InvariantCulture);
        int ePos = e.IndexOf('E');
        int x = int.Parse(e.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (x < -4 || x >= Precision)
        {
            var mant = StripZeros(e[..ePos]);
            var sign = x < 0 ? '-' : '+';
            return $"{mant}e{sign}{Math.Abs(x):000}";
        }
        var f = d.ToString("F" + (Precision - 1 - x), CultureInfo.InvariantCulture);
        return StripZeros(f);
    }

    static string StripZeros(string s)
    {
        if (s.IndexOf('.') < 0) return s;
        s = s.TrimEnd('0');
        return s.EndsWith('.') ? s[..^1] : s;
    }

    static bool IsSpace(char c) => c == ' ' || (c >= '\t' && c <= '\r');

    /// <summary>luaO_str2d: strtod must consume something, then only white space may follow.</summary>
    public static bool TryParse(string s, out double result)
    {
        result = 0;
        int i = 0, n = s.Length;
        while (i < n && IsSpace(s[i])) i++;
        int start = i;
        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
        int digits = 0;
        while (i < n && char.IsAsciiDigit(s[i])) { i++; digits++; }
        if (i < n && s[i] == '.')
        {
            i++;
            while (i < n && char.IsAsciiDigit(s[i])) { i++; digits++; }
        }
        if (digits == 0) return false;
        int mantEnd = i;
        if (i < n && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < n && (s[j] == '+' || s[j] == '-')) j++;
            int expDigits = 0;
            while (j < n && char.IsAsciiDigit(s[j])) { j++; expDigits++; }
            if (expDigits > 0) mantEnd = j;
        }
        var num = s.AsSpan(start, mantEnd - start);
        i = mantEnd;
        while (i < n && IsSpace(s[i])) i++;
        if (i != n) return false;
        // "1." and ".5" are valid strtod input; .NET needs a digit on both sides of the point.
        var text = num.ToString();
        if (text.EndsWith('.')) text += "0";
        text = text.Replace("-.", "-0.").Replace("+.", "+0.").Replace(".e", ".0e").Replace(".E", ".0E");
        if (text.StartsWith('.')) text = "0" + text;
        result = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return true;
    }
}
