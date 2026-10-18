namespace H5.Scripting.Lua;

/// <summary>
/// Stack-machine opcodes with the semantics of Lua 4.0's lvm.c (the engine's VM uses the stock
/// 4.0 opcode set). Operands index the frame relative to its base, or count from the stack top
/// where noted.
/// </summary>
public enum Op : byte
{
    End,          // return no values
    Return,       // A: first result slot; results are base+A .. top
    Call,         // A: function slot; args are A+1 .. top; B: wanted results (-1 = all)
    PushNil,      // A: count
    Pop,          // A: count
    PushNum,      // A: number constant
    PushStr,      // A: string constant
    PushUpval,    // A: upvalue index
    GetLocal,     // A: local slot
    GetGlobal,    // A: string constant (name)
    GetTable,     // t=top-2, k=top-1 → t[k]
    PushSelf,     // A: string constant; obj=top-1 → obj[K], obj
    CreateTable,  // A: size hint
    SetLocal,     // A: local slot ← pop
    SetGlobal,    // A: name constant ← pop
    SetTable,     // t=top-A, k=top-A+1, v=top-1; t[k]=v; pop B
    SetList,      // A: flush group (base index A*64), B: count; table below the values
    SetMap,       // A: pair count; table below the pairs
    Add, Sub, Mul, Div, Pow,
    Concat,       // A: count (≥2)
    Minus, Not,
    Eq, Ne, Lt, Le, Gt, Ge,
    Jmp,          // A: target pc
    JmpT, JmpF,   // pop; jump if truthy / nil
    JmpOnT, JmpOnF, // and/or: jump keeping top if truthy / nil, else pop
    ForPrep, ForLoop,   // A: target pc
    LForPrep, LForLoop, // A: target pc
    Closure,      // A: nested proto index, B: number of upvalue values popped
}

public struct Instr
{
    public Op Op;
    public int A, B;
    public int Line;
    public override readonly string ToString() => $"{Op} {A} {B} ; line {Line}";
}

/// <summary>A compiled function.</summary>
public sealed class Proto
{
    public string Chunk = "";
    /// <summary>Key of the compiled source in <see cref="LuaState.Chunks"/> (unique per distinct source).</summary>
    public string ChunkKey = "";
    public string Name = "?";
    public int LineDefined;
    public int NumParams;
    public bool IsVararg;
    public List<Instr> Code = new();
    public List<double> Numbers = new();
    public List<string> Strings = new();
    public List<Proto> Protos = new();
    public int NumUpvalues;
    /// <summary>Per Call instruction: a name for the callee used in error messages ("global `Foo'").</summary>
    public Dictionary<int, string> CallNames = new();
    /// <summary>Stable id within its chunk (pre-order index), used to re-link closures after loading a save.</summary>
    public int Index;
    public Proto? Parent;

    public override string ToString() => $"{Chunk}:{LineDefined} {Name}";
}
