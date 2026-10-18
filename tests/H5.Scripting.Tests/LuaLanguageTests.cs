using H5.Scripting.Lua;
using H5.Scripting.Runtime;

namespace H5.Scripting.Tests;

public class LuaLanguageTests
{
    static (LuaState L, List<string> Out) NewState()
    {
        var L = new LuaState();
        var output = new List<string>();
        L.Output = output.Add;
        uint seed = 12345;
        BaseLibrary.Register(L, _ => null, () => seed = seed * 1103515245 + 12345);
        return (L, output);
    }

    static List<string> Run(string src, int segments = 0)
    {
        var (L, output) = NewState();
        Assert.True(L.DoString(src, "test"), string.Join("\n", output));
        for (int i = 0; i < segments; i++) L.Step();
        return output;
    }

    static string One(string src) => string.Join("|", Run(src));

    [Fact]
    public void NoBooleansComparisonsYieldOneOrNil()
    {
        Assert.Equal("1nil1nil", One("print(1 < 2, 2 < 1, not nil, not 0)"));
        // `true`/`false` are ordinary globals in Lua 4.0; the game's startup script defines them.
        Assert.Equal("1nil", One("true = not nil  false = nil  print(true, false)"));
    }

    [Fact]
    public void AndOrShareOnePrecedenceLevelLeftAssociative()
    {
        // Lua 4.0: (nil or 1) and nil  → nil ; Lua 5 would give nil or (1 and nil) → nil as well,
        // so use a case that differs: 1 or nil and nil = (1 or nil) and nil = nil in 4.0, 1 in 5.x.
        Assert.Equal("nil", One("print(1 or nil and nil)"));
    }

    [Fact]
    public void NumberFormattingMatchesPrintfG16()
    {
        Assert.Equal("0.1|3|1e+020|-0.5|123456789012|1.5", string.Join("|", Run(
            "print(0.1) print(1+2) print(1e20) print(-1/2) print(123456789012) print('1' + 0.5)")));
        Assert.Equal("x1.25", One("print('x' .. 1.25)"));
        Assert.Equal("0.3333333333333333", One("print(1/3)"));
    }

