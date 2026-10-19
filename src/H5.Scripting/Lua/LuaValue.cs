using System.Runtime.CompilerServices;

namespace H5.Scripting.Lua;

/// <summary>
/// Lua 4.0 value tags, numbered as in the original engine (lua.h of 4.0 and the VM in
/// H5_Game.exe: 0 userdata, 1 nil, 2 number, 3 string, 4 table, 5 function).
/// Lua 4.0 has no booleans: "true" is any non-nil value; comparisons yield 1 or nil.
/// </summary>
public enum LuaType : byte { Userdata = 0, Nil = 1, Number = 2, String = 3, Table = 4, Function = 5 }

/// <summary>
/// An immutable Lua value. Strings are byte strings stored as .NET strings with one
/// char per byte (Latin-1), so hashing, length and comparison match the original exactly.
/// </summary>
public readonly struct LuaValue : IEquatable<LuaValue>
{
    public readonly LuaType Type;
    public readonly double N;
    public readonly object? O;

    LuaValue(LuaType t, double n, object? o) { Type = t; N = n; O = o; }

    public static readonly LuaValue Nil = new(LuaType.Nil, 0, null);
    /// <summary>The canonical "true" of Lua 4.0 (what `not nil` and comparisons produce).</summary>
    public static readonly LuaValue True = new(LuaType.Number, 1, null);

    public static LuaValue Number(double d) => new(LuaType.Number, d, null);
    public static LuaValue String(string s) => new(LuaType.String, 0, s);
    public static LuaValue Table(LuaTable t) => new(LuaType.Table, 0, t);
    public static LuaValue Function(LuaFunction f) => new(LuaType.Function, 0, f);
    public static LuaValue Userdata(LuaUserdata u) => new(LuaType.Userdata, 0, u);
    public static LuaValue Bool(bool b) => b ? True : Nil;

    public static implicit operator LuaValue(double d) => Number(d);
    public static implicit operator LuaValue(int i) => Number(i);
    public static implicit operator LuaValue(string? s) => s == null ? Nil : String(s);
    public static implicit operator LuaValue(LuaTable t) => Table(t);

    public bool IsNil => Type == LuaType.Nil;
    public bool IsTruthy => Type != LuaType.Nil;
    public bool IsNumber => Type == LuaType.Number;
    public bool IsString => Type == LuaType.String;
    public bool IsTable => Type == LuaType.Table;
    public bool IsFunction => Type == LuaType.Function;

    public string Str => (string)O!;
    public LuaTable TableValue => (LuaTable)O!;
    public LuaFunction FunctionValue => (LuaFunction)O!;

    /// <summary>luaV_tonumber: numbers, or strings that convert completely (luaO_str2d).</summary>
    public bool TryToNumber(out double d)
    {
        if (Type == LuaType.Number) { d = N; return true; }
        if (Type == LuaType.String) return LuaNumber.TryParse(Str, out d);
        d = 0;
        return false;
    }

    /// <summary>luaV_tostring: strings, or numbers formatted with "%.16g".</summary>
    public bool TryToStr(out string s)
    {
        if (Type == LuaType.String) { s = Str; return true; }
        if (Type == LuaType.Number) { s = LuaNumber.Format(N); return true; }
        s = "";
        return false;
    }

    /// <summary>luaO_equalObj: same tag and (numerically equal | same string | same object).</summary>
    public bool Equals(LuaValue o)
    {
        if (Type != o.Type) return false;
        return Type switch
        {
            LuaType.Nil => true,
            LuaType.Number => N == o.N,
            LuaType.String => string.Equals(Str, o.Str, StringComparison.Ordinal),
            _ => ReferenceEquals(O, o.O),
        };
    }

    public override bool Equals(object? obj) => obj is LuaValue v && Equals(v);
    public override int GetHashCode() => Type switch
    {
        LuaType.Number => N.GetHashCode(),
        LuaType.String => StringComparer.Ordinal.GetHashCode(Str),
        LuaType.Nil => 0,
        _ => RuntimeHelpers.GetHashCode(O!),
    };

    public static string TypeName(LuaType t) => t switch
    {
        LuaType.Userdata => "userdata",
        LuaType.Nil => "nil",
        LuaType.Number => "number",
        LuaType.String => "string",
        LuaType.Table => "table",
        LuaType.Function => "function",
        _ => "?",
    };

    public string TypeName() => TypeName(Type);

    public override string ToString() => Type switch
    {
        LuaType.Nil => "nil",
        LuaType.Number => LuaNumber.Format(N),
        LuaType.String => Str,
        LuaType.Table => $"table: {((LuaTable)O!).Id:x8}",
        LuaType.Function => O is HostFunction h ? $"function: {h.Name}" : $"function: {((LuaClosure)O!).Id:x8}",
        _ => "userdata",
    };
}

/// <summary>Base of every heap object; Id is a deterministic allocation number (used where the
/// original hashed by pointer, and for serialisation).</summary>
public abstract class LuaObject
{
    public int Id { get; internal set; }
}

public abstract class LuaFunction : LuaObject { }

/// <summary>A Lua function value: prototype plus the upvalue values copied at creation (Lua 4.0
/// upvalues are values frozen when the closure is made, not shared variables).</summary>
public sealed class LuaClosure : LuaFunction
{
    public readonly Proto Proto;
    public readonly LuaValue[] Upvalues;
    public LuaClosure(Proto p, LuaValue[] upvalues) { Proto = p; Upvalues = upvalues; }
}

/// <summary>A function implemented by the host (the C functions of the original engine).</summary>
public sealed class HostFunction : LuaFunction
{
    public readonly string Name;
    public readonly HostCall Call;
    public HostFunction(string name, HostCall call) { Name = name; Call = call; }
}

public sealed class LuaUserdata : LuaObject
{
    public object? Value;
}

/// <summary>A host function receives its arguments and pushes results into <see cref="HostContext.Results"/>.</summary>
public delegate void HostCall(HostContext ctx);
