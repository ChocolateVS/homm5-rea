using H5.Scripting.Lua;
using H5.Scripting.Runtime;

namespace H5.Scripting.Tests;

public class SnapshotTests
{
    static (LuaState L, List<string> Out) NewState()
    {
        var L = new LuaState();
        var output = new List<string>();
        L.Output = output.Add;
        uint seed = 7;
        BaseLibrary.Register(L, _ => null, () => seed = seed * 1103515245 + 12345);
        return (L, output);
    }

    const string Script = @"
        counter = 0
        names = {}
        names[0] = 'a' names[1] = 'b' names[7] = 'h' names[12] = 'm'
        local secret = 'up'
        function worker(n, tag)
            local i = 0
            while i < n do
                i = i + 1
                counter = counter + 1
                print(tag .. i .. %secret)
                sleep(2)
            end
        end
        function walker()
            for k, v in names do
                print('walk ' .. k .. v)
                sleep(1)
            end
        end
        startThread(worker, 4, 'x')
        startThread(worker, 3, 'y')
        startThread(walker)
        cb = parse('print(\'parsed \' .. counter)')
    ";

    [Fact]
    public void RestoredStateContinuesIdentically()
    {
        var (a, outA) = NewState();
        Assert.True(a.DoString(Script, "snap"));
        for (int i = 0; i < 3; i++) a.Step();

        var json = LuaSnapshot.Capture(a).ToJson();

        var (b, outB) = NewState();
        LuaSnapshot.FromJson(json).Restore(b);

        outA.Clear();
        for (int i = 0; i < 12; i++) { a.Step(); b.Step(); }
        a.CallGlobal("cb");
        b.CallGlobal("cb");

        Assert.NotEmpty(outA);
        Assert.Equal(outA, outB);
        Assert.Equal(LuaSnapshot.Capture(a).ToJson(), LuaSnapshot.Capture(b).ToJson());
    }

    [Fact]
    public void TableLayoutSurvivesRoundTrip()
    {
        var (a, _) = NewState();
        a.DoString("t = {} for i = 1, 40 do t[i * 7] = i end t[14] = nil t.x = 1", "t");
        var json = LuaSnapshot.Capture(a).ToJson();
        var (b, _) = NewState();
        LuaSnapshot.FromJson(json).Restore(b);
        var ta = a.GetGlobal("t").TableValue.Pairs().Select(p => p.Key.ToString()).ToList();
        var tb = b.GetGlobal("t").TableValue.Pairs().Select(p => p.Key.ToString()).ToList();
        Assert.Equal(ta, tb);
    }
}
