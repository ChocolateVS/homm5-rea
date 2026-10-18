namespace H5.Scripting.Lua;

/// <summary>
/// One-pass compiler for Lua 4.0, structured after lparser.c of 4.0 so observable behaviour
/// matches the original engine: right-to-left stores in multiple assignment, table
/// constructors inserting their items in descending order (SETLIST/SETMAP), `and`/`or`
/// sharing one left-associative precedence level, value-copied `%upvalues`, the
/// "cannot access a variable in outer scope" rule, single-level `function a.b()` names,
/// two-name generic `for k,v in t`, and `in`/`true`/`false` not being reserved.
/// </summary>
public sealed class Compiler
{
    const int ListFieldsPerFlush = 64, RecFieldsPerFlush = 32;

    enum EK { Global, Local, Indexed, Value }

    struct Exp
    {
        public EK K;
        public int Info;          // local slot
        public string? Name;      // global name / callee name hint
        public bool Open;         // value is a call whose result count is still open
        public int CallPc, CallFunc;
    }

    sealed class BreakLabel
    {
        public int Level;
        public readonly List<int> Jumps = new();
    }

    readonly record struct UpvalDesc(bool IsLocal, int Index, string Name);

    sealed class FuncState
    {
        public readonly Proto P = new();
        public FuncState? Prev;
        public readonly List<string> Locals = new();
        public int Level;
        public readonly List<UpvalDesc> Upvals = new();
        public readonly List<BreakLabel> Breaks = new();
        public readonly Dictionary<string, int> StrK = new(StringComparer.Ordinal);
        public readonly Dictionary<double, int> NumK = new();
    }

    readonly Lexer _lx;
    readonly string _chunk;
    FuncState _fs = null!;
    int _lastLine = 1;
    int _protoCounter;

    Compiler(string source, string chunk)
    {
        _chunk = chunk;
        _lx = new Lexer(source, chunk);
    }

    public static Proto Compile(string source, string chunk)
    {
        var c = new Compiler(source, chunk);
        return c.Main();
    }

    Proto Main()
    {
        _fs = new FuncState();
        _fs.P.Chunk = _chunk;
        _fs.P.Name = "main chunk";
        _fs.P.Index = _protoCounter++;
        Next();
        Chunk();
        if (_lx.Token != Tok.Eof) throw Error("<eof> expected");
        Emit(Op.End, 0, 0, 0);
        return _fs.P;
    }

    // ---- token helpers -------------------------------------------------------------------

    void Next()
    {
        _lastLine = _lx.TokenLine;
        _lx.Next();
    }

    LuaSyntaxException Error(string msg) => _lx.Error(msg + NearToken());

    string NearToken() => _lx.Token switch
    {
        Tok.Eof => " near `<eof>'",
        Tok.Name or Tok.String => $" near `{_lx.Text}'",
        Tok.Number => $" near `{_lx.Text}'",
        _ => "",
    };

    void Check(Tok t, string what)
    {
        if (_lx.Token != t) throw Error($"`{what}' expected");
        Next();
    }

    void CheckMatch(Tok what, string whatText, string whoText, int line)
    {
        if (_lx.Token != what)
        {
            if (line == _lx.Line) throw Error($"`{whatText}' expected");
            throw Error($"`{whatText}' expected (to close `{whoText}' at line {line})");
        }
        Next();
    }

    bool Optional(Tok t)
    {
        if (_lx.Token != t) return false;
        Next();
        return true;
    }

    string CheckName()
    {
        if (_lx.Token != Tok.Name) throw Error("<name> expected");
        var s = _lx.Text;
        Next();
        return s;
    }

    bool BlockFollow => _lx.Token is Tok.Else or Tok.Elseif or Tok.End or Tok.Until or Tok.Eof;

    // ---- code emission ------------------------------------------------------------------

    int Emit(Op op, int a, int b, int delta)
    {
        _fs.P.Code.Add(new Instr { Op = op, A = a, B = b, Line = _lastLine });
        _fs.Level += delta;
        return _fs.P.Code.Count - 1;
    }

