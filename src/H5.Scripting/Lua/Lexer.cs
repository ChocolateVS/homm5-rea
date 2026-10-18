using System.Text;

namespace H5.Scripting.Lua;

public enum Tok
{
    Eof, Name, Number, String,
    // reserved words (Lua 4.0; note: `in`, `true`, `false` are NOT reserved)
    And, Break, Do, Else, Elseif, End, For, Function, If, Local, Nil, Not, Or, Repeat, Return, Then, Until, While,
    // symbols
    Plus, Minus, Star, Slash, Caret, Percent, Concat, Dots, Eq, Ne, Lt, Le, Gt, Ge, Assign,
    LParen, RParen, LBrace, RBrace, LBracket, RBracket, Semi, Colon, Comma, Dot,
}

public sealed class LuaSyntaxException : Exception
{
    public string Chunk { get; }
    public int Line { get; }
    public LuaSyntaxException(string chunk, int line, string message) : base($"{chunk}:{line}: {message}")
    {
        Chunk = chunk;
        Line = line;
    }
}

/// <summary>Lexer following Lua 4.0's llex.c.</summary>
public sealed class Lexer
{
    static readonly Dictionary<string, Tok> Reserved = new(StringComparer.Ordinal)
    {
        ["and"] = Tok.And, ["break"] = Tok.Break, ["do"] = Tok.Do, ["else"] = Tok.Else, ["elseif"] = Tok.Elseif,
        ["end"] = Tok.End, ["for"] = Tok.For, ["function"] = Tok.Function, ["if"] = Tok.If, ["local"] = Tok.Local,
        ["nil"] = Tok.Nil, ["not"] = Tok.Not, ["or"] = Tok.Or, ["repeat"] = Tok.Repeat, ["return"] = Tok.Return,
        ["then"] = Tok.Then, ["until"] = Tok.Until, ["while"] = Tok.While,
    };

    readonly string _src;
    readonly string _chunk;
    int _p;
    public int Line { get; private set; } = 1;

    public Tok Token;
    public string Text = "";
    public double Num;
    public int TokenLine;

    public Lexer(string source, string chunk)
    {
        _src = source;
        _chunk = chunk;
        if (_src.StartsWith('#'))
            while (_p < _src.Length && _src[_p] != '\n') _p++;
    }

    public LuaSyntaxException Error(string msg) => new(_chunk, Line, msg);

    public readonly record struct State(int P, int Line, Tok Token, string Text, double Num, int TokenLine);

    public State Save() => new(_p, Line, Token, Text, Num, TokenLine);

    public void Restore(State s)
    {
        _p = s.P;
        Line = s.Line;
        Token = s.Token;
        Text = s.Text;
        Num = s.Num;
        TokenLine = s.TokenLine;
    }

    char Cur => _p < _src.Length ? _src[_p] : '\0';
    char Peek(int k = 1) => _p + k < _src.Length ? _src[_p + k] : '\0';
    bool AtEnd => _p >= _src.Length;

    public void Next()
    {
        Token = Scan();
        TokenLine = Line;
    }

    static bool IsNameStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
    static bool IsNameChar(char c) => IsNameStart(c) || (c >= '0' && c <= '9');

