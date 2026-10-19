namespace H5.Scripting.Lua;

public sealed partial class LuaState
{
    /// <summary>
    /// Resumes a thread until it finishes, sleeps or fails. Errors are reported through
    /// _ERRORMESSAGE and abort the thread, as in the original.
    /// </summary>
    public void Run(LuaThread t)
    {
        if (t.Failed || t.Finished) return;
        var prev = Current;
        Current = t;
        try { Execute(t); }
        catch (LuaRuntimeException e) { Fail(t, e.Message); }
        catch (LuaSyntaxException e) { Fail(t, e.Message); }
        catch (Exception e) when (e is InvalidCastException or IndexOutOfRangeException or ArgumentException
                                      or KeyNotFoundException or NullReferenceException or InvalidOperationException)
        {
            // A bug in a host binding must not take the game down; surface it like a script error.
            Fail(t, $"internal error in script host: {e.GetType().Name}: {e.Message}");
        }
        finally { Current = prev; }
    }

    void Fail(LuaThread t, string message)
    {
        var where = Where(t);
        t.Failed = true;
        t.Frames.Clear();
        t.Top = 0;
        ReportError(where.Length > 0 ? $"{message} [{where}]" : message, t.Label);
    }

    static string Where(LuaThread t)
    {
        if (t.Frames.Count == 0) return "";
        var f = t.Frames[^1];
        var code = f.Closure.Proto.Code;
        int pc = Math.Clamp(f.Pc - 1, 0, Math.Max(0, code.Count - 1));
        int line = code.Count > 0 ? code[pc].Line : 0;
        return $"{f.Closure.Proto.Chunk}:{line} in {f.Closure.Proto.Name}";
    }

    /// <summary>
    /// Prepares a call of the function at <paramref name="funcSlot"/> with arguments up to Top.
    /// Lua functions get a frame (returns true); host functions run immediately (returns false).
    /// </summary>
    internal bool PrepareCall(LuaThread t, int funcSlot, int want, string? calleeName = null)
    {
        var f = t.Stack[funcSlot];
        if (f.Type != LuaType.Function)
        {
            throw new LuaRuntimeException(calleeName != null
                ? $"attempt to call {calleeName} (a {f.TypeName()} value)"
                : $"attempt to call a {f.TypeName()} value");
        }
        if (f.O is LuaClosure c)
        {
            var p = c.Proto;
            int b = funcSlot + 1;
            int nargs = t.Top - b;
            if (nargs < p.NumParams)
            {
                t.EnsureStack(b + p.NumParams + 1);
                for (int i = nargs; i < p.NumParams; i++) t.Stack[b + i] = LuaValue.Nil;
                t.Top = b + p.NumParams;
                nargs = p.NumParams;
            }
            if (p.IsVararg)
            {
                // luaV_pack: `arg` = {extra args...; n = count}, built in ascending order.
                int extra = nargs - p.NumParams;
                var arg = NewTable(extra + 1);
                for (int i = 0; i < extra; i++) arg.Set(i + 1, t.Stack[b + p.NumParams + i]);
                arg.Set("n", extra);
                t.Top = b + p.NumParams;
                t.Push(LuaValue.Table(arg));
            }
            else t.Top = b + p.NumParams;
            t.Frames.Add(new CallFrame { Closure = c, Base = b, Pc = 0, FuncSlot = funcSlot, Want = want });
            return true;
        }

        var h = (HostFunction)f.O!;
        int nhost = t.Top - funcSlot - 1;
        var args = new LuaValue[nhost];
        Array.Copy(t.Stack, funcSlot + 1, args, 0, nhost);
        var ctx = new HostContext(this, t, args, h.Name);
        h.Call(ctx);
        PlaceResults(t, funcSlot, ctx.Results, want);
        GcCheck();
        return false;
    }

    static void PlaceResults(LuaThread t, int at, List<LuaValue> results, int want)
    {
        int n = want < 0 ? results.Count : want;
        t.EnsureStack(at + n + 1);
        for (int i = 0; i < n; i++) t.Stack[at + i] = i < results.Count ? results[i] : LuaValue.Nil;
        t.Top = at + n;
    }