    int Pc => _fs.P.Code.Count;

    void Patch(int pc, int target)
    {
        var i = _fs.P.Code[pc];
        i.A = target;
        _fs.P.Code[pc] = i;
    }

    int StrK(string s)
    {
        if (_fs.StrK.TryGetValue(s, out var k)) return k;
        k = _fs.P.Strings.Count;
        _fs.P.Strings.Add(s);
        _fs.StrK[s] = k;
        return k;
    }

    int NumK(double d)
    {
        // Distinguish -0 from 0 so both keep their bit pattern.
        if (d == 0 && double.IsNegative(d))
        {
            _fs.P.Numbers.Add(d);
            return _fs.P.Numbers.Count - 1;
        }
        if (_fs.NumK.TryGetValue(d, out var k)) return k;
        k = _fs.P.Numbers.Count;
        _fs.P.Numbers.Add(d);
        _fs.NumK[d] = k;
        return k;
    }

    /// <summary>luaK_adjuststack: n &gt; 0 pops n values, n &lt; 0 pushes -n nils.</summary>
    void AdjustStack(int n)
    {
        if (n > 0) Emit(Op.Pop, n, 0, -n);
        else if (n < 0) Emit(Op.PushNil, -n, 0, -n);
    }

    void ToStack(ref Exp e, bool onlyOne)
    {
        switch (e.K)
        {
            case EK.Global:
                Emit(Op.GetGlobal, StrK(e.Name!), 0, +1);
                break;
            case EK.Local:
                Emit(Op.GetLocal, e.Info, 0, +1);
                break;
            case EK.Indexed:
                Emit(Op.GetTable, 0, 0, -1);
                break;
            case EK.Value:
                if (e.Open && onlyOne) SetCallReturns(ref e, 1);
                return;
        }
        e.K = EK.Value;
        e.Open = false;
    }

    void SetCallReturns(ref Exp e, int n)
    {
        var ins = _fs.P.Code[e.CallPc];
        ins.B = n;
        _fs.P.Code[e.CallPc] = ins;
        _fs.Level = e.CallFunc + n;
        e.Open = false;
    }

    void StoreVar(ref Exp v)
    {
        switch (v.K)
        {
            case EK.Local: Emit(Op.SetLocal, v.Info, 0, -1); break;
            case EK.Global: Emit(Op.SetGlobal, StrK(v.Name!), 0, -1); break;
            case EK.Indexed: Emit(Op.SetTable, 3, 3, -3); break;
            default: throw Error("syntax error");
        }
    }

    // ---- variables ------------------------------------------------------------------------

    /// <summary>Returns the function level where name is a local (0 = current), or -1 for global.</summary>
    int SearchLocal(string name, out int index)
    {
        int level = 0;
        for (var fs = _fs; fs != null; fs = fs.Prev, level++)
        {
            for (int i = fs.Locals.Count - 1; i >= 0; i--)
                if (fs.Locals[i] == name) { index = i; return level; }
        }
        index = -1;
        return -1;
    }

    void SingleVar(string name, out Exp v)
    {
        v = default;
        int level = SearchLocal(name, out var idx);
        if (level >= 1) throw Error("cannot access a variable in outer scope");
        if (level == 0) { v.K = EK.Local; v.Info = idx; v.Name = name; }
        else { v.K = EK.Global; v.Name = name; }
    }

    void PushUpvalue(string name)
    {
        int level = SearchLocal(name, out var idx);
        UpvalDesc d;
        if (level == -1)
        {
            if (_fs.Prev == null) throw Error("cannot access upvalue in main");
            d = new UpvalDesc(false, 0, name);
        }
        else if (level != 1) throw Error("upvalue must be global or local to immediately outer scope");
        else d = new UpvalDesc(true, idx, name);
        int i = _fs.Upvals.FindIndex(u => u.IsLocal == d.IsLocal && (d.IsLocal ? u.Index == d.Index : u.Name == d.Name));
        if (i < 0) { i = _fs.Upvals.Count; _fs.Upvals.Add(d); }
        Emit(Op.PushUpval, i, 0, +1);
    }