    Tok Scan()
    {
        for (;;)
        {
            if (AtEnd) return Tok.Eof;
            char c = Cur;
            switch (c)
            {
                case '\n': Line++; _p++; continue;
                case ' ': case '\t': case '\r': case '\f': case '\v': _p++; continue;
                case '-':
                    if (Peek() != '-') { _p++; return Tok.Minus; }
                    while (!AtEnd && Cur != '\n') _p++;
                    continue;
                case '[':
                    if (Peek() != '[') { _p++; return Tok.LBracket; }
                    ReadLongString();
                    return Tok.String;
                case '=':
                    _p++;
                    if (Cur == '=') { _p++; return Tok.Eq; }
                    return Tok.Assign;
                case '<':
                    _p++;
                    if (Cur == '=') { _p++; return Tok.Le; }
                    return Tok.Lt;
                case '>':
                    _p++;
                    if (Cur == '=') { _p++; return Tok.Ge; }
                    return Tok.Gt;
                case '~':
                    _p++;
                    if (Cur != '=') throw Error("unexpected symbol `~'");
                    _p++;
                    return Tok.Ne;
                case '"':
                case '\'':
                    ReadString(c);
                    return Tok.String;
                case '.':
                    if (Peek() == '.')
                    {
                        if (Peek(2) == '.') { _p += 3; return Tok.Dots; }
                        _p += 2;
                        return Tok.Concat;
                    }
                    if (char.IsAsciiDigit(Peek())) { ReadNumber(); return Tok.Number; }
                    _p++;
                    return Tok.Dot;
                case '+': _p++; return Tok.Plus;
                case '*': _p++; return Tok.Star;
                case '/': _p++; return Tok.Slash;
                case '^': _p++; return Tok.Caret;
                case '%': _p++; return Tok.Percent;
                case '(': _p++; return Tok.LParen;
                case ')': _p++; return Tok.RParen;
                case '{': _p++; return Tok.LBrace;
                case '}': _p++; return Tok.RBrace;
                case ']': _p++; return Tok.RBracket;
                case ';': _p++; return Tok.Semi;
                case ':': _p++; return Tok.Colon;
                case ',': _p++; return Tok.Comma;
                default:
                    if (char.IsAsciiDigit(c)) { ReadNumber(); return Tok.Number; }
                    if (IsNameStart(c))
                    {
                        int s = _p;
                        while (IsNameChar(Cur)) _p++;
                        Text = _src[s.._p];
                        return Reserved.TryGetValue(Text, out var r) ? r : Tok.Name;
                    }
                    throw Error($"unexpected symbol `{c}'");
            }
        }
    }

    void ReadNumber()
    {
        int s = _p;
        while (char.IsAsciiDigit(Cur)) _p++;
        if (Cur == '.')
        {
            _p++;
            if (Cur == '.') throw Error("ambiguous syntax (decimal point x string concatenation)");
            while (char.IsAsciiDigit(Cur)) _p++;
        }
        if (Cur == 'e' || Cur == 'E')
        {
            _p++;
            if (Cur == '+' || Cur == '-') _p++;
            while (char.IsAsciiDigit(Cur)) _p++;
        }
        Text = _src[s.._p];
        if (!LuaNumber.TryParse(Text, out Num)) throw Error($"malformed number `{Text}'");
    }

    void ReadString(char delim)
    {
        _p++;
        var sb = new StringBuilder();
        while (Cur != delim)
        {
            if (AtEnd) throw Error("unfinished string");
            char c = Cur;
            if (c == '\n') throw Error("unfinished string");
            if (c == '\\')
            {
                _p++;
                c = Cur;
                switch (c)
                {
                    case 'a': sb.Append('\a'); _p++; break;
                    case 'b': sb.Append('\b'); _p++; break;
                    case 'f': sb.Append('\f'); _p++; break;
                    case 'n': sb.Append('\n'); _p++; break;
                    case 'r': sb.Append('\r'); _p++; break;
                    case 't': sb.Append('\t'); _p++; break;
                    case 'v': sb.Append('\v'); _p++; break;
                    case '\n': sb.Append('\n'); Line++; _p++; break;
                    default:
                        if (char.IsAsciiDigit(c))
                        {
                            int v = 0, i = 0;
                            while (i < 3 && char.IsAsciiDigit(Cur)) { v = v * 10 + (Cur - '0'); _p++; i++; }
                            if (v > 255) throw Error("escape sequence too large");
                            sb.Append((char)v);
                        }
                        else
                        {
                            if (AtEnd) throw Error("unfinished string");
                            sb.Append(c);
                            _p++;
                        }
                        break;
                }
                continue;
            }
            sb.Append(c);
            _p++;
        }
        _p++;
        Text = sb.ToString();
    }

    void ReadLongString()
    {
        _p += 2;
        int level = 1;
        var sb = new StringBuilder();
        if (Cur == '\n') { Line++; _p++; }
        for (;;)
        {
            if (AtEnd) throw Error("unfinished long string");
            char c = Cur;
            if (c == '[' && Peek() == '[') { level++; sb.Append("[["); _p += 2; continue; }
            if (c == ']' && Peek() == ']')
            {
                if (--level == 0) { _p += 2; break; }
                sb.Append("]]");
                _p += 2;
                continue;
            }
            if (c == '\n') Line++;
            sb.Append(c);
            _p++;
        }
        Text = sb.ToString();
    }
}