    [Fact]
    public void UpvaluesAreCopiedAtClosureCreation()
    {
        Assert.Equal("1", One(@"
            local x = 1
            local f = function() return %x end
            x = 2
            print(f())"));
    }

    [Fact]
    public void OuterLocalWithoutPercentIsACompileError()
    {
        var (L, output) = NewState();
        Assert.False(L.DoString("local x = 1 function f() return x end", "t"));
        Assert.Contains(output, s => s.Contains("cannot access a variable in outer scope"));
    }

    [Fact]
    public void MultipleAssignmentEvaluatesThenStoresRightToLeft()
    {
        Assert.Equal("21", One("a, b = 1, 2  a, b = b, a  print(a, b)"));
        // Same key assigned twice: the leftmost store happens last.
        Assert.Equal("first", One("t = {}  t.x, t.x = 'first', 'second'  print(t.x)"));
    }

    [Fact]
    public void CallResultsAdjust()
    {
        Assert.Equal("12nil|1|123", string.Join("|", Run(@"
            function f() return 1, 2, 3 end
            local a, b = f(), 2 print(f(), 2, nil and 1)
            local x = f() print(x)
            print(f())")));
    }

    [Fact]
    public void VarargsBuildArgTable()
    {
        Assert.Equal("3ac", One("function f(...) print(arg.n, arg[1], arg[3]) end f('a', 'b', 'c')"));
    }

    [Fact]
    public void NumericForControlVariableIsTheCounter()
    {
        // In Lua 4.0 the loop variable IS the counter: changing it changes the iteration.
        Assert.Equal("1|3|5", string.Join("|", Run("for i = 1, 5 do print(i) i = i + 1 end")));
        Assert.Equal("3|2|1", string.Join("|", Run("for i = 3, 1, -1 do print(i) end")));
    }

    [Fact]
    public void GenericForWalksNodeOrder()
    {
        // Keys 0..4 inserted ascending into a 4.0 table (size 8 after one rehash): node order is
        // hash & 7 = the key itself, so traversal is ascending.
        Assert.Equal("0a1b2c3d4e", One(@"
            t = {} t[0]='a' t[1]='b' t[2]='c' t[3]='d' t[4]='e'
            s = '' for k, v in t do s = s .. k .. v end print(s)"));
        // Constructor items are inserted in DESCENDING order (SETLIST), which changes collisions.
        Assert.Equal("1a2b3c", One("s = '' for k, v in {'a','b','c'} do s = s .. k .. v end print(s)"));
    }

    [Fact]
    public void TableLayoutMatchesLua40ForCollidingKeys()
    {
        // Size-4 table. 1 → node 1. 5 collides with 1 (main position 1, occupied by a key in its own
        // main position) → goes to firstfree node 3. 3's main position (node 3) holds 5, which is out
        // of its main position and after firstfree → 5 moves to node 2 and 3 takes node 3.
        var t = new LuaTable(0);
        t.Set(1, "a");
        t.Set(5, "b");
        t.Set(3, "c");
        Assert.Equal(new[] { "1", "5", "3" }, t.Pairs().Select(p => p.Key.ToString()).ToArray());
    }

    [Fact]
    public void ErrorsAbortThreadAndCallErrorMessage()
    {
        var output = Run("x = nil  x.y = 1  print('not reached')");
        Assert.Single(output);
        Assert.Contains("attempt to index a nil value", output[0]);
        output = Run("Foo()");
        Assert.Contains("attempt to call global `Foo' (a nil value)", output[0]);
    }

    [Fact]
    public void PowerOperatorFailsWithoutTagMethod()
    {
        var output = Run("print(2^3)");
        Assert.Contains("attempt to perform arithmetic on a number value", output[0]);
    }

    [Fact]
    public void SleepSuspendsUntilSchedulerSegments()
    {
        var (L, output) = NewState();
        L.DoString(@"
            print('a') sleep(2) print('b')", "t");
        Assert.Equal(new[] { "a" }, output);
        L.Step();
        Assert.Equal(new[] { "a" }, output);
        L.Step();
        Assert.Equal(new[] { "a", "b" }, output);
        Assert.Empty(L.Threads);
    }

    [Fact]
    public void StartThreadRunsWhenSchedulerReachesIt()
    {
        var (L, output) = NewState();
        L.DoString(@"
            function worker(n) print('w' .. n) sleep(1) print('w' .. n .. 'done') end
            startThread(worker, 1)
            print('main')", "t");
        Assert.Equal(new[] { "main" }, output);  // queued, not run
        L.Step();
        Assert.Equal(new[] { "main", "w1" }, output);
        L.Step();
        Assert.Equal(new[] { "main", "w1", "w1done" }, output);
    }

    [Fact]
    public void ThreadsResumeInCreationOrder()
    {
        var (L, output) = NewState();
        L.DoString(@"
            function a() while 1 do print('a') sleep(1) end end
            function b() while 1 do print('b') sleep(1) end end
            startThread(a) startThread(b)", "t");
        L.Step();
        L.Step();
        Assert.Equal(new[] { "a", "b", "a", "b" }, output);
    }

    [Fact]
    public void SleepWithoutArgumentIsOneSegmentAndZeroDoesNotYield()
    {
        Assert.Equal("1|2", string.Join("|", Run("print(1) sleep(0) print(2)")));
        Assert.Equal("1|2", string.Join("|", Run("print(1) sleep() print(2)", 1)));
    }

    [Fact]
    public void ConstructorsWithListAndRecordParts()
    {
        Assert.Equal("12xy", One("t = {1, 2; a = 'x', ['b'] = 'y'} print(t[1], t[2], t.a, t.b)"));
        Assert.Equal("5", One("t = {n = 5} print(t.n)"));
    }

    [Fact]
    public void MethodsAndSelf()
    {
        Assert.Equal("7", One("o = {v = 3} function o:add(x) return self.v + x end print(o:add(4))"));
    }

    [Fact]
    public void StringNumberCoercion()
    {
        Assert.Equal("15|105", string.Join("|", Run("print('10' + 5) print(10 .. 5)")));
        var output = Run("print('a' < 1)");
        Assert.Contains("attempt to compare string with number", output[0]);
    }

    [Fact]
    public void MathBuiltinsUseSinglePrecisionArguments()
    {
        // sqrt(2) computed from the float 2.0 is exact; 0.1 as float is 0.100000001490116...
        Assert.Equal("1.414213562373095|0.2000000014901161", string.Join("|", Run("print(sqrt(2)) print(floor(0.1 * 10) / 10 + frac(0.1))")));
        Assert.Equal("2|-2|3", string.Join("|", Run("print(intg(2.7)) print(intg(-2.7)) print(round(2.5))")));
        Assert.Equal("1", One("print(mod(7, 3))"));
    }

    [Fact]
    public void BreakPopsLoopLocals()
    {
        Assert.Equal("3|after", string.Join("|", Run(@"
            for i = 1, 10 do local a = i if i == 3 then print(a) break end end
            local k = 0 while 1 do k = k + 1 if k > 2 then break end end
            print('after')")));
    }

    [Fact]
    public void RepeatConditionDoesNotSeeBlockLocals()
    {
        // In Lua 4.0 the block's locals are out of scope in `until`; `x` is the global.
        Assert.Equal("done", One("x = 1 repeat local x = nil until x print('done')"));
    }
}