    void NewLocal(string name) => _fs.Locals.Add(name);

    void RemoveLocals(int n) => _fs.Locals.RemoveRange(_fs.Locals.Count - n, n);

    // ---- expressions ---------------------------------------------------------------------

    static readonly (int Left, int Right)[] Priority =
    {
        (5, 5), (5, 5), (6, 6), (6, 6), // + - * /
        (9, 8), (4, 3),                 // ^ .. (right associative)
        (2, 2), (2, 2),                 // == ~=
        (2, 2), (2, 2), (2, 2), (2, 2), // < <= > >=
        (1, 1), (1, 1),                 // and or
    };
    const int UnaryPriority = 7;

    enum BinOp { Add, Sub, Mul, Div, Pow, Concat, Eq, Ne, Lt, Le, Gt, Ge, And, Or, None }

    BinOp GetBinOp() => _lx.Token switch
    {
        Tok.Plus => BinOp.Add,
        Tok.Minus => BinOp.Sub,
        Tok.Star => BinOp.Mul,
        Tok.Slash => BinOp.Div,
        Tok.Caret => BinOp.Pow,
        Tok.Concat => BinOp.Concat,
        Tok.Eq => BinOp.Eq,
        Tok.Ne => BinOp.Ne,
        Tok.Lt => BinOp.Lt,
        Tok.Le => BinOp.Le,
        Tok.Gt => BinOp.Gt,
        Tok.Ge => BinOp.Ge,
        Tok.And => BinOp.And,
        Tok.Or => BinOp.Or,
        _ => BinOp.None,
    };

    void Expr(out Exp v) => SubExpr(out v, -1);

    void Exp1()
    {
        Expr(out var v);
        ToStack(ref v, true);
    }

    BinOp SubExpr(out Exp v, int limit)
    {
        if (_lx.Token is Tok.Not or Tok.Minus)
        {
            bool not = _lx.Token == Tok.Not;
            Next();
            SubExpr(out v, UnaryPriority);
            ToStack(ref v, true);
            Emit(not ? Op.Not : Op.Minus, 0, 0, 0);
        }
        else SimpleExp(out v);

        var op = GetBinOp();
        while (op != BinOp.None && Priority[(int)op].Left > limit)
        {
            Next();
            ToStack(ref v, true);
            int jump = -1;
            if (op == BinOp.And) jump = Emit(Op.JmpOnF, 0, 0, -1);
            else if (op == BinOp.Or) jump = Emit(Op.JmpOnT, 0, 0, -1);
            var nextOp = SubExpr(out var v2, Priority[(int)op].Right);
            ToStack(ref v2, true);
            switch (op)
            {
                case BinOp.And:
                case BinOp.Or:
                    Patch(jump, Pc);
                    break;
                case BinOp.Concat: Emit(Op.Concat, 2, 0, -1); break;
                default: Emit(ArithOp(op), 0, 0, -1); break;
            }
            v = new Exp { K = EK.Value };
            op = nextOp;
        }
        return op;
    }

    static Op ArithOp(BinOp op) => op switch
    {
        BinOp.Add => Op.Add,
        BinOp.Sub => Op.Sub,
        BinOp.Mul => Op.Mul,
        BinOp.Div => Op.Div,
        BinOp.Pow => Op.Pow,
        BinOp.Eq => Op.Eq,
        BinOp.Ne => Op.Ne,
        BinOp.Lt => Op.Lt,
        BinOp.Le => Op.Le,
        BinOp.Gt => Op.Gt,
        BinOp.Ge => Op.Ge,
        _ => throw new InvalidOperationException(),
    };