    /// <summary>Moves results base..top of the finishing frame to its function slot.</summary>
    void PostCall(LuaThread t, CallFrame f, int first)
    {
        int count = t.Top - first;
        int n = f.Want < 0 ? count : f.Want;
        int dst = f.FuncSlot;
        t.EnsureStack(dst + n + 1);
        for (int i = 0; i < n; i++) t.Stack[dst + i] = i < count ? t.Stack[first + i] : LuaValue.Nil;
        t.Top = dst + n;
        t.Frames.RemoveAt(t.Frames.Count - 1);
        GcCheck();
    }

    bool ShouldYield(LuaThread t) => NoYield < 1 && t.Sleep > 0;

    void Execute(LuaThread t)
    {
        long budget = InstructionBudget;
        while (t.Frames.Count > 0)
        {
            if (ShouldYield(t) || t.Failed) return;
            var f = t.Frames[^1];
            var p = f.Closure.Proto;
            var code = p.Code;
            int b = f.Base;

            // Inner loop: run this frame until a call/return changes frames or a yield is due.
            for (;;)
            {
                if (--budget < 0)
                    throw new LuaRuntimeException("script did not yield (infinite loop without sleep?)");
                var ins = code[f.Pc++];
                var s = t.Stack;
                switch (ins.Op)
                {
                    case Op.End:
                        PostCall(t, f, t.Top);
                        goto nextFrame;
                    case Op.Return:
                        PostCall(t, f, b + ins.A);
                        goto nextFrame;
                    case Op.Call:
                        {
                            int slot = b + ins.A;
                            p.CallNames.TryGetValue(f.Pc - 1, out var name);
                            if (PrepareCall(t, slot, ins.B, name)) goto nextFrame;
                            if (ShouldYield(t)) return;
                            break;
                        }
                    case Op.PushNil:
                        t.EnsureStack(t.Top + ins.A + 1);
                        for (int i = 0; i < ins.A; i++) t.Stack[t.Top++] = LuaValue.Nil;
                        break;
                    case Op.Pop:
                        t.Top -= ins.A;
                        break;
                    case Op.PushNum:
                        t.Push(LuaValue.Number(p.Numbers[ins.A]));
                        break;
                    case Op.PushStr:
                        t.Push(LuaValue.String(p.Strings[ins.A]));
                        break;
                    case Op.PushUpval:
                        t.Push(f.Closure.Upvalues[ins.A]);
                        break;
                    case Op.GetLocal:
                        t.Push(s[b + ins.A]);
                        break;
                    case Op.GetGlobal:
                        t.Push(Globals.Get(LuaValue.String(p.Strings[ins.A])));
                        break;
                    case Op.GetTable:
                        {
                            var tv = s[t.Top - 2];
                            if (tv.Type != LuaType.Table) throw IndexError(tv);
                            s[t.Top - 2] = tv.TableValue.Get(s[t.Top - 1]);
                            t.Top--;
                            break;
                        }
                    case Op.PushSelf:
                        {
                            var obj = s[t.Top - 1];
                            if (obj.Type != LuaType.Table) throw IndexError(obj);
                            s[t.Top - 1] = obj.TableValue.Get(LuaValue.String(p.Strings[ins.A]));
                            t.Push(obj);
                            break;
                        }
                    case Op.CreateTable:
                        GcCheck();
                        t.Push(LuaValue.Table(NewTable(ins.A)));
                        break;
                    case Op.SetLocal:
                        s[b + ins.A] = s[--t.Top];
                        break;
                    case Op.SetGlobal:
                        Globals.Set(LuaValue.String(p.Strings[ins.A]), s[--t.Top]);
                        break;
                    case Op.SetTable:
                        {
                            int ti = t.Top - ins.A;
                            var tv = s[ti];
                            if (tv.Type != LuaType.Table) throw IndexError(tv);
                            tv.TableValue.Set(s[ti + 1], s[t.Top - 1]);
                            t.Top -= ins.B;
                            break;
                        }
                    case Op.SetList:
                        {
                            int n = ins.B;
                            int aux = ins.A * 64;
                            var arr = s[t.Top - n - 1].TableValue;
                            for (; n > 0; n--) arr.Set(n + aux, s[--t.Top]);
                            break;
                        }
                    case Op.SetMap:
                        {
                            int n = ins.A;
                            var arr = s[t.Top - 2 * n - 1].TableValue;
                            for (; n > 0; n--)
                            {
                                t.Top -= 2;
                                arr.Set(s[t.Top], s[t.Top + 1]);
                            }
                            break;
                        }
                    case Op.Add:
                    case Op.Sub:
                    case Op.Mul:
                    case Op.Div:
                        {
                            var x = s[t.Top - 2];
                            var y = s[t.Top - 1];
                            if (!x.TryToNumber(out var nx)) throw ArithError(x);
                            if (!y.TryToNumber(out var ny)) throw ArithError(y);
                            double r = ins.Op switch
                            {
                                Op.Add => nx + ny,
                                Op.Sub => nx - ny,
                                Op.Mul => nx * ny,
                                _ => nx / ny,
                            };
                            s[t.Top - 2] = LuaValue.Number(r);
                            t.Top--;
                            break;
                        }
                    case Op.Pow:
                        {
                            // Lua 4.0 has no built-in power: `^` needs a "pow" tag method, which the
                            // game never installs (no math library), so it always fails like this.
                            var x = s[t.Top - 2];
                            var y = s[t.Top - 1];
                            throw ArithError(x.Type == LuaType.Number ? y : x);
                        }
                    case Op.Concat:
                        {
                            // luaV_strconc: A operands, folded pairwise from the top (Lua 4.0 merges a..b..c into one CONCAT).
                            int n = ins.A;
                            int top = t.Top;
                            do
                            {
                                var x = s[top - 2];
                                var y = s[top - 1];
                                if (!x.TryToStr(out var sx)) throw new LuaRuntimeException($"attempt to concat a {x.TypeName()} value");
                                if (!y.TryToStr(out var sy)) throw new LuaRuntimeException($"attempt to concat a {y.TypeName()} value");
                                s[top - 2] = LuaValue.String(string.Concat(sx, sy));
                                top--;
                                n--;
                            } while (n > 1);
                            t.Top = top;
                            GcCheck();
                            break;
                        }
                    case Op.Minus:
                        {
                            var x = s[t.Top - 1];
                            if (!x.TryToNumber(out var nx)) throw ArithError(x);
                            s[t.Top - 1] = LuaValue.Number(-nx);
                            break;
                        }
                    case Op.Not:
                        s[t.Top - 1] = s[t.Top - 1].IsNil ? LuaValue.True : LuaValue.Nil;
                        break;
                    case Op.Eq:
                    case Op.Ne:
                        {
                            bool eq = s[t.Top - 2].Equals(s[t.Top - 1]);
                            if (ins.Op == Op.Ne) eq = !eq;
                            s[t.Top - 2] = LuaValue.Bool(eq);
                            t.Top--;
                            break;
                        }
                    case Op.Lt:
                    case Op.Le:
                    case Op.Gt:
                    case Op.Ge:
                        {
                            var x = s[t.Top - 2];
                            var y = s[t.Top - 1];
                            bool r = ins.Op switch
                            {
                                Op.Lt => LessThan(x, y),
                                Op.Le => !LessThan(y, x),
                                Op.Gt => LessThan(y, x),
                                _ => !LessThan(x, y),
                            };
                            s[t.Top - 2] = LuaValue.Bool(r);
                            t.Top--;
                            break;
                        }
                    case Op.Jmp:
                        f.Pc = ins.A;
                        break;
                    case Op.JmpT:
                        if (s[--t.Top].IsTruthy) f.Pc = ins.A;
                        break;
                    case Op.JmpF:
                        if (s[--t.Top].IsNil) f.Pc = ins.A;
                        break;
                    case Op.JmpOnT:
                        if (s[t.Top - 1].IsTruthy) f.Pc = ins.A;
                        else t.Top--;
                        break;
                    case Op.JmpOnF:
                        if (s[t.Top - 1].IsNil) f.Pc = ins.A;
                        else t.Top--;
                        break;
                    case Op.ForPrep:
                        {
                            if (!ToNumberInPlace(s, t.Top - 1)) throw new LuaRuntimeException("`for' step must be a number");
                            if (!ToNumberInPlace(s, t.Top - 2)) throw new LuaRuntimeException("`for' limit must be a number");
                            if (!ToNumberInPlace(s, t.Top - 3)) throw new LuaRuntimeException("`for' initial value must be a number");
                            double step = s[t.Top - 1].N, limit = s[t.Top - 2].N, init = s[t.Top - 3].N;
                            if (step > 0 ? init > limit : init < limit)
                            {
                                t.Top -= 3;
                                f.Pc = ins.A;
                            }
                            break;
                        }
                    case Op.ForLoop:
                        {
                            if (s[t.Top - 3].Type != LuaType.Number) throw new LuaRuntimeException("`for' index must be a number");
                            double step = s[t.Top - 1].N, limit = s[t.Top - 2].N;
                            double idx = s[t.Top - 3].N + step;
                            s[t.Top - 3] = LuaValue.Number(idx);
                            if (step > 0 ? idx > limit : idx < limit) t.Top -= 3;
                            else f.Pc = ins.A;
                            break;
                        }
                    case Op.LForPrep:
                        {
                            var tv = s[t.Top - 1];
                            if (tv.Type != LuaType.Table) throw new LuaRuntimeException("`for' table must be a table");
                            if (!tv.TableValue.Next(LuaValue.Nil, out var k, out var v))
                            {
                                t.Top--;
                                f.Pc = ins.A;
                            }
                            else
                            {
                                t.Push(k);
                                t.Push(v);
                            }
                            break;
                        }
                    case Op.LForLoop:
                        {
                            var tv = s[t.Top - 3];
                            if (!tv.TableValue.Next(s[t.Top - 2], out var k, out var v)) t.Top -= 3;
                            else
                            {
                                s = t.Stack;
                                s[t.Top - 2] = k;
                                s[t.Top - 1] = v;
                                f.Pc = ins.A;
                            }
                            break;
                        }
                    case Op.Closure:
                        {
                            var np = p.Protos[ins.A];
                            var ups = new LuaValue[ins.B];
                            Array.Copy(s, t.Top - ins.B, ups, 0, ins.B);
                            t.Top -= ins.B;
                            t.Push(LuaValue.Function(NewClosure(np, ups)));
                            GcCheck();
                            break;
                        }
                    default:
                        throw new LuaRuntimeException($"bad opcode {ins.Op}");
                }
            }
        nextFrame:;
        }
    }

    static bool ToNumberInPlace(LuaValue[] s, int i)
    {
        if (s[i].Type == LuaType.Number) return true;
        if (!s[i].TryToNumber(out var d)) return false;
        s[i] = LuaValue.Number(d);
        return true;
    }

    /// <summary>luaV_lessthan: numbers compare numerically, strings by bytes (strcoll in the C locale).</summary>
    static bool LessThan(in LuaValue x, in LuaValue y)
    {
        if (x.Type == LuaType.Number && y.Type == LuaType.Number) return x.N < y.N;
        if (x.Type == LuaType.String && y.Type == LuaType.String) return string.CompareOrdinal(x.Str, y.Str) < 0;
        if (x.Type != y.Type)
            throw new LuaRuntimeException($"attempt to compare {x.TypeName()} with {y.TypeName()}");
        throw new LuaRuntimeException($"attempt to compare two {x.TypeName()} values");
    }

    static LuaRuntimeException ArithError(in LuaValue v) => new($"attempt to perform arithmetic on a {v.TypeName()} value");
    static LuaRuntimeException IndexError(in LuaValue v) => new($"attempt to index a {v.TypeName()} value");
}
