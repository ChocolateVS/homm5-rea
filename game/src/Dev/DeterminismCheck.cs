using System.Diagnostics;
using Godot;
using H5.Adventure;
using H5.Adventure.Map;
using H5.Combat;

namespace H5Reborn.Dev;

/// <summary>
/// `--determinism-check[=trace]` (optionally `--determinism-case=NAME`): runs <see cref="DeterminismSuite"/> inside the
/// game (desktop or browser build) and compares every result with the one recorded on desktop x64. Spread over frames
/// (one command or adventure turn at a time, about 30 ms of work per frame) so the single-threaded browser build keeps
/// drawing. `=trace` also prints the state hash after every command / turn (diff two platforms' logs to find the first
/// diverging one). Ends with "[H5] determinism: done N/M match"; the desktop build then quits (exit code 0 only when
/// all match).
/// </summary>
public partial class DeterminismCheck : Node
{
    static bool _trace;
    static string? _only;
    readonly Queue<object> _todo = new();
    DeterminismSuite.Runner? _battle;
    DeterminismSuite.AdvRunner? _adv;
    int _matched, _checked;
    string _target = "";

    public static bool HandleArg(string arg)
    {
        if (arg == "--determinism-check" || arg == "--determinism-check=trace")
        {
            _trace = arg.EndsWith("=trace");
            Callable.From(Start).CallDeferred();
            return true;
        }
        if (arg.StartsWith("--determinism-case=")) { _only = arg["--determinism-case=".Length..]; return true; }
        return false;
    }

    static void Start()
    {
        if (Engine.GetMainLoop() is SceneTree tree) tree.Root.AddChild(new DeterminismCheck { Name = "DeterminismCheck" });
    }

    bool Wanted(string name) => _only == null || name == _only || name.StartsWith(_only + "-");

    public override void _Ready()
    {
        _target = Game.Instance.Install.IsMmh55 ? "mmh55" : "stock";
        _todo.Enqueue("math");
        foreach (var c in DeterminismSuite.Cases) if (Wanted(c.Name)) _todo.Enqueue(c);
        foreach (var c in DeterminismSuite.AdvCases) if (Wanted(c.Name)) _todo.Enqueue(c);
        GD.Print($"[H5] determinism: {_todo.Count - 1} case(s), target {_target}, "
                 + $"{(OperatingSystem.IsBrowser() ? "browser" : "desktop")} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
    }

    void Report(string name, string hash, string what, IReadOnlyDictionary<string, string>? expected)
    {
        string? want = expected != null && expected.TryGetValue(name, out var h) ? h : null;
        if (want != null) { _checked++; if (want == hash) _matched++; }
        GD.Print($"[H5] determinism: {name} {hash} {what}"
                 + (want == null ? "not recorded" : want == hash ? "match" : $"MISMATCH (desktop {want})"));
    }

    IReadOnlyDictionary<string, string>? Expected => DeterminismSuite.Expected.TryGetValue(_target, out var t) ? t : null;

    public override void _Process(double delta)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 30)
        {
            if (_battle != null)
            {
                var cmd = _battle.Step();
                if (_trace) GD.Print($"[H5] determinism step {_battle.Case.Name} {_battle.Commands} {_battle.State.ComputeHash()} {MessageJson.Serialize(cmd)}");
                if (_battle.Done) { Report(_battle.Case.Name, _battle.State.ComputeHash(), $"({_battle.Commands} commands) ", Expected); _battle = null; }
                continue;
            }
            if (_adv != null)
            {
                _adv.Step();
                if (_trace) GD.Print($"[H5] determinism step {_adv.Case.Name} {_adv.Turns} {_adv.State.ComputeHash()} day {_adv.State.Day}");
                if (_adv.Done) { Report(_adv.Case.Name, _adv.State.ComputeHash(), $"({_adv.Turns} turns) ", Expected); _adv = null; }
                continue;
            }
            if (_todo.Count == 0) { Finish(); return; }
            var db = Adventure.AdventureSession.SharedDb;
            switch (_todo.Dequeue())
            {
                case "math":
                    foreach (var (name, hash) in DeterminismSuite.MathProbes()) Report(name, hash, "", DeterminismSuite.ExpectedMath);
                    break;
                case DeterminismSuite.Case c:
                    _battle = new DeterminismSuite.Runner(db, c);
                    if (_trace) GD.Print($"[H5] determinism step {c.Name} 0 {_battle.State.ComputeHash()}");
                    break;
                case DeterminismSuite.AdvCase a:
                    var mapRef = MapCatalog.Find(Game.Instance.Fs, Game.Instance.Install, a.Map) ?? throw new FileNotFoundException(a.Map);
                    _adv = new DeterminismSuite.AdvRunner(db, new MapLoader(Game.Instance.Db, Game.Instance.Fs).Load(mapRef), a);
                    if (_trace) GD.Print($"[H5] determinism step {a.Name} 0 {_adv.State.ComputeHash()}");
                    return;   // the map load was this frame's work
            }
        }
    }

    void Finish()
    {
        SetProcess(false);
        GD.Print($"[H5] determinism: done {_matched}/{_checked} match");
        if (!OperatingSystem.IsBrowser()) GetTree().Quit(_matched == _checked && _checked > 0 ? 0 : 1);
    }
}