    void SimpleExp(out Exp v)
    {
        v = default;
        switch (_lx.Token)
        {
            case Tok.Number:
                Emit(Op.PushNum, NumK(_lx.Num), 0, +1);
                Next();
                break;
            case Tok.String:
                Emit(Op.PushStr, StrK(_lx.Text), 0, +1);
                Next();
                break;
            case Tok.Nil:
                Emit(Op.PushNil, 1, 0, +1);
                Next();
                break;
            case Tok.LBrace:
                Constructor();
                break;
            case Tok.Function:
                {
                    int line = _lx.TokenLine;
                    Next();
                    Body(false, line, "anonymous");
                    break;
                }
            case Tok.LParen:
                Next();
                Expr(out v);
                Check(Tok.RParen, ")");
                return;
            default:
                VarOrFunc(out v);
                return;
        }
        v.K = EK.Value;
    }

    void VarOrFunc(out Exp v)
    {
        if (Optional(Tok.Percent))
        {
            PushUpvalue(CheckName());
            v = new Exp { K = EK.Value };
        }
        else SingleVar(CheckName(), out v);
        VarOrFuncTail(ref v);
    }

    void VarOrFuncTail(ref Exp v)
    {
        for (;;)
        {
            switch (_lx.Token)
            {
                case Tok.Dot:
                    {
                        Next();
                        string? parent = v.Name;
                        ToStack(ref v, true);
                        var name = CheckName();
                        Emit(Op.PushStr, StrK(name), 0, +1);
                        v = new Exp { K = EK.Indexed, Name = parent != null ? $"{parent}.{name}" : name };
                        break;
                    }
                case Tok.LBracket:
                    Next();
                    ToStack(ref v, true);
                    Exp1();
                    Check(Tok.RBracket, "]");
                    v = new Exp { K = EK.Indexed };
                    break;
                case Tok.Colon:
                    {
                        Next();
                        var name = CheckName();
                        ToStack(ref v, true);
                        Emit(Op.PushSelf, StrK(name), 0, +1);
                        FuncArgs(ref v, true, "method `" + name + "'");
                        break;
                    }
                case Tok.LParen:
                case Tok.String:
                case Tok.LBrace:
                    {
                        string callName = v.K switch
                        {
                            EK.Global => $"global `{v.Name}'",
                            EK.Local => $"local `{v.Name}'",
                            EK.Indexed when v.Name != null => $"field `{v.Name}'",
                            _ => "",
                        };
                        ToStack(ref v, true);
                        FuncArgs(ref v, false, callName);
                        break;
                    }
                default:
                    return;
            }
        }
    }

    void FuncArgs(ref Exp v, bool self, string callName)
    {
        int funcPos = _fs.Level - (self ? 2 : 1);
        switch (_lx.Token)
        {
            case Tok.LParen:
                {
                    if (_lx.TokenLine != _lastLine) throw Error("ambiguous syntax (function call x new statement)");
                    int line = _lx.TokenLine;
                    Next();
                    if (_lx.Token != Tok.RParen) ExpList1(out _);
                    CheckMatch(Tok.RParen, ")", "(", line);
                    break;
                }
            case Tok.LBrace:
                Constructor();
                break;
            case Tok.String:
                Emit(Op.PushStr, StrK(_lx.Text), 0, +1);
                Next();
                break;
            default:
                throw Error("function arguments expected");
        }
        int pc = Emit(Op.Call, funcPos, -1, 0);
        if (callName.Length > 0) _fs.P.CallNames[pc] = callName;
        _fs.Level = funcPos + 1;
        v = new Exp { K = EK.Value, Open = true, CallPc = pc, CallFunc = funcPos };
    }

    int ExpList1(out Exp last)
    {
        int n = 1;
        Expr(out var v);
        while (_lx.Token == Tok.Comma)
        {
            ToStack(ref v, true);
            Next();
            Expr(out v);
            n++;
        }
        ToStack(ref v, false);
        last = v;
        return n;
    }

    void Constructor()
    {
        int line = _lx.TokenLine;
        int pc = Emit(Op.CreateTable, 0, 0, +1);
        Check(Tok.LBrace, "{");
        var (n, kind) = ConstructorPart();
        if (Optional(Tok.Semi))
        {
            var (n2, kind2) = ConstructorPart();
            if (kind == kind2) throw Error("invalid constructor syntax");
            n += n2;
        }
        CheckMatch(Tok.RBrace, "}", "{", line);
        var ins = _fs.P.Code[pc];
        ins.A = n;
        _fs.P.Code[pc] = ins;
    }

