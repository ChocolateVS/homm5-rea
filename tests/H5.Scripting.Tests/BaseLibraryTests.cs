using H5.Scripting.Lua;
using H5.Scripting.Runtime;

namespace H5.Scripting.Tests;

/// <summary>The base script functions (registration table 0x00f7bcb8) against the exe's behaviour.</summary>
public class BaseLibraryTests
{
    static (LuaState L, List<string> Out) NewState(Dictionary<string, string>? files = null)
    {
        var L = new LuaState();
        var output = new List<string>();
        L.Output = output.Add;
        uint seed = 12345;
        BaseLibrary.Register(L, p => files != null && files.TryGetValue(p, out var s) ? s : null, () => seed = seed * 1103515245 + 12345);
        return (L, output);
    }

    [Fact]
    public void ModAndSqrtUseTheExeErrorTexts()
    {
        var (L, o) = NewState();
        L.DoString("print(mod(7, 3)) print(mod(-1, 3)) mod(1, 0) print('not reached')", "t");
        Assert.Equal("1", o[0]);
        Assert.Equal("2", o[1]);
        Assert.Contains("Divisor can`t be zero", o[2]);
        Assert.DoesNotContain(o, s => s.Contains("not reached"));
        var (L2, o2) = NewState();
        L2.DoString("print(sqrt(0)) sqrt(-1) print('not reached')", "t");
        Assert.Equal("0", o2[0]);
        Assert.Contains("Value under root can`t be negative", o2[1]);
        Assert.Equal(2, o2.Count);
    }

    [Fact]
    public void ParseSyntaxErrorIsReportedAndStopsTheCaller()
    {
        var (L, o) = NewState();
        L.DoString("f = parse('return 1 + 2') print(f()) parse('x = = 1') print('not reached')", "t");
        Assert.Equal("3", o[0]);
        Assert.StartsWith("(Script) ERROR: ", o[1]);
        Assert.DoesNotContain(o, s => s.Contains("not reached"));
    }

    [Fact]
    public void DoFileRunsInANewThreadAndReportsMissingOrEmptyFiles()
    {
        var files = new Dictionary<string, string> { ["/a.lua"] = "print('a') sleep(1) print('a2')", ["/e.lua"] = "" };
        var (L, o) = NewState(files);
        L.DoString("doFile('/a.lua') print('after') doFile('/none.lua') doFile('/e.lua')", "t");
        Assert.Equal(new[] { "a", "after", "Couldn't open file /none.lua", "File /e.lua is empty." }, o);
        L.Step();
        Assert.Equal("a2", o[^1]);
    }

    [Fact]
    public void PrintFormatsLikeTheExe()
    {
        var (L, o) = NewState();
        L.DoString("print(nil, print, function() end, '007', 'x') print({ a = 'b' }) print({ 1, 2 })", "t");
        Assert.Equal("nil[C-function][function]7x", o[0]);
        Assert.Equal("\n{\na = \"b\"\n,\n}", o[1]);
        Assert.Equal("\n{\n1\n\n2\n,\n}", o[2]);
    }

    [Fact]
    public void ConsoleSetvarStoresTheValue()
    {
        var (L, _) = NewState();
        L.DoString("consoleCmd('setvar ai_time_limit = 1800') consoleCmd('bind enter_pressed \\'Z\\'')", "t");
        lock (BaseLibrary.ConsoleVars)
        {
            Assert.Equal("1800", BaseLibrary.ConsoleVars["ai_time_limit"]);
            BaseLibrary.ConsoleVars["ai_time_limit"] = "180000";
        }
    }
}
