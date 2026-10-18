using System.Text;

namespace H5.Scripting.Runtime;

/// <summary>
/// Script files are 8-bit text (Windows-1251 comments in some campaign maps). Lua strings are
/// byte strings, so files are decoded one byte per char (Latin-1) to keep string contents,
/// lengths and hashes identical to the original.
/// </summary>
public static class ScriptText
{
    public static string FromBytes(byte[] b) => Encoding.Latin1.GetString(b);
    public static byte[] ToBytes(string s) => Encoding.Latin1.GetBytes(s);
}