    /// <summary>Returns (element count, kind): kind 0 = list, 1 = record, 2/3 = empty before ';' / '}'.</summary>
    (int, int) ConstructorPart()
    {
        switch (_lx.Token)
        {
            case Tok.Semi: return (0, 2);
            case Tok.RBrace: return (0, 3);
            case Tok.Name:
                if (LookaheadIsAssign()) return (RecFields(), 1);
                return (ListFields(), 0);
            case Tok.LBracket:
                return (RecFields(), 1);
            default:
                return (ListFields(), 0);
        }
    }

    bool LookaheadIsAssign()
    {
        var save = _lx.Save();
        int lastLine = _lastLine;
        Next();
        bool r = _lx.Token == Tok.Assign;
        _lx.Restore(save);
        _lastLine = lastLine;
        return r;
    }

    int ListFields()
    {
        int n = 1;
        Exp1();
        while (_lx.Token == Tok.Comma)
        {
            Next();
            if (_lx.Token is Tok.Semi or Tok.RBrace) break;
            Exp1();
            n++;
            if (n % ListFieldsPerFlush == 0)
                Emit(Op.SetList, n / ListFieldsPerFlush - 1, ListFieldsPerFlush, -ListFieldsPerFlush);
        }
        int rest = n % ListFieldsPerFlush;
        Emit(Op.SetList, n / ListFieldsPerFlush, rest, -rest);
        return n;
    }

    int RecFields()
    {
        int n = 1;
        RecField();
        while (_lx.Token == Tok.Comma)
        {
            Next();
            if (_lx.Token is Tok.Semi or Tok.RBrace) break;
            RecField();
            n++;
            if (n % RecFieldsPerFlush == 0) Emit(Op.SetMap, RecFieldsPerFlush, 0, -2 * RecFieldsPerFlush);
        }
        int rest = n % RecFieldsPerFlush;
        Emit(Op.SetMap, rest, 0, -2 * rest);
        return n;
    }

    void RecField()
    {
        switch (_lx.Token)
        {
            case Tok.Name:
                Emit(Op.PushStr, StrK(CheckName()), 0, +1);
                break;
            case Tok.LBracket:
                Next();
                Exp1();
                Check(Tok.RBracket, "]");
                break;
            default:
                throw Error("<name> or `[' expected");
        }
        Check(Tok.Assign, "=");
        Exp1();
    }

    // ---- functions -----------------------------------------------------------------------

    void Body(bool needSelf, int line, string name)
    {
        var parent = _fs;
        var fs = new FuncState { Prev = parent };
        fs.P.Chunk = _chunk;
        fs.P.Name = name;
        fs.P.LineDefined = line;
        fs.P.Parent = parent.P;
        fs.P.Index = _protoCounter++;
        _fs = fs;

        Check(Tok.LParen, "(");
        if (needSelf) NewLocal("self");
        if (_lx.Token != Tok.RParen)
        {
            for (;;)
            {
                if (_lx.Token == Tok.Dots) { Next(); fs.P.IsVararg = true; break; }
                NewLocal(CheckName());
                if (!Optional(Tok.Comma)) break;
            }
        }
        Check(Tok.RParen, ")");
        fs.P.NumParams = fs.Locals.Count;
        if (fs.P.IsVararg) NewLocal("arg");
        fs.Level = fs.Locals.Count;

        Chunk();
        CheckMatch(Tok.End, "end", "function", line);
        Emit(Op.End, 0, 0, 0);
        fs.P.NumUpvalues = fs.Upvals.Count;

        _fs = parent;
        int protoIndex = parent.P.Protos.Count;
        parent.P.Protos.Add(fs.P);
        foreach (var u in fs.Upvals)
        {
            if (u.IsLocal) Emit(Op.GetLocal, u.Index, 0, +1);
            else Emit(Op.GetGlobal, StrK(u.Name), 0, +1);
        }
        Emit(Op.Closure, protoIndex, fs.Upvals.Count, 1 - fs.Upvals.Count);
    }

    // ---- statements ----------------------------------------------------------------------

    void Chunk()
    {
        bool isLast = false;
        while (!isLast && !BlockFollow)
        {
            isLast = Statement();
            Optional(Tok.Semi);
        }
    }

    void Block()
    {
        int nact = _fs.Locals.Count;
        Chunk();
        int n = _fs.Locals.Count - nact;
        AdjustStack(n);
        RemoveLocals(n);
    }

    bool Statement()
    {
        int line = _lx.TokenLine;
        switch (_lx.Token)
        {
            case Tok.If: IfStat(line); return false;
            case Tok.While: WhileStat(line); return false;
            case Tok.Do:
                Next();
                Block();
                CheckMatch(Tok.End, "end", "do", line);
                return false;
            case Tok.For: ForStat(line); return false;
            case Tok.Repeat: RepeatStat(line); return false;
            case Tok.Function: FuncStat(line); return false;
            case Tok.Local: LocalStat(); return false;
            case Tok.Return: RetStat(); return true;
            case Tok.Break: BreakStat(); return true;
            default: ExprStat(); return false;
        }
    }

    int Cond()
    {
        Expr(out var v);
        ToStack(ref v, true);
        return Emit(Op.JmpF, 0, 0, -1);
    }

    int TestThenBlock()
    {
        Next();
        int jf = Cond();
        Check(Tok.Then, "then");
        Block();
        return jf;
    }

    void IfStat(int line)
    {
        var escapes = new List<int>();
        int jf = TestThenBlock();
        while (_lx.Token == Tok.Elseif)
        {
            escapes.Add(Emit(Op.Jmp, 0, 0, 0));
            Patch(jf, Pc);
            jf = TestThenBlock();
        }
        if (_lx.Token == Tok.Else)
        {
            escapes.Add(Emit(Op.Jmp, 0, 0, 0));
            Patch(jf, Pc);
            Next();
            Block();
        }
        else Patch(jf, Pc);
        foreach (var e in escapes) Patch(e, Pc);
        CheckMatch(Tok.End, "end", "if", line);
    }

    BreakLabel EnterBreak()
    {
        var bl = new BreakLabel { Level = _fs.Level };
        _fs.Breaks.Add(bl);
        return bl;
    }

    void LeaveBreak(BreakLabel bl)
    {
        _fs.Breaks.Remove(bl);
        foreach (var j in bl.Jumps) Patch(j, Pc);
    }

    void WhileStat(int line)
    {
        Next();
        int init = Pc;
        int jf = Cond();
        Check(Tok.Do, "do");
        var bl = EnterBreak();
        Block();
        Emit(Op.Jmp, init, 0, 0);
        Patch(jf, Pc);
        CheckMatch(Tok.End, "end", "while", line);
        LeaveBreak(bl);
    }

    void RepeatStat(int line)
    {
        Next();
        int init = Pc;
        var bl = EnterBreak();
        Block();
        CheckMatch(Tok.Until, "until", "repeat", line);
        int jf = Cond();
        Patch(jf, init);
        LeaveBreak(bl);
    }

    void ForStat(int line)
    {
        var bl = EnterBreak();
        Next();
        var varName = CheckName();
        switch (_lx.Token)
        {
            case Tok.Assign: ForNum(varName); break;
            case Tok.Comma: ForList(varName); break;
            default: throw Error("`=' or `,' expected");
        }
        CheckMatch(Tok.End, "end", "for", line);
        LeaveBreak(bl);
    }

    void ForNum(string varName)
    {
        Check(Tok.Assign, "=");
        Exp1();
        Check(Tok.Comma, ",");
        Exp1();
        if (Optional(Tok.Comma)) Exp1();
        else Emit(Op.PushNum, NumK(1), 0, +1);
        NewLocal(varName);
        NewLocal("(limit)");
        NewLocal("(step)");
        ForBody(3, Op.ForPrep, Op.ForLoop, 0);
    }

    void ForList(string indexName)
    {
        Check(Tok.Comma, ",");
        var valName = CheckName();
        if (_lx.Token != Tok.Name || _lx.Text != "in") throw Error("`in' expected");
        Next();
        Exp1();
        NewLocal("(table)");
        NewLocal(indexName);
        NewLocal(valName);
        ForBody(3, Op.LForPrep, Op.LForLoop, 2);
    }

    void ForBody(int nvar, Op prep, Op loop, int prepPush)
    {
        int prepPc = Emit(prep, 0, 0, prepPush);
        int blockInit = Pc;
        Check(Tok.Do, "do");
        Block();
        Emit(loop, blockInit, 0, 0);
        Patch(prepPc, Pc);
        _fs.Level -= nvar;
        RemoveLocals(nvar);
    }

    void FuncStat(int line)
    {
        Next();
        SingleVar(CheckName(), out var v);
        bool needSelf = false;
        string name = v.Name ?? "?";
        if (_lx.Token is Tok.Colon or Tok.Dot)
        {
            needSelf = _lx.Token == Tok.Colon;
            Next();
            ToStack(ref v, true);
            var field = CheckName();
            Emit(Op.PushStr, StrK(field), 0, +1);
            name = $"{name}{(needSelf ? ":" : ".")}{field}";
            v = new Exp { K = EK.Indexed };
        }
        Body(needSelf, line, name);
        StoreVar(ref v);
    }

    void LocalStat()
    {
        var names = new List<string>();
        do
        {
            Next();
            names.Add(CheckName());
        } while (_lx.Token == Tok.Comma);
        int nexps = 0;
        Exp last = default;
        if (Optional(Tok.Assign)) nexps = ExpList1(out last);
        AdjustMultAssign(names.Count, nexps, ref last);
        foreach (var n in names) NewLocal(n);
    }

    void AdjustMultAssign(int nvars, int nexps, ref Exp last)
    {
        int diff = nexps - nvars;
        if (nexps > 0 && last.Open)
        {
            diff--;
            if (diff <= 0) { SetCallReturns(ref last, -diff); diff = 0; }
            else SetCallReturns(ref last, 0);
        }
        AdjustStack(diff);
    }

    void ExprStat()
    {
        VarOrFunc(out var v);
        if (v.K == EK.Value)
        {
            if (!v.Open) throw Error("syntax error");
            SetCallReturns(ref v, 0);
        }
        else
        {
            int left = Assignment(ref v, 1);
            AdjustStack(left);
        }
    }

    int Assignment(ref Exp v, int nvars)
    {
        int left = 0;
        if (_lx.Token == Tok.Comma)
        {
            Next();
            VarOrFunc(out var nv);
            if (nv.K == EK.Value) throw Error("syntax error");
            left = Assignment(ref nv, nvars + 1);
        }
        else
        {
            Check(Tok.Assign, "=");
            int nexps = ExpList1(out var last);
            AdjustMultAssign(nvars, nexps, ref last);
        }
        if (v.K != EK.Indexed) StoreVar(ref v);
        else
        {
            Emit(Op.SetTable, left + nvars + 2, 1, -1);
            left += 2;
        }
        return left;
    }

    void RetStat()
    {
        Next();
        if (!BlockFollow && _lx.Token != Tok.Semi) ExpList1(out _);
        Emit(Op.Return, _fs.Locals.Count, 0, 0);
        _fs.Level = _fs.Locals.Count;
    }

    void BreakStat()
    {
        if (_fs.Breaks.Count == 0) throw Error("no loop to break");
        var bl = _fs.Breaks[^1];
        Next();
        int current = _fs.Level;
        AdjustStack(current - bl.Level);
        bl.Jumps.Add(Emit(Op.Jmp, 0, 0, 0));
        _fs.Level = current;
    }
}
